-- =============================================================================
-- 267 — a job-disabled event is about its job, so the job's next good run clears it
-- =============================================================================
--
-- WHY
-- ---
-- An event's subject is its key up to the first dot (EventSubjects), and a later
-- info event with the same subject means that subject is fine again. Every disable
-- was published as `scheduler.job-disabled`: nothing publishes an info event under
-- `scheduler.`, re-enabling a job publishes nothing, and two disabled jobs shared
-- one subject. A disable therefore stayed outstanding in the rail, the bell and the
-- events panel until retention removed it. The scheduler now publishes
-- `<job type>.disabled`, which that job's next `<job type>.succeeded` clears
-- (ADR-0102 D1).
--
-- WHAT
-- ----
-- Existing `scheduler.job-disabled` rows, in ledger_events and system_events, take
-- the per-job key. The job type comes from the row's own summary, which every such
-- event has carried since the key was introduced:
--
--     Scheduled <job type> has been switched OFF after ...
--
-- DETERMINISTIC. The new key is derived from the row alone, so an install upgraded
-- now and one restored and upgraded later end up with the same rows. A row whose
-- summary does not have that form keeps its key.
--
-- RERUNNABLE OVER ITS OWN RESULT. The runner journals the script after the COMMIT
-- below, so a stop between the two runs it again; a second run finds no legacy key
-- to rewrite.
-- =============================================================================

BEGIN;

UPDATE ledger_events
   SET event_key = substring(summary FROM '^Scheduled ([a-z0-9-]+) has been switched OFF')
                   || '.disabled'
 WHERE event_key = 'scheduler.job-disabled'
   AND summary ~ '^Scheduled [a-z0-9-]+ has been switched OFF';

UPDATE system_events
   SET event_key = substring(summary FROM '^Scheduled ([a-z0-9-]+) has been switched OFF')
                   || '.disabled'
 WHERE event_key = 'scheduler.job-disabled'
   AND summary ~ '^Scheduled [a-z0-9-]+ has been switched OFF';

COMMIT;
