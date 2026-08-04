-- =============================================================================
-- 120 — denormalize posting counts onto txn_legs (ADR-0046; refines ADR-0036)
-- =============================================================================
--
-- WHY
--
-- `resolved_transactions` computed `account_postings_on_header` and
-- `header_total_postings` (ADR-0036's originating-vs-target discriminator)
-- via TWO correlated `COUNT(DISTINCT posting_index)` subqueries that ran
-- once PER ROW. On the windowed register that's ~10ms/page, but on a
-- full-account or account-group aggregation (reports) it is the dominant
-- per-row cost (~110ms over a 15.7K-leg account, measured) and inflates
-- the view's planner cost enough to trigger pathological JIT compilation.
--
-- Per "optimize each layer independently" (the data layer must be fast by
-- design, not because the UI happens to window): denormalize both counts
-- onto txn_legs so the view reads two columns instead of running the
-- correlated subqueries. Fast for BOTH the windowed page and full scans.
--
-- MAINTENANCE — explicit recompute, NOT a trigger.
--
-- Per ADR-0032 / ADR-0034 the project deliberately removed the
-- data-maintenance trigger family in favour of explicit call-site
-- recompute funnelled through BalanceRecomputeService +
-- BalanceRecomputeInterceptor. Posting counts derive from the same
-- txn_legs structural changes the interceptor already snapshots (it
-- computes the distinct affected header ids), so this slice folds
-- posting-count recompute into that same service/interceptor — one
-- snapshot, one path. No new trigger.
--
-- This migration ships the SQL half:
--   1. The two count columns (NOT NULL DEFAULT 1 — a single-posting txn;
--      inserts that omit them get the default, the interceptor recomputes
--      the correct value at the terminal SaveChanges boundary, exactly
--      like balances).
--   2. fn_recompute_posting_counts_for_header(uuid) + the TVF wrapper
--      recompute_posting_counts_for_header(uuid) EF binds to.
--   3. A one-shot set-based backfill of every existing leg.
--   4. The view rewrite (read the columns; drop the subqueries).
--   5. The 4 missing FK indexes (hygiene; unrelated to the counts).
-- =============================================================================

-- 1. Columns. DEFAULT 1 keeps inserts that don't set them valid; the
--    recompute corrects multi-posting headers post-commit.
ALTER TABLE txn_legs
    ADD COLUMN account_postings_on_header INT NOT NULL DEFAULT 1,
    ADD COLUMN header_total_postings      INT NOT NULL DEFAULT 1;

-- 2a. The void recompute: re-derive both counts for every leg of one
--     header. account_postings_on_header is per-(header, account) so it
--     varies across the header's legs; header_total_postings is constant
--     for the header. Both subqueries scan only this header's legs (few
--     rows) — cheap.
CREATE OR REPLACE FUNCTION fn_recompute_posting_counts_for_header(p_header_id UUID)
RETURNS VOID AS $$
BEGIN
    UPDATE txn_legs l
       SET header_total_postings = (
               SELECT COUNT(DISTINCT g.posting_index)
                 FROM txn_legs g
                WHERE g.header_id = p_header_id),
           account_postings_on_header = (
               SELECT COUNT(DISTINCT g.posting_index)
                 FROM txn_legs g
                WHERE g.header_id = p_header_id
                  AND g.account_id = l.account_id)
     WHERE l.header_id = p_header_id;
END;
$$ LANGUAGE plpgsql;

-- 2b. TVF wrapper so EF can invoke the void fn via LINQ (same pattern as
--     recompute_balances_for_account, mig 102). Returns the header id so
--     EF has a typed projection; callers discard it.
CREATE OR REPLACE FUNCTION recompute_posting_counts_for_header(p_header_id UUID)
RETURNS TABLE(header_id UUID) AS $$
BEGIN
    PERFORM fn_recompute_posting_counts_for_header(p_header_id);
    RETURN QUERY SELECT p_header_id;
END;
$$ LANGUAGE plpgsql;

-- 3. One-shot backfill — every existing leg, in one pass. COUNT(DISTINCT)
--    isn't a window aggregate in Postgres, so pre-aggregate per header and
--    per (header, account), then join.
WITH htp AS (
    SELECT header_id, COUNT(DISTINCT posting_index) AS total
      FROM txn_legs GROUP BY header_id
), aph AS (
    SELECT header_id, account_id, COUNT(DISTINCT posting_index) AS cnt
      FROM txn_legs GROUP BY header_id, account_id
)
UPDATE txn_legs l
   SET header_total_postings      = htp.total,
       account_postings_on_header = aph.cnt
  FROM htp, aph
 WHERE htp.header_id = l.header_id
   AND aph.header_id = l.header_id
   AND aph.account_id = l.account_id;

-- 4. View rewrite. Identical column set / order / types as mig 119; the
--    only change is the two count columns now read l.* instead of running
--    the correlated subqueries. CREATE OR REPLACE (column shape unchanged).
CREATE OR REPLACE VIEW resolved_transactions AS
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
    psm.security_id AS ingest_security_id,
    h.ingest_shares,
    h.ingest_unit_price,
    h.ingest_fee,
    h.ingest_security_ticker_hint,
    h.provider_raw_payload,
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    h.provider_key,
    h.is_merge_winner,
    h.import_source,
    -- Mig 120: denormalized — read straight off the leg instead of two
    -- per-row correlated COUNT(DISTINCT) subqueries (maintained by
    -- fn_recompute_posting_counts_for_header via the recompute interceptor).
    l.account_postings_on_header,
    l.header_total_postings,
    COALESCE(
        h.action,
        CASE
            WHEN this_account.account_type <> 'category'
                AND ca.account_type IS NOT NULL
                AND ca.account_type <> 'category'
                THEN 'Xfr'
            ELSE NULL
        END
    ) AS derived_action,
    this_account.account_type AS account_type
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab
            ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm
            ON psm.ledger_id = h.ledger_id
           AND psm.provider_key = h.provider_key
           AND psm.provider_security_id = h.ingest_security_ticker_hint;

ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;

-- 5. Missing FK indexes (hygiene). Partial — these FK columns are
--    nullable and mostly NULL, so index only the populated rows.
CREATE INDEX IF NOT EXISTS idx_accounts_parent_id
    ON accounts (parent_id) WHERE parent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_accounts_holdings_account_id
    ON accounts (holdings_account_id) WHERE holdings_account_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_accounts_feed_connection_id
    ON accounts (feed_connection_id) WHERE feed_connection_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_recurring_transactions_source_account_id
    ON recurring_transactions (source_account_id) WHERE source_account_id IS NOT NULL;

-- 6. Retire insert_investment_legs (069/070). It existed solely to batch
--    multi-row leg inserts into ONE statement so the per-statement
--    txn_legs AFTER-trigger family fired once. ADR-0032/0034 removed that
--    trigger family (zero triggers on txn_legs today), so its reason to
--    exist is gone — and because it INSERTs server-side via a TVF it
--    bypassed the EF ChangeTracker, forcing InvestmentTransactionsRepository
--    to drive balance/holdings/posting-count recompute by hand. The
--    repository now inserts legs as EF-tracked rows, so
--    LegDerivedRecomputeInterceptor + HoldingsRecomputeInterceptor cover
--    every recompute automatically. Signature is (text): mig 070 dropped
--    the original (jsonb) overload and recreated it taking TEXT.
DROP FUNCTION IF EXISTS insert_investment_legs(text);
