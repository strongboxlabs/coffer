-- =============================================================================
-- 228 — carry an imported row's AUTHORITATIVE amount, so it is not rebuilt
--        from shares x price.
-- =============================================================================
--
-- WHAT WAS WRONG. An OFX REINVEST carries the reinvested dollar value in its
-- TOTAL. The importer maps it to a cash-leg amount of ZERO, correctly: a
-- reinvestment moves no cash (the dividend funds the purchase), and reporting
-- the total as a cash movement walks the account's balance down on every
-- reinvest. But that was the only copy of the number. Nothing else carried it,
-- so opening the row rebuilt the amount as shares x unit_price.
--
-- Those are not the same number, and ADR-0073 D1 says so itself: "price x
-- shares need NOT equal the amount -- a rounded price against an exact total is
-- normal and faithful to the feed". A file stating 6.584 units at 48.05 with a
-- total of 316.37 rebuilds as 316.36, and Accept persists the rebuilt value.
--
-- hintToDraft.ts names the hazard exactly -- "Recomputing from shares x price
-- would silently change the amount just by opening the row, and Accept would
-- persist the wrong value" -- and solves it for buy/sell from the real cash
-- leg. A reinvest's cash leg is deliberately zero, so it fell through to the
-- path that comment warns about. The reasoning was right; the CARRIER was
-- missing.
--
-- WHY A COLUMN. ingest_shares / ingest_unit_price / ingest_fee already exist
-- for exactly this job (mig 113): preserve what the file said so the editor can
-- prefill without inventing. The authoritative total belongs beside them.
--
-- NO BACKFILL IS POSSIBLE, and that is worth stating rather than leaving a
-- reader to wonder. The OFX path sets provider_raw_payload to NULL ("doesn't
-- preserve raw element text"), so for rows already imported the original TOTAL
-- is gone. Rows imported BEFORE this migration keep whatever amount was derived
-- for them; only future imports carry the file's figure.
--
-- SECURITY_INVOKER MUST BE RE-ASSERTED. pg_get_viewdef returns only the SELECT;
-- reloptions are not part of it, and CREATE OR REPLACE VIEW does not preserve
-- them. Both of these views carry security_invoker=true, and that option IS the
-- RLS boundary — without it the view runs as its owner and a caller reads every
-- ledger. Recreating from an extracted definition therefore OPENS A SECURITY
-- HOLE unless the option is set again, which is why migrations 171 and 210 both
-- end with an explicit ALTER VIEW. The first draft of this migration omitted it
-- and the RLS suite failed with 82 visible rows where 2 were expected.
--
-- TWO VIEWS, IN ORDER. resolved_transactions reads the header through
-- live_txn_headers, so the column has to surface there first or the outer view
-- cannot see it -- which is exactly how the first draft of this migration
-- failed ("column h.ingest_amount does not exist"). Both are extracted from the
-- LIVE catalogue with pg_get_viewdef rather than copied from an older
-- migration: the discipline mig 226/227 used, and the reason mig 205 silently
-- reverted a fix that 209 had to undo. The new column is appended at the END of
-- each select list, which is what CREATE OR REPLACE VIEW permits.
-- =============================================================================

BEGIN;

-- Money-shaped, matching ingest_fee. NULL means "the file stated none", which
-- covers every row imported before now and every provider without such a
-- field -- the prefill falls back to its previous behaviour there.
ALTER TABLE txn_headers
    ADD COLUMN IF NOT EXISTS ingest_amount NUMERIC(19,4);

COMMENT ON COLUMN txn_headers.ingest_amount IS
    'The authoritative total the source file stated for this row, preserved so '
    'the editor never rebuilds it from shares x price (ADR-0073 D1). NULL when '
    'the provider stated none, or for rows imported before migration 228.';

-- 1 of 2: the inner view, or the outer one cannot reference the column.
CREATE OR REPLACE VIEW live_txn_headers AS
SELECT id,
    ledger_id,
    origin,
    external_id,
    payee,
    memo,
    posted_at,
    transacted_at,
    check_number,
    is_pending,
    is_hidden,
    is_merged_into,
    import_source,
    created_at,
    online_match_fitid,
    online_match_fi_id,
    needs_review,
    action,
    ingest_action_hint,
    provider_raw_payload,
    seq,
    provider_key,
    is_merge_winner,
    ingest_shares,
    ingest_unit_price,
    ingest_fee,
    ingest_security_ticker_hint,
    is_recurring_template,
    recurring_transaction_id,
    occurrence_date,
    ingest_amount
   FROM txn_headers
  WHERE NOT is_recurring_template;


-- CREATE OR REPLACE VIEW does NOT carry reloptions, and pg_get_viewdef does not
-- emit them — so the recreate above silently dropped security_invoker and the
-- view reverted to running as its OWNER. That is the RLS boundary: without it a
-- caller reads every ledger's rows through this view. The RLS suite caught it
-- (82 rows where 2 were expected), which is the only reason it is not in this
-- migration. Re-assert it exactly as migrations 171 and 210 do.
ALTER VIEW live_txn_headers SET (security_invoker = true);

-- 2 of 2: the register/editor projection.
CREATE OR REPLACE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    COALESCE(lr.status, 'uncleared'::text) AS status,
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
            WHEN l.header_total_postings > 1 THEN h.id
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
    lr.cleared_at,
    lr.cleared_by_user_id,
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
    l.account_postings_on_header,
    l.header_total_postings,
    COALESCE(h.action,
        CASE
            WHEN this_account.account_type <> 'category'::text AND ca.account_type IS NOT NULL AND ca.account_type <> 'category'::text THEN 'Xfr'::text
            ELSE NULL::text
        END) AS derived_action,
    this_account.account_type,
    h.ingest_amount
   FROM txn_legs l
     JOIN live_txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_leg_recon lr ON lr.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm ON psm.ledger_id = h.ledger_id AND psm.provider_key = h.provider_key AND psm.provider_security_id = h.ingest_security_ticker_hint;

-- Same reason as above.
ALTER VIEW resolved_transactions SET (security_invoker = true);

COMMIT;
