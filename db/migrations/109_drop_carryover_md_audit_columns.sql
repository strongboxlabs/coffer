-- =============================================================================
-- 109 — drop carry-through MD audit columns; rewrite mig-105 CHECK in
--        post-mig-107 vocabulary (ADR-0035 §3 / §4 amendment)
-- =============================================================================
--
-- Four columns identified by the bootstrap-vs-classification audit:
--
--   * online_match_status  — MD ol.match-status verbatim; zero production
--                            readers (only mirrored through DTO + SPA
--                            test fixtures as `null`).
--   * online_match_type    — MD ol.match-type verbatim; zero readers.
--   * online_match_orig_id — MD ol.orig-txn verbatim; zero readers.
--   * is_user_defined      — predates the (origin, import_source) model
--                            (mig 002 / 011 / 022 era). Fully redundant
--                            with `origin = 'manual' AND import_source IS
--                            NULL` (equivalently `external_id IS NULL`).
--
-- The OFX dedup key — `online_match_fitid` + `online_match_fi_id` —
-- STAYS. It's the composite identity of an OFX-shape row, structurally
-- load-bearing for `uq_txn_headers_online_match` + dedup tests.
--
-- Any future reader of the three MD audit fields can pick them out of
-- `provider_raw_payload` (JSONB) — the canonical home per ADR-0035 §3.
--
-- Order in this migration:
--   1. Rewrite the mig-105 CHECK so `is_user_defined` is no longer
--      referenced before we drop it.
--   2. Update the resolved_transactions view to stop projecting the
--      four columns we're about to drop (CREATE OR REPLACE; appended-
--      column rule from CREATE OR REPLACE VIEW only applies to ADDING
--      columns — DROPPING is allowed if no client references the column,
--      which the API + SPA changes in this PR ensure).
--   3. Drop the four columns.
--   4. Update the column comments for `import_source` / `origin` to
--      lock in the §2.5 separation.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. Rewrite mig-105 CHECK in the post-mig-107 vocabulary.
--
--    Was:  is_user_defined OR external_id IS NOT NULL
--    Now:  external_id IS NOT NULL OR origin = 'manual'
--
--    Same invariant: every row either carries a provider-side id, or
--    was authored manually in Coffer's UI. Expressed using the new
--    origin column so the legacy is_user_defined flag can be dropped.
-- -----------------------------------------------------------------------------

ALTER TABLE txn_headers
    DROP CONSTRAINT ck_txn_headers_external_id_for_non_user_defined;

ALTER TABLE txn_headers
    ADD CONSTRAINT ck_txn_headers_external_id_for_non_manual
    CHECK (external_id IS NOT NULL OR origin = 'manual');

-- -----------------------------------------------------------------------------
-- 2. Re-define resolved_transactions WITHOUT the four dropped columns.
--    Same body as mig 108, minus online_match_status / online_match_type /
--    online_match_orig_id. (is_user_defined was never projected in the
--    view, so no view change for that one.)
--
--    Note: CREATE OR REPLACE VIEW can only ADD columns at the end —
--    it cannot drop or reorder. To remove the three projected
--    audit columns we DROP + CREATE. Any function/view that depends
--    on resolved_transactions would need to be in CASCADE; none do
--    today (register_entry_keys queries it but is recreated in step
--    3.5 below).
-- -----------------------------------------------------------------------------

-- Drop the function explicitly first — Postgres SQL functions don't
-- declare view dependencies, so CASCADE on the view doesn't reach
-- them. We recreate the function in step 3.5 below.
DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER);
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

-- -----------------------------------------------------------------------------
-- 3. Drop the four carry-through columns.
-- -----------------------------------------------------------------------------

ALTER TABLE txn_headers
    DROP COLUMN online_match_status,
    DROP COLUMN online_match_type,
    DROP COLUMN online_match_orig_id,
    DROP COLUMN is_user_defined;

-- -----------------------------------------------------------------------------
-- 3.5 Recreate register_entry_keys — the DROP VIEW … CASCADE above
--     dropped the function too (function depends on the view).
--     Same body as mig 108 — no logic change here, just restoration.
-- -----------------------------------------------------------------------------

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_seq        BIGINT,
    p_direction         TEXT,
    p_limit             INTEGER
)
RETURNS TABLE(
    posted_at  TIMESTAMPTZ,
    seq        BIGINT,
    entry_key  UUID
)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    SELECT posted_at, seq, entry_key FROM (
        SELECT
            MAX(rt.posted_at)  AS posted_at,
            MAX(rt.header_seq) AS seq,
            CASE
                WHEN rt.account_postings_on_header < rt.header_total_postings
                    THEN rt.id
                ELSE rt.header_id
            END AS entry_key
        FROM resolved_transactions rt
        WHERE rt.is_hidden = FALSE
          AND rt.is_merged_into IS NULL
          AND (p_account_id IS NULL OR rt.account_id = p_account_id)
          AND (
              p_account_id IS NOT NULL
              OR EXISTS (
                  SELECT 1 FROM accounts a
                  WHERE a.id = rt.account_id AND a.ledger_id = p_ledger_id
              )
          )
        GROUP BY
            CASE
                WHEN rt.account_postings_on_header < rt.header_total_postings
                    THEN rt.id
                ELSE rt.header_id
            END
        HAVING
            p_cursor_posted_at IS NULL
            OR (p_direction = 'before' AND (
                MAX(rt.posted_at) < p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.header_seq) < p_cursor_seq)
            ))
            OR (p_direction = 'after' AND (
                MAX(rt.posted_at) > p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.header_seq) > p_cursor_seq)
            ))
        ORDER BY
            CASE WHEN p_direction = 'after' THEN MAX(rt.posted_at) END ASC,
            CASE WHEN p_direction = 'after' THEN MAX(rt.header_seq) END ASC,
            MAX(rt.posted_at) DESC,
            MAX(rt.header_seq) DESC
        LIMIT p_limit
    ) sub
    ORDER BY posted_at DESC, seq DESC;
$$;

GRANT EXECUTE ON FUNCTION
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER)
    TO coffer_app;

-- -----------------------------------------------------------------------------
-- 4. Lock in the §2.5 separation via column comments.
-- -----------------------------------------------------------------------------

COMMENT ON COLUMN txn_headers.import_source IS
    'Bootstrap fact: one-time delivery path into Coffer. Set to '
    '"moneydance-import:<file>" on rows from the MD JSON bootstrap; '
    'NULL on every Coffer-native row + live SimpleFIN sync + future '
    'OFX/CSV uploads. Independent of origin — a row can be both '
    'bootstrap (this column set) and classified as online_import '
    '/ file_import / manual (origin column) simultaneously. ADR-0035 '
    'amendment §2.5 (mig 109).';

COMMENT ON COLUMN txn_headers.origin IS
    'Classification: source mechanism that originally produced the '
    'row. One of manual / online_import / file_import. Independent '
    'of import_source — describes the row''s underlying provenance, '
    'not how it reached Coffer. Derived from provider_raw_payload for '
    'MD-bootstrap rows; written directly by the importer / API on '
    'fresh writes. ADR-0035 / mig 107 / amendment 109.';
