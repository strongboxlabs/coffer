-- =============================================================================
-- 149 — accounts.tax_status (ADR-0066)
-- =============================================================================
--
-- account_type ('investment', 'bank', ...) can't distinguish a taxable brokerage
-- from a 401k/IRA — both are 'investment'. tax_status is the orthogonal axis: how
-- the account is taxed, independent of its type. Drives realized-gains / tax
-- reporting (a 401k's realized gains aren't 1099-B-reportable) and composes with
-- the security-level tax_character (ADR-0067).
--
-- Nullable (NULL = unknown); the account editor sets it and the MD importer seeds
-- a best-guess. accounts already carries RLS + grants; ADD COLUMN inherits them.
-- =============================================================================

ALTER TABLE accounts
    ADD COLUMN tax_status TEXT
    CHECK (tax_status IN ('taxable', 'tax_deferred', 'tax_free', 'other'));

COMMENT ON COLUMN accounts.tax_status IS
    'ADR-0066: how the account is taxed (taxable / tax_deferred / tax_free / '
    'other), orthogonal to account_type. NULL = unknown. Distinct from the '
    'security-level tax_character (ADR-0067).';
