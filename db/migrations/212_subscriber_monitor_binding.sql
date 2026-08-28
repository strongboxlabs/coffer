-- 212 — a dead-man's-switch subscriber is bound to ONE monitor
--
-- A healthchecks.io check is a single ping URL. Its request body is stored for
-- diagnostics but does NOT affect alerting, and it has no concept of severity: the
-- only things it can express are "this one thing is alive" and "this one thing is
-- down". Verified against the current pinging API docs, not assumed.
--
-- WHAT WAS WRONG. notification_subscribers had `topics` (a filter) and `min_severity`,
-- and nothing saying WHICH recurring job a URL watches. Routing then delivered any
-- Critical event to a heartbeat target and turned it into /fail — so a critical
-- CONSISTENCY event would have marked the BACKUP check down, because the check's
-- identity and the event's subject were unrelated. `topics` could not fix that: a
-- filter narrows what arrives, it does not say what the URL IS.
--
-- `monitors` is that binding. A heartbeat subscriber receives its own monitor's
-- signals and nothing else; a message subscriber is bound to nothing and keeps the
-- severity floor. Because the value names a check rather than describing an event, it
-- also makes coverage answerable: for each known monitor, is anything watching for its
-- absence? The old question — "does ANY heartbeat subscriber exist?" — reported
-- absence detection as covered when one URL bound to backups said nothing about
-- snapshots.
--
-- Nullable, and NOT constrained to a value list here. Which monitors exist is a
-- property of the BUILD (NotificationMonitors), not of the schema: a CHECK listing
-- them would have to be amended by a migration every time a job is added, and an
-- install mid-upgrade would reject a value its own code considers valid. The API
-- validates against NotificationMonitors.All on write, and requires it for a
-- heartbeat-capability provider while forbidding it for a message one — a rule the
-- database cannot express, since capability lives in the provider, not the row.
-- ---------------------------------------------------------------------------

BEGIN;

ALTER TABLE notification_subscribers
    ADD COLUMN monitors TEXT NULL;

ALTER TABLE ledger_notification_subscribers
    ADD COLUMN monitors TEXT NULL;

COMMENT ON COLUMN notification_subscribers.monitors IS
    'For a heartbeat (dead-man''s switch) subscriber: the ONE monitor this URL watches, '
    'e.g. ''backup''. NULL for a message subscriber, which is bound to nothing. One '
    'healthchecks.io URL is one check, so a second monitored job needs a second row.';

COMMENT ON COLUMN ledger_notification_subscribers.monitors IS
    'Per-ledger counterpart of notification_subscribers.monitors.';

-- Existing heartbeat rows predate the binding and would otherwise receive nothing (the
-- new routing requires a monitor match). Backfilling to 'backup' is right rather than
-- convenient: it is the only monitor that exists in this build, and every heartbeat
-- target configured before now was configured to watch the backup — the only heartbeat
-- event the system has ever published is backup.succeeded.
UPDATE notification_subscribers
   SET monitors = 'backup'
 WHERE monitors IS NULL
   AND subscriber_key = 'healthchecks';

UPDATE ledger_notification_subscribers
   SET monitors = 'backup'
 WHERE monitors IS NULL
   AND subscriber_key = 'healthchecks';

COMMIT;
