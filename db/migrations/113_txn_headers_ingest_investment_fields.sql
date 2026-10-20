-- =============================================================================
-- 113 — txn_headers: prefill columns for investment OFX rows
-- =============================================================================
--
-- ADR-0031 Phase 4 slice 2 shipped the OFX investment classifier
-- (`OfxBuyStock` → `buy`, `OfxIncome` → `dividend_cash`, etc.) and
-- piped the action + ticker hint through IngestActionHint /
-- IngestSecurityId. The orchestrator's file-import path still
-- inserts BANK-shape rows (cash leg + Uncategorized) so the
-- per-row investment fields (Units / UnitPrice / Commission + Fees +
-- Load + Markup + Markdown) the OFX wire CARRIES are discarded.
-- The editor's hintToDraft then has nothing to prefill into the
-- shares / price / fee draft slots — user retypes data the system
-- literally just parsed.
--
-- Three nullable numeric columns close that gap. The values get
-- persisted at orchestrator-insert time (OFX investment branch
-- populates them; everything else leaves null). The editor's
-- bank→investment upgrade flow reads them on row-open and
-- pre-fills the draft fields.
--
-- Numeric precision matches the existing per-leg shapes:
--   * Shares — share-decimals (NUMERIC(28,8) per holdings.quantity)
--   * UnitPrice — per-share dollars (NUMERIC(19,6) per
--     security_prices.price)
--   * Fee — aggregated dollar amount (NUMERIC(19,4) matching
--     txn_legs.amount). Single field per ADR-0029's editor model
--     (no load-vs-commission-vs-markup breakdown).

ALTER TABLE txn_headers
    ADD COLUMN ingest_shares     numeric(28, 8),
    ADD COLUMN ingest_unit_price numeric(19, 6),
    ADD COLUMN ingest_fee        numeric(19, 4);

COMMENT ON COLUMN txn_headers.ingest_shares IS
    'Provider-extracted share count from the file-import wire (OFX investment '
    'UNITS field). Populated only on investment rows where the provider '
    'carries the data; NULL for bank/credit rows and for SimpleFIN brokerage '
    'rows (SimpleFIN does not carry shares natively). Read by the editor''s '
    'bank→investment upgrade (hintToDraft).';

COMMENT ON COLUMN txn_headers.ingest_unit_price IS
    'Provider-extracted per-share price (OFX investment UNITPRICE). Same '
    'population rules as ingest_shares.';

COMMENT ON COLUMN txn_headers.ingest_fee IS
    'Provider-extracted aggregated fee — sum of Commission + Fees + Load + '
    'Markup + Markdown depending on which OFX investment subtype carried '
    'them. NULL when the wire had no fee-shaped fields OR when they all '
    'summed to zero. Pre-fills the editor''s Fee field (ADR-0029 editor '
    'uses a single aggregated fee, not a per-kind breakdown).';

-- -----------------------------------------------------------------------------
-- resolved_transactions: project the three new columns so the SPA's
-- hintToDraft (Phase 3d.1) can read them on row open. Same body as
-- mig 109 plus three additional projections from h.
-- -----------------------------------------------------------------------------

DROP VIEW IF EXISTS resolved_transactions;

CREATE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    h.status,
    COALESCE(o.is_hidden, h.is_hidden, false) AS is_hidden,
    o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL AS has_overrides,
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN (EXISTS ( SELECT 1
               FROM txn_legs g
              WHERE g.header_id = h.id AND g.posting_index > 0)) THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    COALESCE(ARRAY( SELECT tg.name
           FROM txn_header_tags tt
             JOIN tags tg ON tg.id = tt.tag_id
          WHERE tt.header_id = h.id
          ORDER BY tg.name), ARRAY[]::text[]) AS tags,
    h.id AS header_id,
    h.cleared_at,
    h.cleared_by_user_id,
    COALESCE(lo.leg_memo, l.leg_memo) AS leg_memo,
    COALESCE(o.memo, h.memo) AS header_memo,
    h.online_match_fitid,
    h.online_match_fi_id,
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    h.ingest_security_id,
    -- Mig 113: investment-row prefill carriers projected to the
    -- read surface so the editor's bank→investment upgrade flow
    -- (hintToDraft) can populate the shares / price / fee draft
    -- slots without an extra round-trip.
    h.ingest_shares,
    h.ingest_unit_price,
    h.ingest_fee,
    h.provider_raw_payload,
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    h.provider_key,
    h.is_merge_winner,
    h.import_source,
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id
        AND g.account_id = l.account_id)::int AS account_postings_on_header,
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id)::int AS header_total_postings,
    COALESCE(
        h.action,
        CASE
            WHEN this_account.account_type <> 'category'
                AND ca.account_type IS NOT NULL
                AND ca.account_type <> 'category'
                THEN 'Xfr'
            ELSE NULL
        END
    ) AS derived_action
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab
            ON thab.header_id = h.id AND thab.account_id = l.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;
