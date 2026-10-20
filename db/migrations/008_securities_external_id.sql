-- Phase 2 PR 2.3: track the source-system identifier on securities so the
-- Moneydance importer can be re-run idempotently. The `external_id` is the
-- raw MD UUID for securities originating from a Moneydance export; it stays
-- NULL for securities created by other paths (manual entry, future
-- SimpleFIN-only securities, etc.).
--
-- The unique partial index ensures that two MD-imported rows with the same
-- MD UUID can't both exist, while still allowing many securities with
-- NULL external_id (e.g. manual entries).

ALTER TABLE securities
    ADD COLUMN external_id TEXT;

CREATE UNIQUE INDEX uq_securities_external_id
    ON securities(external_id)
    WHERE external_id IS NOT NULL;
