-- =============================================================================
-- 075 — provider_security_mappings table (ADR-0031 Phase 3a)
-- =============================================================================
--
-- Persists the link between a provider's security identifier and a
-- Coffer `securities.id` so syncs from connection-bearing pull
-- providers (today SimpleFIN; future OFX, CSV) auto-resolve known
-- tickers to the user's security catalog. Once the user resolves
-- "ETFA" → security X in their ledger by saving an investment
-- transaction, every subsequent sync of that ticker writes against
-- security X without re-prompting.
--
-- Identity:
--   * UNIQUE (ledger_id, provider_key, provider_security_id) — one
--     mapping per (ledger, provider, ticker). Composite key matches
--     the lookup the orchestrator does on every classified row.
--
-- FKs:
--   * ledger_id → ledgers ON DELETE CASCADE — mapping cleans up
--     when the ledger goes away.
--   * (security_id, ledger_id) → securities (id, ledger_id) via the
--     composite UNIQUE from migration 049. ON DELETE RESTRICT —
--     don't orphan a mapping; the user must explicitly re-link
--     before deleting the security.
--   * created_by_user_id → users ON DELETE SET NULL — audit
--     attribution outlives the user account.
--
-- RLS: same flattened pattern as migrations 071/072 — ledger_id
-- IN (visible grants). The composite FK guarantees ledger_id stays
-- coherent with the referenced security's ledger.
-- =============================================================================

CREATE TABLE provider_security_mappings (
    id                   UUID PRIMARY KEY,
    ledger_id            UUID NOT NULL,
    provider_key         TEXT NOT NULL,
    provider_security_id TEXT NOT NULL,
    security_id          UUID NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by_user_id   UUID NULL,

    CONSTRAINT fk_provider_security_mappings_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,
    CONSTRAINT fk_provider_security_mappings_security
        FOREIGN KEY (security_id, ledger_id)
        REFERENCES securities(id, ledger_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_provider_security_mappings_user
        FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE SET NULL,

    CONSTRAINT uq_provider_security_mappings_per_ledger_provider_id
        UNIQUE (ledger_id, provider_key, provider_security_id),

    CONSTRAINT ck_provider_security_mappings_provider_key_nonempty
        CHECK (length(btrim(provider_key)) > 0),
    CONSTRAINT ck_provider_security_mappings_provider_security_id_nonempty
        CHECK (length(btrim(provider_security_id)) > 0)
);

-- Lookup index for the orchestrator's hot path: every classified
-- ingested row hits this composite. The UNIQUE constraint above
-- already creates a btree on the same columns in the same order,
-- so no extra index needed.

-- Reverse lookup: when a security is deleted (rare; RESTRICTed by
-- the FK above but useful for "what mappings point at this
-- security?" diagnostics).
CREATE INDEX idx_provider_security_mappings_security
    ON provider_security_mappings (security_id);


-- -----------------------------------------------------------------------------
-- RLS — same per-ledger flattened policy as migrations 071/072.
-- -----------------------------------------------------------------------------

ALTER TABLE provider_security_mappings ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider_security_mappings FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS provider_security_mappings_per_user ON provider_security_mappings;
CREATE POLICY provider_security_mappings_per_user ON provider_security_mappings
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

-- coffer_service bypasses RLS (BYPASSRLS attribute); coffer_app is
-- the per-request role gated by the policy.
GRANT SELECT, INSERT, UPDATE, DELETE ON provider_security_mappings TO coffer_app;
GRANT ALL ON provider_security_mappings TO coffer_service;
