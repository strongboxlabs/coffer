-- Sync activity log (Phase 5 slice 2c.1).
--
-- The `sync_runs` table has existed since migration 002 but never
-- been written to — slice 2c shipped the live sync flow without
-- wiring observability. This slice fills that gap: every Sync now
-- click writes one `sync_runs` row, and partial-failure detail
-- (errlist + promote-on-clear events) lands in two new child
-- tables that the SPA can expand on demand.
--
-- Schema changes:
--
-- 1. `sync_runs` gains a `ledger_id` RLS anchor (one-hop via
--    user_ledger_grants), a `triggered_by_user_id` audit column,
--    three new counters (`txns_promoted`, `txns_already_known`,
--    `txns_still_pending`), and a widened `status` CHECK that
--    distinguishes partial-success and needs_reauth outcomes.
--
-- 2. The pre-existing RLS policy (feed_connection_id transitive,
--    rejecting NULL FK rows) is replaced with a direct ledger_id
--    policy so detached runs (post-disconnect cleanup, FK SET NULL)
--    remain visible to their ledger owner.
--
-- 3. New child tables `sync_run_errors` + `sync_run_promotions`
--    capture the granular per-event detail. Each is RLS-scoped
--    transitively through its sync_run_id.
--
-- Legacy counters `txns_merged` and `txns_queued` are from the
-- pre-2c merge-pipeline / staging-table model. Kept NOT NULL
-- DEFAULT 0 for column compatibility; no longer written. Drop
-- scheduled in a follow-up alongside `pending_transactions`.

-- ---------------------------------------------------------------------------
-- 1) sync_runs column additions
-- ---------------------------------------------------------------------------

ALTER TABLE sync_runs
    ADD COLUMN ledger_id            UUID,
    ADD COLUMN triggered_by_user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN txns_promoted        INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN txns_already_known   INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN txns_still_pending   INTEGER NOT NULL DEFAULT 0;

-- Backfill ledger_id from feed_connections for any rows that may
-- already exist (no production writer today, but local dev DBs may
-- carry experimental rows). Orphan rows whose feed_connection_id
-- doesn't resolve get dropped — they're un-attributable and the
-- column is about to become NOT NULL.
UPDATE sync_runs r
   SET ledger_id = fc.ledger_id
  FROM feed_connections fc
 WHERE r.feed_connection_id = fc.id
   AND r.ledger_id IS NULL;

DELETE FROM sync_runs WHERE ledger_id IS NULL;

ALTER TABLE sync_runs
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT fk_sync_runs_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

COMMENT ON COLUMN sync_runs.ledger_id IS
    'RLS anchor — one-hop visibility check via user_ledger_grants. '
    'Set on insert from the connection''s ledger_id; remains valid '
    'even after feed_connection_id gets nulled by a disconnect.';

COMMENT ON COLUMN sync_runs.triggered_by_user_id IS
    'User who clicked Sync now. NULL for system-triggered runs '
    '(future scheduled-sync worker) or when the original user has '
    'been removed.';

COMMENT ON COLUMN sync_runs.txns_promoted IS
    'In-place updates on existing txn_headers (slice 2c promote-'
    'on-clear): a previously-pending FITID re-arrived with '
    'pending:false. Distinct from txns_inserted (new rows).';

COMMENT ON COLUMN sync_runs.txns_already_known IS
    'FITID matches against txn_headers where the existing row is '
    'already bank-posted — no-op skips. Drives the "X already '
    'known" SPA copy.';

COMMENT ON COLUMN sync_runs.txns_still_pending IS
    'Subset of txns_inserted that landed with is_pending=TRUE '
    '(SimpleFIN pending:true). Drives the "Y still pending at '
    'the bank" SPA copy.';

-- ---------------------------------------------------------------------------
-- 2) Status CHECK widening
-- ---------------------------------------------------------------------------

ALTER TABLE sync_runs DROP CONSTRAINT sync_runs_status_check;
ALTER TABLE sync_runs
    ADD CONSTRAINT sync_runs_status_check
    CHECK (status IN ('running', 'completed', 'partial', 'failed', 'needs_reauth'));

COMMENT ON COLUMN sync_runs.status IS
    'Outcome state:'
    ' running — sync in flight (synchronous today; the row only '
    'sticks in this state if the process crashes before the '
    'final UPDATE);'
    ' completed — clean 2xx, errlist empty;'
    ' partial — clean 2xx, errlist non-empty;'
    ' failed — non-403 non-2xx, surfaced as SimpleFinException;'
    ' needs_reauth — 403 from SimpleFIN (access URL revoked / '
    'expired). Paired with feed_connections.status flip.';

-- ---------------------------------------------------------------------------
-- 3) Index for the per-connection recent-runs list query
-- ---------------------------------------------------------------------------

CREATE INDEX idx_sync_runs_feed_connection_started
    ON sync_runs (feed_connection_id, started_at DESC)
    WHERE feed_connection_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 4) Replace RLS policy: ledger_id anchor instead of feed_connection_id
--    transitive. Old policy rejected rows with NULL feed_connection_id
--    (the post-disconnect "detached" state), which would have made
--    historical runs disappear from the SPA after the user removed
--    the connection. New policy reads through the still-present
--    ledger_id.
-- ---------------------------------------------------------------------------

DROP POLICY sync_runs_per_user ON sync_runs;
CREATE POLICY sync_runs_per_user ON sync_runs FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

-- ---------------------------------------------------------------------------
-- 5) sync_run_errors — one row per SimpleFIN v2 errlist[] entry
-- ---------------------------------------------------------------------------

CREATE TABLE sync_run_errors (
    id                       UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    sync_run_id              UUID         NOT NULL REFERENCES sync_runs(id) ON DELETE CASCADE,
    code                     TEXT         NOT NULL,
    message                  TEXT         NOT NULL,
    simplefin_connection_id  TEXT,
    simplefin_account_id     TEXT,
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT now()
);

COMMENT ON TABLE sync_run_errors IS
    'Persisted SimpleFIN v2 errlist[] entries — partial-failure '
    'messages the bank reported alongside successful accounts. '
    'The SPA expands a run to show these; without persistence they '
    'were visible only in the immediate sync response.';

CREATE INDEX idx_sync_run_errors_run ON sync_run_errors(sync_run_id);

ALTER TABLE sync_run_errors ENABLE ROW LEVEL SECURITY;
CREATE POLICY sync_run_errors_per_user ON sync_run_errors FOR ALL TO coffer_app
    USING      (sync_run_id IN (SELECT id FROM sync_runs))
    WITH CHECK (sync_run_id IN (SELECT id FROM sync_runs));

-- ---------------------------------------------------------------------------
-- 6) sync_run_promotions — one row per promote-on-clear event
-- ---------------------------------------------------------------------------

CREATE TABLE sync_run_promotions (
    id              UUID            PRIMARY KEY DEFAULT gen_random_uuid(),
    sync_run_id     UUID            NOT NULL REFERENCES sync_runs(id) ON DELETE CASCADE,
    header_id       UUID            NOT NULL REFERENCES txn_headers(id) ON DELETE CASCADE,
    was_amount      NUMERIC(19,4)   NOT NULL,
    became_amount   NUMERIC(19,4)   NOT NULL,
    promoted_at     TIMESTAMPTZ     NOT NULL DEFAULT now()
);

COMMENT ON TABLE sync_run_promotions IS
    'Audit of slice 2c promote-on-clear events: bank-side amount '
    'change between the pending hold and the cleared transaction '
    '(restaurant tip, exchange rate, etc.). One row per affected '
    'txn_headers row per sync run. Cascades on header delete — '
    'the audit is meaningful only in the context of an existing '
    'register row.';

CREATE INDEX idx_sync_run_promotions_run ON sync_run_promotions(sync_run_id);
CREATE INDEX idx_sync_run_promotions_header ON sync_run_promotions(header_id);

ALTER TABLE sync_run_promotions ENABLE ROW LEVEL SECURITY;
CREATE POLICY sync_run_promotions_per_user ON sync_run_promotions FOR ALL TO coffer_app
    USING      (sync_run_id IN (SELECT id FROM sync_runs))
    WITH CHECK (sync_run_id IN (SELECT id FROM sync_runs));
