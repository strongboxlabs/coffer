# 0009 — Tags modelled at transaction level, not split level

* Status: Accepted — amended by [ADR-0103](0103-tags-at-both-levels.md) and [ADR-0108](0108-memo-and-tags-per-side.md) (tags are per side; transaction-level tags exist only on a transaction with several postings)
* Date: 2026-05-08

## Context

Moneydance supports tags. The export contains both transaction-level (`tags`) and split-level (`N.tags`) tag fields:

- A transaction carries its tags in `tags`.
- A split carries its own in `N.tags`. On a single-split transaction a split tag and a transaction tag make the same claim, so the two fields overlap.

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
- True split-level tag fidelity is lost in the round-trip. For Moneydance users who attached different tags to different splits within a single transaction, those distinctions collapse. Accepted as the cost of a simpler model for a personal-finance app.

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

## Amendment, 2026-10-27 — tags are per side (ADR-0108)

The Decision above no longer says where tags live. ADR-0103 gave postings tags of their
own; ADR-0108 D1 places every tag set, like every memo, on a side of the transaction:

- **One posting.** The transaction has no transaction-level tags. Each side's leg holds
  its own set, and a side without one shows the other side's.
- **Several postings.** The header carries the transaction's tags, and each line carries
  its own. The header's set is in play only in the register of the account the
  transaction was split from; the other side of a line sees that line alone.
- **An investment event** shows one tag set: on its brokerage leg when it has one
  posting, on the header when it has several.

The Moneydance import no longer aggregates split tags up to the transaction. Each tag
lands on the side Moneydance kept it for, and a set Moneydance kept for a side Coffer
shows read-only is merged into the side that can edit it, so nothing is dropped.

## Alternatives considered

- **Tag splits, derive transaction-level tags as the union.** Faithful to Moneydance but adds query complexity everywhere. Rejected while no split-level tag pattern needed it. *(2026-09-30: a payment split across two projects, each line tagged with its project, needs it. [ADR-0103](0103-tags-at-both-levels.md) reopens this — and does not take this option: postings get their own tags, transactions keep theirs, and neither is derived from the other.)*
- **Drop tags entirely.** Loses every tag an imported ledger carries. Rejected — added in Phase 1 specifically to preserve this data.
