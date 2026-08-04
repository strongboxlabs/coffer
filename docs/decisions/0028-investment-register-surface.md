# 0028 — Investment register surface

* Status: Accepted
* Date: 2026-05-20
* Refines: ADR-0021 (Rule 6: bank register columns), ADR-0027 (action catalog)
* Related: ADR-0019, ADR-0022, ADR-0025 (data shape this UI renders)

## Context

ADR-0021 Rule 6 locked the **bank register's** column layout
(checkbox · status · date · check# · payee/memo · category · tags ·
amount · balance). Investment accounts share the same scaffold but
need a different surface because:

  - A single user-visible "event" maps to N postings (Buy+Fee
    is 2 postings, DivXfr+Fee is 3). The bank's "— N splits —"
    split-parent affordance is wrong here: the user thinks of a
    Buy as one row, not "buy plus fee, expand to see".
  - The Holdings sibling sub-account (ADR-0019) is structural
    noise on the brokerage register — the user bought IDXA,
    not "transferred to the Holdings sub-account".
  - Investment-specific facts that bank rows don't carry need
    real estate: action chip (Buy / Sell / Div / …), security ·
    ticker · qty @ price, fee subtitle on amount, MD-meaningful
    check_number values (Auto / EXfr / Xfr / numeric).
  - Per ADR-0027, one transaction can carry **three** semantic
    counterparties at once (DivXfr+Fee: income category +
    transfer destination + fee category). The bank's single
    "category chip" slot cannot represent this.

Per memory `feedback_md_parity_not_visual`: parity with MD means
the same *concepts*, designed within Ledger's system — not a
literal MD-register clone.

## Decision

### Grid: 9 columns (vs bank's 8)

| # | Slot                          | Width                |
|---|-------------------------------|----------------------|
| 1 | status badge                  | 1.75rem              |
| 2 | selection checkbox            | 2.25rem              |
| 3 | date + check# / tax-date sub  | 6.5rem               |
| 4 | action chip                   | 6rem                 |
| 5 | description (payee + memo)    | `minmax(5rem, 1fr)`  |
| 6 | category · transfer · fee     | 20rem                |
| 7 | security · qty @ price        | `minmax(5rem, 1fr)`  |
| 8 | amount + fee subtitle         | 7rem                 |
| 9 | cash balance                  | 6.5rem               |

Slot 6 gets the widest content allocation because it carries up
to three chips. Description and Security flex with smaller
minimums so slot 6 keeps its real estate at narrow widths.

### Multi-posting aggregation

The SPA's `investmentAggregator` collapses every multi-leg entry
(`kind: 'group'`) on a brokerage register into a single synthesized
`kind: 'txn'` row before `buildDisplayRows` runs. The split-parent
code path (— N splits — / expand children) is never reached on the
brokerage register. Bank/credit/cash/asset/liability keep the
split-parent affordance unchanged.

> **Amended by [ADR-0036](0036-originating-vs-target-register-entries.md):**
> the "investment events are ONE row" policy applies only when the
> brokerage account is the originating side of the header — when
> every posting of the header touches the account. Target-side
> accounts (touched by *some but not all* of the header's postings,
> e.g. a paycheck split's transfer legs landing in a brokerage cash
> sleeve) get one entry per posting from `AssembleEntries` and
> render as `kind='txn'` rows with derived `'Xfr'` actions. The
> aggregator's collapse path runs only on originating-side groups;
> target-side single-leg entries flow through `normalizeSingleLeg`
> as before.

### Slot 6: three chips across two visual lines

Aggregator output stamps four slots onto the synthesized row,
classified by `txn_legs.posting_role` (ADR-0019, migration 057):

| Role + position                       | Stamped onto              |
|---------------------------------------|---------------------------|
| `'income'`                            | `categoryAccount*`        |
| `'fee'` at `legIndex == 0`            | `categoryAccount*`        |
| `'transfer'`                          | `transferAccount*`        |
| `'fee'` at `legIndex > 0`             | `feeCategoryName`+`feeAmount` |

Renderer:

  - **Line 1**: category chip + transfer chip, side-by-side. Both
    render only when populated; only-category, only-transfer,
    and both-populated all valid shapes.
  - **Line 2**: fee chip (typically "Investment Fees"). Renders
    only when present.
  - **Empty cell** when none of the four are set (e.g., solo Buy
    where Holdings sibling is stripped).

**No positional placeholders.** Earlier iterations used em-dashes
on empty halves of line 1 and a "|" separator between category and
transfer; both were dropped. Chip variant (color by account type)
+ the Action column already carry the semantic distinction.
Position is no longer a load-bearing cue inside slot 6.

The column header reads `"Category | Transfer · Fee"` — the "|" in
the header label is fine because it announces the three sub-slots;
it just looked busy as a per-row literal.

### Holdings-sibling suppression

Legs whose counterparty is the brokerage's
`accounts.holdings_account_id` are dropped during aggregation
(both multi-leg and single-leg paths). The legacy
`counterpartyAccount*` field also gets zeroed on solo
Buy/Sell rows so no consumer ever sees the Holdings name.

### Misc as fees-allowed for both income AND expense shape

MD's `inc` (income) and `exp` (expense) splittypes both map to
Ledger action `misc` (ADR-0027). The aggregator's role-table
treats them symmetrically:

  - `inc` shape: income leg at `legIndex=0` → categoryAccount;
    optional fee leg at `legIndex>0` → fee subtitle.
  - `exp` shape: fee leg at `legIndex=0` → categoryAccount (MD
    uses `'fee'` role for the main outgoing leg on `exp`); optional
    additional fee leg at `legIndex>0` → fee subtitle.

`transfer` (MD `bank`) is the only Ledger action with no optional
fee leg — bank-style transfers don't carry brokerage fees.

### Date cell: check# under date on investment, tax-date on bank

Investment rows are always two-line cells (security carries
`qty @ price` on line 2; amount carries `fee $X.XX` on line 2).
Using the date cell's line 2 for `check_number` keeps the row's
vertical rhythm uniform AND surfaces MD's investment-specific
marker values (Auto / EXfr / Xfr / numeric) that would otherwise
have no visible home.

Column header on investment register: `"Date · Check #"`.

Bank register keeps the existing `tax YYYY-MM-DD` sub-label on
line 2 when `transactedAt !== postedAt` (the only register where
tax-date is currently visible at all). A systemic
tax-date redesign (editor field, reports grouping, exports,
register UX) is captured in
[follow-ups.md → Tax / transaction date](../follow-ups.md).

### Strategy-pattern surface

`RegisterStrategy` is the per-account-type contract that
RegisterTable plugs into. The investment surface introduces two
additions:

  - `dateHeader: string` — replaces the hardcoded "Date" column
    header. Bank: `"Date"`. Investment: `"Date · Check #"`.
  - `renderDateSubLabel?(txn): ReactNode | null` — optional
    line-2 renderer for the date cell. Bank: not provided →
    RegisterPage falls back to the tax-date sub-label.
    Investment: returns `txn.checkNumber`.

Per-row fallback chain in RegisterPage:
`strategy.renderDateSubLabel?.(txn) ?? (taxDateLabel ? tax {…} : null)`.

> **Update (2026-06):** the polymorphic `RegisterStrategy` *dispatch*
> (`pickRegisterStrategy`, the `RegisterStrategy` contract type) has been
> removed. `RegisterRouter` routes each account type to a dedicated page
> (`BankRegisterPage` / `InvestmentRegisterPage`), so per-row strategy
> dispatch was never reached at runtime — each page imports its own cell
> renderers directly. `strategies/bankStrategy.tsx` and
> `strategies/investmentStrategy.tsx` remain as plain cell-renderer modules;
> only the indirection layer was collapsed. A future per-type surface
> (e.g. loan accounts) gets its own page, not a strategy entry.

### Per-action UI field mapping

The investment register's columns (using their visible UI
headings) draw from either the header row or a specific
`posting_role` leg. The mapping is exhaustive: every field shown
in the register has a single, documented source.

#### Header-level fields (uniform across all actions)

| UI heading | Source |
|---|---|
| (status badge, slot 2) | `header.status` ↦ `cleared` / `reconciling` / `uncleared` |
| **Date** (line 1 of slot 3) | `header.postedAt` |
| **Check #** (line 2 of slot 3) | `header.checkNumber` — shown when non-null |
| **Action** (slot 4) | `header.action` ↦ `ACTION_LABEL[]` |
| **Payee** (line 1 of slot 5) | `header.payee` |
| **Memo** (line 2 of slot 5) | `header.memo` — shown when non-null |
| **Amount** (line 1 of slot 8) | `SUM(leg.amount)` across this account's legs of the header — order-independent |
| **Balance** (slot 9) | brokerage cash balance **after the whole transaction** completes. Impl: `txn_header_account_balances.balance_after` keyed by `(header_id, brokerage_account_id)`, surfaced through `resolved_transactions.balance_after`. Per ADR-0034 there is one value per `(header, account)`, no posting-leg picker. |

#### Action-varying fields (slot 6 + slot 7 + Amount subtitle)

The columns below map onto the three sub-fields of slot 6
(**Category** \| **Transfer** · **Fee chip**), the two sub-fields
of slot 7 (**Security** / **Shares @ Price**), and the slot 8
fee subtitle (**Fee amt**). Every populated cell sources from
exactly one `posting_role` leg (legend below the matrix).

| Ledger `action` | Category (s6 L1L) | Transfer (s6 L1R) | Fee chip (s6 L2) | Security (s7 L1) | Shares @ Price (s7 L2) | Fee amt (s8 L2) |
|---|---|---|---|---|---|---|
| `buy`               | —          | —              | `fee` leg † | `security` leg | `security` leg's qty @ price | `fee` leg's \|amount\| † |
| `buyx`              | —          | `transfer` leg | `fee` leg † | `security` leg | `security` leg's qty @ price | `fee` leg's \|amount\| † |
| `sell`              | —          | —              | `fee` leg † | `security` leg | `security` leg's qty (negative) @ price | `fee` leg's \|amount\| † |
| `sellx`             | —          | `transfer` leg | `fee` leg † | `security` leg | `security` leg's qty (negative) @ price | `fee` leg's \|amount\| † |
| `dividend_cash`     | `income` leg | —            | `fee` leg † | `security` leg (qty = 0) | — (qty = 0 suppressed) | `fee` leg's \|amount\| † |
| `dividend_reinvest` | `income` leg | —            | `fee` leg † | `security` leg | `security` leg's qty @ price (shares acquired) | `fee` leg's \|amount\| † |
| `divx`              | `income` leg | `transfer` leg | `fee` leg † | `security` leg (qty = 0) | — (qty = 0 suppressed) | `fee` leg's \|amount\| † |
| `transfer`          | —          | `transfer` leg | — *(no fee per ADR-0027)* | — *(no `security` leg on `bank` txntype)* | — | — |
| `misc`              | `income` leg § | —          | `fee` leg † | `security` leg (qty = 0) | — (qty = 0 suppressed) | `fee` leg's \|amount\| † |

Legend:
- **`{role}` leg** = the unique `txn_legs` row of this header
  whose `posting_role` = `{role}`. MD guarantees at most one of
  each kind per investment txn (no multi-fee, no multi-income
  shapes — see `moneydance-investment-actions.md`).
- **Source for Category / Transfer / Fee chip cells**: the named
  leg's `counterpartyAccountName` (with a chip variant from
  `categoryChipVariant()` based on the account's type / kind).
- **Source for Security cell**: the `security` leg's
  `securityTicker` + `securityName`.
- **Source for Shares @ Price cell**: the `security` leg's
  `quantity` + `unitPrice`; suppressed when `quantity = 0` (MD
  emits a zero-qty `sec` split on `div` / `divx` / `inc` / `exp`
  to keep the security_id linkable for per-security queries —
  but there are no real shares to render).
- **Source for Fee amt subtitle**: the `fee` leg's `amount`
  (displayed as `|amount|` since the sign is implied by the word
  "fee").
- **`—`** = the action's structural shape doesn't include this
  posting role. Cell stays empty (no placeholder character).
- **† Conditional on `fee` split presence.** MD's `fee` split is
  optional on every fee-eligible txntype. The cell is empty
  when the txn has no fee leg.
- **§ Sign discriminates inc vs exp.** Per ADR-0027, MD's `inc`
  and `exp` splittypes both stamp `posting_role='income'` on the
  category leg. Direction comes from the sign of the brokerage-
  cash-side leg's amount (positive = income, negative = expense).

### Refinement (2026-06): bank-shape target-splits use the split-parent UI

The original decision said the investment register "can never reach
the split-parent code path" — true for **investment events** (Buy+Fee,
DivReinvest, …), which still collapse to ONE flat row. But ADR-0036's
target-split clusters proved an exception:

  - When a header that **originates elsewhere** (a paycheck, a manual
    cross-account split) posts **2+ legs** onto this brokerage's cash
    sleeve, those legs arrive as separate `kind:'txn'` target entries
    (`accountPostingsOnHeader > 1` and `< headerTotalPostings`). The
    earlier treatment rendered each leg as its own flat row with a
    fabricated per-leg running balance (`legBalanceOverrides`), which
    showed distinct amounts against misleading intermediate balances.
  - These now collapse into ONE **expandable split-parent row** —
    exactly mirroring the bank register (`buildDisplayRows` +
    `SplitParentRowCells` / `SplitLegRowCells`). The parent shows the
    cluster's **net amount** and the **real** post-header balance, plus
    a "▸ N splits" affordance; expanding reveals each leg with its own
    amount and a **blank** balance cell. The cluster is read-only here
    (ADR-0036): no edit / delete; "Show other side" jumps to the
    originating register. Selecting the parent registers as read-only,
    so bulk Delete stays disabled.

So the split-parent path **is** reached on the investment register —
but only for bank-shape target-split clusters, never for true
investment events (those stay one flat row). The aggregator
(`investmentAggregator.ts`) runs a re-grouping pass after collapsing
investment events: runs of `kind:'txn'` target legs sharing
`(headerId, accountId)` with `accountPostingsOnHeader > 1` are emitted
as one `kind:'group'` entry (legs in `legIndex` order, count fields
retained for read-only detection). The `legBalanceOverrides`
fabricated-balance plumbing was removed.

## Consequences

  - The investment register reaches the split-parent code path for
    bank-shape **target-split clusters** (above); true investment
    events still collapse to ONE flat row. A future per-leg drill-down
    on a genuine brokerage event (e.g. expanding a Buy+Fee) would still
    need a distinct affordance, not a reuse of this target-split path.
  - Slot 6's three-chip shape relies on `posting_role` being
    populated correctly by the importer (migration 057's trigger
    enforces). A non-investment leg with `posting_role IS NULL`
    that somehow ended up on a brokerage register would render
    as a blank slot 6 — by design (no inference fallback per the
    `feedback_constraints_over_workarounds` rule).
  - Bank rows' tax-date sub-label is now best understood as a
    placeholder for the systemic tax-date treatment captured in
    follow-ups.md, not the final UX.
  - The `dateHeader` + `renderDateSubLabel` strategy additions
    are extensible: a future `loanStrategy` (loan accounts) can
    plug in its own date treatment (e.g. payment-due date as
    line 2) without touching RegisterPage.
