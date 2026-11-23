-- 271 — ledger_snapshot_stored_sizes: what each snapshot of a ledger occupies in the database.
--
-- The snapshots panel showed content_size_uncompressed: the length of the payload's JSON text,
-- measured once at capture. That is not what a snapshot costs. Postgres compresses what it
-- stores, so the same snapshot holds a fraction of that on disk, and the figure sat beside a
-- backup (compressed, and without snapshots) as if the two were the same measure.
--
-- This reports what is stored, by the storage generation of each snapshot:
--   v3 (mig 193): the sum over its chunk rows in ledger_snapshot_parts,
--   v2 (mig 179): the jsonb value in ledger_snapshots.content_json,
--   v1:           the gzip bytes in ledger_snapshots.content.
-- pg_column_size gives a value's size as stored, compression included. For a value held out of
-- line it reads the pointer and does not fetch or decompress the value, so it is cheap beside the
-- payload it measures. Nothing is written, so it covers every existing snapshot with no backfill.
--
-- Service role only, like the chunk table it reads (mig 254, FU-086): a chunk holds every member's
-- rows of the ledger, private ones included, so coffer_app has no privilege on it. The endpoints
-- call this through the service role once they have established that the caller may see the
-- ledger, as snapshot create already does. It returns sizes and nothing of the content.

CREATE OR REPLACE FUNCTION ledger_snapshot_stored_sizes(p_ledger_id uuid)
RETURNS TABLE (snapshot_id uuid, stored_bytes bigint)
LANGUAGE sql
STABLE
AS $$
    SELECT s.id,
           COALESCE(
               (SELECT sum(pg_column_size(p.content))
                  FROM ledger_snapshot_parts p
                 WHERE p.snapshot_id = s.id),
               pg_column_size(s.content_json),
               octet_length(s.content)
           )::bigint
      FROM ledger_snapshots s
     WHERE s.ledger_id = p_ledger_id;
$$;

COMMENT ON FUNCTION ledger_snapshot_stored_sizes(uuid) IS
    'Bytes each snapshot of a ledger occupies as stored, compression included (v3 chunks, '
    'v2 jsonb or v1 gzip). Read-only; what the snapshots panel shows.';

REVOKE ALL ON FUNCTION ledger_snapshot_stored_sizes(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ledger_snapshot_stored_sizes(uuid) TO coffer_service;
