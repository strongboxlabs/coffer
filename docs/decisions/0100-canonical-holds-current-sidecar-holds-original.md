# 0100 — The canonical row holds CURRENT; a sidecar holds the ORIGINAL

* Status: Accepted
* Date: 2026-09-23
* Supersedes: [ADR-0003](0003-immutable-feed-and-overrides.md)

## Context

ADR-0003 put the feed's values on the transaction row and the user's edits in
`txn_header_overrides`, resolved at read time as `COALESCE(o.x, h.x)`. Three
things went wrong with that in practice, and the first is not a bug that could
be fixed inside the design.

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
where the COALESCE could not see it. The user retyped the date, saved
successfully, and the register did not move.

**3. Nothing ever deleted an override row.** Created on first edit and permanent
after, so (2) was not a transient state.

Two smaller facts settled the shape. `txn_leg_overrides` held **zero rows in
every database** and nothing in the API had ever written one — it was a join on
the hottest view and on the balance walk for no rows at all. And
`txn_header_overrides.is_hidden` had exactly one writer in the whole repository:
a test fixture.

## Decision

Flip the layer.

- `txn_headers` always holds the **current** values. Both write paths assign
  them directly.
- `txn_header_originals` holds the **feed's** values, captured once by the first
  edit that would overwrite them, through one shared helper both paths call
  (`HeaderOriginals.CaptureAsync`).
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
most override rows carried only some fields, so `SET posted_at = o.posted_at`
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
  recover what was already overwritten. In practice the 733 override rows in the
  real ledger were all bank-side, so essentially everything that still existed
  to preserve was preserved — but the loss is real and is not recoverable later.
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
