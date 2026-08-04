-- =============================================================================
-- 130 — security_prices.source origin tag (ADR-0054 D2)
-- Source-aware upsert: an automated quote fetch ('fetch') never overwrites a
-- hand-entered ('manual') or importer-seeded ('import') price for a given
-- (security, price_date). Existing rows predate the market-data updater, so
-- they are backfilled to 'import' (the dominant origin; a fresh install has
-- none). Going forward every writer sets source explicitly — orchestrator =
-- 'fetch', importer = 'import', manual price endpoint = 'manual' — so the
-- DEFAULT is dropped: a missed writer fails loudly on NOT NULL rather than
-- silently mis-tagging a row.
-- =============================================================================

BEGIN;

-- Backfill existing rows via the column DEFAULT, then drop it.
ALTER TABLE security_prices
    ADD COLUMN source TEXT NOT NULL DEFAULT 'import';

ALTER TABLE security_prices
    ALTER COLUMN source DROP DEFAULT;

ALTER TABLE security_prices
    ADD CONSTRAINT ck_security_prices_source
        CHECK (source IN ('import', 'fetch', 'manual'));

COMMIT;
