-- Verify migrations 235 + 237 + 238 + 273: only an EXECUTION prices a day, and
-- only one on a header that is neither hidden nor merged into another.
--
-- Cases 1-6 are the share-movement shapes the rule must not price.
-- Case 7 is the property that stops the next one: an action nobody enumerated
-- prices nothing, rather than pricing days by default.
-- Case 8 is the unbacked-price advisory's scope.
-- Cases 9 and 10 are migration 273's header filter: a hidden (9) or merged-away
-- (10) execution sorting later by seq than a visible one on its day, the order
-- in which it would win the day, and that day's rule, checker and advisory dates
-- agreeing on the visible one; then the day left with no visible execution.
-- Case 11 is migration 273's advisory, which reads its dates through
-- fn_unbacked_price_dates: it lists what the per-row test lists (migration 240's
-- fn_price_is_unbacked, dropped by 273, written out here as its body).
--
-- The day's rule is read through fn_trade_execution_for_day (migration 274), which
-- states it once; 274 dropped fn_trade_price_for_day, its price alone.
--
-- The bug this guards, in two parts. A Moneydance ShrsIn/ShrsOut can carry the
-- amount set to the SHARE COUNT; the importer derives
-- unit_price = amount / quantity and a price of about one becomes a `trade`
-- price on funds worth far more. And where a share movement carries a "real"
-- figure it is a lot's carried-over COST BASIS, which sorts after the day's
-- genuine trades by seq and displaces them. 235 handles only the first; 237
-- makes the action the whole rule.
--
-- The discriminator must stay narrow in the other direction too: catching a
-- money-market fund genuinely priced at par would be the same class of error in
-- reverse, so that case is asserted here as well.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_share_movements_are_not_prices.sql
-- All assertions are plpgsql DO blocks; any failure aborts. ROLLBACK at the end.

BEGIN;

DO $$
DECLARE
    v_ledger   uuid := gen_random_uuid();
    v_acct     uuid := gen_random_uuid();
    v_fund     uuid := gen_random_uuid();
    v_mmkt     uuid := gen_random_uuid();
    v_day      date := DATE '2006-01-06';
    v_got      numeric;

    -- One header per case; seq ordering is irrelevant because each case uses a
    -- distinct security.
    v_h_nominal uuid := gen_random_uuid();
    v_h_real    uuid := gen_random_uuid();
    v_h_mmkt    uuid := gen_random_uuid();
    v_h_late    uuid := gen_random_uuid();
    v_h_inkind  uuid := gen_random_uuid();
    v_h_novel   uuid := gen_random_uuid();
    v_orphans   integer;

    -- Cases 9-11: one security each for the hidden and the merged-away shapes.
    v_hid       uuid := gen_random_uuid();
    v_mrg       uuid := gen_random_uuid();
    v_h_shown   uuid := gen_random_uuid();
    v_h_hidden  uuid := gen_random_uuid();
    v_h_winner  uuid := gen_random_uuid();
    v_h_merged  uuid := gen_random_uuid();
    v_h_folded  uuid := gen_random_uuid();
    v_header    uuid;
    v_reason    text;
    v_rows      integer;
    v_one_pass  uuid[];
    v_per_row   uuid[];
    -- The caller's tomorrow, for the advisory's holding value: after every case's day.
    v_boundary  timestamptz := TIMESTAMPTZ '2026-06-16 00:00:00+00';
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Share Movement Test');
    INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                          opening_balance, is_active)
         VALUES (v_acct, v_ledger, 'Brokerage Holdings', 'investment', 'USD', 0, TRUE);
    INSERT INTO securities (id, ledger_id, ticker, name, is_active, share_decimals)
         VALUES (v_fund, v_ledger, 'FUND', 'A Fund Worth Hundreds', TRUE, 4),
                (v_mmkt, v_ledger, 'MMKT', 'A Dollar Money Market',  TRUE, 4);

    -- CASE 1 — a ShrsOut whose amount equals the share count. The placeholder.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_nominal, v_ledger, 'file_import', 'moneydance', 'md-shrsout-1', 'sellx', 'shrs out',
                 v_day, v_day, v_day, '{"qif_invst_action":"ShrsOut"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_nominal, v_ledger, v_acct, 0,
                 -321.46, v_fund, -321.457, 1.000009, 'security');

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION 'A nominal-priced ShrsOut priced the day: got %', v_got;
    END IF;

    -- CASE 2 — the SAME action carrying a perfectly plausible figure. Still not
    -- a price. Migration 235's rule lets this through, treating a real-looking
    -- number as a real observation; on an in-kind move it is lot basis,
    -- and it displaces the day's genuine trades. Nothing in the row
    -- separates basis from price, so the ACTION is the rule.
    UPDATE txn_legs SET amount = -12892.03, unit_price = 40.1050
     WHERE header_id = v_h_nominal;

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION 'A ShrsOut priced its day on a plausible figure: got %', v_got;
    END IF;

    -- CASE 3 — an ordinary buy at a real price is untouched.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_real, v_ledger, 'file_import', 'moneydance', 'md-buy-1', 'buy', 'buy',
                 v_day + 1, v_day + 1, v_day + 1, '{"qif_invst_action":"Buy"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_real, v_ledger, v_acct, 0,
                 -1372.50, v_fund, 10, 137.25, 'security');

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day + 1);
    IF v_got IS DISTINCT FROM 137.2500 THEN
        RAISE EXCEPTION 'An ordinary buy did not price its day: got %', v_got;
    END IF;

    -- CASE 4 — a genuine money-market fund really is priced at par, bought
    -- normally. Amount equals shares here as a fact about the fund, not as a
    -- placeholder, and the action is not a share movement: it must still price.
    -- Catching this would be the same defect in the opposite direction.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_mmkt, v_ledger, 'file_import', 'moneydance', 'md-buy-mmkt', 'buy', 'sweep',
                 v_day, v_day, v_day, '{"qif_invst_action":"Buy"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_mmkt, v_ledger, v_acct, 0,
                 -250.00, v_mmkt, 250, 1.0, 'security');

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_mmkt, v_day);
    IF v_got IS DISTINCT FROM 1.0000 THEN
        RAISE EXCEPTION 'A real dollar money-market buy was excluded: got %', v_got;
    END IF;

    -- CASE 5 — THE failure this file exists for. A genuine trade and a share
    -- movement on the SAME day, with the movement sorting later by seq. The real trade must
    -- win, or a lot's historical purchase price becomes the day's market price.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_late, v_ledger, 'file_import', 'moneydance', 'md-shrsin-late',
                 'buyx', 'shares in', v_day + 1, v_day + 1, v_day + 1,
                 '{"qif_invst_action":"ShrsIn"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_late, v_ledger, v_acct, 0,
                 2894.47, v_fund, 234.56, 12.34, 'security');

    -- v_h_real (CASE 3's ordinary Buy) is on this same day with a LOWER seq.
    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day + 1);
    IF v_got IS DISTINCT FROM 137.2500 THEN
        RAISE EXCEPTION
            'A later ShrsIn displaced the day''s real trade: got % (expected 137.2500)',
            v_got;
    END IF;

    -- CASE 6 — Coffer's OWN share movement. convert_in_kind_transfer deletes
    -- the imported pair and creates a replacement through the native path, so
    -- the header carries action = 'transfer_shares' and NO provider payload:
    -- the Moneydance test in 235/237 cannot see it at all. Its legs are priced
    -- by the FIFO plan at each lot's carried basis.
    --
    -- This case exists only here. A ledger whose in-kind transfers were never
    -- converted has no transfer_shares legs at all, so only a synthetic case
    -- can exercise it.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_inkind, v_ledger, 'manual', 'transfer_shares', 'in-kind',
                 v_day + 2, v_day + 2, v_day + 2);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_inkind, v_ledger, v_acct, 0,
                 -1219.26, v_fund, -12.345, 98.7654, 'security');

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day + 2);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION
            'An in-kind transfer_shares leg priced its day: got %', v_got;
    END IF;

    -- ...and it must not displace a real trade on the same day either. The
    -- transfer sorts LATER by seq, the order in which it would displace the trade.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (gen_random_uuid(), v_ledger, 'file_import', 'moneydance',
                 'md-buy-2', 'buy', 'buy', v_day + 2, v_day + 2, v_day + 2,
                 '{"qif_invst_action":"Buy"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         SELECT gen_random_uuid(), h.id, v_ledger, v_acct, 0,
                -915.00, v_fund, 6, 152.50, 'security'
           FROM txn_headers h WHERE h.external_id = 'md-buy-2';

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day + 2);
    IF v_got IS DISTINCT FROM 152.5000 THEN
        RAISE EXCEPTION
            'An in-kind transfer displaced the day''s real trade: got % (expected 152.5000)',
            v_got;
    END IF;

    -- CASE 7 — the property the allow-list exists for. An action nobody has
    -- considered prices NOTHING until it is named. Under a blacklist it would
    -- price the day by default, and every new share-movement spelling would
    -- need its own exclusion.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_novel, v_ledger, 'manual', 'misc', 'some future action',
                 v_day + 3, v_day + 3, v_day + 3);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_novel, v_ledger, v_acct, 0,
                 -500.00, v_fund, 4, 125.00, 'security');

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day + 3);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION
            'An unenumerated action priced a day: got %. The rule must fail closed.',
            v_got;
    END IF;

    -- CASE 8 — a stored trade price nothing derives is ADVISORY (migrations
    -- 240/241): absent from the consistency findings, present in the
    -- per-security advisory. It cannot be a finding — nothing records WHICH
    -- transaction wrote a price, so this is inferred, and ADR-0084 D4 keeps
    -- some of them deliberately.
    INSERT INTO security_prices (id, ledger_id, security_id, price, currency_code,
                                 price_date, source)
         VALUES (gen_random_uuid(), v_ledger, v_mmkt, 42.4242, 'USD',
                 v_day + 9, 'trade');

    PERFORM 1 FROM fn_trade_price_check(v_ledger) c WHERE c.price_date = v_day + 9;
    IF FOUND THEN
        RAISE EXCEPTION 'An unbacked price was reported as a consistency finding';
    END IF;

    SELECT count(*) INTO v_orphans
      FROM fn_unbacked_price_dates(v_ledger, v_mmkt) d
     WHERE d.price_date = v_day + 9;
    IF v_orphans <> 1 THEN
        RAISE EXCEPTION 'An unbacked price was not advised: got % row(s)', v_orphans;
    END IF;

    -- Editing it by hand makes it `manual`, which takes it out of scope for
    -- good — the mechanism that stops the advisory listing what you have
    -- already dealt with.
    UPDATE security_prices SET source = 'manual'
     WHERE security_id = v_mmkt AND price_date = v_day + 9;

    SELECT count(*) INTO v_orphans
      FROM fn_unbacked_price_dates(v_ledger, v_mmkt) d
     WHERE d.price_date = v_day + 9;
    IF v_orphans <> 0 THEN
        RAISE EXCEPTION 'A manual price was still advised: got % row(s)', v_orphans;
    END IF;

    INSERT INTO securities (id, ledger_id, ticker, name, is_active, share_decimals)
         VALUES (v_hid, v_ledger, 'HIDN', 'A Fund With A Hidden Trade', TRUE, 4),
                (v_mrg, v_ledger, 'MRGD', 'A Fund With A Merged Trade', TRUE, 4);

    -- CASE 9 — migration 273: a HIDDEN execution is no trade. Deleting an imported
    -- row hides it. A visible buy, then a hidden one on the same day sorting LATER by
    -- seq, the order in which it would win the day under the old rule.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_shown, v_ledger, 'file_import', 'moneydance', 'md-buy-shown',
                 'buy', 'buy', v_day, v_day, v_day, '{"qif_invst_action":"Buy"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_shown, v_ledger, v_acct, 0,
                 -500.00, v_hid, 10, 50.00, 'security');
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload, is_hidden)
         VALUES (v_h_hidden, v_ledger, 'file_import', 'moneydance', 'md-buy-hidden',
                 'buy', 'buy', v_day, v_day, v_day, '{"qif_invst_action":"Buy"}'::jsonb,
                 TRUE);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_hidden, v_ledger, v_acct, 0,
                 -600.00, v_hid, 10, 60.00, 'security');

    IF (SELECT seq FROM txn_headers WHERE id = v_h_hidden)
       <= (SELECT seq FROM txn_headers WHERE id = v_h_shown) THEN
        RAISE EXCEPTION 'Premise: the hidden execution must sort later by seq';
    END IF;

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_hid, v_day);
    IF v_got IS DISTINCT FROM 50.0000 THEN
        RAISE EXCEPTION
            'A later hidden execution priced its day: got % (expected 50.0000)', v_got;
    END IF;

    -- The checker derives the same price, from the visible execution.
    SELECT c.expected_price, c.header_id INTO v_got, v_header
      FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_hid AND c.price_date = v_day;
    IF v_got IS DISTINCT FROM 50.0000 OR v_header IS DISTINCT FROM v_h_shown THEN
        RAISE EXCEPTION
            'The checker derived % from header % for a day with a hidden execution '
            '(expected 50.0000 from the visible one)', v_got, v_header;
    END IF;

    -- The writer stores what the rule names; the checker agrees with the stored row,
    -- and the advisory's dates do not list the day.
    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_hid, v_day);
    SELECT c.stored_price, c.reason INTO v_got, v_reason
      FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_hid AND c.price_date = v_day;
    IF v_got IS DISTINCT FROM 50.0000 OR v_reason IS NOT NULL THEN
        RAISE EXCEPTION
            'The checker disagreed with the stored price: stored %, reason %', v_got, v_reason;
    END IF;
    PERFORM 1 FROM fn_unbacked_price_dates(v_ledger, v_hid) d WHERE d.price_date = v_day;
    IF FOUND THEN
        RAISE EXCEPTION 'The advisory''s dates listed a day a visible execution prices';
    END IF;

    -- The visible execution hidden too: the day has no trade. The rule names no price,
    -- the checker no longer examines the day, and the advisory's dates list the price
    -- the visible execution left (ADR-0084 D4 keeps it).
    UPDATE txn_headers SET is_hidden = TRUE WHERE id = v_h_shown;
    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_hid, v_day);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION 'A day whose every execution is hidden was priced: got %', v_got;
    END IF;
    PERFORM 1 FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_hid AND c.price_date = v_day;
    IF FOUND THEN
        RAISE EXCEPTION 'The checker examined a day whose every execution is hidden';
    END IF;
    SELECT count(*) INTO v_rows
      FROM fn_unbacked_price_dates(v_ledger, v_hid) d WHERE d.price_date = v_day;
    IF v_rows <> 1 THEN
        RAISE EXCEPTION
            'A day whose every execution is hidden was not advised: got % row(s)', v_rows;
    END IF;

    -- CASE 10 — the same with a MERGED-AWAY execution: a visible buy, then one merged
    -- into it sorting later by seq on the same day.
    INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id,
                             action, payee, posted_at, transacted_at, created_at,
                             provider_raw_payload)
         VALUES (v_h_winner, v_ledger, 'file_import', 'moneydance', 'md-buy-winner',
                 'buy', 'buy', v_day, v_day, v_day, '{"qif_invst_action":"Buy"}'::jsonb);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_winner, v_ledger, v_acct, 0,
                 -700.00, v_mrg, 10, 70.00, 'security');
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at, is_merged_into)
         VALUES (v_h_merged, v_ledger, 'manual', 'buy', 'buy',
                 v_day, v_day, v_day, v_h_winner);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_merged, v_ledger, v_acct, 0,
                 -800.00, v_mrg, 10, 80.00, 'security');

    IF (SELECT seq FROM txn_headers WHERE id = v_h_merged)
       <= (SELECT seq FROM txn_headers WHERE id = v_h_winner) THEN
        RAISE EXCEPTION 'Premise: the merged-away execution must sort later by seq';
    END IF;

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_mrg, v_day);
    IF v_got IS DISTINCT FROM 70.0000 THEN
        RAISE EXCEPTION
            'A later merged-away execution priced its day: got % (expected 70.0000)', v_got;
    END IF;

    SELECT c.expected_price, c.header_id INTO v_got, v_header
      FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_mrg AND c.price_date = v_day;
    IF v_got IS DISTINCT FROM 70.0000 OR v_header IS DISTINCT FROM v_h_winner THEN
        RAISE EXCEPTION
            'The checker derived % from header % for a day with a merged-away execution '
            '(expected 70.0000 from the visible one)', v_got, v_header;
    END IF;

    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_mrg, v_day);
    SELECT c.stored_price, c.reason INTO v_got, v_reason
      FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_mrg AND c.price_date = v_day;
    IF v_got IS DISTINCT FROM 70.0000 OR v_reason IS NOT NULL THEN
        RAISE EXCEPTION
            'The checker disagreed with the stored price: stored %, reason %', v_got, v_reason;
    END IF;
    PERFORM 1 FROM fn_unbacked_price_dates(v_ledger, v_mrg) d WHERE d.price_date = v_day;
    IF FOUND THEN
        RAISE EXCEPTION 'The advisory''s dates listed a day a visible execution prices';
    END IF;

    -- A trade the next day, priced while visible and then folded into the first day's
    -- buy: its day has no trade left. The rule names no price, the checker no longer
    -- examines the day, and the advisory's dates list the price it left.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_folded, v_ledger, 'manual', 'buy', 'buy',
                 v_day + 1, v_day + 1, v_day + 1);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_folded, v_ledger, v_acct, 0,
                 -820.00, v_mrg, 10, 82.00, 'security');
    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_mrg, v_day + 1);
    UPDATE txn_headers SET is_merged_into = v_h_winner WHERE id = v_h_folded;

    SELECT price INTO v_got FROM fn_trade_execution_for_day(v_ledger, v_mrg, v_day + 1);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION 'A day whose only execution is merged away was priced: got %', v_got;
    END IF;
    PERFORM 1 FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_mrg AND c.price_date = v_day + 1;
    IF FOUND THEN
        RAISE EXCEPTION 'The checker examined a day whose only execution is merged away';
    END IF;
    SELECT count(*) INTO v_rows
      FROM fn_unbacked_price_dates(v_ledger, v_mrg) d WHERE d.price_date = v_day + 1;
    IF v_rows <> 1 THEN
        RAISE EXCEPTION
            'A day whose only execution is merged away was not advised: got % row(s)', v_rows;
    END IF;

    -- CASE 11 — migration 273's advisory takes each security's dates from
    -- fn_unbacked_price_dates. It lists exactly what the per-row test lists when asked
    -- of each stored price, in the same order: here the two securities cases 9 and 10
    -- left one unbacked price each, and nothing else. Case 8's price is manual, and the
    -- fund's trade price written here is backed by case 3's buy, so a security holding
    -- only backed trade prices is in scope and must not be listed. The per-row test is
    -- migration 240's fn_price_is_unbacked, which 273 drops; its body is written out:
    -- a trade-source price on a day the rule (fn_trade_execution_for_day) prices nothing.
    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_fund, v_day + 1);
    PERFORM 1 FROM security_prices p
     WHERE p.security_id = v_fund AND p.price_date = v_day + 1 AND p.source = 'trade';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Premise: the fund must hold a backed trade-source price';
    END IF;

    SELECT count(*) INTO v_rows
      FROM ((SELECT s.security_id, s.unbacked_count, s.earliest, s.latest
               FROM fn_unbacked_price_securities(v_ledger, v_boundary) s
             EXCEPT ALL
             SELECT p.security_id, count(*), min(p.price_date), max(p.price_date)
               FROM security_prices p
              WHERE p.ledger_id = v_ledger
                AND p.source = 'trade'
                AND NOT EXISTS (SELECT 1 FROM fn_trade_execution_for_day(p.ledger_id, p.security_id, p.price_date))
              GROUP BY p.security_id)
            UNION ALL
            (SELECT p.security_id, count(*), min(p.price_date), max(p.price_date)
               FROM security_prices p
              WHERE p.ledger_id = v_ledger
                AND p.source = 'trade'
                AND NOT EXISTS (SELECT 1 FROM fn_trade_execution_for_day(p.ledger_id, p.security_id, p.price_date))
              GROUP BY p.security_id
             EXCEPT ALL
             SELECT s.security_id, s.unbacked_count, s.earliest, s.latest
               FROM fn_unbacked_price_securities(v_ledger, v_boundary) s)) diff;
    IF v_rows <> 0 THEN
        RAISE EXCEPTION
            'The advisory and the per-row test disagree on % row(s)', v_rows;
    END IF;

    SELECT array_agg(s.security_id ORDER BY s.ord) INTO v_one_pass
      FROM fn_unbacked_price_securities(v_ledger, v_boundary)
           WITH ORDINALITY AS s(security_id, unbacked_count, earliest, latest,
                                holding_value, ord);
    -- Nothing is held, so the order is by count, then security id.
    SELECT array_agg(x.security_id ORDER BY x.n DESC, x.security_id) INTO v_per_row
      FROM (SELECT p.security_id, count(*) AS n
              FROM security_prices p
             WHERE p.ledger_id = v_ledger
               AND p.source = 'trade'
               AND NOT EXISTS (SELECT 1 FROM fn_trade_execution_for_day(p.ledger_id, p.security_id, p.price_date))
             GROUP BY p.security_id) x;
    IF v_one_pass IS DISTINCT FROM v_per_row THEN
        RAISE EXCEPTION 'The advisory listed % where the per-row order is %',
            v_one_pass, v_per_row;
    END IF;

    SELECT count(*) INTO v_rows
      FROM fn_unbacked_price_securities(v_ledger, v_boundary) s
     WHERE (s.security_id = v_hid AND s.unbacked_count = 1
            AND s.earliest = v_day AND s.latest = v_day AND s.holding_value = 0)
        OR (s.security_id = v_mrg AND s.unbacked_count = 1
            AND s.earliest = v_day + 1 AND s.latest = v_day + 1 AND s.holding_value = 0);
    IF v_rows <> 2 OR array_length(v_one_pass, 1) IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION
            'The advisory did not list exactly the hidden and the merged-away days: % of 2 '
            'expected rows, % listed', v_rows, array_length(v_one_pass, 1);
    END IF;

    RAISE NOTICE 'verify_share_movements_are_not_prices: 11 cases OK';
END;
$$;

ROLLBACK;
