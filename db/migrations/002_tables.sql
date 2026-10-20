-- Phase 1: core tables.
-- Order is chosen so foreign keys resolve forward: feed_connections -> securities -> accounts -> transactions -> rest.
-- Enum-style fields use TEXT + CHECK constraints (cheaper to evolve than Postgres ENUMs).

-- ---------------------------------------------------------------------------
-- feed_connections
-- ---------------------------------------------------------------------------
CREATE TABLE feed_connections (
    id                  UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    provider            TEXT         NOT NULL CHECK (provider IN ('simplefin', 'plaid', 'manual')),
    provider_item_id    TEXT,
    status              TEXT         NOT NULL DEFAULT 'active'
                                     CHECK (status IN ('active', 'needs_reauth', 'error', 'disconnected')),
    last_synced_at      TIMESTAMPTZ,
    token_expires_at    TIMESTAMPTZ,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- securities
-- ---------------------------------------------------------------------------
CREATE TABLE securities (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    ticker        TEXT,
    cusip         TEXT,
    name          TEXT         NOT NULL,
    asset_class   TEXT         CHECK (asset_class IN ('equity', 'bond', 'etf', 'mutual_fund', 'cash_equivalent', 'other')),
    exchange      TEXT,
    is_active     BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX uq_securities_cusip ON securities(cusip) WHERE cusip IS NOT NULL;

-- ---------------------------------------------------------------------------
-- accounts (categories live here too: account_type IN ('income','expense'))
-- ---------------------------------------------------------------------------
CREATE TABLE accounts (
    id                   UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_id            UUID           REFERENCES accounts(id) ON DELETE SET NULL,
    name                 TEXT           NOT NULL,
    account_type         TEXT           NOT NULL CHECK (account_type IN (
                                            'bank', 'credit_card', 'investment',
                                            'asset', 'liability',
                                            'income', 'expense'
                                        )),
    currency_code        TEXT           NOT NULL DEFAULT 'USD',
    opening_balance      NUMERIC(19, 4) NOT NULL DEFAULT 0,
    is_placeholder       BOOLEAN        NOT NULL DEFAULT FALSE,
    is_active            BOOLEAN        NOT NULL DEFAULT TRUE,
    feed_connection_id   UUID           REFERENCES feed_connections(id) ON DELETE SET NULL,
    created_at           TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- transactions (raw feed values; user edits live in transaction_overrides)
-- ---------------------------------------------------------------------------
CREATE TABLE transactions (
    id                   UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id           UUID           NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
    origin               TEXT           NOT NULL CHECK (origin IN (
                                            'manual', 'simplefin',
                                            'moneydance_import', 'ofx_import', 'csv_import'
                                        )),
    external_id          TEXT,
    is_pending           BOOLEAN        NOT NULL DEFAULT FALSE,
    is_merged_into       UUID           REFERENCES transactions(id) ON DELETE SET NULL,
    investment_action    TEXT           CHECK (investment_action IS NULL OR investment_action IN (
                                            'buy', 'sell',
                                            'dividend_cash', 'dividend_reinvest',
                                            'contribution', 'withdrawal',
                                            'split',
                                            'transfer_in', 'transfer_out',
                                            'fee'
                                        )),
    feed_payee           TEXT,
    feed_memo            TEXT,
    feed_amount          NUMERIC(19, 4) NOT NULL,
    feed_posted_at       TIMESTAMPTZ    NOT NULL,
    feed_transacted_at   TIMESTAMPTZ,
    feed_status          TEXT,
    balance_after        NUMERIC(19, 4),
    import_source        TEXT,
    created_at           TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- splits (every transaction has at least one; "other side" of double-entry)
-- ---------------------------------------------------------------------------
CREATE TABLE splits (
    id                UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    transaction_id    UUID           NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    account_id        UUID           NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
    feed_amount       NUMERIC(19, 4) NOT NULL,
    feed_memo         TEXT,
    is_user_defined   BOOLEAN        NOT NULL DEFAULT FALSE,
    created_at        TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- transaction_overrides (one row per overridden txn; NULLs mean "use feed value")
-- ---------------------------------------------------------------------------
CREATE TABLE transaction_overrides (
    id                UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    transaction_id    UUID           NOT NULL UNIQUE REFERENCES transactions(id) ON DELETE CASCADE,
    payee             TEXT,
    memo              TEXT,
    amount            NUMERIC(19, 4),
    posted_at         TIMESTAMPTZ,
    transacted_at     TIMESTAMPTZ,
    status            TEXT,
    is_hidden         BOOLEAN        NOT NULL DEFAULT FALSE,
    overridden_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    overridden_by     TEXT
);

-- ---------------------------------------------------------------------------
-- transaction_rules (auto-categorize / normalize feed payees on each sync)
-- ---------------------------------------------------------------------------
CREATE TABLE transaction_rules (
    id                  UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    match_field         TEXT         NOT NULL CHECK (match_field IN ('feed_payee', 'feed_memo', 'amount')),
    match_operator      TEXT         NOT NULL CHECK (match_operator IN ('contains', 'equals', 'starts_with', 'regex')),
    match_value         TEXT         NOT NULL,
    apply_account_id    UUID         REFERENCES accounts(id) ON DELETE SET NULL,
    apply_payee         TEXT,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    priority            INTEGER      NOT NULL DEFAULT 100,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- holdings + lots + security_prices + inv_txn_securities
-- ---------------------------------------------------------------------------
CREATE TABLE holdings (
    id            UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id    UUID           NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
    security_id   UUID           NOT NULL REFERENCES securities(id) ON DELETE RESTRICT,
    quantity      NUMERIC(19, 6) NOT NULL DEFAULT 0,
    cost_basis    NUMERIC(19, 4) NOT NULL DEFAULT 0,
    as_of         TIMESTAMPTZ    NOT NULL DEFAULT now(),
    UNIQUE (account_id, security_id)
);

CREATE TABLE lots (
    id                UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    holding_id        UUID           NOT NULL REFERENCES holdings(id) ON DELETE CASCADE,
    transaction_id    UUID           NOT NULL REFERENCES transactions(id) ON DELETE RESTRICT,
    quantity          NUMERIC(19, 6) NOT NULL,
    unit_cost         NUMERIC(19, 4) NOT NULL,
    acquired_at       TIMESTAMPTZ    NOT NULL,
    is_closed         BOOLEAN        NOT NULL DEFAULT FALSE
);

CREATE TABLE security_prices (
    id              UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    security_id     UUID           NOT NULL REFERENCES securities(id) ON DELETE CASCADE,
    price           NUMERIC(19, 4) NOT NULL,
    currency_code   TEXT           NOT NULL DEFAULT 'USD',
    price_date      TIMESTAMPTZ    NOT NULL,
    UNIQUE (security_id, price_date)
);

CREATE TABLE inv_txn_securities (
    id                UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    transaction_id    UUID           NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    security_id       UUID           NOT NULL REFERENCES securities(id) ON DELETE RESTRICT,
    quantity          NUMERIC(19, 6) NOT NULL,
    unit_price        NUMERIC(19, 4) NOT NULL,
    commission        NUMERIC(19, 4) NOT NULL DEFAULT 0
);

-- ---------------------------------------------------------------------------
-- merge pipeline tables
-- ---------------------------------------------------------------------------
CREATE TABLE merge_rules (
    id                       UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    date_window_days         INTEGER        NOT NULL DEFAULT 3,
    amount_tolerance         NUMERIC(19, 4) NOT NULL DEFAULT 0.0000,
    payee_similarity_min     REAL           NOT NULL DEFAULT 0.4,
    auto_merge_threshold     REAL           NOT NULL DEFAULT 0.95,
    auto_reject_threshold    REAL           NOT NULL DEFAULT 0.2,
    created_at               TIMESTAMPTZ    NOT NULL DEFAULT now()
);

CREATE TABLE sync_runs (
    id                     UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    feed_connection_id     UUID         REFERENCES feed_connections(id) ON DELETE SET NULL,
    status                 TEXT         NOT NULL DEFAULT 'running'
                                        CHECK (status IN ('running', 'completed', 'failed')),
    txns_fetched           INTEGER      NOT NULL DEFAULT 0,
    txns_inserted          INTEGER      NOT NULL DEFAULT 0,
    txns_merged            INTEGER      NOT NULL DEFAULT 0,
    txns_queued            INTEGER      NOT NULL DEFAULT 0,
    txns_skipped           INTEGER      NOT NULL DEFAULT 0,
    error_message          TEXT,
    started_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    completed_at           TIMESTAMPTZ
);

CREATE TABLE merge_candidates (
    id                  UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    incoming_txn_id     UUID         NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    existing_txn_id     UUID         NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    match_basis         TEXT         NOT NULL CHECK (match_basis IN (
                                        'external_id_exact',
                                        'amount_date_exact',
                                        'amount_date_fuzzy_payee',
                                        'manual_link'
                                     )),
    confidence_score    REAL         NOT NULL,
    status              TEXT         NOT NULL DEFAULT 'pending_review'
                                     CHECK (status IN ('pending_review', 'auto_merged', 'confirmed', 'rejected', 'manually_linked')),
    resolved_by         TEXT,
    resolved_at         TIMESTAMPTZ,
    sync_run_id         UUID         REFERENCES sync_runs(id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE TABLE pending_transactions (
    id                       UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id               UUID           NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    external_pending_id      TEXT,
    feed_payee               TEXT,
    amount                   NUMERIC(19, 4) NOT NULL,
    transacted_at            TIMESTAMPTZ    NOT NULL,
    last_seen_at             TIMESTAMPTZ    NOT NULL DEFAULT now(),
    UNIQUE (account_id, external_pending_id)
);

-- ---------------------------------------------------------------------------
-- recurring_transactions  (Moneydance "reminders")
-- ---------------------------------------------------------------------------
CREATE TABLE recurring_transactions (
    id                       UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    source_account_id        UUID           NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
    target_account_id        UUID           REFERENCES accounts(id) ON DELETE SET NULL,
    description              TEXT           NOT NULL,
    memo                     TEXT,
    amount                   NUMERIC(19, 4) NOT NULL,
    frequency                TEXT           NOT NULL CHECK (frequency IN (
                                                'daily', 'weekly', 'monthly', 'yearly', 'custom'
                                            )),
    monthly_day              INTEGER        CHECK (monthly_day BETWEEN 1 AND 31),
    weekly_dow               INTEGER        CHECK (weekly_dow BETWEEN 0 AND 6),
    interval_units           INTEGER        NOT NULL DEFAULT 1,
    start_date               DATE           NOT NULL,
    end_date                 DATE,
    next_due_date            DATE,
    last_acknowledged_date   DATE,
    is_loan_reminder         BOOLEAN        NOT NULL DEFAULT FALSE,
    is_active                BOOLEAN        NOT NULL DEFAULT TRUE,
    origin                   TEXT           NOT NULL DEFAULT 'manual'
                                            CHECK (origin IN ('manual', 'moneydance_import')),
    created_at               TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- tags
-- ---------------------------------------------------------------------------
CREATE TABLE tags (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT         NOT NULL UNIQUE,
    color       TEXT,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE TABLE transaction_tags (
    transaction_id   UUID  NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    tag_id           UUID  NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (transaction_id, tag_id)
);
