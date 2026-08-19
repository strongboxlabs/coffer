-- =============================================================================
-- 194 — scheduler failure tracking: stop a failed job from retrying forever
-- =============================================================================
--
-- SchedulerRunner advances next_run_at in memory after the handler returns, then
-- persists every due job's advance with a single SaveChangesAsync AFTER the loop
-- — over the same AppDbContext the handler just used. When a job kills its own
-- connection, that save fails too, the advance is lost, and the job is still due
-- on the next tick.
--
-- The failure modes that most deserve *not* to be retried are exactly the ones
-- that prevent recording "I ran". Observed on prod 2026-08-13: the daily snapshot
-- OOM-killed its Postgres backend (see mig 193), the postmaster entered crash
-- recovery, SaveChangesAsync failed against the recovering database, and a daily
-- job re-ran every 15 minutes for ~2 days — taking the nightly whole-DB backup
-- down with it as collateral. Nothing counted the failures, nothing backed off,
-- nothing gave up, and nothing was visible outside the container log.
--
-- The C# side of the fix commits the advance BEFORE dispatching, per job, so the
-- bookkeeping cannot be destroyed by the work. These columns add the second half:
-- a failure count that drives exponential backoff and an eventual auto-disable,
-- plus enough context to see what happened without reading container logs.
--
--   consecutive_failures  reset to 0 on success; drives backoff and auto-disable
--   last_error            truncated handler exception message, newest only
--   last_failure_at       when consecutive_failures last incremented
--
-- last_error is a message, never a stack trace or payload — the operations doc
-- forbids logging raw tokens, uploads or full memos, and this column is surfaced
-- in the SPA.
-- =============================================================================

ALTER TABLE scheduled_jobs
    ADD COLUMN consecutive_failures INTEGER     NOT NULL DEFAULT 0,
    ADD COLUMN last_error           TEXT        NULL,
    ADD COLUMN last_failure_at      TIMESTAMPTZ NULL;

ALTER TABLE global_scheduled_jobs
    ADD COLUMN consecutive_failures INTEGER     NOT NULL DEFAULT 0,
    ADD COLUMN last_error           TEXT        NULL,
    ADD COLUMN last_failure_at      TIMESTAMPTZ NULL;

COMMENT ON COLUMN scheduled_jobs.consecutive_failures IS
    'mig 194: consecutive handler failures; 0 on success. Drives backoff and the '
    'auto-disable threshold in SchedulerRunner.';
COMMENT ON COLUMN global_scheduled_jobs.consecutive_failures IS
    'mig 194: consecutive handler failures; 0 on success. Drives backoff and the '
    'auto-disable threshold in SchedulerRunner.';
