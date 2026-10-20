-- =============================================================================
-- 139 — global_scheduled_jobs: deployment-wide (non-ledger) daily schedules
-- =============================================================================
--
-- ADR-0060. The whole-DB backup is a deployment-global action — it has no
-- owning ledger, so it can't live in scheduled_jobs (mig 136), whose PK is
-- (ledger_id, job_type) with a NOT NULL FK to ledgers. This sibling table is
-- keyed by job_type alone; the single SchedulerService scans both (per-ledger
-- via IScheduledJobHandler, global via IGlobalScheduledJobHandler).
--
-- The 'backup' row also carries passphrase_ciphertext: the one admin-set backup
-- passphrase, sealed under the master KEK (ADR-0026), used to encrypt BOTH
-- scheduled and manual backups so there's a single restore secret. The KEK
-- lives only in COFFER_MASTER_KEK_BASE64 (env), never in the DB or a backup, so
-- this column is inert if the DB is stolen without the KEK.
--
-- No row is seeded: backups are off until an admin configures them. RLS is
-- enabled with NO policy (deny-all for the app role) and the table is reserved
-- to the service role, matching the bootstrap_tokens pattern (mig 017) — it is
-- global config with no per-ledger predicate to scope by. The admin HTTP
-- surface is gated by the RequireAdmin policy (ADR-0060 ③a) and reaches this
-- table via the service role; the scheduler (BYPASSRLS) reads it directly.
-- =============================================================================

CREATE TABLE global_scheduled_jobs (
    job_type              TEXT        NOT NULL,
    enabled               BOOLEAN     NOT NULL DEFAULT FALSE,
    hour_local            SMALLINT    NOT NULL DEFAULT 3,
    minute_local          SMALLINT    NOT NULL DEFAULT 0,
    timezone              TEXT        NULL,
    passphrase_ciphertext BYTEA       NULL,
    configured_by_user_id UUID        NULL,
    last_run_at           TIMESTAMPTZ NULL,
    next_run_at           TIMESTAMPTZ NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_global_scheduled_jobs PRIMARY KEY (job_type),
    -- SET NULL (not RESTRICT): if the configuring admin is later removed, the
    -- deployment's backup schedule + passphrase must survive — attribution is
    -- nice-to-have, the schedule is operationally critical.
    CONSTRAINT fk_global_scheduled_jobs_user
        FOREIGN KEY (configured_by_user_id) REFERENCES users (id) ON DELETE SET NULL,
    CONSTRAINT ck_global_scheduled_jobs_type   CHECK (job_type IN ('backup')),
    CONSTRAINT ck_global_scheduled_jobs_hour   CHECK (hour_local   BETWEEN 0 AND 23),
    CONSTRAINT ck_global_scheduled_jobs_minute CHECK (minute_local BETWEEN 0 AND 59)
);

COMMENT ON TABLE global_scheduled_jobs IS
    'Deployment-wide (non-ledger) daily schedules, one row per job_type. The '
    'backup row also holds the master-KEK-sealed backup passphrase (ADR-0060). '
    'RLS deny-all; reserved to the service role.';

COMMENT ON COLUMN global_scheduled_jobs.passphrase_ciphertext IS
    'Backup passphrase sealed under the master KEK (ADR-0026): '
    'AES-GCM nonce(12) || ciphertext || tag(16). NULL until an admin sets it.';

-- Worker hot path: "is the global job due?" (mirrors idx_scheduled_jobs_due).
CREATE INDEX idx_global_scheduled_jobs_due
    ON global_scheduled_jobs (next_run_at)
    WHERE enabled;

-- RLS: enabled + forced, no policy → deny-all for coffer_app. Only BYPASSRLS
-- roles (coffer_service) can read/write; the admin endpoints use the service
-- role for this global table.
ALTER TABLE global_scheduled_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE global_scheduled_jobs FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE global_scheduled_jobs FROM coffer_app;
GRANT  ALL ON TABLE global_scheduled_jobs TO   coffer_service;
