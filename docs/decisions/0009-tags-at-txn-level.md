# 0009 — Tags modelled at transaction level, not split level

* Status: Accepted — amended by [ADR-0103](0103-tags-at-both-levels.md)
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

## Amendment, 2026-10-07 — one name cap, enforced on import too

`tags.name` is bare `TEXT` with no CHECK. The API capped it at 64 characters in
two places, each with its own `const int 64`; the importer capped it nowhere and
only trimmed. A Moneydance export could therefore create tag names the
application's own tag management would refuse to create, and a later rename of
one would be judged against a limit the stored value already broke.

**The cap is now one constant** (`TagName.MaxLength`), and both API call sites
read it. It lives in `Importer.Moneydance` rather than in the API because `Api`
references the importer and not the reverse, so that is the lowest assembly both
can see — an odd home for a product rule, taken over leaving two numbers to
drift.

**An over-length imported name is TRUNCATED, not rejected.** Refusing an entire
import over a label is the worse failure: a tag name carries no identity and
nothing downstream keys on it. The cost is real and deliberate — two distinct
names sharing their first 64 characters collapse into one tag, and rows carrying
either land on it. `TagNameTruncationTests` pins that collapse so it stays a
known consequence; if that test fails, the policy changed.

Truncation happens in `TransactionMapper`, not in `TagsRepository`, and the
difference is not stylistic. The import step resolves links with
`tagIdByName.TryGetValue(name, …)` and ignores a miss, so truncating at the
repository would key that dictionary by the short name while the caller asked
with the long one: no exception, no log, the tag simply never attaches.

## Alternatives considered

- **Tag splits, derive transaction-level tags as the union.** Faithful to Moneydance but adds query complexity everywhere. Rejected unless real use surfaces a split-level tag pattern that matters. *(2026-09-30: a rental split across three properties is that pattern. [ADR-0103](0103-tags-at-both-levels.md) reopens this — and does not take this option: postings get their own tags, transactions keep theirs, and neither is derived from the other.)*
- **Drop tags entirely.** Loses thousands of transactions' worth of user data. Rejected — added in Phase 1 specifically to preserve this data.
