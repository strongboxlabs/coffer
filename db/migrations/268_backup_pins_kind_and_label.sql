-- =============================================================================
-- 268 — backup_pins: a kind (admin or upgrade) and a label (ADR-0110)
-- =============================================================================
--
-- An upgrade started from the app takes a fresh backup first and holds it with a pin of
-- kind 'upgrade', labelled with the versions ("before upgrade 0.128.1 to 0.129.0"). Only the
-- newest two upgrade pins are kept, trimmed when an upgrade succeeds; admin pins are never
-- touched by that. Existing rows are admin pins. An admin pin on an upgrade-kind backup
-- converts the row to kind 'admin'.
-- =============================================================================

ALTER TABLE backup_pins
    ADD COLUMN kind  TEXT NOT NULL DEFAULT 'admin',
    ADD COLUMN label TEXT;

ALTER TABLE backup_pins
    ADD CONSTRAINT ck_backup_pins_kind  CHECK (kind IN ('admin', 'upgrade')),
    ADD CONSTRAINT ck_backup_pins_label CHECK (label IS NULL OR length(label) <= 200);

COMMENT ON COLUMN backup_pins.kind IS
    'admin: an admin never-delete pin. upgrade: held for an upgrade started from the app; the newest two are kept (ADR-0110).';
COMMENT ON COLUMN backup_pins.label IS
    'Shown in the backup list for an upgrade pin, e.g. "before upgrade 0.128.1 to 0.129.0".';
