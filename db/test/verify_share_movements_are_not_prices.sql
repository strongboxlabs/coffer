-- Verify migrations 235 + 237 + 238: only an EXECUTION prices a day.
--
-- Cases 1-6 are the share movements that reached production one at a time.
-- Case 7 is the property that stops the next one: an action nobody enumerated
-- prices nothing, rather than pricing days by default.
--
-- The bug this guards, in two parts. Quicken wrote ShrsIn/ShrsOut into MD with
-- the amount set to the SHARE COUNT; the importer derived
-- unit_price = amount / quantity and ~$1.00 became a `trade` price on funds
-- worth hundreds. And where a share movement carries a "real" figure it is a
-- lot's carried-over COST BASIS, which sorts after the day's genuine trades by
-- seq and displaces them. 235 caught only the first; 237 made the action the
-- whole rule.
--
-- The discriminator must stay narrow in the other direction too: catching a
-- money-market fund genuinely worth $1.00 would be the same class of error in
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
                 -506.31, v_fund, -506.309, 1.000002, 'security');

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION 'A nominal-priced ShrsOut priced the day: got %', v_got;
    END IF;

    -- CASE 2 — the SAME action carrying a perfectly plausible figure. Still not
    -- a price. Migration 235 let this through on the reasoning that a real-
    -- looking number is a real observation; on production it was lot basis from
    -- an in-kind move, displacing the day's genuine trades. Nothing in the row
    -- separates basis from price, so the ACTION is the rule.
    UPDATE txn_legs SET amount = -10323.54, unit_price = 20.3898
     WHERE header_id = v_h_nominal;

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day);
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

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day + 1);
    IF v_got IS DISTINCT FROM 137.2500 THEN
        RAISE EXCEPTION 'An ordinary buy did not price its day: got %', v_got;
    END IF;

    -- CASE 4 — a genuine money-market fund really is a dollar a share, bought
    -- normally. Dollars equal shares here as a fact about the fund, not as a
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

    v_got := fn_trade_price_for_day(v_ledger, v_mmkt, v_day);
    IF v_got IS DISTINCT FROM 1.0000 THEN
        RAISE EXCEPTION 'A real dollar money-market buy was excluded: got %', v_got;
    END IF;

    -- CASE 5 — THE production failure. A genuine trade and a share movement on
    -- the SAME day, with the movement sorting later by seq. The real trade must
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
                 4979.51, v_fund, 468.88, 10.62, 'security');

    -- v_h_real (an ordinary Buy at 137.25) is on this same day with a LOWER seq.
    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day + 1);
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
    -- This case exists only here. The dev dataset has zero transfer_shares
    -- legs — its in-kind transfers were never converted — so no measurement
    -- against real data can exercise it, which is exactly how it reached
    -- production twice.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_inkind, v_ledger, 'manual', 'transfer_shares', 'in-kind',
                 v_day + 2, v_day + 2, v_day + 2);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_inkind, v_ledger, v_acct, 0,
                 -1100.31, v_fund, -10.588, 103.9204, 'security');

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day + 2);
    IF v_got IS NOT NULL THEN
        RAISE EXCEPTION
            'An in-kind transfer_shares leg priced its day: got %', v_got;
    END IF;

    -- ...and it must not displace a real trade on the same day either. The
    -- transfer sorts LATER by seq, as it did on production.
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

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day + 2);
    IF v_got IS DISTINCT FROM 152.5000 THEN
        RAISE EXCEPTION
            'An in-kind transfer displaced the day''s real trade: got % (expected 152.5000)',
            v_got;
    END IF;

    -- CASE 7 — the property the allow-list exists for. An action nobody has
    -- considered prices NOTHING until it is named. Under the blacklist this
    -- priced the day by default, which is how three separate share-movement
    -- spellings each reached production before being excluded one at a time.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_novel, v_ledger, 'manual', 'misc', 'some future action',
                 v_day + 3, v_day + 3, v_day + 3);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_novel, v_ledger, v_acct, 0,
                 -500.00, v_fund, 4, 125.00, 'security');

    v_got := fn_trade_price_for_day(v_ledger, v_fund, v_day + 3);
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

    RAISE NOTICE 'verify_share_movements_are_not_prices: 8 cases OK';
END;
$$;

ROLLBACK;
