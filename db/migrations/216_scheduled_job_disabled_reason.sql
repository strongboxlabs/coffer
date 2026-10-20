-- =============================================================================
-- 216 — why a scheduled job is off, when it was not a person who turned it off.
-- =============================================================================
--
-- WHY: `enabled = FALSE` has three authors and records none of them. A user unticks
-- the box; the scheduler switches a job off after five consecutive failures
-- (SchedulerRunner.DisableAfterConsecutiveFailures); and KekReconciliationService
-- switches the backup job off after a cross-install restore leaves it without usable
-- key material ("Off is the honest state until an admin sets a new one"). All three
-- produce the same FALSE, so the panel cannot say which happened, and the only trace of
-- the second is a LogError line inside a container — precisely the situation mig 194
-- set out to escape.
--
-- The consequence is not academic. A job the scheduler gave up on looks exactly like one
-- the operator meant to leave off, so the state that needs attention is
-- indistinguishable from the state that does not.
--
-- WHY IT CANNOT BE DERIVED. The obvious inference — disabled AND
-- consecutive_failures >= 5 — has a false-positive class: the scheduler disables at
-- five, a user re-enables (which until this release left the counter at five), the user
-- later switches it off themselves, and the row now reads as scheduler-disabled. It also
-- evaporates: one successful run zeroes the counter, and the auto-disable becomes
-- undiscoverable. A guess is not a derivation.
--
-- TEXT, NOT A BOOLEAN. `auto_disabled BOOLEAN` would fold the KEK-reconciliation case
-- into "the scheduler did it", which is false, and would need widening the first time a
-- fourth actor appears. drive_sync (mig 142) already settled this shape for the same
-- question: a status plus human text, not a flag.
--
-- NO CHECK CONSTRAINT, which is this repo's position on vocabulary columns — set by 191
-- (admin_audit_events.action), 212 (monitors) and 214. The values live in a C# constant
-- class and are validated on write, so adding one does not need a migration.
--
-- NO BACKFILL, deliberately. Existing rows keep NULL and the column populates forward.
-- The inference above is the only thing a backfill could use, and it is unsound in the
-- direction that matters — it would assert "the scheduler disabled this" about rows
-- where a person did. Mig 194 took the same position on the columns it added.
--
-- SCOPE NOTE, so the next reader does not go looking: this column answers "why is this
-- job off RIGHT NOW". It is current state, not history — it clears when the job is
-- re-enabled. The durable record of the disable event is published to
-- ledger_events / system_events by SchedulerRunner in the same change, which is what
-- survives a re-enable.
-- =============================================================================

BEGIN;

ALTER TABLE scheduled_jobs
    ADD COLUMN IF NOT EXISTS disabled_reason TEXT NULL;

ALTER TABLE global_scheduled_jobs
    ADD COLUMN IF NOT EXISTS disabled_reason TEXT NULL;

COMMENT ON COLUMN scheduled_jobs.disabled_reason IS
    'Why this job is disabled when a person did not do it: NULL for enabled or '
    'user-disabled, otherwise a value from ScheduleDisableReasons (deliberately '
    'unconstrained here — see 191/212/214). Current state only; cleared on re-enable. '
    'The durable record of the event is a published notification.';

COMMENT ON COLUMN global_scheduled_jobs.disabled_reason IS
    'Deployment-scope twin of scheduled_jobs.disabled_reason; same vocabulary and the '
    'same clear-on-re-enable rule. Reaches the KEK-reconciliation case, which the '
    'per-ledger table never sees.';

COMMIT;
