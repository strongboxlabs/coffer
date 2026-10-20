-- =============================================================================
-- 080 — feed_connection_accounts.last_provider_raw_payload
-- =============================================================================
--
-- Companion to migration 078 (per-transaction raw payload). Captures
-- the verbatim account-level JSON SimpleFIN sends — including the
-- `holdings[]` block (per-position records with `symbol`, `shares`,
-- `cost_basis`, `market_value`, `purchase_price`) that isn't carried
-- on the per-transaction payload.
--
-- Real-world data from a major brokerage surfaced the gap: the orchestrator's
-- typed projection (SimpleFinAccount) keeps a handful of display
-- fields but discards the holdings array, so we can't see what
-- positions the broker reports without re-fetching SimpleFIN.
-- Storing the verbatim per-account JSON makes the holdings list
-- queryable locally for classifier iteration:
--
--   SELECT external_id,
--          last_provider_raw_payload->'holdings'
--   FROM feed_connection_accounts
--   WHERE feed_connection_id = ?;
--
-- Latest snapshot only — overwritten on each sync's directory
-- upsert (see IngestOrchestrator.UpsertConnectionAccountsAsync).
-- History table is a separate slice if audit need surfaces.
-- =============================================================================

ALTER TABLE feed_connection_accounts
    ADD COLUMN last_provider_raw_payload JSONB NULL;

COMMENT ON COLUMN feed_connection_accounts.last_provider_raw_payload IS 'ADR-0031 follow-up: verbatim per-account JSON from the provider (SimpleFIN account shape including holdings[]). Overwritten on each sync. Diagnostic / classifier-iteration use only; NOT a source of truth for derived data.';
