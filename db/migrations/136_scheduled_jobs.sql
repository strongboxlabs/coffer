-- =============================================================================
-- 136 — scheduled_jobs: one per-ledger daily scheduler for all job types
-- =============================================================================
--
-- Generalizes quote_schedules (mig 135) into a single table keyed by
-- (ledger_id, job_type), so the quote-refresh and auto-snapshot schedulers
-- share one mechanism instead of duplicating tables/workers/endpoints. A single
-- background worker (SchedulerService) polls next_run_at and dispatches each due
-- row by job_type to a registered handler.
--
-- Migrates the existing quote_schedules rows in as job_type='quote-refresh',
-- then drops the table. (snapshot auto-run, ADR-0037, is the second job_type;
-- it replaces the original fixed-weekly auto-snap, which was a no-op under RLS.)
-- =============================================================================

CREATE TABLE scheduled_jobs (
    ledger_id             UUID        NOT NULL,
    job_type              TEXT        NOT NULL,
    enabled               BOOLEAN     NOT NULL DEFAULT FALSE,
    hour_local            SMALLINT    NOT NULL DEFAULT 19,
    minute_local          SMALLINT    NOT NULL DEFAULT 0,
    configured_by_user_id UUID        NOT NULL,
    last_run_at           TIMESTAMPTZ NULL,
    next_run_at           TIMESTAMPTZ NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_scheduled_jobs PRIMARY KEY (ledger_id, job_type),
    CONSTRAINT fk_scheduled_jobs_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers (id) ON DELETE CASCADE,
    CONSTRAINT fk_scheduled_jobs_user
        FOREIGN KEY (configured_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT ck_scheduled_jobs_type   CHECK (job_type IN ('quote-refresh', 'snapshot')),
    CONSTRAINT ck_scheduled_jobs_hour   CHECK (hour_local   BETWEEN 0 AND 23),
    CONSTRAINT ck_scheduled_jobs_minute CHECK (minute_local BETWEEN 0 AND 59)
);

COMMENT ON TABLE scheduled_jobs IS
    'Per-ledger daily scheduler, one row per (ledger, job_type). A single '
    'background worker polls next_run_at and dispatches by job_type to a handler '
    '(quote-refresh — ADR-0054 B; snapshot — ADR-0037).';

-- Carry over the merged quote_schedules rows (mig 135) as the first job_type.
INSERT INTO scheduled_jobs
    (ledger_id, job_type, enabled, hour_local, minute_local,
     configured_by_user_id, last_run_at, next_run_at, created_at, updated_at)
SELECT ledger_id, 'quote-refresh', enabled, hour_local, minute_local,
       configured_by_user_id, last_run_at, next_run_at, created_at, updated_at
FROM quote_schedules;

DROP TABLE quote_schedules;

-- Worker hot path: "which jobs are due?"
CREATE INDEX idx_scheduled_jobs_due
    ON scheduled_jobs (next_run_at)
    WHERE enabled;

-- RLS — per-ledger visibility (flattened policy, migs 071/072/127). The worker
-- reads via the BYPASSRLS service role.
ALTER TABLE scheduled_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduled_jobs FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scheduled_jobs_per_ledger ON scheduled_jobs;
CREATE POLICY scheduled_jobs_per_ledger ON scheduled_jobs
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON scheduled_jobs TO coffer_app;
GRANT ALL ON scheduled_jobs TO coffer_service;
