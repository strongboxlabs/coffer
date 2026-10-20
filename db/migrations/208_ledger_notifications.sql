-- =============================================================================
-- 208 — ledger-scope notifications: events, targets, and inherit-or-own
-- =============================================================================
--
-- WHY. Migration 207 built the deployment-scope half of ADR-0096. Its D1 lists
-- "this ledger's projections disagree" as LEDGER scope, and the consistency check
-- had nowhere to publish, because the ledger half did not exist. Writing ledger
-- events into `system_events` would have broken the scope split the ADR spends its
-- length arguing for, so the split gets honoured instead.
--
-- SEPARATE TABLES, NOT A NULLABLE ledger_id. The same four tests D1 applies:
--
--   * authorization — a ledger event is gated by GRANT (an RLS question), a system
--     event by admin. One table means one policy answering both, and NULL is where
--     fail-open/fail-closed bugs live;
--   * lifecycle — these rows die with their ledger (ON DELETE CASCADE), while "the
--     backup failed" outlives every ledger;
--   * snapshot capture — ADR-0037 captures per-ledger tables, and BOTH of these are
--     deliberately excluded (see SchemaDriftGuardTests). ledger_events is a record
--     of what was announced, so restoring it would resurrect notices already acted
--     on and erase newer ones; ledger_notification_subscribers is configuration that
--     must survive a data rollback, and capturing it would let an old snapshot
--     re-arm a delivery target the user had deliberately removed. The drift guard
--     refused to let this be left as "arguably right", which is what it is for;
--   * audience — the ledger's holders vs whoever operates the install.
--
-- INHERIT OR OWN. A ledger either rides on the deployment's configured targets or
-- names its own. Two modes rather than three: `own` with no targets configured IS
-- silence, so a separate `none` would duplicate a state that already exists and
-- give two ways to express one intention.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- Which targets a ledger's events go to.
-- ---------------------------------------------------------------------------
ALTER TABLE ledgers
    ADD COLUMN notification_mode TEXT NOT NULL DEFAULT 'inherit',
    ADD CONSTRAINT ck_ledgers_notification_mode
        CHECK (notification_mode IN ('inherit', 'own'));

COMMENT ON COLUMN ledgers.notification_mode IS
    'inherit = deliver this ledger''s events to the deployment''s targets; '
    'own = deliver only to this ledger''s own targets (none configured = silence, '
    'which is why there is no third "none" mode). ADR-0096.';

-- ---------------------------------------------------------------------------
-- Ledger-scope events. Mirrors system_events, plus the ledger that owns them.
-- ---------------------------------------------------------------------------
CREATE TABLE ledger_events (
    id           UUID        NOT NULL DEFAULT gen_random_uuid(),
    ledger_id    UUID        NOT NULL,
    occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    severity     TEXT        NOT NULL,
    topic        TEXT        NOT NULL,
    event_key    TEXT        NOT NULL,
    summary      TEXT        NOT NULL,
    detail       JSONB       NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT pk_ledger_events PRIMARY KEY (id),
    CONSTRAINT fk_ledger_events_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,
    CONSTRAINT ck_ledger_events_severity
        CHECK (severity IN ('info', 'warning', 'critical')),
    CONSTRAINT ck_ledger_events_topic
        CHECK (topic IN ('backup', 'snapshot', 'sync', 'quotes', 'consistency', 'scheduler'))
);

COMMENT ON TABLE ledger_events IS
    'Ledger-scope notification history (ADR-0096 D1). Distinct from '
    'ledger_operations, which is a RUN log — a drift notice is not a run. Cascades '
    'with its ledger, unlike system_events.';

CREATE INDEX ix_ledger_events_ledger_occurred
    ON ledger_events (ledger_id, occurred_at DESC);

-- ---------------------------------------------------------------------------
-- A ledger's own delivery targets, for notification_mode = 'own'.
-- ---------------------------------------------------------------------------
CREATE TABLE ledger_notification_subscribers (
    id             UUID        NOT NULL DEFAULT gen_random_uuid(),
    ledger_id      UUID        NOT NULL,
    subscriber_key TEXT        NOT NULL,
    display_name   TEXT        NOT NULL,
    is_enabled     BOOLEAN     NOT NULL DEFAULT TRUE,
    min_severity   TEXT        NOT NULL DEFAULT 'warning',
    topics         TEXT[]      NULL,
    config_ciphertext BYTEA    NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_success_at      TIMESTAMPTZ NULL,
    last_failure_at      TIMESTAMPTZ NULL,
    last_error           TEXT        NULL,
    consecutive_failures INT         NOT NULL DEFAULT 0,
    CONSTRAINT pk_ledger_notification_subscribers PRIMARY KEY (id),
    CONSTRAINT fk_ledger_notification_subscribers_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,
    CONSTRAINT ck_ledger_notification_subscribers_min_severity
        CHECK (min_severity IN ('info', 'warning', 'critical'))
);

COMMENT ON TABLE ledger_notification_subscribers IS
    'Per-ledger delivery targets (ADR-0096). config_ciphertext is sealed with the '
    'master key, as in notification_subscribers — a webhook URL routinely carries '
    'its own token.';

CREATE INDEX ix_ledger_notification_subscribers_ledger
    ON ledger_notification_subscribers (ledger_id) WHERE is_enabled;

-- ---------------------------------------------------------------------------
-- RLS. This is the reason these are separate tables rather than a nullable
-- ledger_id on the system ones: the policy is a plain grant check with no NULL
-- case to get wrong.
-- ---------------------------------------------------------------------------
ALTER TABLE ledger_events                    ENABLE ROW LEVEL SECURITY;
ALTER TABLE ledger_notification_subscribers  ENABLE ROW LEVEL SECURITY;

-- Role-aware, per mig 174 / ADR-0083 D2. NOT the mig-017 `accounts_per_user` shape:
-- that was a single FOR ALL policy gated on the mere PRESENCE of a grant, which mig
-- 174 retired from every ledger-scoped table precisely because it let a `viewer`
-- write. 174 iterates a fixed table list and installs no event trigger, so a table
-- added later gets no protection unless it declares the pair itself — and
-- ALTER DEFAULT PRIVILEGES (mig 017) already hands coffer_app INSERT/UPDATE/DELETE
-- on new tables, so "no write policy" means "no refusal", not "no permission".
--
-- _read  : FOR SELECT, any grant.
-- _write : FOR ALL, owner/editor only, USING and WITH CHECK both, so a viewer can
--          neither mutate an existing row nor insert a new one.
CREATE POLICY ledger_events_read ON ledger_events FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

-- No write policy for ledger_events on purpose. NotificationPublisher writes it
-- through ServiceDbContextFactory (BYPASSRLS), and no app-role path writes it at all
-- — the panel only reads. RLS is default-deny for anything without a permissive
-- policy, so an app-role INSERT/UPDATE/DELETE here is refused outright. This is the
-- same treatment mig 174 gave the other service-written logs (provider_runs,
-- ledger_operations, mcp_tool_invocations) rather than handing them a write pair
-- nothing would use.

CREATE POLICY ledger_notification_subscribers_read
    ON ledger_notification_subscribers FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

CREATE POLICY ledger_notification_subscribers_write
    ON ledger_notification_subscribers FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    );

COMMIT;
