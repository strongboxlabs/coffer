-- =============================================================================
-- 188 — snapshot restore performance: capture realized_gains, set-based balances
-- =============================================================================
--
-- A full-ledger restore ran ~27s on a real ledger. Reading the restore body, the
-- cost was not where the delete+reinsert is -- it was in the two derived-state
-- rebuilds bolted on after it, and BOTH were avoidable.
--
--  1. The FIFO recompute was rebuilding data the payload already carried.
--     `holdings` and `lots` are both captured, so restore reinserted them
--     verbatim -- and then `recompute_holdings_cost_basis(p_ledger_id)` walked the
--     entire ledger and OVERWROTE them, via nested plpgsql loops (per holding ->
--     per event -> re-query every open lot, joining lots -> txn_legs ->
--     live_txn_headers on each event). The only thing that walk produced which the
--     payload lacked was `realized_gains`.
--
--     CORRECTION (comment only; the SQL below is unchanged and was never wrong).
--     This header originally asserted that those loops "are the ~27s". The
--     stress lane then measured them, and they are not: whole-ledger recompute is
--     ~0.3s at 200 holdings x 22 events and ~2.3s at 20 holdings x 500 events with
--     8,000 lots. Restore at 50k transactions is ~65s, dominated by the
--     delete+reinsert of an ~85 MB payload -- not by either rebuild removed here.
--     What this migration does is still right (do not re-derive state the payload
--     already carries; do not run provably dead work; make restore reproduce the
--     snapshot rather than approximate it), and it is a real if modest saving. But
--     the attribution was an assumption dressed as a fact, and the lever for
--     restore latency is the reinsert path. See follow-ups.md.
--
--     So: capture realized_gains and drop the recompute. It round-trips 1:1 --
--     it keys on `sell_leg_id`, a txn_legs row the payload already carries, plus
--     its own ledger_id -- which is exactly the argument mig 181 used to justify
--     capturing txn_leg_recon. Restore now reproduces the snapshot instead of
--     re-deriving it, which is also what "snapshot" should mean.
--
--     Safe because restore REFUSES a cross-schema-version restore
--     (LedgerSnapshotsRepository.RestoreAsync -> SchemaVersionMismatch, ADR-0037
--     Phase 1): a payload can only be restored while the live schema equals the
--     schema it was captured under, so the derivation logic cannot have changed
--     in between. That coupling is load-bearing -- if the version guard is ever
--     relaxed, revisit this, because captured derived state could then be stale
--     relative to newer logic. The assertion below fails loudly rather than
--     silently restoring an empty realized_gains if the key is ever absent.
--
--  2. The per-account balance loop was doing provably dead work. It ran
--     `fn_recompute_balances_for_account(account, '0001-01-01')` once per account,
--     where each call (a) re-ran a DELETE that can match nothing, because restore
--     already deleted every balance row for the ledger wholesale, and (b) looked
--     up a "previous balance" that with a 0001-01-01 floor can only ever resolve
--     to the account's opening_balance. It also re-scanned `live_txn_headers`
--     once per account -- including every category account with no legs at all,
--     which after ADR-0091 means >=103 no-op iterations on every ledger.
--
--     Replaced with fn_recompute_balances_for_ledger: one set-based pass with the
--     running total as a window function PARTITION BY account_id. Measured on dev
--     (112 accounts / 19 headers) the loop was ~26ms, so this is a correctness-
--     and-clarity cleanup rather than the perf win -- the win is (1).
--
-- fn_recompute_balances_for_account is UNCHANGED and still the incremental path
-- used by the write triggers/interceptors, which narrow by account + posted_at.
-- Only restore -- the one caller that rebuilds everything from a 0001-01-01 floor
-- -- switches to the set-based version.
--
-- Not addressed here: the payload reinsert, which the stress lane identifies as
-- where restore's time actually goes. Hoisting the FIFO walk's open-lot re-query
-- out of its per-event loop was the expected follow-up, but measurement demoted it
-- (~0.1s for a single position, which is what a transaction write pays).
-- =============================================================================

-- ----- Set-based, whole-ledger balance rebuild --------------------------------
-- Equivalent to looping fn_recompute_balances_for_account(a, '0001-01-01') over
-- every account in the ledger, minus the per-account overhead. Assumes the caller
-- has already cleared txn_header_account_balances for the ledger (restore does).
CREATE OR REPLACE FUNCTION fn_recompute_balances_for_ledger(p_ledger_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    WITH header_net AS (
        -- Same predicates as the per-account function: live headers only, not
        -- merged away, not hidden (override-aware), override-aware posted_at and
        -- leg amount. Grouped per (account, header) instead of filtered to one
        -- account.
        SELECT l.account_id,
               h.id                               AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq,
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM live_txn_headers h
          JOIN txn_legs l                   ON l.header_id = h.id
          -- Join accounts to scope to this ledger AND to reach opening_balance;
          -- this is the same account set the old loop iterated.
          JOIN accounts a                   ON a.id = l.account_id AND a.ledger_id = p_ledger_id
          LEFT JOIN txn_leg_overrides lo    ON lo.leg_id = l.id
          LEFT JOIN txn_header_overrides o  ON o.header_id = h.id
         WHERE h.is_merged_into IS NULL
           AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE
         GROUP BY l.account_id, h.id, COALESCE(o.posted_at, h.posted_at), h.seq
    )
    SELECT hn.header_id,
           hn.account_id,
           p_ledger_id,
           -- The 0001-01-01 floor means there is never a prior balance to carry,
           -- so every account's running total starts from its opening_balance.
           COALESCE(a.opening_balance, 0) + SUM(hn.net_amount) OVER (
               PARTITION BY hn.account_id
               ORDER BY hn.posted_at, hn.seq
               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
           ) AS balance_after,
           hn.net_amount
      FROM header_net hn
      JOIN accounts a ON a.id = hn.account_id;
END;
$$;

COMMENT ON FUNCTION fn_recompute_balances_for_ledger(uuid) IS
    'Set-based whole-ledger rebuild of txn_header_account_balances (mig 188). '
    'Caller must have cleared the ledger''s rows first. For incremental updates '
    'use fn_recompute_balances_for_account, which the write path still uses.';

-- ----- Payload: capture realized_gains ---------------------------------------
-- CREATE OR REPLACE of the mig-181 body + the realized_gains key.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_payload(p_ledger_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'accounts',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM accounts t WHERE t.ledger_id = p_ledger_id),
        'securities',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM securities t WHERE t.ledger_id = p_ledger_id),
        'user_account_groups',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_groups t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'user_account_group_members',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_group_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_recon',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_recon t WHERE t.ledger_id = p_ledger_id),
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        -- mig 188: realized_gains is derived, but re-deriving it on restore cost
        -- ~27s and overwrote the holdings/lots restored above. Keys on
        -- sell_leg_id (a captured txn_legs row) so it round-trips 1:1.
        'realized_gains',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM realized_gains t WHERE t.ledger_id = p_ledger_id),
        'txn_header_overrides',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_overrides t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_overrides',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_overrides t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$$;

-- ----- Restore: reinsert realized_gains, set-based balances, no FIFO walk -----
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_restore(
    p_ledger_id uuid,
    p_payload   text
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_payload jsonb := p_payload::jsonb;
BEGIN
    -- A payload without this key would silently restore zero realized gains,
    -- since jsonb_populate_recordset(NULL::t, NULL) yields no rows. Unreachable
    -- while the schema-version guard holds (a pre-188 snapshot cannot pass it),
    -- so this is an assertion about that invariant, not a fallback. An empty
    -- ledger still yields '[]', which is distinguishable from a missing key.
    IF v_payload->'realized_gains' IS NULL THEN
        RAISE EXCEPTION
            'snapshot payload has no realized_gains key (pre-mig-188 payload?); '
            'restore would leave realized gains empty. Recapture the snapshot, or '
            'if the schema-version guard was relaxed, restore this payload with a '
            'recompute_holdings_cost_basis(%) pass instead.', p_ledger_id;
    END IF;

    -- ----- 1. Delete existing rows in reverse-FK order ------------------
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
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
    -- realized_gains cascades off txn_legs, but delete it explicitly so the
    -- order is deterministic and readable like its siblings (mig 188).
    DELETE FROM realized_gains             WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_recon              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_overrides          WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_overrides       WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags            WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / account-groups / per-security data.
    DELETE FROM user_account_group_members WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups        WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids       WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                       WHERE ledger_id = p_ledger_id;
    -- Roots last.
    DELETE FROM securities                 WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                   WHERE ledger_id = p_ledger_id;
    -- The materialised balance table.
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;

    -- ----- 2. Insert rows from the payload (forward-FK order) -----------
    -- Roots first.
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    -- loan_terms references accounts (incl. interest/escrow targets) — after accounts.
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    -- Children of roots.
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO user_account_groups        SELECT * FROM jsonb_populate_recordset(NULL::user_account_groups,        v_payload->'user_account_groups');
    INSERT INTO user_account_group_members SELECT * FROM jsonb_populate_recordset(NULL::user_account_group_members, v_payload->'user_account_group_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    -- Transaction graph.
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    -- txn_leg_recon references txn_legs (leg_id, ledger_id) — after txn_legs.
    INSERT INTO txn_leg_recon              SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_recon,              v_payload->'txn_leg_recon');
    -- realized_gains references txn_legs (sell_leg_id) + accounts + securities — after all three (mig 188).
    INSERT INTO realized_gains             SELECT * FROM jsonb_populate_recordset(NULL::realized_gains,             v_payload->'realized_gains');
    -- recurring_occurrence_exceptions references recurring_transactions — after it.
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    -- Override layers last.
    INSERT INTO txn_header_overrides       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_overrides,       v_payload->'txn_header_overrides');
    INSERT INTO txn_leg_overrides          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_overrides,          v_payload->'txn_leg_overrides');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');

    -- ----- 3. Rebuild materialised balances (one set-based pass, mig 188) -----
    -- txn_header_account_balances is the only derived table NOT captured in the
    -- payload (it is keyed on header+account and cheap to rebuild set-based).
    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);

    -- No FIFO recompute here (mig 188). holdings, lots AND realized_gains all
    -- come from the payload, so recompute_holdings_cost_basis would only spend
    -- ~27s overwriting them with the same values. See the header for why that is
    -- safe (schema-version guard) and what it means (restore reproduces the
    -- snapshot rather than re-deriving it).
END;
$$;
