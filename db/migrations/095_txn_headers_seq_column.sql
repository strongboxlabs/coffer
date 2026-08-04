-- =============================================================================
-- 095 — txn_headers.seq: monotonic insertion-order column (ADR-0034 v2)
-- =============================================================================
--
-- THE PROBLEM (continued)
--
-- ADR-0034 elevated `(posted_at, created_at, id)` to the canonical
-- ordering. Real data exposed a gap: the importer batch-INSERTs all
-- headers in one statement, so every header in the batch gets the
-- IDENTICAL `created_at` (now() is evaluated once per statement).
-- Ordering then falls through to `id`, a random UUID.
--
-- That alone would be acceptable IF every consumer sorted by the same
-- UUID column. They don't. The trigger sorts headers by `h.id`. The
-- register's `register_entry_keys` falls back to `l.id` for single-
-- posting events (via `COALESCE(txn_group_id, id)`). Two independent
-- random UUIDs as tiebreakers → trigger walk order and register
-- display order disagree on same-day batch-imported events.
--
-- The user-visible symptom: balance values cascade correctly with
-- SOME ordering but the rows are laid out in a DIFFERENT order, so
-- top-down reading looks scrambled.
--
-- THE STRUCTURAL FIX
--
-- Introduce a strictly-monotonic integer column `seq` on
-- `txn_headers`, populated by a Postgres SEQUENCE. The canonical
-- ordering becomes `(posted_at, seq)`. Two columns. No UUID
-- tiebreaker. Within a batch INSERT, each row calls nextval() once
-- and receives a distinct sequence value, so insertion order survives
-- end-to-end.
--
-- Every consumer — trigger (mig 096), resolved_transactions view +
-- register_entry_keys (mig 097), HoldingsRepository, pagination
-- cursor — keys off this single pair.
-- =============================================================================

-- 1) Global sequence. Per-ledger isolation isn't needed: seq values
-- are only ever compared within a ledger-scoped query, and a global
-- sequence is the simplest shape (no per-ledger bookkeeping).
CREATE SEQUENCE txn_headers_seq;
GRANT USAGE ON SEQUENCE txn_headers_seq TO coffer_app, coffer_service;

-- 2) Add the column, nullable for now so the backfill can populate it.
ALTER TABLE txn_headers ADD COLUMN seq BIGINT;

-- 3) Backfill: assign seq in (created_at, id) order. For existing data
-- the original insertion order isn't recoverable, but (created_at, id)
-- is the closest stable proxy — it's deterministic and matches what
-- ADR-0034's prior trigger used.
WITH ordered AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY created_at, id) AS rn
      FROM txn_headers
)
UPDATE txn_headers t
   SET seq = ordered.rn
  FROM ordered
 WHERE t.id = ordered.id;

-- 4) Advance the sequence past the backfilled max so the next INSERT
-- continues from the high-water mark.
--
-- Postgres SEQUENCE values must be >= 1; calling setval(seq, 0, TRUE)
-- on an empty table errors. Branch on whether any rows exist:
--   * Non-empty: setval(MAX, is_called=TRUE) so next nextval returns MAX+1.
--   * Empty:     setval(1,  is_called=FALSE) so next nextval returns 1.
DO $$
DECLARE
    v_max BIGINT;
BEGIN
    SELECT MAX(seq) INTO v_max FROM txn_headers;
    IF v_max IS NULL THEN
        PERFORM setval('txn_headers_seq', 1, FALSE);
    ELSE
        PERFORM setval('txn_headers_seq', v_max, TRUE);
    END IF;
END $$;

-- 5) Lock the column: NOT NULL, default to nextval, UNIQUE.
ALTER TABLE txn_headers
    ALTER COLUMN seq SET NOT NULL,
    ALTER COLUMN seq SET DEFAULT nextval('txn_headers_seq'),
    ADD CONSTRAINT uq_txn_headers_seq UNIQUE (seq);

-- 6) Read-path index: register paginates by (posted_at DESC, seq DESC)
-- with the visible-only filter. Partial to match mig 022's
-- idx_txn_headers_ledger_visible — keeps maintenance cost down and the
-- index is tightly aligned to the dominant query shape.
CREATE INDEX idx_txn_headers_ledger_posted_seq
    ON txn_headers (ledger_id, posted_at DESC, seq DESC)
    WHERE NOT is_hidden AND is_merged_into IS NULL;

-- 7) Immutability defense: seq is set once on INSERT and must never
-- change, just like (id, created_at). Column-level BEFORE UPDATE
-- trigger rejects any mutation. Same posture as
-- trg_reject_txn_headers_created_at_update (mig 093).
CREATE OR REPLACE FUNCTION fn_reject_txn_headers_seq_update()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.seq IS DISTINCT FROM OLD.seq THEN
        RAISE EXCEPTION
            'txn_headers.seq is immutable (ADR-0034). header_id=%, old=%, new=%',
            OLD.id, OLD.seq, NEW.seq
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_reject_txn_headers_seq_update
BEFORE UPDATE OF seq ON txn_headers
FOR EACH ROW
EXECUTE FUNCTION fn_reject_txn_headers_seq_update();

COMMENT ON COLUMN txn_headers.seq IS
    'Strictly-monotonic insertion-order tiebreaker for the canonical '
    '(posted_at, seq) ordering used by the running-balance trigger, '
    'the resolved_transactions view, register_entry_keys, and the '
    'register cursor codec. Globally unique via txn_headers_seq.';
