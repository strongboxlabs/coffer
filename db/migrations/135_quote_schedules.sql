-- =============================================================================
-- 135 — quote_schedules (ADR-0054 slice B): per-ledger scheduled price refresh
-- =============================================================================
--
-- A ledger can auto-refresh its prices on a daily schedule. One row per ledger:
-- enabled + the local time-of-day to run + bookkeeping (last_run_at /
-- next_run_at) the background worker polls.
--
-- The worker runs as the schedule's configuring user (configured_by_user_id),
-- so the scheduled run uses exactly that user's `quotes` opt-in (ADR-0057) — our
-- user_preferences RLS is own-user, so a system-user pref couldn't be set from
-- the UI. The run is recorded triggered_via='scheduled' (ADR-0055).
--
-- hour_local/minute_local are interpreted in the server's local timezone (the
-- self-hosted box's clock). A per-ledger timezone is a future refinement.
-- =============================================================================

CREATE TABLE quote_schedules (
    ledger_id             UUID        PRIMARY KEY,
    enabled               BOOLEAN     NOT NULL DEFAULT FALSE,
    hour_local            SMALLINT    NOT NULL DEFAULT 19,   -- 7pm
    minute_local          SMALLINT    NOT NULL DEFAULT 0,
    configured_by_user_id UUID        NOT NULL,
    last_run_at           TIMESTAMPTZ NULL,
    next_run_at           TIMESTAMPTZ NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT fk_quote_schedules_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers (id) ON DELETE CASCADE,
    CONSTRAINT fk_quote_schedules_user
        FOREIGN KEY (configured_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT ck_quote_schedules_hour   CHECK (hour_local   BETWEEN 0 AND 23),
    CONSTRAINT ck_quote_schedules_minute CHECK (minute_local BETWEEN 0 AND 59)
);

COMMENT ON TABLE quote_schedules IS
    'ADR-0054 slice B: per-ledger daily scheduled price refresh. The background '
    'worker polls next_run_at; the run uses configured_by_user_id''s quotes '
    'opt-in and is recorded triggered_via=scheduled.';

-- Worker hot path: "which ledgers are due?" — partial index on enabled rows.
CREATE INDEX idx_quote_schedules_due
    ON quote_schedules (next_run_at)
    WHERE enabled;

-- RLS — per-ledger visibility (flattened policy, migs 071/072/127). Any user who
-- can see the ledger can view/set its schedule (it's a ledger setting, not a
-- personal pref). The worker reads via the BYPASSRLS service role.
ALTER TABLE quote_schedules ENABLE ROW LEVEL SECURITY;
ALTER TABLE quote_schedules FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS quote_schedules_per_ledger ON quote_schedules;
CREATE POLICY quote_schedules_per_ledger ON quote_schedules
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON quote_schedules TO coffer_app;
GRANT ALL ON quote_schedules TO coffer_service;
