-- =============================================================================
-- 215 — bank feed sync becomes a scheduled per-ledger job.
-- =============================================================================
--
-- WHY: sync was manual only (POST .../feed-connections/{id}/sync). A feed nobody
-- remembers to press is a feed that goes stale, and stale balances are the failure this
-- app exists to avoid — silently, because the UI shows the last figures it has with no
-- indication of how old they are.
--
-- The schedule rides on the existing per-ledger scheduler (mig 136): one row per
-- (ledger_id, job_type), a daily local time, and the same claim-before-work,
-- five-strikes-and-disable machinery quote-refresh and snapshot already use. Adding the
-- value to this CHECK is the whole schema change — SchedulesEndpoints is generic over
-- job_type and validates against JobTypes.All, so the API and the failure tracking come
-- for free.
--
-- DAILY, and that is a real limitation rather than an oversight. DailyScheduleTiming
-- always advances by a day and scheduled_jobs stores only hour_local/minute_local, so
-- "sync every six hours" is not expressible. Faking it with an interval column would
-- change timing for quote-refresh, snapshot AND the global backup, which is its own
-- decision — so the UI says "daily" rather than implying more.
--
-- NOT a second table, and not per-connection. scheduled_jobs' PK is
-- (ledger_id, job_type), so per-connection scheduling would need a schema change and a
-- row per bank. One daily pass over the ledger's connections is what a person actually
-- wants, and the handler already has to tolerate one bank being down (mig-less fix in
-- IngestOrchestrator: a provider fault is a typed result, not an exception).
-- =============================================================================

BEGIN;

ALTER TABLE scheduled_jobs
    DROP CONSTRAINT IF EXISTS ck_scheduled_jobs_type;

ALTER TABLE scheduled_jobs
    ADD CONSTRAINT ck_scheduled_jobs_type
        CHECK (job_type IN ('quote-refresh', 'snapshot', 'feed-sync'));

COMMENT ON CONSTRAINT ck_scheduled_jobs_type ON scheduled_jobs IS
    'Mirrors Coffer.Api.Scheduling.JobTypes. A unit test asserts this set matches '
    'NotificationMonitors.Ledger exactly, so every schedulable job has a dead-man''s '
    'switch available and no switch offers a job that cannot run.';

COMMIT;
