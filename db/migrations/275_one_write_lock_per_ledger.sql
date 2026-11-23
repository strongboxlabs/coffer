-- =============================================================================
-- 275 — one write lock per ledger
-- =============================================================================
--
-- WHY
-- ---
-- fn_recompute_balances_for_account (migration 230) deletes an account's stored running
-- balances from its anchor on and inserts them again from the walk.
-- recompute_holdings_cost_basis (migration 257, the body of 227) deletes a position's
-- realized gains and inserts them again, updates its lots and holding, and creates the
-- holding when the position has none. Every save runs them in its own transaction
-- (LegDerivedRecomputeInterceptor, HoldingsRecomputeInterceptor), as do the bulk writers,
-- the edits that recompute explicitly and the consistency repairs. Two of them over one
-- account or one position at once, a save's and a repair's or two saves', collide unless
-- the later waits for the earlier to commit:
--
--   * when the later one meets rows the earlier, still uncommitted, has deleted, it waits
--     at its delete; once the earlier commits, those rows are gone, and the later inserts
--     its own copies over the ones the earlier committed: a unique violation
--     (txn_header_account_balances_pkey, uq_realized_gains_sell_leg) that fails it, a save
--     with a 500 and nothing saved;
--   * when a balances run's anchor lies past every committed row, it waits for nothing,
--     reads its seed and its walk without the earlier one's transaction, and stores a
--     running balance that leaves it out, which only the consistency check finds.
--
-- A lock taken when the run starts is too late. A writer row-locks and writes before it
-- recomputes (a delete's cascade, a merge's FOR UPDATE, the first part of an edit saved in
-- two parts), so it would wait for that lock while holding rows the run that holds it
-- waits on: a deadlock. And which rows a writer will recompute is not known before its
-- first statement on every path (a merge reads them under its row locks; a save's touched
-- lines are read after the save), while its ledger always is. One lock per ledger, which
-- every writer that can recompute takes before it locks or writes anything, leaves no
-- such wait.
--
-- WHAT CHANGES
-- ------------
--   * fn_ledger_write_lock (new): the ledger write lock, transaction-scoped (THE LEDGER
--     WRITE LOCK below);
--   * fn_ledger_write_session_lock / fn_ledger_write_session_unlock (new): the same lock
--     held by a session, for snapshot restore (THE SESSION FORM below);
--   * fn_recompute_balances_for_account and recompute_holdings_cost_basis keep their
--     signatures, return types and results, and each takes its ledger's write lock before
--     it reads anything it writes from: balances, the account's ledger, before the seed is
--     read; holdings, the ledger named or the account's, before the holding is created and
--     before any walk is read. In a transaction that took the lock first (every writer
--     that can recompute) that does nothing. A statement of its own (the balances,
--     holdings and realized-gains repairs run one statement per account or position)
--     takes it for that statement. A transaction that wrote without it is refused, which
--     is what makes a writer that misses the lock fail rather than race. A holdings call
--     that names neither a ledger nor an account (migrations 227 and 229 make one) takes
--     each holding's ledger in turn, which outside a migration's session is refused at
--     the second ledger;
--   * snapshot_ledger_lock (migration 247) is re-issued: it refuses a session that holds
--     the same ledger's write lock (THE ORDER AMONG ADVISORY LOCKS below).
--
-- A run that finds the lock held waits until the holder commits or rolls back, at most
-- the wait below. Every caller runs READ COMMITTED, where each statement after the wait
-- takes a new snapshot, so the run then reads what the holder committed and rewrites from
-- it: both succeed and the stored figures are what a fresh walk derives.
--
-- WHAT TAKES TURNS
-- ----------------
-- Every transaction that writes to a ledger and can recompute its balances or holdings
-- takes the lock first, and so does every consistency repair: the balances, holdings and
-- realized-gains repairs one statement at a time, through the two functions above. Writes
-- to one ledger therefore run one after another, even when they share no account. Two
-- reminder fires of one ledger take this lock before migration 218's slot lock, so they
-- take turns even on different slots, which supersedes that migration's reason for keying
-- its lock on the slot (different slots firing at once). Two trade-price re-derivations
-- of one ledger by such writers take turns here before migration 274's step 1, which
-- supersedes that migration's "re-derivations do not wait for each other" for them. Reads,
-- snapshot creates (which take migration 247's lock alone) and writes to other ledgers do
-- not wait for it.
--
-- THE LOCK KIND
-- -------------
-- An advisory lock on a namespaced key hashed with hashtextextended, as migration 247
-- serialises snapshot creates per ledger. Here it is the transaction-scoped form: 247
-- needed a session lock because its lock had to come before a REPEATABLE READ
-- transaction's snapshot, and the writers run READ COMMITTED, where a lock taken first is
-- followed by fresh reads; a transaction lock is also released however the transaction
-- ends. Snapshot restore, which is REPEATABLE READ, takes the session form.
--
-- Not a row lock on the ledger: every row of the ledger references it, so a row lock
-- strong enough to exclude writers would make every insert wait, and it would join
-- migration 264's and 274's row-lock orders. An advisory key conflicts with nothing but
-- the same key. A collision of two ledgers' hashes only makes their writers take turns.
--
-- THE LEDGER WRITE LOCK
-- ---------------------
-- fn_ledger_write_lock(ledger, wait_ms) takes a transaction-scoped advisory lock on
-- 'coffer.ledger-write:<ledger>', hashed with hashtextextended as above, held until the
-- transaction ends. A transaction that writes to a ledger and can recompute its balances
-- or holdings takes it as its first statement (LedgerWriteLock in the API opens the
-- transaction and takes it), so two such transactions over one ledger run one after the
-- other, and one that waits for it holds nothing in the ledger while it waits.
--
-- It refuses rather than trusting every caller, with SQLSTATE 25000
-- (invalid_transaction_state) for the first three and 22023 (invalid_parameter_value) for
-- the last:
--
--   * a transaction that has already written or row-locked anything and does not hold it
--     (txid_current_if_assigned() is not null): the lock would come after rows another
--     writer may be waiting on. A row lock assigns the transaction an id as a write does,
--     so this covers migration 264's and 274's fn_lock_* and the trade-price FOR SHARE.
--     Two transactions are exempt. The one that created the ledger row: its row version
--     is this transaction's (xmin) and its created_at is this transaction's start (the
--     column's default), which an update of an older ledger's row leaves at that ledger's
--     creation. The Moneydance import creates the ledger it fills, which no other session
--     sees until it commits. And a migration's: MigrationRunner opens its sessions with
--     coffer.migration_session = on, and migrations run before the API serves, with
--     nothing to take turns with, so there the function does nothing. No other path sets
--     that setting;
--   * any isolation level but READ COMMITTED, as migration 264's fn_lock_* refuse it: a
--     writer reads, after the wait, what the previous holder committed, which only a READ
--     COMMITTED statement's fresh snapshot sees;
--   * a session that holds another advisory lock (THE ORDER AMONG ADVISORY LOCKS below);
--   * a null ledger, or a wait that is not positive.
--
-- It does nothing when this transaction already holds the lock, or when this session
-- holds the session form. A transaction-local setting, coffer.ledger_write_held, lists the
-- ledgers this transaction took it for (set_config(..., true): it ends with the
-- transaction, and a savepoint rolled back takes it away with the lock), so a second call
-- returns without reading pg_locks; holding one ledger's lock never stands for another's.
--
-- THE WAIT
-- --------
-- The acquire runs under lock_timeout = wait_ms, set transaction-locally and restored as
-- soon as the lock is granted, so the transaction's later row-lock waits keep the
-- caller's setting. A wait past it is refused with SQLSTATE LW001, which the API answers
-- as a 422 business refusal (ledger-write-busy): nothing was written, and a retry once the
-- other write has committed succeeds. The default, 15 seconds, is half of Npgsql's
-- default 30-second command timeout, which no connection string the API runs with
-- overrides and which its request and job contexts keep unless an operation sets a longer
-- one on its own context (snapshot capture and restore do): a statement that waits here is
-- refused before its command timeout cancels it, and a statement that takes the lock and
-- then rewrites in the same statement (a repair's call of the two functions above, which
-- take the default) keeps the other half for the rewrite. The API passes half of its
-- context's own command timeout, so a context that runs longer commands (snapshot
-- restore's) waits longer.
--
-- THE ORDER AMONG ADVISORY LOCKS
-- ------------------------------
-- The ledger write lock is the first lock of the transaction: before the reminder slot
-- lock (migration 218), then migration 274's step 0, migration 264's row and foreign-key
-- locks, migration 274's steps 1 to 3, and the stored-notes recompute's header locks
-- (LegNotes, fn_lock_txn_headers of migration 264). A
-- transaction that waits for it holds nothing in the ledger, and two transactions that
-- hold it are never in the ledger at once, so no wait between two of them closes a cycle,
-- however many steps either recomputes in. The refusal above covers the row locks. The
-- transaction id cannot show an advisory lock, so the function reads this session's
-- granted advisory locks in pg_locks and refuses when it holds any but one: the same
-- ledger's snapshot-create lock (migration 247), a session lock taken before a snapshot
-- capture begins. pg_locks does not say whether a lock is a session's or a transaction's,
-- so that exception is by key. A transaction therefore holds the write lock of one ledger
-- at most.
--
-- A cycle made only of row locks, with a transaction that does not take this lock,
-- remains what migrations 264 and 274 order, under the limits they state.
--
-- snapshot_ledger_lock refuses a session that holds the same ledger's write lock, in
-- either form, so the two session locks are only ever taken in one order: the
-- snapshot-create lock, then the ledger write lock. No path takes both: a snapshot capture
-- takes only the first, a restore only the second.
--
-- THE SESSION FORM
-- ----------------
-- Snapshot restore (LedgerSnapshotsRepository.RestoreAsync) rewrites the ledger in a
-- REPEATABLE READ transaction and rebuilds its balances (fn_recompute_balances_for_ledger).
-- A lock taken inside that transaction would come after its snapshot, the reason migration
-- 247 gives for its session lock, so restore takes fn_ledger_write_session_lock before its
-- transaction begins and fn_ledger_write_session_unlock after it ends, on the one
-- connection it holds open across both. The key is the transaction form's, so a restore
-- and a writer of the ledger take turns, and while this session holds it
-- fn_ledger_write_lock does nothing, at any isolation level. It refuses after a write or
-- row lock, and while this session holds another advisory lock but the ledger's
-- snapshot-create lock, and it waits as the transaction form does.
--
-- EXISTING INSTALLS
-- -----------------
-- No row is written. A running balance or a position left stale by an earlier overlap
-- is what the consistency check reports, and its repair rewrites it.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION public.fn_recompute_balances_for_account(p_account_id uuid, p_from_posted_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    -- The ledger write lock (mig 275), before anything written from is read: nothing for a
    -- writer that took it first, this statement's for a statement of its own, refused in a
    -- transaction that wrote without it. A writer in flight is waited for, and everything
    -- below reads what it committed.
    PERFORM 1 FROM fn_ledger_write_lock(v_ledger_id);

    -- Seed: the last stored balance strictly before the anchor, else the
    -- account's opening balance.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN live_txn_headers h ON h.id = thab.header_id
     WHERE thab.account_id = p_account_id
       AND h.posted_at < p_from_posted_at
     ORDER BY h.posted_at DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    DELETE FROM txn_header_account_balances thab
     USING live_txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    SELECT w.header_id, p_account_id, v_ledger_id, w.balance_after, w.net_amount
      FROM balance_walk(NULL, p_account_id, p_from_posted_at, v_starting) w;
END;
$function$;

COMMENT ON FUNCTION fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'ADR-0034 balance writer for one account from an anchor. Seeds from the last stored '
    'balance before the anchor (else opening_balance, else 0), then replaces the window '
    'with what balance_walk derives. Since mig 211 it shares that one walk with the '
    'whole-ledger recompute, so the two cannot disagree. Since mig 275 it takes the '
    'account''s ledger write lock (fn_ledger_write_lock) before it reads the seed: nothing '
    'for a writer that took it first, this statement''s for a statement of its own (the '
    'balances repair), refused in a transaction that wrote without it.';

CREATE OR REPLACE FUNCTION public.recompute_holdings_cost_basis(p_ledger_id uuid DEFAULT NULL::uuid, p_account_id uuid DEFAULT NULL::uuid, p_security_id uuid DEFAULT NULL::uuid)
 RETURNS integer
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_holding RECORD;
    v_walk    RECORD;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
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

        IF COALESCE(array_length(v_walk.o_gains, 1), 0) > 0 THEN
            INSERT INTO realized_gains (
                ledger_id, account_id, security_id, sell_leg_id,
                sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
            SELECT v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                   g.sell_leg_id, g.sold_at,
                   ROUND(g.quantity, 12),
                   ROUND(g.proceeds, 2),
                   ROUND(g.cost_basis_sold, 2),
                   ROUND(g.realized_gain, 2),
                   ROUND(g.proceeds_lt, 2),
                   ROUND(g.cost_basis_sold_lt, 2),
                   ROUND(g.realized_gain_lt, 2)
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
    'Bounds what it stores at each column''s OWN scale (money 2dp per mig 182, shares '
    '12dp). Money is rounded once, at its own scale: rounding to 4dp first would round '
    'twice against these NUMERIC(19,2) columns and could shift a disposal by a cent. '
    'Since mig 275 it takes the ledger write lock (fn_ledger_write_lock) of the account''s '
    'or the named ledger before it reads or creates a holding: nothing for a writer that '
    'took it first, this statement''s for a statement of its own (the holdings and '
    'realized-gains repairs), refused in a transaction that wrote without it. A call naming '
    'neither ledger nor account takes each holding''s ledger in turn.';

-- ---------------------------------------------------------------------------
-- The ledger write lock (THE LEDGER WRITE LOCK, THE WAIT and THE ORDER AMONG ADVISORY
-- LOCKS in the header).
-- ---------------------------------------------------------------------------
CREATE FUNCTION fn_ledger_write_lock(p_ledger_id uuid, p_wait_ms integer DEFAULT 15000)
RETURNS TABLE (locked boolean)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_held     text := current_setting('coffer.ledger_write_held', true);
    v_key      bigint;
    v_snapshot bigint;
    v_keys     bigint[];
    v_txid     bigint;
    v_prior    text;
BEGIN
    IF p_ledger_id IS NULL OR p_wait_ms IS NULL OR p_wait_ms <= 0 THEN
        RAISE EXCEPTION
            'fn_ledger_write_lock needs a ledger and a positive wait in milliseconds, not (%, %)',
            p_ledger_id, p_wait_ms
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- A migration's session (MigrationRunner): nothing runs beside it.
    IF current_setting('coffer.migration_session', true) = 'on' THEN
        RETURN QUERY SELECT true;
        RETURN;
    END IF;

    -- Taken by this transaction already.
    IF p_ledger_id::text = ANY (string_to_array(COALESCE(v_held, ''), ',')) THEN
        RETURN QUERY SELECT true;
        RETURN;
    END IF;

    -- This session's granted advisory locks, as the bigint keys they were taken on (a
    -- two-integer key reads as NULL here, and is never one of ours).
    v_key      := hashtextextended('coffer.ledger-write:' || p_ledger_id::text, 0);
    v_snapshot := hashtextextended('coffer.snapshot-create:' || p_ledger_id::text, 0);
    SELECT COALESCE(array_agg(CASE WHEN l.objsubid = 1
                                   THEN (l.classid::bigint << 32) | l.objid::bigint END), '{}')
      INTO v_keys
      FROM pg_locks l
     WHERE l.locktype = 'advisory'
       AND l.pid = pg_backend_pid()
       AND l.granted;

    -- Held by this session in the session form (restore), or by this transaction.
    IF v_key = ANY (v_keys) THEN
        RETURN QUERY SELECT true;
        RETURN;
    END IF;

    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_ledger_write_lock must run in a READ COMMITTED transaction, not %: the writer '
            'reads after the wait what the previous holder committed, which only READ '
            'COMMITTED sees', current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;

    -- Written or row-locked already, unless this transaction created the ledger.
    v_txid := txid_current_if_assigned();
    IF v_txid IS NOT NULL AND NOT EXISTS (
        SELECT 1
          FROM ledgers l
         WHERE l.id = p_ledger_id
           AND l.xmin::text::bigint = v_txid % 4294967296
           AND l.created_at = transaction_timestamp())
    THEN
        RAISE EXCEPTION
            'fn_ledger_write_lock(%) was called after this transaction wrote or locked a row: '
            'the ledger write lock comes before every write and row lock of a transaction '
            '(migration 275)', p_ledger_id
            USING ERRCODE = 'invalid_transaction_state';
    END IF;

    -- Any other advisory lock but the ledger's snapshot-create lock: this one comes first.
    IF EXISTS (SELECT 1 FROM unnest(v_keys) k WHERE k IS DISTINCT FROM v_snapshot) THEN
        RAISE EXCEPTION
            'fn_ledger_write_lock(%) was called while this session holds another advisory '
            'lock: the ledger write lock comes before every other advisory lock (migration 275)',
            p_ledger_id
            USING ERRCODE = 'invalid_transaction_state';
    END IF;

    -- The bounded wait. lock_timeout is the caller's again once the lock is granted.
    v_prior := current_setting('lock_timeout');
    BEGIN
        PERFORM set_config('lock_timeout', p_wait_ms::text, true);
        PERFORM pg_advisory_xact_lock(v_key);
    EXCEPTION WHEN lock_not_available THEN
        RAISE EXCEPTION 'ledger % is being written by another transaction (waited % ms)',
            p_ledger_id, p_wait_ms
            USING ERRCODE = 'LW001',
                  HINT = 'Nothing was written; a retry once the other write has committed succeeds.';
    END;
    PERFORM set_config('lock_timeout', v_prior, true);

    PERFORM set_config('coffer.ledger_write_held',
                       concat_ws(',', NULLIF(v_held, ''), p_ledger_id::text), true);
    RETURN QUERY SELECT true;
END;
$$;

COMMENT ON FUNCTION fn_ledger_write_lock(uuid, integer) IS
    'Mig 275. The ledger write lock: a transaction-scoped advisory lock on '
    '''coffer.ledger-write:<ledger>'', taken as the first statement of a transaction that '
    'writes to the ledger and can recompute its balances or holdings. Refuses a transaction '
    'that has written or row-locked without it (unless it created the ledger row, or its '
    'session is a migration''s, where it does nothing), any isolation level but READ '
    'COMMITTED, and a session holding another advisory lock but the ledger''s '
    'snapshot-create lock. Does nothing when the transaction or the session already holds '
    'it. Waits at most p_wait_ms (default 15000, half of the API''s 30 s command timeout), '
    'then refuses with SQLSTATE LW001.';

GRANT EXECUTE ON FUNCTION fn_ledger_write_lock(uuid, integer) TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- The session form, for snapshot restore (THE SESSION FORM in the header).
-- ---------------------------------------------------------------------------
CREATE FUNCTION fn_ledger_write_session_lock(p_ledger_id uuid, p_wait_ms integer DEFAULT 15000)
RETURNS TABLE (locked boolean)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_key      bigint;
    v_snapshot bigint;
    v_prior    text;
BEGIN
    IF p_ledger_id IS NULL OR p_wait_ms IS NULL OR p_wait_ms <= 0 THEN
        RAISE EXCEPTION
            'fn_ledger_write_session_lock needs a ledger and a positive wait in milliseconds, '
            'not (%, %)', p_ledger_id, p_wait_ms
            USING ERRCODE = 'invalid_parameter_value';
    END IF;
    IF txid_current_if_assigned() IS NOT NULL THEN
        RAISE EXCEPTION
            'fn_ledger_write_session_lock(%) was called after this transaction wrote or locked '
            'a row: the ledger write lock comes before every write and row lock (migration 275)',
            p_ledger_id
            USING ERRCODE = 'invalid_transaction_state';
    END IF;

    v_key      := hashtextextended('coffer.ledger-write:' || p_ledger_id::text, 0);
    v_snapshot := hashtextextended('coffer.snapshot-create:' || p_ledger_id::text, 0);
    IF EXISTS (
        SELECT 1
          FROM pg_locks l
         WHERE l.locktype = 'advisory'
           AND l.pid = pg_backend_pid()
           AND l.granted
           AND (l.objsubid <> 1 OR ((l.classid::bigint << 32) | l.objid::bigint) <> v_snapshot))
    THEN
        RAISE EXCEPTION
            'fn_ledger_write_session_lock(%) was called while this session holds another '
            'advisory lock: the ledger write lock comes before every other advisory lock '
            '(migration 275)', p_ledger_id
            USING ERRCODE = 'invalid_transaction_state';
    END IF;

    v_prior := current_setting('lock_timeout');
    BEGIN
        PERFORM set_config('lock_timeout', p_wait_ms::text, true);
        PERFORM pg_advisory_lock(v_key);
    EXCEPTION WHEN lock_not_available THEN
        RAISE EXCEPTION 'ledger % is being written by another transaction (waited % ms)',
            p_ledger_id, p_wait_ms
            USING ERRCODE = 'LW001',
                  HINT = 'Nothing was written; a retry once the other write has committed succeeds.';
    END;
    PERFORM set_config('lock_timeout', v_prior, true);
    RETURN QUERY SELECT true;
END;
$$;

COMMENT ON FUNCTION fn_ledger_write_session_lock(uuid, integer) IS
    'Mig 275. The ledger write lock held by this session until '
    'fn_ledger_write_session_unlock releases it, for a writer whose transaction is REPEATABLE '
    'READ (snapshot restore), taken before that transaction begins. Same key as '
    'fn_ledger_write_lock, which does nothing while this session holds it. Refuses after a '
    'write or row lock and while the session holds another advisory lock but the ledger''s '
    'snapshot-create lock. Waits at most p_wait_ms, then refuses with SQLSTATE LW001.';

GRANT EXECUTE ON FUNCTION fn_ledger_write_session_lock(uuid, integer) TO coffer_app, coffer_service;

CREATE FUNCTION fn_ledger_write_session_unlock(p_ledger_id uuid)
RETURNS TABLE (unlocked boolean)
LANGUAGE sql
VOLATILE
AS $$
    SELECT pg_advisory_unlock(hashtextextended('coffer.ledger-write:' || p_ledger_id::text, 0));
$$;

COMMENT ON FUNCTION fn_ledger_write_session_unlock(uuid) IS
    'Mig 275. Releases the session form of the ledger write lock '
    '(fn_ledger_write_session_lock). Returns whether this session held it.';

GRANT EXECUTE ON FUNCTION fn_ledger_write_session_unlock(uuid) TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- Migration 247's snapshot-create lock, refusing a session that holds the same ledger's
-- write lock: the snapshot-create lock comes first (THE ORDER AMONG ADVISORY LOCKS).
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION snapshot_ledger_lock(p_ledger_id uuid)
RETURNS TABLE (locked boolean)
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM pg_locks l
         WHERE l.locktype = 'advisory'
           AND l.pid = pg_backend_pid()
           AND l.granted
           AND l.objsubid = 1
           AND ((l.classid::bigint << 32) | l.objid::bigint)
               = hashtextextended('coffer.ledger-write:' || p_ledger_id::text, 0))
    THEN
        RAISE EXCEPTION
            'snapshot_ledger_lock(%) was called while this session holds the ledger''s write '
            'lock: the snapshot-create lock comes first (migration 275)', p_ledger_id
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    PERFORM pg_advisory_lock(hashtextextended('coffer.snapshot-create:' || p_ledger_id::text, 0));
    RETURN QUERY SELECT true;
END;
$$;

COMMENT ON FUNCTION snapshot_ledger_lock(uuid) IS
    'Mig 247, re-issued by mig 275. A session advisory lock on '
    '''coffer.snapshot-create:<ledger>'' serialising snapshot creates of one ledger, taken '
    'before the capture transaction begins. Refuses a session that holds the same ledger''s '
    'write lock: the snapshot-create lock is taken first.';

COMMIT;
