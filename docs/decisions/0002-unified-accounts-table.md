# 0002 — Unified `accounts` table; categories are accounts

* Status: Accepted (refined by [ADR-0017](0017-account-discriminator.md))
* Date: 2026-05-08

## Context

Personal finance applications can model "categories" (Groceries, Salary, Utilities) as either:

1. A separate `categories` table referenced by `splits.category_id`.
2. Rows in the `accounts` table with `account_type IN ('income', 'expense')`, referenced by `splits.account_id`.

Moneydance — the system being replaced and migrated *from* — uses approach (2). Confirmed directly by Moneydance support: *"Categories are accounts. They are accounts with type income or expense."* When a $100 grocery transaction posts in checking, Moneydance creates a corresponding entry in the Groceries expense account; right-clicking "Show Other Side" reveals this register.

## Decision

Use approach (2): **one `accounts` table** holding both real accounts and categories, discriminated by `account_type`. Splits reference accounts by FK. There is no separate `categories` table.

## Consequences

**Positive**
- Imports from Moneydance are a structural translation, not a semantic one. The 772 `acct` rows in the export map 1:1 to our `accounts`.
- Reports that "spend by category" and "transactions for a category" use the same primitives as "transactions for a checking account" — uniform query surface.
- "Show other side" of any transaction is trivial: follow the splits.
- Income/expense accounts can have their own running balance (lifetime-to-date totals), which is useful for category-level analytics.

**Negative**
- The `accounts` table mixes concepts that some users mentally separate. Mitigated by `account_type` filtering everywhere.
- A few constraints are shaped by this choice — e.g. `is_placeholder` does double duty as "folder for real accounts" and "category group". That's acceptable.

## Alternatives considered

- **Separate `categories` table.** Cleaner mental model for users who don't think double-entry, but it forces every report and view to reason about two distinct primitives that mean the same thing. Rejected because the import source already uses unified accounts and the unification is more honest about what categories are.
- **`accounts` + `categories` with a polymorphic `splits.target_kind`/`splits.target_id`.** Triple-cost without benefit. Rejected.
