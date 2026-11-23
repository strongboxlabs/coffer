-- =============================================================================
-- 301 — each currency keeps its own minor units (ADR-0114 D15)
-- =============================================================================
--
-- WHY
-- ---
-- Until now every money figure was held at 2 places, whatever its currency: migration
-- 159 CHECKed txn_legs.amount to 2 places (ADR-0073 D3), and migrations 182 and 225
-- typed the realized-gains and budget-target money at scale 2. ADR-0114 D15 holds each
-- figure at its currency's own ISO 4217 minor units instead: 0, 2 or 3 places. A CHECK
-- on txn_legs cannot read the leg's account currency, so the exact rule per currency
-- is the API writers' (no trigger behind it, ADR-0032), and the database holds a
-- ceiling every accepted currency fits under: 3 places.
--
-- WHAT CHANGES
-- ------------
--   * currency_minor_units(text) (new): the places a stored account or ledger code is
--     held at. A frozen copy of the ISO 4217 data file the API and the SPA compile in
--     (src/Web/src/lib/currencyMinorUnits.json, List One as SIX published it on
--     2026-09-17): the codes whose exponent is 0 or 3 are listed, and every other code
--     answers 2. That "every other" covers the 2-place currencies and also every code
--     an account can no longer take: one List One does not hold, an "N.A." entry, and
--     the two fund units with more places than the ceiling (CLF, UYW). Those keep
--     working at 2 places, as before, the same answer CurrencyMinorUnits.PlacesOf
--     gives. IMMUTABLE and PARALLEL SAFE, and not STRICT, so NULL answers 2 rather than
--     NULL and it inlines. MinorUnitsSqlGuardTests pins it to the data file code by
--     code: a change to the file needs a new migration that re-issues this function.
--   * txn_legs: ck_txn_legs_amount_scale_2 (migration 159, never edited) is dropped and
--     ck_txn_legs_amount_at_most_3_places, CHECK (amount = round(amount, 3)), is added
--     NOT VALID and then validated. The column type is unchanged.
--   * The money columns that held scale 2 widen to scale 3, and their precision grows
--     with the scale so 17 integer digits remain:
--       - budget_targets.amount NUMERIC(20,3)
--       - realized_gains.proceeds NUMERIC(20,3)
--       - realized_gains.proceeds_lt NUMERIC(20,3)
--       - realized_gains.cost_basis_sold NUMERIC(20,3)
--       - realized_gains.cost_basis_sold_lt NUMERIC(20,3)
--       - realized_gains.realized_gain NUMERIC(20,3)
--       - realized_gains.realized_gain_lt NUMERIC(20,3)
--     realized_gains.quantity keeps its share scale. budget_targets gets no CHECK per
--     currency: migration 280 stamped existing targets with a ledger currency that may
--     have 0 or 3 places, so a validated CHECK would fail this migration on such an
--     install, and even a NOT VALID one would refuse an unchanged row on any UPDATE. A
--     typed target is checked by the API like any typed amount.
--   * recompute_holdings_cost_basis (as migration 275 left it) and realized_gains_walk
--     (migration 217) round each realized-gain money figure once, half away from zero
--     (PostgreSQL's numeric ROUND), at the places of the realized_gains row's own
--     account: currency_minor_units of that account's currency_code. The two still
--     round identically, so the consistency check compares like for like. Nothing else
--     in either body changes; holdings.cost_basis keeps its own scale. A missing
--     account reads NULL, which the function maps to 2.
--   * txn_header_originals gains amount (NUMERIC, no scale limit: the bank's figure
--     exactly) and rounded_at_import (BOOLEAN NOT NULL DEFAULT false), and
--     resolved_transactions exposes them, through its existing join to the sidecar,
--     as original_amount and amount_rounded_at_import. The same redefinition adds the
--     posting's other leg's amount and its account's currency, counterparty_amount and
--     counterparty_currency_code, which a register row on a transfer between
--     currencies shows (ADR-0114 D14). The four columns go last, so every existing
--     column keeps its place.
--   * txn_headers.ingest_amount and txn_headers.ingest_fee widen from 4 places to
--     NUMERIC with no scale limit. They are carriers of the provider's figure as it
--     sent it, which no import fits to the currency; the editor rounds its prefill
--     from them once, at the account currency's places (ADR-0114 D14, D15). A column
--     held at 4 places rounded a finer figure first, so the prefill rounded twice and
--     could land a minor unit away from the provider's figure. A column a view reads
--     cannot change type, so live_txn_headers, template_txn_headers,
--     balance_walk_headers and resolved_transactions are dropped and created again, each
--     with its last body (266's, 277's, and 266's plus the four columns above), the same
--     columns in the same order, and live_txn_headers and resolved_transactions with the
--     two columns below appended.
--   * txn_headers gains review_cleared_amount (NUMERIC, no scale limit) and
--     review_cleared_by_match (BOOLEAN NOT NULL DEFAULT false): why a promote-on-clear
--     put the row back in review when the reason is a cleared figure the row does not
--     hold (ADR-0114 D15). The amount is the bank's cleared figure as sent; the flag says
--     whose it is, a merged loser's (the row is its winner) or the row's own (a split
--     whose lines add up to another total). A CHECK keeps the flag false without an
--     amount. The Review marker reads them while the row needs review, so a winner held
--     for its loser no longer shows a rounded note left from its own import. They cannot
--     be derived from data already stored: a merge does not require equal amounts, so a
--     loser's figure can differ from its winner's from the merge on, and a split's
--     originals hold an imported figure on a row nothing cleared. live_txn_headers and
--     resolved_transactions expose them, appended last.
--   * The COMMENTs that said money is held at 2 places are issued again: the two
--     functions' and reminder_estimate_samples'.
--
-- SECURITY
-- --------
-- A view created again carries none of its old options, so without security_invoker it
-- would run as its owner, the service role that bypasses RLS. Each of the four views
-- has the option set by ALTER VIEW, and coffer_app's SELECT is granted again (254
-- revoked the default grants), as 266 does; balance_walk_headers' comment is issued
-- again. The new function is granted to coffer_app explicitly for the same reason.
--
-- EXISTING INSTALLS
-- -----------------
-- Every existing leg passes the new CHECK, since 159 has held every leg at 2 places;
-- a backup older than 159 is rounded by 159 first. The widens keep every value; an
-- ingest carrier stored before 301 keeps the 4 places it was cut to. Existing
-- originals rows read no amount and a cleared flag: nothing is backfilled, because a
-- leg's amount today may be an edit, not the bank's. Existing headers read no review
-- figure: a row awaiting review today shows the note it showed before. Existing realized_gains rows are
-- derived data and are not derived again here: a row not at its account's places is
-- ordinary realized_gains drift to the consistency check, and its existing repair
-- derives it again. Stored amounts that do not fit their currency are kept; the
-- consistency check lists them. Nothing reads the clock, so the migration is
-- deterministic, and every statement is guarded or idempotent, so a rerun is harmless.
--
-- LOCKS
-- -----
-- The DROP and ADD CONSTRAINT take txn_legs ACCESS EXCLUSIVE for the whole script
-- (one transaction), and the VALIDATE scans every leg under it. Each ALTER TYPE
-- rewrites its table under ACCESS EXCLUSIVE. No view reads realized_gains or
-- budget_targets, and their policies gate on ledger_id, so the types can change. The
-- txn_headers ALTER TYPE takes that table ACCESS EXCLUSIVE but does not rewrite it,
-- since removing a numeric's scale limit changes no stored value. ADD COLUMN with a
-- constant default does not rewrite txn_header_originals or txn_headers; the review
-- CHECK scans txn_headers under the lock its ALTER TYPE already holds.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. The minor-units function.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.currency_minor_units(p_code text)
 RETURNS integer
 LANGUAGE sql
 IMMUTABLE PARALLEL SAFE
AS $function$
    SELECT CASE
        WHEN p_code IN ('BIF', 'CLP', 'DJF', 'GNF', 'ISK', 'JPY', 'KMF', 'KRW', 'PYG', 'RWF', 'UGX', 'UYI', 'VND', 'VUV', 'XAF', 'XOF', 'XPF') THEN 0
        WHEN p_code IN ('BHD', 'IQD', 'JOD', 'KWD', 'LYD', 'OMR', 'TND') THEN 3
        ELSE 2
    END
$function$;

GRANT EXECUTE ON FUNCTION currency_minor_units(text) TO coffer_app;

COMMENT ON FUNCTION currency_minor_units(text) IS
    'The places money in a stored account or ledger currency code is held at '
    '(ADR-0114 D15): its ISO 4217 exponent when that is 0 or 3, else 2. The else covers the 2-place '
    'currencies and every code an account can no longer take (not on List One, an N.A. '
    'entry, or a fund unit with more places than the 3-place ceiling), which keep working '
    'at 2 places, as CurrencyMinorUnits.PlacesOf answers. A frozen copy of '
    'src/Web/src/lib/currencyMinorUnits.json (List One as SIX published it on 2026-09-17), '
    'pinned to it by MinorUnitsSqlGuardTests: a change to the file needs a migration that '
    're-issues this. Not STRICT: NULL answers 2. Used by recompute_holdings_cost_basis and '
    'realized_gains_walk only.';

-- ---------------------------------------------------------------------------
-- 2. The 3-place ceiling on txn_legs.amount.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_legs DROP CONSTRAINT IF EXISTS ck_txn_legs_amount_scale_2;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'ck_txn_legs_amount_at_most_3_places'
           AND conrelid = 'public.txn_legs'::regclass
    ) THEN
        ALTER TABLE txn_legs
            ADD CONSTRAINT ck_txn_legs_amount_at_most_3_places
                CHECK (amount = round(amount, 3)) NOT VALID;
    END IF;
END $$;

ALTER TABLE txn_legs VALIDATE CONSTRAINT ck_txn_legs_amount_at_most_3_places;

COMMENT ON CONSTRAINT ck_txn_legs_amount_at_most_3_places ON txn_legs IS
    'ADR-0114 D15 (amending ADR-0073 D3): a leg''s amount has at most 3 places, the '
    'most an accepted currency has, whatever its account''s currency. The exact rule per '
    'currency (0, 2 or 3 places) is the API writers'', which refuse a typed amount finer '
    'than its currency and round a computed one once at its currency''s places, half away '
    'from zero; this CHECK cannot read the account. Replaces migration 159''s 2-place '
    'CHECK.';

-- ---------------------------------------------------------------------------
-- 3. Scale 3 for the money columns that held scale 2.
-- ---------------------------------------------------------------------------
ALTER TABLE budget_targets
    ALTER COLUMN amount TYPE numeric(20, 3);

ALTER TABLE realized_gains
    ALTER COLUMN proceeds           TYPE numeric(20, 3),
    ALTER COLUMN proceeds_lt        TYPE numeric(20, 3),
    ALTER COLUMN cost_basis_sold    TYPE numeric(20, 3),
    ALTER COLUMN cost_basis_sold_lt TYPE numeric(20, 3),
    ALTER COLUMN realized_gain      TYPE numeric(20, 3),
    ALTER COLUMN realized_gain_lt   TYPE numeric(20, 3);

-- ---------------------------------------------------------------------------
-- 4. The bank's figure in the originals sidecar.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_header_originals
    ADD COLUMN IF NOT EXISTS amount NUMERIC,
    ADD COLUMN IF NOT EXISTS rounded_at_import BOOLEAN NOT NULL DEFAULT false;

COMMENT ON COLUMN txn_header_originals.amount IS
    'The transaction''s amount on its originating account (the sum of its legs there) '
    'as the feed gave it, in the row''s stored sign and before any rounding '
    '(ADR-0114 D15): at an import that rounds, the provider''s figure; at a first edit, the amount '
    'as it stood. No scale limit, so the bank''s figure is kept exactly. NULL on a row '
    'captured before migration 301.';

COMMENT ON COLUMN txn_header_originals.rounded_at_import IS
    'True when an import or a promote-on-clear rounded the amount to its account''s '
    'currency''s places, so amount is the bank''s figure and differs from the legs for '
    'that reason, not because of an edit (ADR-0114 D15). With needs_review it shows the '
    'Review marker. False on every other row, rows captured before migration 301 '
    'included.';

-- ---------------------------------------------------------------------------
-- 5. The two ingest carriers hold the provider's figure exactly.
-- ---------------------------------------------------------------------------
-- A column a view reads cannot change type, so the views over txn_headers are dropped
-- first, each after the views that read it, and created again below.
DROP VIEW IF EXISTS resolved_transactions;
DROP VIEW IF EXISTS balance_walk_headers;
DROP VIEW IF EXISTS live_txn_headers;
DROP VIEW IF EXISTS template_txn_headers;

ALTER TABLE txn_headers
    ALTER COLUMN ingest_amount TYPE NUMERIC,
    ALTER COLUMN ingest_fee    TYPE NUMERIC;

-- ---------------------------------------------------------------------------
-- 5b. Why a promote-on-clear held the row for review.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ADD COLUMN IF NOT EXISTS review_cleared_amount NUMERIC,
    ADD COLUMN IF NOT EXISTS review_cleared_by_match BOOLEAN NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'ck_txn_headers_review_cleared_by_match_has_amount'
           AND conrelid = 'public.txn_headers'::regclass
    ) THEN
        ALTER TABLE txn_headers
            ADD CONSTRAINT ck_txn_headers_review_cleared_by_match_has_amount
                CHECK (review_cleared_amount IS NOT NULL OR NOT review_cleared_by_match);
    END IF;
END $$;

COMMENT ON COLUMN txn_headers.review_cleared_amount IS
    'The bank''s cleared figure, exactly as sent, that put the row back in review because '
    'the row does not hold it (ADR-0114 D15): a merged loser''s when review_cleared_by_match, '
    'else the row''s own, on a split whose lines add up to another total. Written by every '
    'promote-on-clear that sets needs_review, NULL when the reason is a rounding; a new row '
    'lands NULL. Read only while needs_review, for the Review marker.';

COMMENT ON COLUMN txn_headers.review_cleared_by_match IS
    'True when review_cleared_amount is a merged loser''s cleared figure, so the row is the '
    'winner it was folded into (ADR-0114 D15); false when it is the row''s own, or there '
    'is none.';

-- Migration 266's bodies; live_txn_headers with the two review columns appended.
CREATE VIEW live_txn_headers AS
 SELECT id,
    ledger_id,
    origin,
    external_id,
    payee,
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
    ingest_amount,
    review_cleared_amount,
    review_cleared_by_match
   FROM txn_headers
  WHERE NOT is_recurring_template;

CREATE VIEW template_txn_headers AS
 SELECT id,
    ledger_id,
    origin,
    external_id,
    payee,
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
    occurrence_date
   FROM txn_headers
  WHERE is_recurring_template;

-- Migration 277's body and comment, unchanged.
CREATE VIEW balance_walk_headers AS
 SELECT h.id,
        h.ledger_id,
        h.posted_at,
        h.seq
   FROM live_txn_headers h
  WHERE h.is_merged_into IS NULL
    AND COALESCE(h.is_hidden, FALSE) = FALSE;

COMMENT ON VIEW balance_walk_headers IS
    'The headers the balance walk counts (mig 277): not a recurring template '
    '(live_txn_headers), not hidden, not merged into another. balance_walk reads its '
    'headers from it, and fn_recompute_balances_for_account its seed, so the two cannot '
    'disagree about which headers carry a running balance.';

-- ---------------------------------------------------------------------------
-- 6. resolved_transactions: migration 266's body, six columns appended.
-- ---------------------------------------------------------------------------
CREATE VIEW resolved_transactions WITH (security_invoker = true) AS
 SELECT l.id,
    l.account_id,
    h.payee AS payee,
    l.amount AS amount,
    h.posted_at AS posted_at,
    h.transacted_at AS transacted_at,
    COALESCE(lr.status, 'uncleared'::text) AS status,
    COALESCE(h.is_hidden, false) AS is_hidden,
    orig.header_id IS NOT NULL AS has_overrides,
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    h.check_number AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN l.header_total_postings > 1 THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    h.id AS header_id,
    lr.cleared_at,
    lr.cleared_by_user_id,
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
    h.ingest_amount,
    l.notes_kind,
    l.notes_is_read_only,
    l.notes_groups_by_header,
    l.notes_memo,
    l.notes_memo_inherited,
    l.notes_inherited_memo,
    l.notes_inherit_from_account_id,
    l.notes_tags_inherited,
    l.notes_header_memo,
    l.notes_header_in_play,
    other.notes_kind AS counterparty_notes_kind,
    other.notes_is_read_only AS counterparty_notes_is_read_only,
    other.notes_groups_by_header AS counterparty_notes_groups_by_header,
    other.notes_memo AS counterparty_notes_memo,
    other.notes_memo_inherited AS counterparty_notes_memo_inherited,
    other.notes_inherited_memo AS counterparty_notes_inherited_memo,
    other.notes_inherit_from_account_id AS counterparty_notes_inherit_from_account_id,
    other.notes_tags_inherited AS counterparty_notes_tags_inherited,
    other.notes_header_memo AS counterparty_notes_header_memo,
    other.notes_header_in_play AS counterparty_notes_header_in_play,
    orig.amount AS original_amount,
    COALESCE(orig.rounded_at_import, false) AS amount_rounded_at_import,
    other.amount AS counterparty_amount,
    ca.currency_code AS counterparty_currency_code,
    h.review_cleared_amount,
    h.review_cleared_by_match
   FROM txn_legs l
     JOIN live_txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_originals orig ON orig.header_id = h.id
     LEFT JOIN txn_leg_recon lr ON lr.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm ON psm.ledger_id = h.ledger_id AND psm.provider_key = h.provider_key AND psm.provider_security_id = h.ingest_security_ticker_hint;

ALTER VIEW live_txn_headers      SET (security_invoker = true);
ALTER VIEW template_txn_headers  SET (security_invoker = true);
ALTER VIEW balance_walk_headers  SET (security_invoker = true);
ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON live_txn_headers, template_txn_headers, balance_walk_headers, resolved_transactions
    TO coffer_app;

-- ---------------------------------------------------------------------------
-- 7. The realized-gains writer and its walk, at the account currency's places.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.recompute_holdings_cost_basis(p_ledger_id uuid DEFAULT NULL::uuid, p_account_id uuid DEFAULT NULL::uuid, p_security_id uuid DEFAULT NULL::uuid)
 RETURNS integer
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_holding RECORD;
    v_walk    RECORD;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
    v_places  INTEGER;
BEGIN
    -- The ledger write lock (mig 275), before any holding is looked for, created or
    -- walked: the account's ledger, or the ledger named. Nothing for a writer that took it
    -- first, this statement's for a statement of its own (the holdings and realized-gains
    -- repairs), refused in a transaction that wrote without it. A writer in flight is
    -- waited for, and everything below reads what it committed.
    IF p_account_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id FROM accounts WHERE id = p_account_id;
        -- No such account, or not in the ledger named: no holding to walk.
        IF v_resolved_ledger_id IS NULL
           OR (p_ledger_id IS NOT NULL AND p_ledger_id <> v_resolved_ledger_id)
        THEN
            RETURN 0;
        END IF;
        PERFORM 1 FROM fn_ledger_write_lock(v_resolved_ledger_id);
    ELSIF p_ledger_id IS NOT NULL THEN
        PERFORM 1 FROM fn_ledger_write_lock(p_ledger_id);
    END IF;

    -- A position's first trade has no holding yet. Two writers of one ledger never hold its
    -- lock at once, so two first trades cannot both create it.
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM holdings
                       WHERE account_id = p_account_id AND security_id = p_security_id)
    THEN
        INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
        VALUES (gen_random_uuid(), p_account_id, p_security_id, v_resolved_ledger_id, 0, 0, NOW());
    END IF;

    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
        ORDER BY account_id, security_id
    LOOP
        -- A call naming neither a ledger nor an account spans every ledger: each holding's
        -- ledger is locked in turn, which outside a migration's session (where the lock
        -- does nothing) is refused at the second ledger.
        IF p_ledger_id IS NULL AND p_account_id IS NULL THEN
            PERFORM 1 FROM fn_ledger_write_lock(v_holding.ledger_id);
        END IF;

        SELECT * INTO v_walk
        FROM holdings_fifo_walk(v_holding.account_id, v_holding.security_id, NULL);

        DELETE FROM realized_gains
        WHERE account_id = v_holding.account_id AND security_id = v_holding.security_id;

        -- Money at the places of the holding's own account's currency (mig 301, ADR-0114
        -- D15), the places realized_gains_walk rounds at.
        v_places := currency_minor_units((SELECT currency_code FROM accounts WHERE id = v_holding.account_id));

        IF COALESCE(array_length(v_walk.o_gains, 1), 0) > 0 THEN
            INSERT INTO realized_gains (
                ledger_id, account_id, security_id, sell_leg_id,
                sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
            SELECT v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                   g.sell_leg_id, g.sold_at,
                   ROUND(g.quantity, 12),
                   ROUND(g.proceeds, v_places),
                   ROUND(g.cost_basis_sold, v_places),
                   ROUND(g.realized_gain, v_places),
                   ROUND(g.proceeds_lt, v_places),
                   ROUND(g.cost_basis_sold_lt, v_places),
                   ROUND(g.realized_gain_lt, v_places)
            FROM unnest(v_walk.o_gains) AS g;
        END IF;

        -- Lot rows are created by the write path; here they are brought to the state
        -- the walk derived. The lots columns carry their own precision, so the
        -- assignment bounds these.
        IF COALESCE(array_length(v_walk.o_lots, 1), 0) > 0 THEN
            UPDATE lots l
            SET quantity  = w.quantity,
                unit_cost = w.unit_cost,
                is_closed = w.is_closed
            FROM unnest(v_walk.o_lots) AS w
            WHERE l.holding_id = v_holding.id
              AND l.leg_id     = w.leg_id;
        END IF;

        -- ...and retire the lots the walk did NOT produce. Those belong to legs on
        -- a hidden or merged header: the walk excludes them (mig 163), so until now
        -- they were "left untouched", which meant left at whatever quantity they
        -- held BEFORE the header was hidden or merged.
        --
        -- Holdings were right and the lots were not. Merging a duplicate buy into
        -- its winner left holdings.quantity at the winner's shares and left the
        -- loser's lot open beside the winner's — open lots summing to more shares
        -- than the position holds. The lots table is derived (the FIFO walk
        -- reads txn_legs, not lots), so nothing recomputed itself back into
        -- agreement, and every reader of lots saw the phantom.
        UPDATE lots l
        SET quantity  = 0,
            is_closed = TRUE
        WHERE l.holding_id = v_holding.id
          AND NOT l.is_closed
          AND NOT EXISTS (
              SELECT 1 FROM unnest(v_walk.o_lots) AS w WHERE w.leg_id = l.leg_id
          );

        UPDATE holdings
        SET cost_basis = ROUND(v_walk.o_cost_basis, 4),
            quantity   = ROUND(v_walk.o_quantity, 12)
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$function$;

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'ADR-0064 FIFO basis writer. Since mig 202 a thin persist over holdings_fifo_walk, '
    'so the algorithm is shared with the as-of read path and the two cannot drift. '
    'Bounds what it stores at each column''s own scale: realized-gain money once, half '
    'away from zero, at the minor units of the holding account''s currency '
    '(currency_minor_units, mig 301, ADR-0114 D15), exactly as realized_gains_walk does; '
    'shares at 12 places; the holding''s cost basis at 4. Money is rounded once, at its '
    'own places: rounding to a finer scale first would round twice and could shift a '
    'disposal by one minor unit. Since mig 275 it takes the ledger write lock '
    '(fn_ledger_write_lock) of the account''s or the named ledger before it reads or '
    'creates a holding: nothing for a writer that took it first, this statement''s for a '
    'statement of its own (the holdings and realized-gains repairs), refused in a '
    'transaction that wrote without it. A call naming neither ledger nor account takes '
    'each holding''s ledger in turn.';

CREATE OR REPLACE FUNCTION public.realized_gains_walk(p_account_id uuid, p_security_id uuid)
 RETURNS TABLE(sell_leg_id uuid, sold_at timestamp with time zone, quantity numeric, proceeds numeric, cost_basis_sold numeric, realized_gain numeric, proceeds_lt numeric, cost_basis_sold_lt numeric, realized_gain_lt numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    SELECT (g).sell_leg_id,
           (g).sold_at,
           ROUND((g).quantity, 12),
           ROUND((g).proceeds, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id))),
           ROUND((g).cost_basis_sold, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id))),
           ROUND((g).realized_gain, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id))),
           ROUND((g).proceeds_lt, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id))),
           ROUND((g).cost_basis_sold_lt, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id))),
           ROUND((g).realized_gain_lt, currency_minor_units((SELECT a.currency_code FROM accounts a WHERE a.id = p_account_id)))
      FROM holdings_fifo_walk(p_account_id, p_security_id, NULL) w,
           unnest(w.o_gains) AS g;
$function$;

COMMENT ON FUNCTION realized_gains_walk(UUID, UUID) IS
    'Pure per-disposal realized gains (mig 206, widened to the long-term columns by '
    '217), rounded exactly as recompute_holdings_cost_basis rounds them on write, so a '
    'consistency check compares like for like: each money figure once, half away from '
    'zero, at the minor units of the account''s currency (currency_minor_units, mig 301, '
    'ADR-0114 D15). All nine money-bearing columns: a reference implementation that '
    'omits one cannot detect drift in it.';

-- ---------------------------------------------------------------------------
-- 8. The estimate's samples are rounded in C# at the destination's places.
-- ---------------------------------------------------------------------------
COMMENT ON FUNCTION reminder_estimate_samples(UUID, UUID) IS
    'The last N committed, non-hidden, non-merged occurrences of each estimating series '
    'on a ledger, as a COUNT and a SUM of their source-side nets — never an average. The '
    'division and its single rounding happen in C#, once, half away from zero, at the '
    'places of the currency the estimate lands in (ADR-0114 D15), because rounding here '
    'and again on write is the double-rounding defect mig 209 fixed. '
    'p_recurring_transaction_id NULL scopes to the whole ledger, so the agenda and the '
    'fire path read ONE implementation (the balance_walk scoping convention, mig 211).';

COMMIT;
