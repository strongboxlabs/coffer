# 0009 — Tags modelled at transaction level, not split level

* Status: Accepted
* Date: 2026-05-08

## Context

Moneydance supports tags. The export contains both transaction-level (`tags`) and split-level (`N.tags`) tag fields:

- Thousands of transactions have at least one tag.
- Thousands of split rows reference tags (most of these are duplicates of their parent transaction's tags, since most transactions have one split).

Most personal-finance UX treats tags as a property of the whole transaction, not of the individual splits within it. Splits are an internal accounting structure; tags are a user-facing concept.

## Decision

Tags are modelled at the **transaction level**:

- `tags(id, name, color)`
- `transaction_tags(transaction_id, tag_id)` — many-to-many join.

During Moneydance import, split-level tags are aggregated up to the transaction level (deduplicated). No data is silently dropped: if a tag exists at any split, it is attached to the parent transaction.

## Consequences

**Positive**
- Schema is simple. Two tables, one join.
- UI consistency: the user attaches tags to transactions, which is how they think about it.
- Reports can join `transaction_tags` directly to `resolved_transactions` without an additional aggregation layer.

**Negative**
- True split-level tag fidelity is lost in the round-trip. For Moneydance users who attached different tags to different splits within a single transaction, those distinctions collapse. This is rare in practice and acceptable for a personal-finance app.

## Alternatives considered

- **Tag splits, derive transaction-level tags as the union.** Faithful to Moneydance but adds query complexity everywhere. Rejected unless real use surfaces a split-level tag pattern that matters.
- **Drop tags entirely.** Loses thousands of transactions' worth of user data. Rejected — added in Phase 1 specifically to preserve this data.
