# 0050 — Account editor + loan amortization modeling

* Status: Accepted
* Date: 2026-06-15
* Related: ADR-0002 (unified accounts table), ADR-0017 (account discriminator /
  `account_type`), ADR-0043 (account/category picker), ADR-0047/0048/0049
  (reminders backend / live-template views / reminders SPA), ADR-0027
  (investment action catalog), ADR-0032 (triggers as a last resort), and the
  "importers report the feed, not a cash model" principle.

## Context

A loan (mortgage) reminder surfaced wrong values: the calendar showed only the
fixed escrow portion of the payment, not the full principal + interest + escrow.
Investigation of the real Moneydance data confirmed the cause is **not** an
import bug:

- A Moneydance **loan account** (`obj_type:"o"`) carries the amortization
  parameters — original principal, annual rate, payment count, payments/year,
  `calc_pmt` (MD computes the P&I payment), a current escrow amount, and the
  interest + escrow account references.
- The loan **reminder** template stores **0** for the principal and interest
  splits (only the fixed escrow leg has an amount). MD recomputes the P&I split
  for each occurrence from the loan account's amortization (current balance ×
  rate), so it never stores them in the reminder. The importer faithfully
  carried MD's zeros.
- Coffer does **not** model loans (no amortization parameters on `accounts`, no
  loan metadata imported) and has **no account create/edit UI at all** — every
  account exists only because import created it; `AccountsEndpoints` exposes only
  narrow patches (feed-mapping, sync, trade-commission, active).

The user chose to model loans properly rather than work around it. Because the
loan parameters need an entry/maintenance point and Coffer has no account editor,
the work has two parts: a **general account editor** and a **loan amortization
model** layered on it.

### Principle: faithful import vs. computed cash model

A standing rule says importers must report the feed, never synthesize a cash
model from feed type or memo strings. Computing P&I via amortization is
**consistent** with that rule here: we import MD's own loan **parameters**
(rate / term / principal — part of the feed) and compute the split exactly the
way MD does from those parameters. The computation is transparent and remains
**user-overridable at post** (adjust-at-post, ADR-0049 D9/D10). We are modeling
per the feed's loan definition, not imposing a treatment inferred from a memo.

## Decisions (agreed with the user)

### D1 — `loan_terms` table, 1:1 with the loan account

A new table keyed by `account_id` (PK + FK → `accounts.id`, `ON DELETE
CASCADE`). Columns (all money/rate as `decimal`): `original_principal`,
`annual_interest_rate` (stored as a percent, e.g. `3.65`, documented),
`payment_count`, `payments_per_year`, `first_payment_date`, `escrow_amount`,
`interest_account_id` (FK → `accounts.id`), `escrow_account_id` (FK →
`accounts.id`), `payment_is_computed` (bool, from MD `calc_pmt`). Check
constraints: rate ≥ 0, `payment_count` > 0, `payments_per_year` > 0,
`original_principal` > 0. The EF entity configures every FK
(`HasOne/WithMany/HasForeignKey/OnDelete`). Loan terms hold only the **fixed
contract**.

### D2 — Current balance is derived, never stored

The amortization reads the loan account's **posted leg sum** as the current
balance (the principal still owed). It is never copied onto `loan_terms`, so it
cannot drift from the ledger.

### D3 — The split is computed; template P&I legs stay 0 (faithful import)

A reminder is "loan-driven" when one of its splits targets a loan account that
has `loan_terms`. For such a reminder the occurrence's postings are computed:

- fixed payment = annuity formula on (original principal, periodic rate, count)
  when `payment_is_computed`; otherwise the stored payment;
- periodic interest = current_balance × (annual_rate / payments_per_year);
- principal = payment − interest;
- escrow = `loan_terms.escrow_amount` — the **current** value (escrow is
  re-recorded for the upcoming payment; the stale reminder leg is ignored),
  editable per occurrence.

The reminder template's P&I legs remain 0 — the import stays faithful; the split
is a computed view, not stored data.

### D4 — Amortization lives in a C# service (business layer)

The repository fetches `loan_terms` + the current balance via LINQ; a C#
amortization service does the arithmetic. This keeps the API data-access layer
LINQ-only (no raw SQL), is unit-testable with mocks, and respects layer
separation. (A Postgres function was considered and rejected for testability +
keeping the business rule in the business layer.)

### D5 — Future occurrences show the fixed total; the split is computed on open

The calendar shows the fixed full payment (P&I + escrow) for every future
occurrence. The precise principal/interest **split** is computed from the live
balance only when the actionable (next-due) occurrence is opened. Projecting the
balance forward for every cell would assume on-time, no-extra-principal payments
— speculative and complex — and the total is fixed regardless, so the split only
matters at post time.

### D6 — Adjust-at-post reuse (no new commit path)

The computed split becomes the **editable prefill** in the occurrence dialog;
the user fine-tunes the small monthly drift; Post commits the confirmed split via
the existing `/fire/bank` path (ADR-0049 D9). `reminderBankPrefill` gains a loan
branch that builds postings from the computed split instead of the zero template
legs. `GetUpcomingAsync` returns the computed full payment as the amount;
`GetDetailAsync` returns the computed legs.

### D7 — Importer populates `loan_terms`

The Moneydance import maps `obj_type:"o"` loan accounts: it already creates the
`account_type='loan'` account; it now also upserts a `loan_terms` row from the MD
fields (rate, payment count, payments/year, principal, escrow amount,
`interest_account_id` / `escrow_account_id` resolved through the import account
map to the **exact** Coffer accounts MD points at, `calc_pmt`). Idempotent, keyed
by account. Real data has duplicate/aliased interest & escrow accounts, so the
mapping resolves the specific referenced ids rather than matching by name.

### D8 — General account editor (new foundational capability)

New account create + edit, for all REAL account types — Coffer had none. Mapped
from MD's per-type account dialogs (bank / credit card / investment / loan),
keeping the universal fields and treating type-specific *terms* as their own
blocks (lift the concept, not the layout).

- **API:** `POST /accounts` (create), `PATCH /accounts/{id}` (edit, partial;
  `account_type` immutable), and `GET /accounts/{id}` → `AccountDetail` (the
  full editable shape incl. the metadata the lean `AccountSummary` omits —
  account/routing number, URL, notes — so the editor prefills on edit without
  bloating every list/picker fetch). Validation per ADR-0017; system accounts
  reject edits; investment create still materializes the Holdings sibling.
- **UI:** a sectioned editor dialog (Identity / Details) — name, type (locked on
  edit), institution, account/card number (label adapts by type), routing
  (bank), currency (dropdown), website, notes, active. The accounts
  **management page** (`/ledgers/$id/accounts`, off the Hub's "Manage accounts")
  lists REAL accounts grouped by type with a "Show inactive" toggle and richer
  rows (institution · currency · inactive badge). **Categories are not shown** —
  they're not accounts and get their own surface.
- **Deferred to the next slice (needs the migration):** Start date (`opened_on`)
  and opening-balance editing.

### D9 — Loan terms stay loan-specific; credit-card terms are a separate feature

We do NOT generalize `loan_terms` into a shared "account terms". Amortizing
loans (fixed payment from rate/principal/term + escrow) and revolving credit
cards (APR / promo / credit limit / min-payment / expiry) are different shapes;
conflating them buys nothing. `loan_terms` stays loan-only (D1); the credit-card
terms block is its **own future feature**. Likewise the investment "default fee
category", and the MD-only extras (default category, account hierarchy, check
numbers, hide-on-summary, net-worth flag, balance adjustment) are out (Deferred).

### D10 — Import seeds once; Coffer owns accounts + categories thereafter

The endgame is to retire Moneydance and run Coffer exclusively at parity, so MD
import is a one-time **seed**: the importer INSERTs new accounts/categories with
their MD metadata but **never updates an existing row** on re-import. From the
first import on, Coffer owns every account/category field — name, institution,
account/routing number, URL, notes, currency, `is_active`, parent/kind, and
`loan_terms`. This fixes the reported bug (re-import flipping a Coffer-deactivated
account back to active) and generalizes it: any in-Coffer edit survives re-import.

- `UpsertWithAdoptionAsync` updates metadata ONLY on a fresh insert; existing
  rows (junction hit or same-name adoption) are returned untouched (it returns
  `(id, inserted)` so the caller can tell). The `UpdateAccountDataFieldsAsync`
  path is removed. (`UpsertByExternalIdAsync` is kept — it's the lower-level
  primitive the importer unit tests exercise and is not on the pipeline's
  re-import path.)
- The category parent-wiring second pass (`UpdateParentByExternalIdAsync`) is
  gated to **freshly-inserted** rows, so re-import doesn't re-parent existing
  categories.
- `loan_terms` is seeded via `INSERT … ON CONFLICT (account_id) DO NOTHING` —
  filled once (incl. for already-imported loan accounts on the next run), then
  Coffer-owned. `accounts.opened_on` (Start Date) is seeded from MD's creation
  date.

Categories are treated exactly like accounts (Coffer will manage them too, later).

## Consequences

- Loan reminders show the correct full payment and a correct, editable P&I +
  escrow split — the motivating defect is fixed.
- Coffer gains a first-class account editor, unblocking manual account
  maintenance generally (not just loans).
- The amortization is a computed view over imported parameters + the live
  balance; no stored schedule to drift, and every value is overridable at post.

## Deferred (own ADRs / later slices)

- Amortization **schedule** view / loan detail page.
- Forward balance **projection** for future occurrences (D5 shows the total
  only).
- Variable-rate / ARM, extra-principal, and balloon handling.
- Loan auto-commit worker.
- Investment-account creation specifics in the new editor.
- **Credit-card terms** (APR / promo / credit limit / payment plan / expiry) —
  its own feature, not folded into `loan_terms` (D9).
- Investment **default fee category** (a categorization feature; no column).
- MD-only account fields with no Coffer concept: default category, account
  hierarchy (Coffer's `parent_id` is categories-only, ADR-0017), check numbers,
  hide-on-summary-if-zero, include-in-net-worth, balance adjustment (an
  adjusting transaction, not an account field).

## Implementation slices (each = one PR, green on CI)

1. **Account editor (API + UI), real types** — create / edit / detail endpoints
   + DTOs + validation + the sectioned editor (identity + details incl. the
   metadata fields) + the management page (grouped, show-inactive, richer rows).
   No schema change. Independently valuable.
2. **`loan_terms` schema + `opened_on` / opening-balance + importer** — table +
   EF entity + DbUp migration (incl. the `opened_on` date column and exposing
   opening-balance editing) + importer population from MD `obj_type:"o"`. Tests.
3. **Loan Terms editor block + opening balance / opened-on** — the editor's
   loan sub-form (REQUIRED on loan accounts) + opening-balance and opened-on
   fields (editable on create AND edit); the create/edit API reads/writes
   `loan_terms` when type = loan; a stateless `loan-payment-preview` endpoint
   computes the estimated payment live as the user types (C# `LoanAmortization`
   is the single source of truth — no amortization math duplicated in the SPA).
   Tests.
4. **Amortization service + loan-aware reminders** — the C# service +
   `GetUpcomingAsync`/`GetDetailAsync`/`reminderBankPrefill`/fire computing the
   split for loan-driven reminders. End-to-end verification on the real loan.

(Credit-card terms + investment default-fee-category: separate later features,
per D9.)
