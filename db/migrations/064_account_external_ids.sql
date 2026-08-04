-- =============================================================================
-- 064 — account_external_ids junction (P4)
-- =============================================================================
--
-- One real-world account (e.g. "CapitolOne Quicksilver Visa") can be
-- represented in MULTIPLE upstream sources: SimpleFIN sync emits one
-- external_id ("ACT-…"), the Moneydance import emits another (the MD
-- acct UUID), and so on. Pre-064 the schema stored a single
-- `accounts.external_id` and the importer keyed on
-- `(ledger_id, external_id)`. When two sources represented the same
-- account, the importer couldn't unify them and created parallel
-- Ledger account rows — which then accumulated as drift and
-- corrupted re-imports added duplicate legs alongside existing ones.
--
-- This migration introduces an `account_external_ids` junction so a
-- single Ledger account can carry N source-specific external_ids,
-- one per source. The importer's account-adoption path (P1, shipped
-- in the same PR's C# changes) consults this table to find an
-- existing account before creating a new one.
--
-- `accounts.external_id` is kept for back-compat during the
-- transition; it's now derived (any one of the rows in the junction).
-- A future migration will drop the column.
-- =============================================================================

CREATE TABLE account_external_ids (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id  UUID NOT NULL,
    ledger_id   UUID NOT NULL,
    source      TEXT NOT NULL,
    external_id TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT account_external_ids_account_fk
        FOREIGN KEY (account_id, ledger_id) REFERENCES accounts(id, ledger_id) ON DELETE CASCADE,
    CONSTRAINT account_external_ids_ledger_fk
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT,
    CONSTRAINT account_external_ids_source_check
        CHECK (source IN ('moneydance', 'simplefin', 'manual'))
);

-- Lookup by (ledger, source, external_id) — the importer's primary path.
CREATE UNIQUE INDEX uq_account_external_ids_source_extid
    ON account_external_ids(ledger_id, source, external_id);

-- An account has at most one external_id per source. SimpleFIN sync
-- and MD import each get one slot; manual adoption gets one slot.
CREATE UNIQUE INDEX uq_account_external_ids_account_source
    ON account_external_ids(account_id, source);

CREATE INDEX idx_account_external_ids_account
    ON account_external_ids(account_id);

COMMENT ON TABLE account_external_ids IS
    'P4 junction (migration 064): maps Ledger accounts to their '
    'source-specific external identifiers. A single Ledger account '
    'can carry one external_id per source (moneydance, simplefin, '
    'manual). The importer''s account-adoption path consults this '
    'table to find an existing account before creating a new one. '
    'Replaces direct keying on accounts.external_id (which only '
    'supported one identity per account and led to dual-source '
    'account drift in dev DBs).';


-- -----------------------------------------------------------------------------
-- Backfill: every existing accounts.external_id becomes a junction row.
-- Source is inferred from the value pattern — ACT-prefix = simplefin,
-- otherwise = moneydance. This is a one-time heuristic; the importer
-- writes source explicitly going forward.
-- -----------------------------------------------------------------------------

INSERT INTO account_external_ids (account_id, ledger_id, source, external_id)
SELECT id, ledger_id,
       CASE WHEN external_id LIKE 'ACT-%' THEN 'simplefin'
            ELSE 'moneydance'
       END,
       external_id
FROM accounts
WHERE external_id IS NOT NULL;

-- Sanity-check the backfill.
DO $$
DECLARE
    v_accounts_with_extid INTEGER;
    v_junction_rows INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_accounts_with_extid FROM accounts WHERE external_id IS NOT NULL;
    SELECT COUNT(*) INTO v_junction_rows FROM account_external_ids;
    IF v_junction_rows <> v_accounts_with_extid THEN
        RAISE EXCEPTION 'Migration 064 backfill mismatch: % accounts with external_id, % junction rows.',
            v_accounts_with_extid, v_junction_rows;
    END IF;
    RAISE NOTICE 'Migration 064: backfilled % account_external_ids rows.', v_junction_rows;
END;
$$;


-- -----------------------------------------------------------------------------
-- RLS: account_external_ids inherits visibility from its parent account.
-- -----------------------------------------------------------------------------

ALTER TABLE account_external_ids ENABLE ROW LEVEL SECURITY;

CREATE POLICY account_external_ids_per_user ON account_external_ids
    TO coffer_app
    USING (account_id IN (SELECT id FROM accounts))
    WITH CHECK (account_id IN (SELECT id FROM accounts));

GRANT SELECT, INSERT, UPDATE, DELETE ON account_external_ids TO coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON account_external_ids TO coffer_service;
