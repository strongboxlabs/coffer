-- =============================================================================
-- 207 — system_events + notification_subscribers (ADR-0096)
-- =============================================================================
--
-- WHY. Two incidents six days apart were the same defect: snapshots and backups
-- died for ~68 and ~47 hours with nothing said, and a data scrub left projections
-- wrong for months, surfacing only because somebody ran a maintenance action by
-- hand. Neither was a missing check. Coffer had no way to tell anyone anything.
--
-- TWO SCOPES, DELIBERATELY APART (ADR-0096 D1). `ledger_operations` already
-- records per-ledger runs and stays as it is. Deployment-scope events get their
-- own table here rather than a nullable `ledger_id` on a shared one, because:
--
--   * authorization differs — a ledger event is gated by grant (an RLS question),
--     a system event by admin. One policy answering both means a NULL scope, and
--     NULL is where fail-open/fail-closed bugs live;
--   * lifecycle differs — a ledger event dies with its ledger, "the backup failed"
--     must outlive it;
--   * snapshot capture differs — ADR-0037 captures per-ledger tables, and system
--     history must never ride inside a ledger snapshot.
--
-- SEVERITY AND TOPIC ARE SEPARATE COLUMNS (D3), not one enum. "Backup succeeded"
-- and "quotes updated" share a severity and want different routing; "backup
-- failed" and "provider unreachable" likewise. Collapsing them forces every
-- subscriber to re-derive the distinction.
--
-- SUBSCRIBER CONFIG IS SEALED HERE, NOT IN secrets/ (D8). Docker secrets are for
-- credentials the app needs BEFORE it can read a database; a webhook URL is a
-- user-configurable integration. Sealed with the master key, the way Drive sync
-- already stores its outbound OAuth blob.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- Deployment-scope events. No ledger_id, by design (D1) — this table is about
-- the INSTALLATION, and a row here must survive deleting every ledger.
-- ---------------------------------------------------------------------------
CREATE TABLE system_events (
    id           UUID        NOT NULL DEFAULT gen_random_uuid(),
    occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Orthogonal by design (D3). Constrained rather than free text so a typo is a
    -- write error instead of an event no subscriber ever matches.
    severity     TEXT        NOT NULL,
    topic        TEXT        NOT NULL,
    -- Short machine-readable discriminator within a topic, e.g. 'backup.succeeded'.
    event_key    TEXT        NOT NULL,
    summary      TEXT        NOT NULL,
    detail       JSONB       NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT pk_system_events PRIMARY KEY (id),
    CONSTRAINT ck_system_events_severity
        CHECK (severity IN ('info', 'warning', 'critical')),
    CONSTRAINT ck_system_events_topic
        CHECK (topic IN ('backup', 'snapshot', 'sync', 'quotes', 'consistency', 'scheduler'))
);

COMMENT ON TABLE system_events IS
    'Deployment-scope notification history (ADR-0096 D1). Deliberately has no '
    'ledger_id: authorization, lifecycle and snapshot capture all differ from '
    'ledger_operations, and a nullable scope would put one RLS policy in front of '
    'two different authorization questions.';

COMMENT ON COLUMN system_events.severity IS
    'info | warning | critical. Orthogonal to topic (D3) — subscribers filter on both.';

-- Newest-first reads dominate (a panel, an admin API page), and the severity
-- filter is what a badge or an alert subscriber asks for.
CREATE INDEX ix_system_events_occurred_at ON system_events (occurred_at DESC);
CREATE INDEX ix_system_events_severity_occurred
    ON system_events (severity, occurred_at DESC);

-- ---------------------------------------------------------------------------
-- Configured delivery targets. One row per configured provider.
-- ---------------------------------------------------------------------------
CREATE TABLE notification_subscribers (
    id             UUID        NOT NULL DEFAULT gen_random_uuid(),
    -- Matches INotificationSubscriber.SubscriberKey, e.g. 'healthchecks', 'webhook'.
    subscriber_key TEXT        NOT NULL,
    display_name   TEXT        NOT NULL,
    is_enabled     BOOLEAN     NOT NULL DEFAULT TRUE,
    -- Which events this target wants. NULL topics = every topic; the severity
    -- floor is inclusive, so 'warning' means warning + critical.
    min_severity   TEXT        NOT NULL DEFAULT 'warning',
    topics         TEXT[]      NULL,
    -- The provider's URL / token, sealed with the master key (D8). Never a plain
    -- column: a webhook URL with an embedded token is a credential.
    config_ciphertext BYTEA    NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Delivery health. A subscriber that silently stops delivering would rebuild
    -- the very failure this table exists to prevent, one layer up, so the failure
    -- state is recorded and surfaced rather than logged.
    last_success_at      TIMESTAMPTZ NULL,
    last_failure_at      TIMESTAMPTZ NULL,
    last_error           TEXT        NULL,
    consecutive_failures INT         NOT NULL DEFAULT 0,
    CONSTRAINT pk_notification_subscribers PRIMARY KEY (id),
    CONSTRAINT ck_notification_subscribers_min_severity
        CHECK (min_severity IN ('info', 'warning', 'critical'))
);

COMMENT ON TABLE notification_subscribers IS
    'Configured delivery targets (ADR-0096 D7/D8). config_ciphertext is sealed '
    'with the master key like Drive sync''s outbound OAuth blob — docker secrets '
    'are for credentials needed before the database is readable, which a webhook '
    'URL is not.';

COMMENT ON COLUMN notification_subscribers.consecutive_failures IS
    'Delivery failures are themselves visible. A subscriber that quietly stops '
    'working would recreate the silent-failure problem one layer up.';

CREATE INDEX ix_notification_subscribers_enabled
    ON notification_subscribers (subscriber_key) WHERE is_enabled;

COMMIT;
