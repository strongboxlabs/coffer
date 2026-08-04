# 0030 — Domain-pure code organization + discriminated-union register

* Status: Accepted (organization + register direction locked; per-slice
  implementation details TBD)
* Date: 2026-05-21
* Related: ADR-0019 (symmetric postings), ADR-0025 (transactions as
  postings list), ADR-0028 (investment register surface), ADR-0029
  (investment transaction editor)

## Context

The SPA `lib/types.ts` (1059 lines) and `lib/api.ts` (971 lines)
grew into mega-files that bundle every domain (auth, ledger,
account, feed, transaction, security, holding, selection, payee).
The next domain we add — Loan (Phase 6) or Asset (Phase 6+) —
would have made the bundle worse. A bank consumer (manual editor,
feed approval flow) imports the bundle and gets investment fields
on the same `ResolvedTransactionDto` it consumes; an investment
consumer (Holdings View, brokerage editor) imports the bundle and
gets bank-feed fields it ignores.

The register page (`RegisterPage.tsx`, 2479 lines) has a parallel
problem: it renders bank rows and investment rows in one
component via per-row strategy dispatch (`bankStrategy.tsx` /
`investmentStrategy.tsx`). The strategy pattern is already there,
but the page itself interleaves cross-domain wiring.

> **Update (2026-06):** the per-row strategy *dispatch* has since been
> removed — `RegisterRouter` routes by account type to dedicated
> `BankRegisterPage` / `InvestmentRegisterPage`, and shared container
> behavior was extracted into `register/shell/useRegisterController` +
> `useRegisterKeyboardNav`. The `bankStrategy`/`investmentStrategy` modules
> remain as plain cell-renderer libraries. (§2 — discriminated-union
> `RegisterRow` — has since shipped via mig 119; see §2 below.) See
> ADR-0028 update.
>
> **Update (2026-06, row-shell):** splitting into dedicated pages left
> *six* near-duplicate row components — `BankTxnRow` / `SplitParentRowCells`
> / `SplitLegRowCells` and the three investment equivalents — each
> re-declaring the same scaffolding (the `role="row"` container, state
> chrome, interaction wiring, and `RegisterRowLead`) and differing only in
> their domain cells. These collapsed into one shared
> `register/shell/RegisterRow` shell + a per-register `RegisterRowStrategy`
> (`strategies/bankRowStrategy` / `investmentRowStrategy`). The SHELL owns
> everything common to every register row of every variant (txn /
> split-parent / split-leg): container, chrome, interaction, the lead, and
> the variant-universal `data-*` attrs. The STRATEGY owns only what
> genuinely differs per register — the grid template, the container layout
> classes, the `renderBody` cells (one variant switch reusing the existing
> `renderBankSlot*` / `investmentStrategy.renderSlot*` renderers), and the
> register-specific container attrs (`containerAttrs` — bank's `aria-rowindex`
> / `data-scheduled`, investment's `data-headerid` / `data-focused`) so the
> shared shell carries no register-specific attribute knowledge. Net: six
> components → one shell + two thin strategies, with `splitCollapse`,
> `registerRowChrome`, and `RegisterRowLead` reused unchanged.

## Decision

### 1. SPA code organization: split by business domain

Every new file lives in a domain-pure home:

```
src/Web/src/lib/
  types/
    auth.ts, ledger.ts             — identity
    account.ts                     — accounts + sidebar groups + per-account PATCH
    feed.ts                        — feed connections + sync runs
    security.ts                    — securities catalog (cross-domain reference data)
    holding.ts                     — Portfolio View (investment-domain)
    payee.ts                       — payee typeahead (universal)
    selection.ts                   — bulk selection (universal)
    register.ts                    — register read surface (universal — see §2)
    bank.ts                        — bank manual editor + bank-feed editor panels
    investment.ts                  — investment manual editor (lands with A4.c.3)
  api/
    _request.ts                    — fetch wrapper + ApiError
    auth.ts, ledger.ts, account.ts, feed.ts, security.ts,
    holding.ts, payee.ts, selection.ts
                                   — universal / cross-domain endpoints
    register.ts                    — fetchRegister + universal mutations
                                     (setReconStatus, deleteTransaction)
    bank.ts                        — createTransaction, patchTransaction,
                                     fetchSimilarPayees, fetchMergeCandidates
    investment.ts                  — investment manual editor endpoints
                                     (lands with A4.c.3)
```

**Barrels (`lib/types.ts`, `lib/api.ts`) re-export everything** so
existing `from '@/lib/types'` / `from '@/lib/api'` imports keep
working — call-site churn is zero.

**Rule for new domains.** Loan-domain types go in `types/loan.ts`
+ `api/loan.ts` + one barrel line. Asset-domain same. No
back-and-forth re-imports across `bank.ts` ↔ `investment.ts` ↔
`loan.ts`; if two domains need the same type, lift it to a
universal file (`register.ts`, `selection.ts`, etc.).

### 2. Discriminated-union `RegisterRow` (SHIPPED — mig 119)

The register-read endpoint stays universal (one URL, one paginated
stream), but its row shape is now a `kind`-discriminated union
instead of one bag with nullable per-domain fields:

```ts
export type RegisterRow = BankRow | InvestmentRow;
interface RegisterRowBase { /* ~40 universal fields, no kind */ }
interface BankRow       extends RegisterRowBase { kind: 'bank' }
interface InvestmentRow extends RegisterRowBase {
    kind: 'investment';
    investmentAction; security{Id,Ticker,Name}; quantity; unitPrice;
    postingRole; ingest{ActionHint,SecurityId,Shares,UnitPrice,Fee,
    SecurityTickerHint};
}
// future: | AssetRow | LoanRow, added the same way.
```

**Why.** The former `ResolvedTransactionDto` carried ~13 nullable
investment-only fields a bank consumer ignored. Bug surface grew
with every domain; consumers couldn't narrow. The union lets each
consumer pattern-match on `kind` and touch only its domain's fields.

**Discriminant = account domain, not per-leg signal.** An
investment register renders *every* one of its rows with investment
chrome — including cash deposits and fee legs that touch no security
— so `kind` follows the owning account's type, not the leg's
`postingRole`. Migration 119 exposes `account_type` on
`resolved_transactions`; the repository's `Project()` branches
`'investment' → InvestmentRow`, everything else → `BankRow`. The
endpoint is account-scoped, so each response is homogeneous; the
union still lets the shared shell stay domain-agnostic while each
page narrows via `Extract<RegisterRow, { kind }>` / a `kind`
type-guard at the page boundary.

**Server contract / migration.** No DB *data* migration — mig 119 is
one additive view column computed from an already-present join.
System.Text.Json `[JsonPolymorphic]` emits/reads the `kind`
discriminator. Because the SPA is the sole in-repo client and ships
atomically with the API, there was no contract-versioning or
feature-flag step (the previously-parked concern dissolved).

**SPA-synthesized fields moved off the wire.** `feeAmount`,
`feeCategoryName`, `categoryAccount*`, `transferAccount*` — added
client-side by the investment aggregator — now live on
`AggregatedInvestmentRow = InvestmentRow & {…}` (the aggregator's
output type), not on the base contract.

**Cross-account `/legs` exception.** `GET …/transactions/{id}/legs`
(investment-editor reload) returns all-`InvestmentRow`: its
`legsToDraft` reads `postingRole` / `securityId` / `quantity` off
the off-account (category / transfer / fee) legs too, so it can't
use account-type discrimination. A dedicated `ProjectInvestment`
path serves it.

**Universal mutations stay universal.** `SetReconStatusRequest`,
`DeleteTransactionResponse` — recon-status and delete work on any
register row regardless of domain. These remain in
`types/register.ts` / `api/register.ts`.

### 3. Domain-specific register pages over a shared shell

The register page architecture follows the same domain split:

```
register/
  RegisterRouter.tsx              dispatcher (account.accountType → page)
  shell/                          — generic, no domain knowledge
    RegisterTopBar.tsx            ✓ shipped (A4.d Phase 1)
    [future: SelectionToolbar / ReconStatusBadge / further hooks
     as the bank-side decomposition progresses]
  bank/
    BankRegisterPage.tsx          ✓ relocated A4.d Phase 2 (full
                                  decomposition into row + strategies
                                  is a future slice — see follow-ups.md)
    BankRegisterPage.test.tsx     ✓ relocated A4.d Phase 2
  investment/
    InvestmentRegisterPage.tsx        ✓ shipped (A4.d Phase 1)
    InvestmentRegisterPage.test.tsx   ✓ shipped (A4.d Phase 1)
    InvestmentRow.tsx                 ✓ shipped (A4.d Phase 1)
    columns.ts                        ✓ shipped (A4.d Phase 1)
```

Router resolves the account's type at navigation time. Every
register URL in the SPA today targets a specific account
(`/ledgers/{lid}/accounts/{aid}`); the ledger root `/ledgers/{lid}`
renders the Ledger Hub, NOT a cross-account register.

- `/accounts/{aid}` where `accountType === 'bank' | 'credit_card'`
  → `<BankRegister />`
- `/accounts/{aid}` where `accountType === 'investment'`
  → `<InvestmentRegister />`
- `/accounts/{aid}` where `accountType === 'asset' | 'loan' | 'liability'`
  → future register pages (Phase 6+)

**Each domain register imports only its own row type** from the
discriminated union (`Extract<RegisterRow, { kind: 'bank' }>`).
No cross-domain rendering exists — every register is bound to one
account, whose type fixes its domain.

## What's NOT decided yet (TBD)

These need their own design work before the implementation slice:

* **Server contract changes.** The API today returns
  `ResolvedTransactionDto` with nullable per-domain fields. Moving
  to a discriminated union means changing the repository
  projection + the JSON shape. Migration plan, versioning, and
  whether to keep the old shape behind a feature flag during the
  cutover are all open.
* **UI walkthrough per register page.** Columns, filters, empty
  states, header treatments per domain — needs a page-by-page
  spec (per `feedback_deliberate_design`) before code.
* **Slice ID + timing.** Lands after A4.c.3 (investment editor)
  ships — A4.c.3 introduces the investment manual-create surface
  that the discriminated-union work will then consume. Likely
  candidate: B0.5 or A4.d.

## Consequences

* The current PR (#117) lands §1 (file organization) with zero
  behavior change. Barrels preserve every existing import path.
* `lib/types/investment.ts` + `lib/api/investment.ts` are empty
  stubs created by the A4.c.3 follow-up PR, not this PR — avoids
  rebase conflict with the in-flight investment editor branch.
* §1 shipped with PR #117 (file organization).
* §3 shipped in two phases:
  - A4.d Phase 1 (PR #119) — `RegisterRouter` dispatcher,
    `register/shell/RegisterTopBar`, full
    `register/investment/InvestmentRegisterPage` surface.
  - A4.d Phase 2 (PR #119) — `RegisterPage` relocated to
    `register/bank/BankRegisterPage` and renamed.
  - `BankRegisterPage` decomposition shipped: shared shell hooks
    (`useRegisterController` / `useRegisterKeyboardNav`, PR #182)
    and row/columns/bulk-bar/menu modules (PR #183).
* §2 (discriminated-union `RegisterRow`) **shipped via migration 119**
  — `account_type` on `resolved_transactions` drives a
  `BankRow | InvestmentRow` polymorphic contract (System.Text.Json
  `[JsonPolymorphic]`), with the SPA narrowing per account-domain.
  Full spec inline in §2 above. No DB data migration; no contract
  versioning (single in-repo client).
* `docs/follow-ups.md` "Domain-split the remaining mega-files"
  entry continues to track the next big-file targets
  (BankRegisterPage, TxnRowEdit, FeedConnectionsPage,
  SecurityDetailPage, TransactionsRepository,
  InvestmentTransactionsRepository) — they become §3-aligned
  rewrites instead of independent splits.
