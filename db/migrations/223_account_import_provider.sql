-- =============================================================================
-- 223 — which brokerage's CSV an investment account is imported from.
-- =============================================================================
--
-- ADR-0031 Phase 6. A brokerage CSV needs no mapping document: the format knowledge
-- lives in a per-brokerage shim that converts to QIF. What the user has to say instead
-- is WHICH brokerage the file came from, and saying it once should be enough.
--
-- WHY ON accounts, AND NOT ANYWHERE ELSE. The brokerage is a fact about the ACCOUNT —
-- this account is at Fidelity — in the same way its type and currency are. Not about
-- the ledger, which routinely holds accounts at two brokerages; and not about the
-- browser, which is where the alternative (localStorage) would have put it, making the
-- answer wrong on a second device and lost on a new one.
--
-- It deliberately does NOT go near feed_csv_mappings. That table describes a FILE
-- SHAPE and its own comment refuses a destination binding; this is the opposite kind
-- of fact, a destination's preference about sources.
--
-- NOT A FOREIGN KEY, and no CHECK against a list of brokerages. The value is a
-- provider key owned by application code (IFileProvider.ProviderKey) — 'csv-fidelity'
-- today. A CHECK would need a migration every time a brokerage is added, which is
-- exactly the churn migration 222's header argued against for the same slice. A stale
-- key is also harmless by construction: the picker only offers providers that exist,
-- and an unknown stored key falls back to "choose one".
--
-- NULLABLE because it is a memory, not a requirement. Every existing account has no
-- answer yet, and an account whose owner never imports a brokerage CSV never needs one.
-- =============================================================================

ALTER TABLE accounts
    ADD COLUMN import_provider_key TEXT;

COMMENT ON COLUMN accounts.import_provider_key IS
    'The file-import provider this account was last imported with (IFileProvider.ProviderKey, '
    'e.g. ''csv-fidelity''). Remembered so the brokerage picker can preselect it. NULL until '
    'the first such import. Not constrained to a list: the set of providers is owned by '
    'application code, and an unrecognised value degrades to an unselected picker.';

-- Length only. The value is an internal key, so anything long enough to be a key is
-- either a bug or an attempt to use the column as storage; 64 is generous for both
-- 'csv-fidelity' and whatever replaces it.
ALTER TABLE accounts
    ADD CONSTRAINT ck_accounts_import_provider_key_length
        CHECK (import_provider_key IS NULL
               OR (length(import_provider_key) BETWEEN 1 AND 64));

-- No index. The column is read one account at a time, on the account already fetched
-- for the register; nothing filters or groups by it.
