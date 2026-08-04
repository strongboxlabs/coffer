-- Per-ledger encryption key (LEK) — ADR-0026, refining ADR-0014.
--
-- Each ledger row carries a fresh 256-bit AES key wrapped under the
-- deployment-level master KEK. All high-value secrets that live
-- inside a ledger (SimpleFIN access URL — first caller, Phase 5
-- slice 1; future per-ledger webhook secrets, etc.) are sealed with
-- this LEK before they hit any other table. The ledger row IS the
-- crypto boundary; per-ledger backup / export / cross-deployment
-- transfer all fall out of this shape.
--
-- Layout of `wrapped_lek` = nonce(12) || ciphertext(32) || tag(16) =
-- 60 bytes. AES-GCM-256 with a random per-wrap nonce. `lek_kek_id`
-- tags which master KEK wrapped this LEK so master-KEK rotation
-- can target the rows that still need re-wrapping.
--
-- Columns NULLABLE in this migration: the API backfills existing
-- ledger rows lazily on first secret-access (or eagerly at startup
-- in a follow-up polish). A subsequent migration sets NOT NULL once
-- backfill is verified complete — Phase 5 slice 1 ships with
-- nullable columns because the secret-write path is the only thing
-- that needs LEKs and it touches one ledger at a time.

ALTER TABLE ledgers
    ADD COLUMN wrapped_lek     BYTEA,
    ADD COLUMN lek_kek_id      TEXT,
    ADD COLUMN lek_created_at  TIMESTAMPTZ;

COMMENT ON COLUMN ledgers.wrapped_lek IS
    'AES-GCM-sealed Ledger Encryption Key (32-byte LEK), wrapped by '
    'the master KEK identified by lek_kek_id. Layout: '
    'nonce(12) || ciphertext(32) || tag(16). NULL = pre-ADR-0026 row '
    'awaiting lazy backfill; freshly-created ledgers always populate.';

COMMENT ON COLUMN ledgers.lek_kek_id IS
    'Identifier of the master KEK that wrapped this LEK. Defaults to '
    '"v1" until master-KEK rotation introduces "v2", etc. Master-KEK '
    'rotation re-wraps each ledger''s LEK and bumps this column.';

COMMENT ON COLUMN ledgers.lek_created_at IS
    'Timestamp the LEK was generated. Drives the LEK-rotation cadence '
    '(re-seal secrets in ledgers older than X). Distinct from '
    'ledgers.created_at because LEKs may be rotated independently of '
    'the ledger row itself.';
