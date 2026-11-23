# 0016 — Moneydance account translation rules

* Status: Accepted; amended 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (an account's currency is
  accepted when the file's places for it are at most Coffer's, and refused at preview when
  they are more; the loan mapper checks a loan's escrow and fixed payment the same way);
  amended 2026-11-18 (`sec_type`, `sec_subtype` and `sec_exchange` are read from the type-`s`
  security account as numeric codes); amended 2026-11-19 (a top-level `e` row with
  `coffer_category_kind` `adjustment` becomes an adjustment category, ADR-0091)
* Date: 2026-05-08

## Context

A Moneydance export carries several MD-internal type codes and structural
patterns the original architecture doc does not anticipate. This ADR records
the translation decisions made before any importer code is written, so the
implementation has a single reference for "what does an MD `acct` row become
in our schema?"

The decisions live next to schema decisions
([0017-account-discriminator.md](0017-account-discriminator.md), which
introduces the `category` account type and `category_kind` column) and rely
on them.

## Decision

### MD `acct.type` translation table

| MD code | Becomes |
|---|---|
| `b` | `account_type='bank'` |
| `c` | `account_type='credit_card'` |
| `v` | `account_type='investment'` |
| `a` | `account_type='asset'` |
| `l` | `account_type='liability'` |
| `o` | `account_type='loan'` (new in migration 007 — distinct from `liability` because MD carries amortization metadata: APR, term, compounding, face value) |
| `i` | `account_type='category', category_kind='income'` |
| `e` | `account_type='category', category_kind='expense'` |
| `s` | **Not an account.** Translates to a `holdings` row when its parent investment account is processed. The `currid` on the `s` row points at the security in `curr`. |
| `r` | **Filtered.** This is MD's global root container (a single root); we don't model a "world" node. Top-level accounts (those whose `parentid` was the `r` row) get `parent_id = NULL` in our schema. |

*Amended 2026-11-19:* a top-level type-`e` row carrying `"coffer_category_kind": "adjustment"` becomes `category_kind='adjustment'`. The key with any other value, on any other row type, or on a child category fails the import ([ADR-0091](0091-one-category-taxonomy.md), amendment of 2026-11-18).

### Hierarchy translation

- MD's `parentid` becomes our `parent_id`. The constraint added in migration
  007 (`accounts_parent_only_for_categories`) enforces that `parent_id` is
  only allowed on category rows.
- For non-category accounts that had a `parentid` pointing at another non-category
  in MD (e.g. a placeholder branch grouping several accounts at one bank
  under it), the hierarchy is **dropped**: children are flattened to the
  account_type root, and the placeholder parent (if it had no transactions of its
  own) is **not imported** at all.
  - This resolves the "no fake accounts as tree branches" concern.
    Real-account hierarchy in MD is purely
    organizational; the underlying entities stand on their own.
  - If a non-category placeholder happens to have its own transactions (possible
    — the importer must check), it's imported as a normal account
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

- `curr.type='s'`: a security. Becomes a `securities` row.
  `sec_type` ('Mutual Fund'/'Stock'/'CD'/'Option'/…) maps primarily to
  **`vehicle_type`** ('mutual_fund'/'stock'/'cd'/'option'/…) and only sets
  `asset_class` when the vehicle implies the economic class (Stock→`equity`,
  Bond→`fixed_income`, CD/Money Market→`cash`, Option→`alternative`); a fund's
  class is unknown at import so `asset_class` stays NULL for it. (ADR-0067
  re-split the formerly-overloaded `asset_class`; `SecurityMapper.TranslateSecType`
  is the producer. Seeds `classification_source='import'`, `confidence='assumed'`.)
  *Superseded 2026-11-18:* `sec_type`, `sec_subtype` and `sec_exchange` are not on
  the `curr` row, and `sec_type` is a numeric code, not a name; see the amendment
  below. The class/vehicle split and the seeded source and confidence stand.
- `curr.type` missing: a currency entry. Used only for
  `currency_code` on accounts; we do not maintain a currencies table.
  *Amended 2026-11-16:* not only for `currency_code`. Its `dec` scales every
  amount in that currency and is checked against Coffer's places for the code
  (see the amendment below), and a `csnap` price takes its currency from a
  currency entry: the security's relative currency, else the file's base entry
  (`isbase`) ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D5).

### Amendment, 2026-11-16 — a currency's places ([ADR-0114](0114-benchmarks-and-currency-conversion.md))

Coffer keeps money at each currency's own minor units, 0, 2 or 3 places (ADR-0114 D15),
and a Moneydance currency entry carries its own count, `dec`. An account's currency is
accepted when the file's `dec` for it is at most Coffer's places for that code, since
every amount then fits exactly. When it is greater, the import is refused at preview,
naming the account and both place counts (settled by Claude). The loan mapper checks a
loan's escrow and fixed payment the same way (settled by Claude). How the currency entry
is found and read is ADR-0114 D5's.

### Amendment, 2026-11-18 — the security type is read from the security account

Moneydance stores a security's type as a parameter of the type-`s` security
ACCOUNT, not of the security's `curr` row: its API documents
`SecurityType.CONFIG_KEY` as the key that stores the type in the account's
parameters, and `CurrencyType` has no type, subtype or exchange. So `sec_type`,
`sec_subtype` and `sec_exchange` sit on the `s` account. The importer read them
from `curr`, which does not carry them, so an imported security was left
unclassified.

- `MdAcct` reads the three parameters on type-`s` accounts only
  (`MdAcct.Security`); `MdCurr` no longer has them.
- The securities step builds, from the `s` accounts, a map from each account's
  `currid` (the held security's `curr` id) to the three values. A security held
  in several accounts takes all three from one account: the first, in item
  order, with a `sec_type`, or failing that the first with any value. The values
  are never mixed across accounts, so a code is never paired with a subtype no
  account gave it.
- `sec_type` is Moneydance's numeric code, translated by the importer-owned table
  `MdSecurityType` (anything that writes an export, such as a generator, uses the
  same constants). The subtype strings are Moneydance's own `secsubtypes` names.

| `sec_type` | Moneydance type | (`asset_class`, `vehicle_type`) |
|---|---|---|
| `0` | CD | (`cash`, `cd`) |
| `1` | Bond | (`fixed_income`, `bond`) |
| `2` | Mutual fund | subtype "Exchange Traded Fund" → (NULL, `etf`); subtype "Money Market" → (`cash`, `money_market`); any other or no subtype → (NULL, `mutual_fund`) |
| `3` | Stock (Moneydance files ETFs here too) | subtype "Exchange Traded Fund" → (NULL, `etf`); else (`equity`, `stock`) |
| `4` | Option | (`alternative`, `option`) |
| any other value, numeric or not | — (the code for Moneydance's Other type is undocumented) | (NULL, `other`), and reported |
| absent or empty | — | (NULL, NULL), unclassified |

- The old name matches ('Mutual Fund', 'ETF', 'Stock', …) are gone: exports
  write codes.
- **Reported.** A new validation check, `security-types-recognised`, fails when
  any security got `other` from a code outside the table; its message names the
  codes and the count, and says they were imported with vehicle type Other. Like
  every check it only warns: the security, its holdings and its trades are all
  imported, and the wizard shows it as a warning box.
- `classification_source='import'` and `confidence='assumed'` are seeded whenever
  a code is present, recognised or not; `exchange` comes from the account.
- ADR-0067 needs no change: it names no source field.

Behaviour change: an import of a Moneydance export now classifies its securities
from their accounts; ledgers imported before this change keep their unclassified
securities until the user sets them in the security editor.

## Consequences

**Positive**
- A single reference table answering "what does this MD `acct` row become?"
- The translation is mechanical and testable — for every shape in the
  export, the importer's mapping is unambiguous.
- The `loan` and `category` types and `category_kind` column give us the
  precise vocabulary the data needs.

**Negative**
- Real-account hierarchy is not preserved — any placeholder grouping
  of real accounts in MD becomes flat. We accept this in exchange
  for the schema cleanliness from
  [0017-account-discriminator.md](0017-account-discriminator.md).
  Re-introducing user-defined account groupings is a future feature
  (likely as labels/folders, not via `parent_id`).
  *2026-10-20:* it is folders, in their own table
  ([ADR-0106](0106-folders-replace-tabs.md)), made in the sidebar. The
  importer still flattens, and creates none.
- The `import_source` field on the affected accounts records the original
  MD parent name for traceability and possible later reconstruction.
  *Corrected 2026-10-20:* it does not. `accounts` has no `import_source`
  column (only `txn_headers` does), and the placeholder parent's name was
  never stored, so no backfill can rebuild an imported ledger's groupings
  as folders.

## Alternatives considered

- **Preserve real-account hierarchy via `parent_id` for all account types.**
  Rejected — directly contradicts the "no fake accounts as tree branches"
  decision and the constraint in migration 007.
- **Map `type='o'` to `account_type='liability'` and lose loan-specific
  metadata.** Rejected — Moneydance distinguishes loans from liabilities
  (a separate account type, different UI sections in MD), and the
  amortization metadata is real.
- **Map `type='s'` to a "security position account" sub-type rather than to
  `holdings`.** Rejected — `holdings` already exists in the schema for
  exactly this purpose, and creating a parallel concept duplicates state.
