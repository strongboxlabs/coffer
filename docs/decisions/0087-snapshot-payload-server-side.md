# 0087 — Snapshot payload stays server-side (OOM fix)

* Status: Accepted
* Date: 2026-07-27
* Extends: [ADR-0037](0037-snapshots-and-backups.md) (capped per-ledger snapshots + whole-DB backups)

## Context

ADR-0037 snapshots stored the in-scope ledger graph as gzip-compressed JSON in
`ledger_snapshots.content` (bytea). Both the create and restore paths
round-tripped the entire payload *through the API process*:

- **Create:** `fn_ledger_snapshot_payload(ledger)` built the graph as jsonb, the
  API read it as a string, then `JsonSerializer` re-shaped it before gzip.
- **Restore:** the API read the bytea, gunzipped it to a string, deserialised it
  into an in-memory object graph, then handed the JSON back to
  `fn_ledger_snapshot_restore`.

For a small ledger this is invisible. For a real one it is not: a ~176 MB
payload inflates to multiple GB of managed heap during (de)serialisation, and
under the API container's `mem_limit` (Workstation GC, capped — see the memory
cap commit) create threw `OutOfMemoryException`. The failure mode was silent —
the weekly auto-snapshot scheduler swallowed it and simply produced no new
snapshot, while the independent daily whole-DB backup (pg_dump, streamed) kept
working. The user lost fresh per-ledger restore points for weeks without a
surfaced error. Streaming the blob would help create but cannot cleanly fix
restore, which needs the whole document to hand to the restore function.

## Decision

**The payload never enters managed memory. Capture and restore both run
entirely inside Postgres.** The API orchestrates and enforces authorization; it
does not carry the bytes.

- New column `ledger_snapshots.content_json jsonb` holds the graph. Postgres
  TOAST-compresses it on disk, so it replaces the hand-rolled gzip.
- **Create** inserts the metadata row (empty `content`, size 0), then calls the
  TVF wrapper `ledger_snapshot_write(snapshot_id, ledger_id)`, which runs
  `content_json := fn_ledger_snapshot_payload(ledger)` and returns only the
  uncompressed byte size for the SPA's "N MB before compression" display. No
  payload crosses the API boundary.
- **Restore** projects `content_json IS NOT NULL` (never the value) to detect
  format, then calls `ledger_snapshot_restore_stored(snapshot_id, ledger_id)`,
  which reads the stored jsonb and reuses the existing
  `fn_ledger_snapshot_restore` body server-side.
- Both wrappers are bound via `HasDbFunction` over keyless result rows — the
  project's blessed pattern; no raw SQL / `NpgsqlCommand` in the data-access
  layer.

### Format compatibility (v1 / v2)

`content_json IS NOT NULL` is the version gate. Pre-existing gzip snapshots
(v1) keep `content` set / `content_json` NULL and restore via the retained
gunzip-then-`fn_ledger_snapshot_restore` path. New snapshots (v2) set
`content_json` and leave `content` empty. Existing v1 snapshots are **not**
migrated — snapshots are convenience restore points, not the DR mechanism (that
is the whole-DB backup), and the four in-flight v1 rows age out under the 5-cap.

## Consequences

- Create and restore are O(1) in API heap regardless of ledger size — the OOM
  is fixed by construction, not by tuning GC or `mem_limit`.
- One fewer serialization format owned in C#: gzip on the write side is gone;
  `GzipDecompress` survives only for v1 read-back until those rows age out.
- The size shown to users is now the true uncompressed jsonb size
  (`octet_length`), computed server-side.
- The silent-failure lesson is separate and already addressed by observability
  work; this ADR is only the storage/capture change.
