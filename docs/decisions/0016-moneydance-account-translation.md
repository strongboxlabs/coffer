# 0016 — Moneydance account translation rules

* Status: Accepted
* Date: 2026-05-08

## Context

Phase 2 prep involved discovering the actual shape of a large real-world
Moneydance export (a multi-megabyte export with tens of thousands of
transactions and hundreds of accounts including
categories). Several MD-internal type codes and structural patterns turned
up that the original architecture doc didn't anticipate. This ADR records
the translation decisions made before any importer code is written, so the
implementation has a single reference for "what does an MD `acct` row become
in our schema?"

The decisions live next to schema decisions
([0017-account-discriminator.md](0017-account-discriminator.md), which
introduces the `category` account type and `category_kind` column) and rely
on them.

## Decision

### MD `acct.type` translation table

| MD code | Observed in a real-world export | Becomes |
|---|---|---|
| `b` | yes | `account_type='bank'` |
| `c` | yes | `account_type='credit_card'` |
| `v` | yes | `account_type='investment'` |
| `a` | yes | `account_type='asset'` |
| `l` | yes | `account_type='liability'` |
| `o` | yes | `account_type='loan'` (new in migration 007 — distinct from `liability` because MD carries amortization metadata: APR, term, compounding, face value) |
| `i` | yes | `account_type='category', category_kind='income'` |
| `e` | yes | `account_type='category', category_kind='expense'` |
| `s` | yes | **Not an account.** Translates to a `holdings` row when its parent investment account is processed. The `currid` on the `s` row points at the security in `curr`. |
| `r` | yes (single root) | **Filtered.** This is MD's global root container; we don't model a "world" node. Top-level accounts (those whose `parentid` was the `r` row) get `parent_id = NULL` in our schema. |

### Hierarchy translation

- MD's `parentid` becomes our `parent_id`. The constraint added in migration
  007 (`accounts_parent_only_for_categories`) enforces that `parent_id` is
  only allowed on category rows.
- For non-category accounts that had a `parentid` pointing at another non-category
  in MD (e.g. a placeholder "Checking" branch grouping two same-bank checking
  accounts under it), the hierarchy is **dropped**: children are flattened to the
  account_type root, and the placeholder parent (if it had no transactions of its
  own) is **not imported** at all.
  - This is the resolution of the "no fake accounts as tree branches" concern
    surfaced during Phase 2 prep. Real-account hierarchy in MD is purely
    organizational; the underlying entities stand on their own.
  - If a non-category placeholder happens to have its own transactions (rare,
    but possible — the importer must check), it's imported as a normal account
    without children. Its former children are flattened to root with a recorded
    rename hint in `import_source` so the user can re-organize manually if
    desired.
- For category accounts (`type='i'` and `type='e'`), full hierarchy is
  preserved via `parent_id`. A category may have children, may have its own
  direct transactions, or both — all three shapes are valid in our model.

### `is_placeholder` is dropped

Per ADR-0017, the `is_placeholder` column is removed entirely. The concept
(an account that exists for organization, not for transactions) is derived
in the UI as "has children AND no own transactions". The schema does not
need to store it.

### Type='s' security sub-accounts → holdings

An MD `acct` row with `type='s'` represents a per-security position inside
an investment account. Its `currid` field points at the security in `curr`
(filtered to `curr.type='s'`). The translation:

- The MD `s` account itself is **not imported** as a row in `accounts`.
- A `holdings` row is created with:
  - `account_id` = the parent investment account (translated from MD's
    `parentid`)
  - `security_id` = the `securities` row corresponding to the `s` account's
    `currid`
- All transactions whose splits target the `s` account become entries in
  `inv_txn_securities` (and, for buys, generate `lots` rows). That logic
  lives in PR 2.6 (investment-txn mapper); this ADR just records the
  representation.

### Currencies vs securities (MD's `curr` rows)

- `curr.type='s'` (most `curr` rows): a security. Becomes a `securities` row.
  `sec_type` ('Mutual Fund'/'Stock'/'CD'/'Option'/…) maps primarily to
  **`vehicle_type`** ('mutual_fund'/'stock'/'cd'/'option'/…) and only sets
  `asset_class` when the vehicle implies the economic class (Stock→`equity`,
  Bond→`fixed_income`, CD/Money Market→`cash`, Option→`alternative`); a fund's
  class is unknown at import so `asset_class` stays NULL for it. (ADR-0067
  re-split the formerly-overloaded `asset_class`; `SecurityMapper.TranslateSecType`
  is the producer. Seeds `classification_source='import'`, `confidence='assumed'`.)
- `curr.type` missing (the remainder): a currency entry. Used only for
  `currency_code` on accounts; we do not maintain a currencies table.

## Consequences

**Positive**
- A single reference table answering "what does this MD `acct` row become?"
- The translation is mechanical and testable — for every shape in the
  export, the importer's mapping is unambiguous.
- The `loan` and `category` types and `category_kind` column give us the
  precise vocabulary the data needs.

**Negative**
- Real-account hierarchy is not preserved — any "Checking →
  same-bank ×2" grouping in MD becomes flat. We accept this in exchange
  for the schema cleanliness from
  [0017-account-discriminator.md](0017-account-discriminator.md).
  Re-introducing user-defined account groupings is a future feature
  (likely as labels/folders, not via `parent_id`).
- The `import_source` field on the affected accounts records the original
  MD parent name for traceability and possible later reconstruction.

## Alternatives considered

- **Preserve real-account hierarchy via `parent_id` for all account types.**
  Rejected — directly contradicts the "no fake accounts as tree branches"
  decision and the constraint in migration 007.
- **Map `type='o'` to `account_type='liability'` and lose loan-specific
  metadata.** Rejected — real-world data clearly distinguishes loans from
  liabilities (different UI sections in MD), and the amortization metadata
  is real.
- **Map `type='s'` to a "security position account" sub-type rather than to
  `holdings`.** Rejected — `holdings` already exists in the schema for
  exactly this purpose, and creating a parallel concept duplicates state.
