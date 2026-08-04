-- =============================================================================
-- 078 — txn_headers.provider_raw_payload (ADR-0031 follow-up)
-- =============================================================================
--
-- Diagnostic / audit storage for the original JSON SimpleFIN (and
-- future OFX / CSV providers) sent for each ingested transaction.
-- Real-world data surfaced a gap: classifier coverage is institution-
-- specific (a major brokerage / 529 / a bank use formats nothing like
-- another brokerage's "YOU BOUGHT (TICKER)" pattern), and we have no way to
-- iterate on classifier patterns without a permanent record of what
-- the providers actually send.
--
-- The orchestrator's pull path now stores this column on every new
-- row insert AND backfills it on the "already known" dedup branch
-- (so re-sync of an existing FITID populates payloads for rows
-- imported before this migration shipped).
--
-- JSONB chosen over TEXT so the user can run ad-hoc queries against
-- specific fields (`payload->>'description' ILIKE '%REINVEST%'`)
-- when iterating on classifier patterns.
--
-- NULL on:
--   * Manual entries (origin = 'manual')
--   * MD-imported rows
--   * Existing feed-imported rows until their next sync backfill
-- =============================================================================

ALTER TABLE txn_headers
    ADD COLUMN provider_raw_payload JSONB NULL;

COMMENT ON COLUMN txn_headers.provider_raw_payload IS 'ADR-0031 follow-up: original provider JSON for this transaction (SimpleFinTransaction shape today; OFX / CSV shapes later). Captured verbatim from the wire; diagnostic / audit use only. NULL on manual + MD-imported rows + feed rows synced before this column existed (re-sync backfills via the orchestrator alreadyKnown branch).';
