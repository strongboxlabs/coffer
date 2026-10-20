-- =============================================================================
-- 150 — rich security classification (ADR-0067)
-- =============================================================================
--
-- securities.asset_class was overloaded: it held the VEHICLE ('mutual_fund',
-- 'etf', 'cash_equivalent') on many rows, mixed with real classes ('equity',
-- 'bond'). This splits the concerns into orthogonal, single-vocabulary columns
-- and adds a look-through table for multi-asset wrappers.
--
-- Style is deliberately NOT one column (that would re-commit the overloading sin
-- — equity style-box vs fixed-income duration/credit are different vocabularies).
-- It's four nullable, single-vocabulary columns, only the relevant pair populated.
-- =============================================================================

-- ---- 1) New orthogonal classification columns -----------------------------
ALTER TABLE securities
    ADD COLUMN vehicle_type TEXT CHECK (vehicle_type IN (
        'mutual_fund', 'etf', 'stock', 'money_market', 'cit',
        'separate_account', 'plan_529', 'option', 'cd', 'bond', 'other')),
    ADD COLUMN region TEXT CHECK (region IN (
        'us', 'developed_ex_us', 'emerging', 'global', 'na')),
    -- Equity style box (size x style) — populated only for equity.
    ADD COLUMN equity_size  TEXT CHECK (equity_size  IN ('large', 'mid', 'small')),
    ADD COLUMN equity_style TEXT CHECK (equity_style IN ('value', 'blend', 'growth')),
    -- Fixed-income character (duration x credit) — populated only for fixed_income.
    ADD COLUMN fi_duration TEXT CHECK (fi_duration IN ('short', 'intermediate', 'long')),
    ADD COLUMN fi_credit   TEXT CHECK (fi_credit   IN ('government', 'investment_grade', 'high_yield')),
    -- The security's own tax nature (muni exemption, tax-managed funds) — distinct
    -- from the account's tax_status (ADR-0066).
    ADD COLUMN tax_character TEXT CHECK (tax_character IN ('taxable', 'tax_managed', 'tax_exempt')),
    ADD COLUMN classification_source TEXT CHECK (classification_source IN ('import', 'manual', 'provider')),
    ADD COLUMN classification_confidence TEXT CHECK (classification_confidence IN ('known', 'assumed')),
    -- TRUE = a multi-asset wrapper whose allocation should decompose via
    -- security_components rather than counting 100% as 'multi_asset'.
    ADD COLUMN needs_look_through BOOLEAN NOT NULL DEFAULT FALSE;

-- ---- 2) Remediate asset_class: move vehicle values out, infer the class ----
-- Drop the old (overloaded) CHECK before writing the new economic-class values.
ALTER TABLE securities DROP CONSTRAINT securities_asset_class_check;

-- Carry the vehicle out where the old value WAS a vehicle.
UPDATE securities SET vehicle_type = 'mutual_fund' WHERE asset_class = 'mutual_fund';
UPDATE securities SET vehicle_type = 'etf'         WHERE asset_class = 'etf';

-- Best-effort economic class from the old value (vehicle values can't tell us the
-- class, so they become NULL = unknown, to be set in the editor / from the
-- per-ledger classified catalog). Mark touched rows as imported + assumed.
UPDATE securities
SET asset_class = CASE asset_class
        WHEN 'equity'          THEN 'equity'
        WHEN 'bond'            THEN 'fixed_income'
        WHEN 'cash_equivalent' THEN 'cash'
        WHEN 'other'           THEN 'alternative'
        WHEN 'etf'             THEN NULL   -- vehicle, not a class
        WHEN 'mutual_fund'     THEN NULL   -- vehicle, not a class
        ELSE asset_class
    END,
    classification_source = 'import',
    classification_confidence = 'assumed'
WHERE asset_class IS NOT NULL;

-- New CHECK: economic classes only.
ALTER TABLE securities
    ADD CONSTRAINT securities_asset_class_check
    CHECK (asset_class IN ('equity', 'fixed_income', 'multi_asset', 'cash', 'real_assets', 'alternative'));

COMMENT ON COLUMN securities.asset_class IS
    'ADR-0067: economic asset class only (equity/fixed_income/multi_asset/cash/'
    'real_assets/alternative). Vehicle moved to vehicle_type.';

-- ---- 3) Look-through components (multi-asset decomposition) ----------------
-- One row per (security, sleeve): the % of the wrapper in a given asset class +
-- region. Allocation decomposes needs_look_through securities through these.
CREATE TABLE security_components (
    id                   UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    security_id          UUID        NOT NULL REFERENCES securities(id) ON DELETE CASCADE,
    component_asset_class TEXT       NOT NULL CHECK (component_asset_class IN (
        'equity', 'fixed_income', 'cash', 'real_assets', 'alternative')),
    component_region     TEXT        CHECK (component_region IN (
        'us', 'developed_ex_us', 'emerging', 'global', 'na')),
    weight               NUMERIC(7, 4) NOT NULL CHECK (weight >= 0),
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT uq_security_components UNIQUE (security_id, component_asset_class, component_region)
);

CREATE INDEX idx_security_components_security ON security_components (security_id);

COMMENT ON TABLE security_components IS
    'ADR-0067: multi-asset look-through. Weight is a percent (0-100) of the '
    'wrapper in each asset_class x region sleeve; allocation decomposes '
    'needs_look_through securities through these.';

ALTER TABLE security_components ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_components FORCE  ROW LEVEL SECURITY;

-- Per-user via the securities sub-select (transitively ledger-scoped) — the
-- security_splits / realized_gains pattern.
CREATE POLICY security_components_per_user ON security_components
    FOR ALL
    TO coffer_app
    USING (security_id IN (SELECT id FROM securities))
    WITH CHECK (security_id IN (SELECT id FROM securities));

GRANT SELECT, INSERT, UPDATE, DELETE ON security_components TO coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON security_components TO coffer_service;
