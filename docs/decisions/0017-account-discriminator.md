# 0017 — Categories as a single `account_type` with a `category_kind` discriminator

* Status: Accepted
* Date: 2026-05-08
* Refines: [0002-unified-accounts-table.md](0002-unified-accounts-table.md)
* Refined by: [ADR-0019](0019-symmetric-postings.md) (adds `is_system` + `holdings_account_id` columns; preserves the `parent_id` invariant by keeping the per-brokerage Holdings sibling at the root)

## Context

[ADR-0002](0002-unified-accounts-table.md) established that categories live
in the `accounts` table — a unified data model matching Moneydance's. The
initial Phase 1 schema implemented this with `account_type ∈ ('income',
'expense')` for categories alongside the real-account types (`bank`,
`credit_card`, `investment`, `asset`, `liability`).

During Phase 2 prep, two problems surfaced with that encoding:

1. **The `account_type` column conflates two orthogonal facts:** *what kind
   of thing is this row* (real account vs budgeting concept) and *which
   flow direction* (money coming in vs going out). They are not the same
   question; mashing them together makes constraints awkward.
2. **The hierarchy concept is asymmetric:** real accounts (banks, credit
   cards, investment accounts) shouldn't have hierarchy in our model
   (per the "no fake accounts as tree branches" concern). Categories
   genuinely do — "Employer Benefit Spouse" with sub-categories
   "Dental"/"Medical"/"Vision" is a meaningful tree. The schema can
   enforce that distinction only if categories are a single discriminated
   type.
3. **Categories carry no real-account state.** They have no feed
   connection, no opening balance, no currency-specific configuration. The
   schema couldn't enforce that without a single category type.

## Decision

The `accounts` table uses one `category` value in `account_type` for all
categories (replacing the prior `'income'` and `'expense'` values), with a
separate `category_kind` column carrying the income/expense distinction.

```
accounts.account_type ∈ ('bank', 'credit_card', 'investment',
                          'asset', 'liability', 'loan',
                          'category')

accounts.category_kind ∈ ('income', 'expense') NULL
```

Three CHECK constraints (added in [migration
007](../../db/migrations/007_phase2_schema.sql)) make the model
self-policing:

```sql
-- category_kind is set IFF this row is a category
CHECK ((account_type = 'category') = (category_kind IS NOT NULL))

-- hierarchy via parent_id is only allowed on categories
CHECK (parent_id IS NULL OR account_type = 'category')

-- categories don't carry real-account state
CHECK (account_type <> 'category'
       OR (feed_connection_id IS NULL AND opening_balance = 0))
```

Additionally, `is_placeholder` is dropped from the schema. "This account is
just a folder" is derived in the UI as "has children AND no own
transactions" rather than stored.

## Consequences

**Positive**
- Each column means one thing. `account_type='category'` says "this is a
  budgeting concept", `category_kind='expense'` says "money flows out
  through it". Reports compose these two filters explicitly instead of
  decoding an overloaded enum.
- Constraint-enforceable invariants: the database now refuses to create a
  bank account with a `category_kind`, a category with a feed connection,
  or a credit-card child of another credit card. None of these were
  expressible before.
- Adding new account flavours later (`tax_account`, `pseudo_account`, ...)
  doesn't fight with the income/expense overload.
- ADR-0002's core principle — categories live in `accounts` — is
  preserved; only its implementation tightens.

**Negative**
- Reports change shape: "expense category total" becomes
  `WHERE account_type='category' AND category_kind='expense'` instead of
  `WHERE account_type='expense'`. Slightly more verbose. Acceptable.
- The MD `acct.type='i'` / `'e'` translation gains one extra column
  assignment per row. Captured in [ADR-0016](0016-moneydance-account-translation.md).
- A small behavioural change at the UI layer: code that previously checked
  `account_type IN ('income','expense')` to identify categories now checks
  `account_type = 'category'`. There is no such code yet (Phase 1 ships
  schema only), so the cost is documentary.

## Alternatives considered

- **Keep `account_type IN ('income','expense')`.** The status quo from Phase
  1. Loses the constraint-enforceability and conflates two concepts.
  Rejected.
- **Separate `categories` table from `accounts` (drop the unified model).**
  Reverts ADR-0002. Forces every report and query to reason about two
  primitives that mean the same thing. Rejected.
- **Separate `account_folders` table for organizational groupings.** An
  earlier draft of this decision proposed it, but the screenshot of the
  user's category tree showed that hierarchy is a *real* concept for
  categories, not a separate "folder" abstraction layered on top. The
  parent-with-direct-balance shape ("Employer Benefit Spouse" with $0 own
  balance + 3 sub-categories with their own transactions) is meaningful
  data, not folder metadata. Rejected.
