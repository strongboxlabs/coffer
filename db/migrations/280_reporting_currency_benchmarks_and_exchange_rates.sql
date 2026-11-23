-- =============================================================================
-- 280 — a ledger's reporting currency, and the outside market series: benchmark
--       growth and exchange rates (ADR-0114)
-- =============================================================================
--
-- WHY
-- ---
-- Two features need a per-day series no ledger table holds: a fund's total-return
-- growth for the Returns report's benchmark (ADR-0114 D1, D2), and exchange rates so a
-- figure that combines accounts in different currencies can be read as one number
-- (ADR-0114 D7). security_prices cannot hold either: its composite foreign key ties
-- every row to one held security, and its 4dp scale is too coarse for a rate. This
-- migration adds the storage (ADR-0114 D6) and the two currency columns the conversion
-- reads (ADR-0114 D7, D12). Nothing here converts anything, and no reader changes.
--
-- WHAT CHANGES
-- ------------
--   * ledgers.currency_code (new, NOT NULL DEFAULT 'USD'): the ledger's reporting
--     currency (ADR-0114 D7). Plain TEXT with no CHECK, the shape accounts.currency_code
--     has: the API validates the code. The default is the code a new ledger takes when
--     a writer names none, the one DisplayCurrency.Fallback names.
--   * budget_targets.currency_code (new, NOT NULL, no default): the currency a target
--     was typed in (ADR-0114 D12). No default, so a writer that forgets to name it is
--     refused rather than stamped with a currency the person may not have typed in.
--   * ledger_benchmarks, benchmark_growth, benchmark_coverage, fx_rates,
--     fx_pair_coverage (new): below.
--   * The five snapshot functions capture, restore and clear the five new tables.
--
-- THE BACKFILL
-- ------------
-- An existing ledger takes the currency DisplayCurrency.Of names today (ADR-0114 D7):
-- the first code in ordinal order among net worth's accounts, else 'USD'. Net worth's
-- accounts are DisplayCurrency.NetWorthAccounts': every account of the ledger that is
-- not a category and that no account of the ledger names as its holdings account
-- (accounts.holdings_account_id), active or not. Ordinal order is COLLATE "C", byte
-- order, which for UTF-8 is code-point order; the C# rule orders by UTF-16 code unit,
-- which agrees with it everywhere outside the surrogate range, so for every code an
-- account can hold that a reader would recognise as ISO 4217. Derived from stored rows alone, so
-- the result is the same on every run and on a restore drill.
--
-- An existing target takes its ledger's new code: until now a ledger had one display
-- currency, and every target was typed against it (ADR-0114 D12).
--
-- Both backfills write only rows whose column is still NULL, so a second run of the
-- script over its own result writes nothing.
--
-- THE NEW TABLES
-- --------------
-- All five carry ledger_id with ON DELETE CASCADE, the anchor budget_targets and
-- account_folders use: fn_ledger_delete deletes the ledger row last, and the cascade
-- takes these with it, so that function needs no change. None of them references
-- another ledger table, so no composite foreign key applies: benchmark_growth and
-- benchmark_coverage have no reference to ledger_benchmarks on purpose (a removed
-- benchmark keeps its history and the record of what was asked for it, so adding it
-- back needs no fetch for a range already asked, ADR-0114 D1), and the two coverage
-- tables describe fetches, not stored rows.
--
--   ledger_benchmarks   up to three symbols per ledger, ordered. The cap is the
--                       API's (ADR-0032: no trigger). position is CHECKed to 1..3 and
--                       unique per ledger, DEFERRABLE INITIALLY DEFERRED so a reorder
--                       can swap two positions inside one transaction. Together they
--                       admit at most three rows, a backstop under the API's check,
--                       which answers first with a business error.
--   benchmark_growth    one growth factor per (ledger, symbol, UTC day): that day's
--                       adjusted close over the previous trading day's, both read from
--                       one response, NUMERIC(20,12), rounded once at write (ADR-0114
--                       D2). The primary key is the identity and what the snapshot
--                       writer chunks on.
--   fx_rates            one rate per (ledger, from, to, UTC day), NUMERIC(28,12)
--                       (ADR-0114 D4, D6). from is the account's currency and to the
--                       one it converts into, so no stored rate is another's inverse.
--   benchmark_coverage  per symbol, the range of days fetches have asked Yahoo for:
--                       [fetched_from_date, fetched_through_date]. Kept when the
--                       benchmark is removed.
--   fx_pair_coverage    per pair, the range of days fetches have asked Frankfurter for:
--                       [fetched_from_date, fetched_through_date]. A day after the
--                       through day has no rate yet, whatever fx_rates holds
--                       (ADR-0114 D9).
--
-- WHY A RANGE ASKED, NOT THE ROWS STORED. A fetch records the days it asked for,
-- whether or not the source had a value for them. Before a currency existed or a fund
-- launched the source answers with nothing, and a range judged by its stored rows
-- would look unfetched and be asked again on every refresh. With the range recorded,
-- a fetch asks only what lies outside it: forward past the through day, and back
-- before the from day when the earliest day a figure needs moves earlier (an older
-- transaction in a covered currency, an older first trade, ADR-0114 D11). Each range
-- is contiguous: a fetch widens it only by a range adjoining it that it read in full.
--
-- FIRST VALUE WINS, NEVER REWRITTEN (ADR-0114 D6, D10). The unique key of each series
-- table is the day, whatever the source: a fetch for a day that holds a hand-entered
-- rate, and a hand entry for a day that holds a fetched one, are both refused by it.
-- coffer_app is granted SELECT and INSERT on the two series tables and nothing else,
-- so a request cannot rewrite or remove a stored day either. Removing rows is the
-- snapshot restore's and the ledger delete's, both of which run as the service role.
--
-- THE ATTRIBUTION OF A HAND-ENTERED RATE (ADR-0114 D10). entered_by_user_id names who
-- typed a manual rate. The FK is ON DELETE SET NULL, so the rate outlives its author.
-- The CHECK is therefore one-way: a user may be named only on a manual row. The
-- converse, that a manual row names a user, cannot be a CHECK: the SET NULL a user
-- delete runs would violate it, and the delete would be refused with a check violation
-- instead, which is RESTRICT under another name. A manual row stays marked 'manual'
-- after its author is gone, so it is still attributable to hand entry. That a manual
-- row is written naming its author is the API writer's rule. No app path deletes a
-- user, so in practice only an operator's SQL reaches the SET NULL.
--
-- THE HOT READ. A conversion asks for the last rate on or before a day for a pair:
-- equality on (ledger_id, from_currency, to_currency) and rate_date <= d, newest first,
-- one row. uq_fx_rates_pair_day is a btree on exactly those columns, and a btree is
-- scanned in either direction, so the newest-first read is a backward scan of it that
-- stops at the first row; an index declared rate_date DESC would hold the same entries in
-- the other order and serve the same plan, so there is none. benchmark_growth's
-- chaining read (a symbol's days in a range) is a forward scan of its primary key.
--
-- ROLES. The role-aware pair on all four, as since migrations 247 and 254: read for any
-- grant, write for owner or editor. Grants are explicit (migration 254 removed the
-- default ones): ledger_benchmarks takes all four privileges (add, reorder, remove),
-- benchmark_coverage and fx_pair_coverage SELECT, INSERT and UPDATE (coverage widens),
-- the series tables SELECT and INSERT (above).
--
-- SNAPSHOTS. All five are captured (ADR-0114 D6): a rollback restores the snapshot's
-- rows and drops the days after it, which the next refresh fetches again, coverage
-- included. ledgers is not captured, so a rollback does not change the reporting
-- currency; budget_targets is, and each target comes back with the currency it was
-- typed in (ADR-0114 D12). fx_rates.entered_by_user_id is a reference out of the
-- snapshot to users, which no app path deletes, like the other user references
-- (SchemaDriftGuardTests' OutwardParentNeverDeleted). The five bodies below are 264's,
-- their last definitions, with the five tables added; nothing else in them changes.
-- The new migration number is a new schema version, so every existing snapshot is
-- retired (ADR-0037).
--
-- ledger_operations' family CHECK does not change: a rates or benchmark fetch rides
-- the quote refresh's own row, family 'quote' (ADR-0114 D11).
--
-- Rerunnable over its own result, like 264: every object is created only where it is
-- missing, a policy is dropped and created, and the rest is CREATE OR REPLACE, a grant
-- or a guarded write.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. ledgers.currency_code, backfilled from net worth's accounts.
-- ---------------------------------------------------------------------------
ALTER TABLE ledgers ADD COLUMN IF NOT EXISTS currency_code TEXT;

UPDATE ledgers l
   SET currency_code = COALESCE(
           (SELECT min(a.currency_code COLLATE "C")
              FROM accounts a
             WHERE a.ledger_id = l.id
               AND a.account_type <> 'category'
               AND NOT EXISTS (SELECT 1 FROM accounts o
                                WHERE o.ledger_id = l.id
                                  AND o.holdings_account_id = a.id)),
           'USD')
 WHERE l.currency_code IS NULL;

ALTER TABLE ledgers
    ALTER COLUMN currency_code SET DEFAULT 'USD',
    ALTER COLUMN currency_code SET NOT NULL;

-- ---------------------------------------------------------------------------
-- 2. budget_targets.currency_code, backfilled from the ledger's.
-- ---------------------------------------------------------------------------
ALTER TABLE budget_targets ADD COLUMN IF NOT EXISTS currency_code TEXT;

UPDATE budget_targets t
   SET currency_code = l.currency_code
  FROM ledgers l
 WHERE l.id = t.ledger_id
   AND t.currency_code IS NULL;

ALTER TABLE budget_targets ALTER COLUMN currency_code SET NOT NULL;

-- ---------------------------------------------------------------------------
-- 3. ledger_benchmarks.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ledger_benchmarks (
    id             UUID         NOT NULL DEFAULT gen_random_uuid(),
    ledger_id      UUID         NOT NULL,

    -- The Yahoo symbol of a fund or ETF whose chart response carries an adjusted
    -- close (ADR-0114 D2), as the add-time check resolved it.
    symbol         TEXT         NOT NULL,

    -- What the footnote names (ADR-0114 D3).
    name           TEXT         NOT NULL,

    -- The currency its closes are quoted in, in the major unit (ADR-0114 D5).
    currency_code  TEXT         NOT NULL,

    -- The order the Benchmark control lists it in.
    position       INTEGER      NOT NULL,

    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),

    CONSTRAINT pk_ledger_benchmarks PRIMARY KEY (id),
    CONSTRAINT uq_ledger_benchmarks_symbol UNIQUE (ledger_id, symbol),
    CONSTRAINT uq_ledger_benchmarks_position UNIQUE (ledger_id, position)
        DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_ledger_benchmarks_position CHECK (position BETWEEN 1 AND 3),
    CONSTRAINT ck_ledger_benchmarks_symbol_not_blank CHECK (length(btrim(symbol)) > 0),
    CONSTRAINT ck_ledger_benchmarks_name_not_blank CHECK (length(btrim(name)) > 0),
    CONSTRAINT fk_ledger_benchmarks_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- 4. benchmark_growth.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS benchmark_growth (
    ledger_id   UUID            NOT NULL,
    symbol      TEXT            NOT NULL,
    -- The UTC trading day (ADR-0070, ADR-0114 D2).
    growth_date DATE            NOT NULL,
    -- That day's adjusted close over the previous trading day's.
    growth      NUMERIC(20,12)  NOT NULL,
    source      TEXT            NOT NULL,
    created_at  TIMESTAMPTZ     NOT NULL DEFAULT now(),

    CONSTRAINT pk_benchmark_growth PRIMARY KEY (ledger_id, symbol, growth_date),
    CONSTRAINT ck_benchmark_growth_positive CHECK (growth > 0),
    -- Fetched only: no one types a growth factor (ADR-0114 D10).
    CONSTRAINT ck_benchmark_growth_source CHECK (source IN ('fetch')),
    CONSTRAINT ck_benchmark_growth_symbol_not_blank CHECK (length(btrim(symbol)) > 0),
    CONSTRAINT fk_benchmark_growth_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- 4b. benchmark_coverage.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS benchmark_coverage (
    ledger_id             UUID  NOT NULL,
    -- The benchmark_growth.symbol the range was asked for. No foreign key: kept after
    -- the benchmark is removed (header).
    symbol                TEXT  NOT NULL,
    -- The first and last day fetches have asked for, whatever the source returned.
    fetched_from_date     DATE  NOT NULL,
    fetched_through_date  DATE  NOT NULL,

    CONSTRAINT pk_benchmark_coverage PRIMARY KEY (ledger_id, symbol),
    CONSTRAINT ck_benchmark_coverage_range CHECK (fetched_from_date <= fetched_through_date),
    CONSTRAINT ck_benchmark_coverage_symbol_not_blank CHECK (length(btrim(symbol)) > 0),
    CONSTRAINT fk_benchmark_coverage_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- 5. fx_rates.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS fx_rates (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    ledger_id           UUID            NOT NULL,
    -- One unit of from_currency is worth rate units of to_currency.
    from_currency       TEXT            NOT NULL,
    to_currency         TEXT            NOT NULL,
    -- The UTC day the rate is for. A day the feed skips converts at the last rate on or
    -- before it (ADR-0114 D4).
    rate_date           DATE            NOT NULL,
    rate                NUMERIC(28,12)  NOT NULL,
    source              TEXT            NOT NULL,
    -- The source Frankfurter names for a fetched rate; NULL on a manual one.
    upstream            TEXT,
    -- Who typed a manual rate; NULL on a fetched one, and on a manual one whose author
    -- has been deleted (header).
    entered_by_user_id  UUID,
    created_at          TIMESTAMPTZ     NOT NULL DEFAULT now(),

    CONSTRAINT pk_fx_rates PRIMARY KEY (id),
    -- First value wins, whatever its source (ADR-0114 D6), and the hot read's index
    -- (header).
    CONSTRAINT uq_fx_rates_pair_day UNIQUE (ledger_id, from_currency, to_currency, rate_date),
    CONSTRAINT ck_fx_rates_distinct_currencies CHECK (from_currency <> to_currency),
    CONSTRAINT ck_fx_rates_rate_positive CHECK (rate > 0),
    CONSTRAINT ck_fx_rates_source CHECK (source IN ('frankfurter', 'manual')),
    CONSTRAINT ck_fx_rates_user_only_on_manual
        CHECK (entered_by_user_id IS NULL OR source = 'manual'),
    CONSTRAINT ck_fx_rates_upstream_only_on_fetched
        CHECK (upstream IS NULL OR source = 'frankfurter'),
    CONSTRAINT fk_fx_rates_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,
    CONSTRAINT fk_fx_rates_entered_by_user
        FOREIGN KEY (entered_by_user_id) REFERENCES users(id) ON DELETE SET NULL
);

-- ---------------------------------------------------------------------------
-- 6. fx_pair_coverage.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS fx_pair_coverage (
    ledger_id        UUID  NOT NULL,
    from_currency    TEXT  NOT NULL,
    to_currency      TEXT  NOT NULL,
    -- The first day fetches of the pair have asked for (header).
    fetched_from_date     DATE  NOT NULL,
    -- The last day fetches of the pair have asked for (ADR-0114 D9).
    fetched_through_date  DATE  NOT NULL,

    CONSTRAINT pk_fx_pair_coverage PRIMARY KEY (ledger_id, from_currency, to_currency),
    CONSTRAINT ck_fx_pair_coverage_distinct_currencies CHECK (from_currency <> to_currency),
    CONSTRAINT ck_fx_pair_coverage_range CHECK (fetched_from_date <= fetched_through_date),
    CONSTRAINT fk_fx_pair_coverage_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- 7. RLS and grants: the role-aware pair on all five.
-- ---------------------------------------------------------------------------
ALTER TABLE ledger_benchmarks ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ledger_benchmarks_read ON ledger_benchmarks;
CREATE POLICY ledger_benchmarks_read ON ledger_benchmarks FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
DROP POLICY IF EXISTS ledger_benchmarks_write ON ledger_benchmarks;
CREATE POLICY ledger_benchmarks_write ON ledger_benchmarks FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
GRANT SELECT, INSERT, UPDATE, DELETE ON ledger_benchmarks TO coffer_app;

ALTER TABLE benchmark_growth ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS benchmark_growth_read ON benchmark_growth;
CREATE POLICY benchmark_growth_read ON benchmark_growth FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
DROP POLICY IF EXISTS benchmark_growth_write ON benchmark_growth;
CREATE POLICY benchmark_growth_write ON benchmark_growth FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
-- A stored day is never rewritten or removed by a request (header).
GRANT SELECT, INSERT ON benchmark_growth TO coffer_app;

ALTER TABLE benchmark_coverage ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS benchmark_coverage_read ON benchmark_coverage;
CREATE POLICY benchmark_coverage_read ON benchmark_coverage FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
DROP POLICY IF EXISTS benchmark_coverage_write ON benchmark_coverage;
CREATE POLICY benchmark_coverage_write ON benchmark_coverage FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
GRANT SELECT, INSERT, UPDATE ON benchmark_coverage TO coffer_app;

ALTER TABLE fx_rates ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fx_rates_read ON fx_rates;
CREATE POLICY fx_rates_read ON fx_rates FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
DROP POLICY IF EXISTS fx_rates_write ON fx_rates;
CREATE POLICY fx_rates_write ON fx_rates FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
-- A stored day is never rewritten or removed by a request (header).
GRANT SELECT, INSERT ON fx_rates TO coffer_app;

ALTER TABLE fx_pair_coverage ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fx_pair_coverage_read ON fx_pair_coverage;
CREATE POLICY fx_pair_coverage_read ON fx_pair_coverage FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
DROP POLICY IF EXISTS fx_pair_coverage_write ON fx_pair_coverage;
CREATE POLICY fx_pair_coverage_write ON fx_pair_coverage FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
GRANT SELECT, INSERT, UPDATE ON fx_pair_coverage TO coffer_app;

-- ---------------------------------------------------------------------------
-- 8. Snapshots: the four tables are captured, inserted after accounts (they
--    reference only ledgers and users, so any position after the roots would do),
--    cleared with the ledger's other rows. Each body is 264's with the four added.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_part_names()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'securities', 'security_components', 'account_folders',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'account_folder_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_originals',
        'tags', 'txn_header_tags', 'txn_leg_tags', 'txn_leg_note_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms',
        'budget_targets', 'saved_reports', 'report_pins', 'ledger_operation_promotions',
        'ledger_benchmarks', 'benchmark_growth', 'benchmark_coverage', 'fx_rates', 'fx_pair_coverage'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_insert_order()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'loan_terms', 'budget_targets', 'securities',
        'security_components', 'tags',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'account_folders', 'account_folder_members',
        'provider_security_mappings', 'recurring_transactions',
        'txn_headers', 'txn_legs', 'lots', 'txn_leg_recon', 'realized_gains',
        'recurring_occurrence_exceptions',
        'txn_header_originals', 'txn_header_tags', 'txn_leg_tags', 'txn_leg_note_tags',
        'saved_reports', 'report_pins', 'ledger_operation_promotions',
        'ledger_benchmarks', 'benchmark_growth', 'benchmark_coverage', 'fx_rates', 'fx_pair_coverage'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_payload(p_ledger_id uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE
AS $function$
DECLARE
    v_result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'accounts',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM accounts t WHERE t.ledger_id = p_ledger_id),
        'securities',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM securities t WHERE t.ledger_id = p_ledger_id),
        'security_components',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_components t WHERE t.ledger_id = p_ledger_id),
        'account_folders',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_folders t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'account_folder_members',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_folder_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_recon',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_recon t WHERE t.ledger_id = p_ledger_id),
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        -- mig 188: realized_gains is derived, but re-deriving it on restore walked
        -- the whole ledger and overwrote the holdings/lots restored above. Keys on
        -- sell_leg_id (a captured txn_legs row) so it round-trips 1:1.
        'realized_gains',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM realized_gains t WHERE t.ledger_id = p_ledger_id),
        'txn_header_originals',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_originals t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_tags',                     (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_tags t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_note_tags',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_note_tags t WHERE t.ledger_id = p_ledger_id),
        'saved_reports',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM saved_reports t WHERE t.ledger_id = p_ledger_id),
        'report_pins',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM report_pins t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id),
        'budget_targets',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM budget_targets t WHERE t.ledger_id = p_ledger_id),
        'ledger_operation_promotions',      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM ledger_operation_promotions t WHERE t.ledger_id = p_ledger_id),
        'ledger_benchmarks',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM ledger_benchmarks t WHERE t.ledger_id = p_ledger_id),
        'benchmark_growth',                 (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM benchmark_growth t WHERE t.ledger_id = p_ledger_id),
        'benchmark_coverage',               (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM benchmark_coverage t WHERE t.ledger_id = p_ledger_id),
        'fx_rates',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM fx_rates t WHERE t.ledger_id = p_ledger_id),
        'fx_pair_coverage',                 (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM fx_pair_coverage t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_restore(p_ledger_id uuid, p_payload text)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_payload  jsonb := p_payload::jsonb;
    v_expected jsonb;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('fn_ledger_snapshot_restore');

    SELECT COALESCE(jsonb_object_agg(t, jsonb_array_length(COALESCE(v_payload -> t, '[]'::jsonb))), '{}'::jsonb)
      INTO v_expected
      FROM unnest(fn_ledger_snapshot_part_names()) AS t;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO budget_targets             SELECT * FROM jsonb_populate_recordset(NULL::budget_targets,             v_payload->'budget_targets');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO security_components        SELECT * FROM jsonb_populate_recordset(NULL::security_components,        v_payload->'security_components');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO account_folders            SELECT * FROM jsonb_populate_recordset(NULL::account_folders,            v_payload->'account_folders');
    INSERT INTO account_folder_members     SELECT * FROM jsonb_populate_recordset(NULL::account_folder_members,     v_payload->'account_folder_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    INSERT INTO txn_leg_recon              SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_recon,              v_payload->'txn_leg_recon');
    INSERT INTO realized_gains             SELECT * FROM jsonb_populate_recordset(NULL::realized_gains,             v_payload->'realized_gains');
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    INSERT INTO txn_header_originals       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_originals,       v_payload->'txn_header_originals');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');
    INSERT INTO txn_leg_tags               SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_tags,               v_payload->'txn_leg_tags');
    INSERT INTO txn_leg_note_tags          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_note_tags,          v_payload->'txn_leg_note_tags');
    INSERT INTO saved_reports              SELECT * FROM jsonb_populate_recordset(NULL::saved_reports,              v_payload->'saved_reports');
    INSERT INTO report_pins                SELECT * FROM jsonb_populate_recordset(NULL::report_pins,                v_payload->'report_pins');
    INSERT INTO ledger_operation_promotions SELECT * FROM jsonb_populate_recordset(NULL::ledger_operation_promotions, v_payload->'ledger_operation_promotions');
    INSERT INTO ledger_benchmarks          SELECT * FROM jsonb_populate_recordset(NULL::ledger_benchmarks,          v_payload->'ledger_benchmarks');
    INSERT INTO benchmark_growth           SELECT * FROM jsonb_populate_recordset(NULL::benchmark_growth,           v_payload->'benchmark_growth');
    INSERT INTO benchmark_coverage         SELECT * FROM jsonb_populate_recordset(NULL::benchmark_coverage,         v_payload->'benchmark_coverage');
    INSERT INTO fx_rates                   SELECT * FROM jsonb_populate_recordset(NULL::fx_rates,                   v_payload->'fx_rates');
    INSERT INTO fx_pair_coverage           SELECT * FROM jsonb_populate_recordset(NULL::fx_pair_coverage,           v_payload->'fx_pair_coverage');

    PERFORM fn_ledger_snapshot_reconcile_external_refs(p_ledger_id);
    -- Fire the deferred checks here, where a violation names this function,
    -- rather than at the caller's COMMIT.
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_clear(p_ledger_id uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
BEGIN
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
    -- budget_targets references accounts; must go before accounts.
    DELETE FROM budget_targets             WHERE ledger_id = p_ledger_id;
    -- recurring_occurrence_exceptions references recurring_transactions; go first.
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    -- recurring_transactions references accounts AND template headers (mig 183);
    -- must go before both accounts and txn_headers.
    DELETE FROM recurring_transactions     WHERE ledger_id = p_ledger_id;
    -- security_splits references securities; must go before securities.
    DELETE FROM security_splits            WHERE ledger_id = p_ledger_id;
    -- Children of txn_legs first: lots, realized gains, the recon overlay,
    -- override layers, tags.
    DELETE FROM lots                       WHERE ledger_id = p_ledger_id;
    DELETE FROM realized_gains             WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_recon              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_originals       WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags            WHERE ledger_id = p_ledger_id;
    DELETE FROM report_pins                WHERE ledger_id = p_ledger_id;
    DELETE FROM saved_reports              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_tags               WHERE ledger_id = p_ledger_id;
    -- Deleted in one statement rather than row by row through the cascades from
    -- its legs and tags.
    DELETE FROM txn_leg_note_tags          WHERE ledger_id = p_ledger_id;
    -- References txn_headers; captured since mig 247. Its parent run in
    -- ledger_operations is NOT cleared — the run log survives a rollback.
    DELETE FROM ledger_operation_promotions WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / folders / per-security data. Folders are deleted explicitly:
    -- deleting accounts cascades only the members, and a folder left behind would
    -- sit beside its restored copy.
    DELETE FROM account_folder_members     WHERE ledger_id = p_ledger_id;
    DELETE FROM account_folders            WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM security_components        WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids       WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                       WHERE ledger_id = p_ledger_id;
    -- The outside market series (mig 280). They reference only the ledger and, for a
    -- hand-entered rate, its author, so nothing above or below depends on their order.
    DELETE FROM ledger_benchmarks          WHERE ledger_id = p_ledger_id;
    DELETE FROM benchmark_growth           WHERE ledger_id = p_ledger_id;
    DELETE FROM benchmark_coverage         WHERE ledger_id = p_ledger_id;
    DELETE FROM fx_rates                  WHERE ledger_id = p_ledger_id;
    DELETE FROM fx_pair_coverage           WHERE ledger_id = p_ledger_id;
    -- Roots last.
    DELETE FROM securities                 WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                   WHERE ledger_id = p_ledger_id;
    -- The materialised balance table.
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;
END;
$function$;

COMMIT;
