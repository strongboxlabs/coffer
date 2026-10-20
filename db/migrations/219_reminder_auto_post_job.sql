-- =============================================================================
-- 219 — reminder auto-post becomes a schedulable job.
-- =============================================================================
--
-- WHY: `RecurrenceBuilder.tsx` has rendered an "Auto-post / N days before" control for a
-- long time. The value validates, persists and round-trips on edit, and NOTHING HAS EVER
-- FIRED IT — src/Api/Scheduling carries handlers for quotes, snapshots and feed sync and
-- none for reminders. A user who set it was told their rent posts itself; it does not,
-- and the missing transaction looks like their own oversight.
--
-- ADR-0097 settles the shape: a FIRST-CLASS job type, not a monitor riding the scheduler
-- tick. ConsistencyMonitor may ride the tick because it only OBSERVES; this WRITES
-- financial transactions, and a job type is what buys failure counting, auto-disable
-- after five consecutive failures, a per-ledger switch and — since mig 216 — a recorded
-- disabled_reason. A writer that can stop silently is the outage class the whole
-- notification subsystem exists for.
--
-- WHAT THIS MIGRATION IS: the CHECK widen, and two comment corrections. That is all.
--   * NO seed rows. mig 136 seeded only quote-refresh, and a money writer must be opted
--     into deliberately — a migration that switched auto-post on for every ledger holding
--     a reminder with acdays set would start posting transactions on the next tick after
--     an upgrade nobody asked to be a behaviour change.
--   * NO index: scheduled_jobs is one row per (ledger, job_type) and already unique.
--   * NO grant or RLS change: scheduled_jobs' policies are per-table, not per-job-type.
--   * NO topic widen: the job publishes under `scheduler`, which ck_ledger_events_topic
--     (mig 208) already allows.
--
-- DROP + full ADD, following mig 215. The new value set is a strict superset, so the
-- validating ADD cannot fail on existing rows and needs no NOT VALID / VALIDATE dance.
-- All four values are restated because the constraint is REPLACED, not extended.
-- =============================================================================

BEGIN;

ALTER TABLE scheduled_jobs
    DROP CONSTRAINT IF EXISTS ck_scheduled_jobs_type;

ALTER TABLE scheduled_jobs
    ADD CONSTRAINT ck_scheduled_jobs_type
        CHECK (job_type IN ('quote-refresh', 'snapshot', 'feed-sync', 'reminder-auto-post'));

-- Mig 215's version of this comment says "a unit test asserts this set matches
-- NotificationMonitors.Ledger exactly". That was true when written and is not now:
-- MonitorScopeTests was deliberately weakened to one-directional when the consistency
-- monitor arrived, because that monitor is not a job and set equality forbade it.
-- Restating the accurate rule rather than copying the stale sentence forward.
COMMENT ON CONSTRAINT ck_scheduled_jobs_type ON scheduled_jobs IS
    'Mirrors Coffer.Api.Scheduling.JobTypes. A unit test asserts every JobTypes.All value '
    'appears in NotificationMonitors.Ledger — so no schedulable job lacks a dead-man''s '
    'switch — and pins the monitors that are deliberately NOT jobs to an explicit '
    'allow-list. The two are not set-equal: the consistency monitor runs on every tick and '
    'is intentionally not configurable.';

-- Mig 124 introduced this column with "(the firing worker is a later slice)". This is
-- that slice, so the parenthetical is now false and can only be corrected by replacing
-- the whole comment.
COMMENT ON COLUMN recurring_transactions.auto_commit_days_before IS
    'ADR-0047 / ADR-0097: MD acdays. NULL = manual approve; N >= 0 = post the occurrence '
    'automatically once it is within N days of its due date, fired by the '
    'reminder-auto-post scheduled job. Capped (see ReminderAutoPostLimits): the '
    'transaction is written early but DATED at its due date, so a large N moves the '
    'ledger''s balance well before the money leaves, and an N beyond the recurrence '
    'interval would open an unbounded pipeline of future-dated real transactions.';

COMMIT;
