-- 272 — system_events: a topic for upgrades.
--
-- An available release is announced as an ordinary deployment-scope notification, subject
-- `upgrade.`: `upgrade.available` at warning, `upgrade.current` at info as its all-clear
-- (ADR-0096, ADR-0102, ADR-0110). Topic is constrained rather than free text, so a new one is a
-- migration, and it is this one.
--
-- Only system_events is widened. An upgrade is a fact about the installation and never about a
-- ledger, so ledger_events keeps its list: a ledger event with this topic stays a write error.
--
-- Every existing row already satisfies the wider list, so the constraint is re-added validated.
-- Dropped with IF EXISTS and re-added, so applying it twice changes nothing.

ALTER TABLE system_events DROP CONSTRAINT IF EXISTS ck_system_events_topic;
ALTER TABLE system_events
    ADD CONSTRAINT ck_system_events_topic
        CHECK (topic IN ('backup', 'snapshot', 'sync', 'quotes', 'consistency', 'scheduler', 'upgrade'));
