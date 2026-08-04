-- 022_txn_headers_and_legs_tables.sql
--
-- ADR-0022 part 1: create the normalised schema (txn_headers + txn_legs
-- + override tables + header_tags) alongside the existing `transactions`
-- table. This migration is ADDITIVE only — no drops, no view rewrites,
-- no trigger changes. The C# layer keeps reading and writing the
-- existing tables; the new tables sit empty until the importer rewrite
-- begins populating them.
--
-- Migration 023 (final commit on this branch) handles the cut-over:
-- rewrites resolved_transactions + register_entry_keys on the new
-- tables, moves the running-balance trigger, drops `transactions` /
-- `transaction_overrides` / `transaction_tags` along with the
-- ADR-0019 symmetric-pair trigger, and retargets the FKs on `lots` and
-- `merge_candidates`. Keeping that drop chain out of this file lets
-- 022 land + be reviewed without putting the database in a half-state
-- while the C# refactor catches up.
--
-- See docs/decisions/0022-txn-headers-and-legs.md for the design
-- rationale; this file implements Rules 1–8 schema-side.

-- ---------------------------------------------------------------------------
-- Rule 1 — txn_headers: event envelope
-- ---------------------------------------------------------------------------
-- One row per MD txn (and one per user-entered txn, one per SimpleFIN
-- event, etc.). Carries fields the user thinks of as belonging to "the
-- transaction" rather than to a per-account posting: payee, memo, date,
-- status, check number, plus the new group-level state (reconciliation
-- timestamps, online-match status, etc.). `external_id` is the source
-- system's txn id alone — no leg suffix — since uniqueness is at the
-- header level.

CREATE TABLE txn_headers (
    id                       UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    ledger_id                UUID           NOT NULL REFERENCES ledgers(id) ON DELETE RESTRICT,
    origin                   TEXT           NOT NULL CHECK (origin IN (
                                                'manual', 'simplefin',
                                                'moneydance_import', 'ofx_import', 'csv_import'
                                            )),
    external_id              TEXT,
    payee                    TEXT,
    memo                     TEXT,
    posted_at                TIMESTAMPTZ    NOT NULL,
    transacted_at            TIMESTAMPTZ,
    status                   TEXT,
    check_number             TEXT,
    is_pending               BOOLEAN        NOT NULL DEFAULT FALSE,
    is_user_defined          BOOLEAN        NOT NULL DEFAULT FALSE,
    is_hidden                BOOLEAN        NOT NULL DEFAULT FALSE,
    is_merged_into           UUID           REFERENCES txn_headers(id) ON DELETE SET NULL,
    import_source            TEXT,
    -- Group-level state surfaced by ADR-0022. Structured columns over a
    -- text status field for the immutable audit trail (who reconciled,
    -- when). In-progress states (e.g. `pending_reconciliation`) live in
    -- `status`; the timestamp is set on commit.
    online_match_status      TEXT           CHECK (online_match_status IS NULL OR online_match_status IN (
                                                'unmatched', 'auto_matched', 'user_matched'
                                            )),
    reconciled_at            TIMESTAMPTZ,
    reconciled_by_user_id    UUID           REFERENCES users(id) ON DELETE SET NULL,
    created_at               TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- Partial unique index mirrors the pattern from `transactions`: idempotent
-- imports key by (ledger, external_id) when the source supplies one.
-- Manual entries (external_id IS NULL) are exempt from uniqueness.
CREATE UNIQUE INDEX uq_txn_headers_ledger_external_id
    ON txn_headers (ledger_id, external_id)
    WHERE external_id IS NOT NULL;

-- Register reads paginate by (posted_at DESC, id DESC) on the header;
-- combined with ledger_id this covers the most common access pattern.
CREATE INDEX idx_txn_headers_ledger_posted_at
    ON txn_headers (ledger_id, posted_at DESC, id DESC);

-- Lookups of merged-into chains (e.g. "what merged into this txn?").
-- Small partial index since most rows have NULL.
CREATE INDEX idx_txn_headers_is_merged_into
    ON txn_headers (is_merged_into)
    WHERE is_merged_into IS NOT NULL;

-- Hidden + pending filters in the register query; both are usually FALSE
-- so partial indexes keep maintenance cost down.
CREATE INDEX idx_txn_headers_ledger_visible
    ON txn_headers (ledger_id, posted_at DESC, id DESC)
    WHERE NOT is_hidden AND is_merged_into IS NULL;

-- ---------------------------------------------------------------------------
-- Rule 2 — txn_legs: per-account postings
-- ---------------------------------------------------------------------------
-- Two legs per posting (one on each side); N postings per multi-split
-- header. `posting_index` pairs the two legs of one posting (same value
-- within the header, on different account rows). `amount` is the impact
-- on `account_id`; the two legs of a posting sum to zero (same-currency
-- invariant; mixed-currency support is out of scope here, same as the
-- prior model).
--
-- Investment metadata (security/quantity/unit_price/commission/
-- investment_action/balance_after) is per-leg — the holdings-side leg
-- of a buy carries shares, the cash-side leg carries dollars. Null on
-- legs that don't apply.

CREATE TABLE txn_legs (
    id                  UUID           PRIMARY KEY DEFAULT gen_random_uuid(),
    header_id           UUID           NOT NULL REFERENCES txn_headers(id) ON DELETE CASCADE,
    account_id          UUID           NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
    posting_index       INTEGER        NOT NULL CHECK (posting_index >= 0),
    leg_memo            TEXT,
    amount              NUMERIC(19, 4) NOT NULL,
    balance_after       NUMERIC(19, 4),
    investment_action   TEXT           CHECK (investment_action IS NULL OR investment_action IN (
                                            'buy', 'sell',
                                            'dividend_cash', 'dividend_reinvest',
                                            'contribution', 'withdrawal',
                                            'split',
                                            'transfer_in', 'transfer_out',
                                            'fee'
                                        )),
    security_id         UUID           REFERENCES securities(id) ON DELETE RESTRICT,
    quantity            NUMERIC(19, 8),
    unit_price          NUMERIC(19, 8),
    commission          NUMERIC(19, 4),
    created_at          TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- Uniqueness:
--   (header_id, posting_index, account_id) — a posting touches each
--   account at most once. Two postings within a header that hit the
--   same account get distinct posting_index values (e.g. a divr's
--   two cash legs on the brokerage).
CREATE UNIQUE INDEX uq_txn_legs_posting
    ON txn_legs (header_id, posting_index, account_id);

-- "All legs for this header, in order" — drives register-row assembly
-- and the AssembleEntries pass in the API.
CREATE INDEX idx_txn_legs_header_posting
    ON txn_legs (header_id, posting_index);

-- "All legs on this account" — drives the per-account register query
-- and the running-balance recompute.
CREATE INDEX idx_txn_legs_account_id
    ON txn_legs (account_id);

-- Investment-register lookups by security (dividends, buys, sells in
-- chronological order for one security). Partial because most legs
-- have NULL security_id.
CREATE INDEX idx_txn_legs_security_id
    ON txn_legs (security_id)
    WHERE security_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Rule 5 — overrides split into header and leg tables
-- ---------------------------------------------------------------------------
-- Symmetric with the underlying tables. The current
-- `transaction_overrides` lumps both kinds together (payee + memo +
-- posted_at on what becomes the header side; amount on what becomes
-- the leg side). Splitting clarifies which field overrides which
-- concept and lets the view's COALESCE chain map 1-1 to the table
-- carrying the underlying column.

CREATE TABLE txn_header_overrides (
    header_id            UUID           PRIMARY KEY REFERENCES txn_headers(id) ON DELETE CASCADE,
    payee                TEXT,
    memo                 TEXT,
    posted_at            TIMESTAMPTZ,
    transacted_at        TIMESTAMPTZ,
    status               TEXT,
    check_number         TEXT,
    is_hidden            BOOLEAN,
    updated_at           TIMESTAMPTZ    NOT NULL DEFAULT now()
);

CREATE TABLE txn_leg_overrides (
    leg_id               UUID           PRIMARY KEY REFERENCES txn_legs(id) ON DELETE CASCADE,
    leg_memo             TEXT,
    amount               NUMERIC(19, 4),
    updated_at           TIMESTAMPTZ    NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Rule 6 — tags live on the header
-- ---------------------------------------------------------------------------
-- Tags describe the event, not the per-account leg. A "vacation" tag on
-- a multi-split paycheck applies to all 14 legs by virtue of belonging
-- to the header. Junction table mirrors the prior `transaction_tags`
-- shape (composite PK on the join columns; ON DELETE CASCADE on both
-- sides).

CREATE TABLE txn_header_tags (
    header_id   UUID  NOT NULL REFERENCES txn_headers(id) ON DELETE CASCADE,
    tag_id      UUID  NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (header_id, tag_id)
);

-- ---------------------------------------------------------------------------
-- Rule 8 — RLS policies
-- ---------------------------------------------------------------------------
-- Headers carry ledger_id directly: one-hop RLS via user_ledger_grants.
-- Legs derive visibility from their header (header_id → headers.id,
-- the policy then composes with the header's policy). Override tables
-- and the tag junction inherit transitively the same way.
--
-- Pattern matches migration 017 sections 6 + 7: FOR ALL with USING ==
-- WITH CHECK on the ledger-scoped anchor (txn_headers); composed
-- predicates via FK chain on the derived tables.

ALTER TABLE txn_headers ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_headers_per_user ON txn_headers FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE txn_legs ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_legs_per_user ON txn_legs FOR ALL TO coffer_app
    USING      (header_id IN (SELECT id FROM txn_headers))
    WITH CHECK (header_id IN (SELECT id FROM txn_headers));

ALTER TABLE txn_header_overrides ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_header_overrides_per_user ON txn_header_overrides FOR ALL TO coffer_app
    USING      (header_id IN (SELECT id FROM txn_headers))
    WITH CHECK (header_id IN (SELECT id FROM txn_headers));

ALTER TABLE txn_leg_overrides ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_leg_overrides_per_user ON txn_leg_overrides FOR ALL TO coffer_app
    USING      (leg_id IN (SELECT id FROM txn_legs))
    WITH CHECK (leg_id IN (SELECT id FROM txn_legs));

ALTER TABLE txn_header_tags ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_header_tags_per_user ON txn_header_tags FOR ALL TO coffer_app
    USING      (header_id IN (SELECT id FROM txn_headers))
    WITH CHECK (header_id IN (SELECT id FROM txn_headers));

-- ---------------------------------------------------------------------------
-- Grants
-- ---------------------------------------------------------------------------
-- The default-privileges grants set up in migration 017 already cover
-- SELECT/INSERT/UPDATE/DELETE for coffer_app + coffer_service on new
-- tables in the public schema. Re-asserting here for the readability of
-- future audits — anyone scanning migration 022 sees the access surface
-- without cross-referencing 017.
GRANT SELECT, INSERT, UPDATE, DELETE ON
    txn_headers, txn_legs,
    txn_header_overrides, txn_leg_overrides,
    txn_header_tags
    TO coffer_app, coffer_service;
