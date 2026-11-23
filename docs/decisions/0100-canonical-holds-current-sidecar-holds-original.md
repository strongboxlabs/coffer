# 0100 — The canonical row holds CURRENT; a sidecar holds the ORIGINAL

* Status: Accepted; amended 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15 (the sidecar keeps the
  transaction's amount and a rounded-at-import flag; an import that rounds an amount seeds
  the row; `CaptureAsync`'s whole-row snapshot now includes the amount)
* Date: 2026-09-23 (amended 2026-10-21: the Moneydance importer seeds originals)
* Supersedes: [ADR-0003](0003-immutable-feed-and-overrides.md)

## Context

ADR-0003 put the feed's values on the transaction row and the user's edits in
`txn_header_overrides`, resolved at read time as `COALESCE(o.x, h.x)`. That
design has three defects, and the first is not a bug that could be fixed inside
the design.

**1. A cleared field is unrepresentable.** The override columns are plain
nullable columns with no companion "is overridden" marker, so NULL has to mean
both *not overridden* and *overridden to empty* — and `COALESCE` always resolves
it as the former. The editor sends null for an emptied payee box, the server
reads it as "leave alone", the old text comes back. You could not clear a payee,
memo or check number on a bank row. Ever. Adding a marker column per field is
the only fix that keeps the layer, and at that point the layer is carrying a
parallel schema.

**2. The two write paths disagreed.** The investment PATCH writes header fields
straight onto `txn_headers`; the bank PATCH wrote the override row. So an
investment edit of a row that already carried an override — every merge winner
does, since the survivor adopts the folded row's date — landed *underneath* it,
where the COALESCE could not see it: retyping the date saved successfully and the
register did not move.

**3. Nothing ever deleted an override row.** Created on first edit and permanent
after, so (2) was not a transient state.

Two smaller facts settled the shape. Nothing in the API had ever written
`txn_leg_overrides` — it was a join on the hottest view and on the balance walk
for a table no code path filled. And
`txn_header_overrides.is_hidden` had exactly one writer in the whole repository:
a test fixture.

## Decision

Flip the layer.

- `txn_headers` always holds the **current** values. Both write paths assign
  them directly.
- `txn_header_originals` holds the **feed's** values, captured once by the first
  edit that would overwrite them, through one shared helper both paths call
  (`HeaderOriginals.CaptureAsync`). *(Amended 2026-10-27, [ADR-0108](0108-memo-and-tags-per-side.md) D1: its one
  caller is the write adapter, `AnnotationWriter.CaptureOriginalsAsync`, which every
  edit path goes through. It captures the transaction's memo as it stands wherever
  it sits: on a one-posting transaction the originating side's own leg memo, on a
  multi-posting one the header's.)*
- Reads go straight to the header — no join, no `COALESCE`. NULL means NULL.
- Request fields are decided by **presence, not nullness**: an omitted key
  leaves its column alone, an explicit `null` clears it. The two NOT NULL date
  columns reject an explicit null rather than ignoring it.
- `txn_leg_overrides` is dropped outright, not flipped.
- `resolved_transactions.has_overrides` keeps its name and changes meaning: *this
  row has an original on file*, i.e. it has been edited. Same question, answered
  from the other side — and now true for investment edits too, which the old
  signal missed entirely.

Migration 230. The fold onto the canonical row is `COALESCE`, never assignment:
an override row could carry only some fields, so `SET posted_at = o.posted_at`
would have blanked the date on every row whose override had none.

## Consequences

**Positive**

- Clearing a field works by construction.
- The hottest view in the app loses a `LEFT JOIN` and five `COALESCE`s, as do
  the balance walk, `account_current_balances`, the payee suggestions function
  and three holdings functions.
- Bank and investment now share one write shape and one capture rule.
- "Reset to original" and the modified indicator survive: the original is a
  single row lookup, not a reconstruction.
- Feed-vs-user disambiguation on re-sync survives too — compare the incoming
  value against the *original* row instead of against current.
- Payee recall still works. It anchors on the BANK's payee and suggests the
  curated one, so it needs both to exist; it now reads the original from the
  sidecar. Folding and discarding would have been simpler and would have broken
  it silently.

**Negative**

- The capture must be atomic with the first edit, or a crash leaves a mutated
  row with no recorded original. Both paths already run in a transaction, so
  this is placement, not new machinery.
- **Rows the investment PATCH edited before migration 230 have lost their
  originals permanently.** That path wrote canonical and captured nothing, so
  the migration preserved what the override table still held and could not
  recover what was already overwritten. Edits through the bank PATCH are
  unaffected — it wrote the override row, and the migration folded every one it
  found — but the loss is real and is not recoverable later.
- Restoring a snapshot captured before 230 needs a compatibility fold, because
  an old payload holds the feed's values in `txn_headers` and the edits in
  `txn_header_overrides`. Ignoring that key would reinstate the feed values and
  silently discard every edit, on the disaster-recovery path. The restore
  function folds instead; two tests cover both arms of that gate.

## On ADR-0003's rejected alternative

ADR-0003 listed and rejected:

> **Mutate `transactions` directly, store original in a sidecar
> `transaction_history`.** Loses the "always-recoverable original" property and
> requires every read to reconstruct history if the user wants to compare.
> Rejected.

That rejection was aimed at a **history log** — many rows per transaction, so
recovering the original means replaying. This is a **single original snapshot**,
written once. Recovering the feed value is one row lookup by primary key, and
the original is never lost, so neither stated objection applies. The rejected
alternative and this decision are not the same design.

ADR-0003's other two alternatives still stand rejected, and for the same
reasons: dropping the original entirely would break re-sync dedup and payee
recall, and event sourcing remains overkill.

## Amendment, 2026-10-21 — the Moneydance importer seeds originals too (FU-024, mig 260)

**`txn_header_originals` has a second writer, and a row no longer means "edited in
Coffer".** Moneydance keeps the bank's own text in `ol.orig-payee` / `ol.orig-memo`
beside the user's curated `desc` / `memo`. The importer writes the curated values onto
the header and used the feed's only as a fallback when the curated one was blank, so
every cleaned-up payee arrived with the bank's string behind it gone. A header written that way
is already in the state the first edit leaves behind (curated on the canonical row, feed
value elsewhere), so the importer now writes the sidecar to match.

- **Only where they differ.** A header gets a seed when a non-blank `ol.orig-payee` or
  `ol.orig-memo` differs, ordinally, from the value actually written. The blank-curated
  fallback is no difference by construction. Where nothing differs, no row is written.
- **A whole-row snapshot, like `CaptureAsync`'s.** Payee and memo come from the feed; a
  side with no feed value takes the header's. `posted_at`, `transacted_at` and
  `check_number` are read off the header row just inserted, so they are the values the
  importer wrote. Both routes seed (`FeedOriginals.SeedFor`); reminder templates do not.
- **`CaptureAsync` is unchanged.** It still captures once per header, so the first API
  edit of a seeded header finds the row on file and leaves it: the sidecar keeps the
  feed's value, and `has_overrides` stays set.

*Amended 2026-10-27 (ADR-0108 D1): `txn_header_originals.memo` is the transaction's feed
memo, wherever the curated value sits.* A one-posting transaction keeps its curated memo
on the originating side's leg, not on the header, so the seed compares the feed's memo
with the memo as written, not with the header column: `FeedOriginals.SeedFor` takes
`writtenMemo`, and a side with no feed value takes that written memo.
- **Existing ledgers** get the same seed from migration 260, replayed over
  `provider_raw_payload`, on headers with no originals row yet. `captured_at` is the
  header's `created_at`, the moment the importer wrote it, never the migration's clock.
- **A header someone edited before the upgrade** already has its row, captured from
  what the old importer wrote: MD's curated payee and memo, and on the investment route
  no check number. Under the new importer that row would hold the feed's text and
  `chk`, so 260 brings it there: a side whose non-blank feed value differs from the
  capture takes the feed value, and a NULL check number on an investment row takes
  `chk`. Neither was a user's choice; the capture snapshotted them. The header itself,
  which holds the user's edits, is not touched.

**What this does to `has_overrides`.** The Decision above says the flag means "this row
has an original on file, i.e. it has been edited". The first half still holds; the
"i.e." does not, for Moneydance rows. A seeded row reads `has_overrides = true` from the
moment it is imported: it means the feed said something other than what the row now
shows, whether the difference was made in Moneydance or in Coffer. The undo-import
count was removed (2026-09-25) partly because it read the table the other way. Nothing
that needs "edited in Coffer" specifically may read this table for it; that question has
no stored answer.

Pinned by `TransactionMapperTests` / `InvestmentTransactionMapperTests` (seed on a
differing payee, a differing memo, both; none when the feed agrees, is blank, or was the
fallback), `HeaderOriginalsSeedImportTests` (persisted rows carry the header's written
values, on both routes) and `BrokerageTransferAndOriginalsBackfillTests` (migration 260,
including rows captured before the upgrade).

## Amendment, 2026-10-26 — the flag leaves the register row

The register row no longer carries `hasOverrides`: the API serves only the SPA, and the
SPA never read it. Nothing else changes: `resolved_transactions.has_overrides`, the
originals table and the rule that a row's existence is the signal all stay as decided
above and in the 2026-10-21 amendment.

## Amendment, 2026-11-16 — the originals keep the bank's amount ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D15)

**`txn_header_originals` gains the transaction's amount and a rounded-at-import flag,
and an import that rounds an amount writes it.** ADR-0114 D15 rounds an ingested amount
with more places than its account's currency allows to the currency's places, and keeps
the bank's figure as sent. That figure is a feed value the canonical row no longer
holds, which is what this table is for, so it lives here and not in a new `ingest_*`
carrier on `txn_headers`: a transaction's feed originals stay in one place.

- **Two columns, added by migration 301 (ADR-0114 D15).** `amount`, an unbounded `NUMERIC`, is the
  transaction's amount on its originating account, the sum of its legs there, which is
  the Amount its own register shows, in the row's stored sign (settled by Claude,
  2026-11-16). `rounded_at_import`, `BOOLEAN NOT NULL DEFAULT false`, says an import
  rounded that amount; it is needed because an edited amount also differs from the
  stored one. Rows captured before the migration keep a null amount and a cleared flag
  (settled by Claude, 2026-11-16).
- **An import that rounds seeds the row.** It writes the originals at import time, as
  whole as `CaptureAsync`'s snapshot: payee, memo, dates and check number as written, as
  the Moneydance seed does, with the unrounded amount and the flag set. An import that
  does not round writes no row.
- **`CaptureAsync`'s unconditional snapshot includes the amount.** It still captures
  once per header and every column, so the first edit of any bank row that has no
  originals on file yet, rounded or not, now keeps the amount from before the edit; that
  consequence is accepted. The first edit of a seeded row finds its row on file and
  leaves it, as for a Moneydance seed.
- **The Moneydance seed carries the amount as written** (settled by Claude, 2026-11-16).
  A seed is a whole-row snapshot like CaptureAsync's, and a later edit finds the row on
  file and keeps nothing, so a seed without the amount would never get one. Its flag is
  false: a Moneydance amount is never rounded. Migration 260's seeds keep none, since
  nothing records what they held when they were made.
- **A promote-on-clear restates the feed's amount** (settled by Claude, 2026-11-16). The
  feed itself changes the amount, so the clear writes the cleared figure and the flag
  (set when it rounds, cleared when it does not) into the row's originals, seeding them
  when it rounds and the row has none. It is the one write that changes an amount
  already on file. The payee, memo, dates and check number are not touched.
- **What this does to `has_overrides`.** A seeded row reads it true from import, as a
  Moneydance seed does, and the 2026-10-21 amendment's caveat covers it: the row means the
  feed said something other than what the row shows. Nothing that reads the flag is
  affected (settled by Claude, 2026-11-16).
- **Payee recall** (settled by Claude, 2026-11-16). Where the SimpleFIN pre-split
  backfill rewrites a row's payee and memo, it writes them into the originals too when
  those still hold the values it replaces, so recall reads a seeded row as it reads one
  with no originals.
