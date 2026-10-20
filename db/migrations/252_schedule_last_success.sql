-- 252 — last_success_at on both schedule tables (FU-070, FU-071).
--
-- last_run_at is stamped when the scheduler CLAIMS a run, before the handler does anything,
-- so a job that has failed or done nothing every night for a week still shows a fresh time.
-- last_success_at moves only when a run did its work, fully or in part. A run that did
-- nothing (an auto-snapshot skipped because every slot holds a manual one; a quote refresh
-- with every provider down; a feed sync where every connection needs reconnecting) or failed
-- does not move it, which is what lets "nothing has been done for a day" be seen at all.

ALTER TABLE scheduled_jobs        ADD COLUMN last_success_at TIMESTAMPTZ;
ALTER TABLE global_scheduled_jobs ADD COLUMN last_success_at TIMESTAMPTZ;

-- Backfill from the row itself, so the result is a function of the data (no now()):
--   * a clean last run (no failures, no message) succeeded at last_run_at;
--   * a last run that left a message but no failure did its work in part — UNLESS the
--     message is one of the three a run that did nothing at all writes. Those stay NULL,
--     and the schedule then reads as having done nothing since it was set up, which for a
--     job whose last run did nothing is the truth this column exists to tell.
-- A failing row (consecutive_failures > 0) stays NULL: its last success is not recorded
-- anywhere to recover.
UPDATE scheduled_jobs
   SET last_success_at = last_run_at
 WHERE last_run_at IS NOT NULL
   AND consecutive_failures = 0
   AND (last_error IS NULL
        OR NOT (last_error LIKE 'Automatic snapshot skipped:%'
             OR last_error LIKE 'Automatic snapshot did not run:%'
             OR last_error LIKE 'No quote provider succeeded%'
             OR last_error LIKE '%nothing was synced.'));

UPDATE global_scheduled_jobs
   SET last_success_at = last_run_at
 WHERE last_run_at IS NOT NULL
   AND consecutive_failures = 0
   AND last_error IS NULL;
