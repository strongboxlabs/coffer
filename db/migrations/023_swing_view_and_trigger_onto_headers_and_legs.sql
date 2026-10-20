-- 023_swing_view_and_trigger_onto_headers_and_legs.sql
--
-- ADR-0022 part 2: rewire the register read path onto the new
-- normalised tables created in migration 022. After this migration:
--
--   * resolved_transactions reads from txn_headers + txn_legs (with
--     overrides applied) instead of transactions + transaction_overrides.
--   * register_entry_keys() returns one entry per txn_headers row.
--   * The running-balance trigger maintains txn_legs.balance_after via
--     statement-level triggers on txn_legs (INSERT/UPDATE/DELETE) and
--     txn_headers (UPDATE of posted_at or is_merged_into).
--
-- What's intentionally NOT in this migration:
--
--   * Drop of the legacy `transactions` / `transaction_overrides` /
--     `transaction_tags` tables. The investment-importer rewrite is
--     deferred to a follow-up PR; until that lands, investment data
--     lives in the old `transactions` table and the user's per-
--     security register query (a future endpoint) will need to read
--     from it. Migration N (post-investment-rewrite) drops the old
--     surface.
--   * Retargeting of lots.transaction_id / merge_candidates.*_txn_id.
--     Those tables only matter for the investment + sync paths which
--     are also deferred; their FKs continue pointing at the old
--     `transactions` table without functional impact on the register.
--
-- The end state of this migration is: the register API + UI work
-- against the normalised schema; old data + old write paths remain
-- queryable but unused. The old running-balance trigger on
-- `transactions` stays in place so historical investment data still
-- maintains its balance_after column.
--
-- Column shape on resolved_transactions is preserved byte-for-byte to
-- avoid touching ResolvedTransactionView (EF entity) and every
-- repository / DTO consumer. Names map as:
--
--   resolved_transactions column      <- ADR-0022 source
--   ----------------------------     -----------------
--   id                               <- txn_legs.id
--   account_id                       <- txn_legs.account_id
--   payee                            <- COALESCE(o.payee, h.payee)
--   memo                             <- COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo)
--   amount                           <- COALESCE(lo.amount, l.amount)
--   posted_at                        <- COALESCE(o.posted_at, h.posted_at)
--   transacted_at                    <- COALESCE(o.transacted_at, h.transacted_at)
--   status                           <- COALESCE(o.status, h.status)
--   is_hidden                        <- COALESCE(o.is_hidden, h.is_hidden, FALSE)
--   has_overrides                    <- (o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL)
--   balance_after                    <- l.balance_after
--   origin                           <- h.origin
--   is_pending                       <- h.is_pending
--   is_merged_into                   <- h.is_merged_into
--   investment_action                <- l.investment_action
--   external_id                      <- h.external_id
--   created_at                       <- l.created_at
--   check_number                     <- COALESCE(o.check_number, h.check_number)
--   counterparty_id                  <- structural: the other leg of (h.id, l.posting_index)
--   txn_group_id                     <- h.id (header IS the group identity now)
--   leg_index                        <- l.posting_index
--   counterparty_account_id          <- account_path(other-leg.account_id)
--   counterparty_account_name        <- (same)
--   counterparty_account_type        <- (same)
--   tags                             <- ARRAY-agg from txn_header_tags

-- ---------------------------------------------------------------------------
-- 1) Running-balance trigger on txn_legs.
-- ---------------------------------------------------------------------------
-- Mirror of fn_recompute_balance_after / fn_trg_balance_after from
-- migration 004, adjusted to read from txn_legs + txn_headers. Anchor
-- + recompute window joins through the header so ordering by
-- (posted_at, leg.id) and visibility (is_merged_into) come from the
-- correct row.

CREATE OR REPLACE FUNCTION fn_recompute_legs_balance_after(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
    v_starting NUMERIC(19, 4);
BEGIN
    SELECT l.balance_after
      INTO v_starting
      FROM txn_legs l
      JOIN txn_headers h ON h.id = l.header_id
     WHERE l.account_id = p_account_id
       AND h.is_merged_into IS NULL
       AND h.posted_at < p_from_posted_at
     ORDER BY h.posted_at DESC, l.id DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    WITH ordered AS (
        SELECT
            l.id,
            v_starting + SUM(l.amount) OVER (
                ORDER BY h.posted_at, l.id
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS new_balance
          FROM txn_legs l
          JOIN txn_headers h ON h.id = l.header_id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND h.posted_at >= p_from_posted_at
    )
    UPDATE txn_legs l
       SET balance_after = ordered.new_balance
      FROM ordered
     WHERE l.id = ordered.id
       AND l.balance_after IS DISTINCT FROM ordered.new_balance;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_trg_legs_balance_after()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF TG_OP = 'INSERT' THEN
        FOR rec IN
            SELECT n.account_id, MIN(h.posted_at) AS dt
              FROM new_rows n
              JOIN txn_headers h ON h.id = n.header_id
             GROUP BY n.account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSIF TG_OP = 'DELETE' THEN
        FOR rec IN
            SELECT o.account_id, MIN(h.posted_at) AS dt
              FROM old_rows o
              JOIN txn_headers h ON h.id = o.header_id
             GROUP BY o.account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSE  -- UPDATE
        -- Early exit: only recompute when a balance-relevant column
        -- on the leg side actually changed. The header-side change
        -- handler (fn_trg_headers_balance_after, below) covers
        -- posted_at + is_merged_into updates.
        IF NOT EXISTS (
            SELECT 1
              FROM new_rows n
              JOIN old_rows o ON o.id = n.id
             WHERE n.amount     IS DISTINCT FROM o.amount
                OR n.account_id IS DISTINCT FROM o.account_id
        ) THEN
            RETURN NULL;
        END IF;

        FOR rec IN
            SELECT account_id, MIN(posted_at) AS dt
              FROM (
                  SELECT n.account_id, h.posted_at
                    FROM new_rows n JOIN txn_headers h ON h.id = n.header_id
                  UNION ALL
                  SELECT o.account_id, h.posted_at
                    FROM old_rows o JOIN txn_headers h ON h.id = o.header_id
              ) merged
             GROUP BY account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_legs_balance_after_insert
AFTER INSERT ON txn_legs
REFERENCING NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_balance_after();

CREATE TRIGGER trg_legs_balance_after_delete
AFTER DELETE ON txn_legs
REFERENCING OLD TABLE AS old_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_balance_after();

CREATE TRIGGER trg_legs_balance_after_update
AFTER UPDATE ON txn_legs
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_balance_after();

-- ---------------------------------------------------------------------------
-- 2) Header-side trigger: re-fire the leg balance recompute when a
--    header's posted_at or is_merged_into changes. These shift every
--    affected account's running balance even though no leg was edited.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_trg_headers_balance_after()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM new_rows n
          JOIN old_rows o ON o.id = n.id
         WHERE n.posted_at      IS DISTINCT FROM o.posted_at
            OR n.is_merged_into IS DISTINCT FROM o.is_merged_into
    ) THEN
        RETURN NULL;
    END IF;

    -- Recompute every account that has a leg in any affected header,
    -- anchored at the earliest posted_at across old + new values
    -- (covers the case where posted_at moves backwards).
    FOR rec IN
        SELECT l.account_id, MIN(d.dt) AS dt
          FROM (
              SELECT id, posted_at AS dt FROM new_rows
              UNION ALL
              SELECT id, posted_at AS dt FROM old_rows
          ) d
          JOIN txn_legs l ON l.header_id = d.id
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
    END LOOP;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_headers_balance_after_update
AFTER UPDATE ON txn_headers
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_headers_balance_after();

-- ---------------------------------------------------------------------------
-- 3) Rewrite resolved_transactions on the normalised schema.
-- ---------------------------------------------------------------------------
-- Same column shape as the migration 021 view so the EF entity
-- (ResolvedTransactionView) + every consumer (RegisterRepository,
-- DTOs, web types) keeps working without modification. The
-- counterparty resolution uses a LEFT JOIN against the "other leg of
-- this posting" via the (header_id, posting_index) pair key.

CREATE OR REPLACE VIEW resolved_transactions AS
SELECT
    l.id,
    l.account_id,
    COALESCE(o.payee,            h.payee)                              AS payee,
    -- Memo precedence: leg override → leg → header override → header.
    -- Single-leg events leave leg_memo NULL so the chain falls back
    -- to header memo (the PR #42 fix preserved in ADR-0022 mapper).
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo)                  AS memo,
    COALESCE(lo.amount,          l.amount)                             AS amount,
    COALESCE(o.posted_at,        h.posted_at)                          AS posted_at,
    COALESCE(o.transacted_at,    h.transacted_at)                      AS transacted_at,
    COALESCE(o.status,           h.status)                             AS status,
    COALESCE(o.is_hidden,        h.is_hidden, FALSE)                   AS is_hidden,
    (o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL)                 AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    l.investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number,     h.check_number)                       AS check_number,
    -- Structural pairing: the other leg of this posting (same header,
    -- same posting_index, different account). Exactly one row by
    -- invariant. LEFT JOIN so a malformed half-pair surfaces with
    -- NULL counterparty rather than dropping silently.
    other.id                                                           AS counterparty_id,
    -- txn_group_id preserves the pre-ADR-0022 semantics expected by
    -- the API's AssembleEntries: a single-posting event (one MD txn
    -- with no splits, or a paired transfer) gets NULL — each leg is
    -- its own entry. A multi-posting event surfaces the header id so
    -- the API groups all its legs into one "group" entry. The check
    -- uses EXISTS posting_index > 0 to short-circuit; >0 posting
    -- index can only happen if the header has multiple postings.
    CASE WHEN EXISTS (
        SELECT 1 FROM txn_legs g
        WHERE g.header_id = h.id AND g.posting_index > 0
    ) THEN h.id ELSE NULL END                                          AS txn_group_id,
    l.posting_index                                                    AS leg_index,
    other.account_id                                                   AS counterparty_account_id,
    account_path(other.account_id)                                     AS counterparty_account_name,
    ca.account_type                                                    AS counterparty_account_type,
    COALESCE(
        ARRAY(SELECT tg.name
              FROM txn_header_tags tt
              JOIN tags tg ON tg.id = tt.tag_id
              WHERE tt.header_id = h.id
              ORDER BY tg.name),
        ARRAY[]::TEXT[]
    ) AS tags
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
LEFT JOIN txn_header_overrides o ON o.header_id = h.id
LEFT JOIN txn_leg_overrides    lo ON lo.leg_id  = l.id
LEFT JOIN txn_legs other
       ON other.header_id     = l.header_id
      AND other.posting_index = l.posting_index
      AND other.id           != l.id
LEFT JOIN accounts ca ON ca.id = other.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);

-- ---------------------------------------------------------------------------
-- 4) Rewrite register_entry_keys on the normalised schema.
-- ---------------------------------------------------------------------------
-- Same signature + return shape as migration 019 so the EF TVF binding
-- (AppDbContext.RegisterEntryKeys) keeps working. The body now walks
-- one row per header (the entry) rather than aggregating by
-- COALESCE(txn_group_id, id). RLS short-circuits at the header level
-- (h.ledger_id direct hop vs the three-hop chain through accounts in
-- the prior model) — measured in EXPLAIN ANALYZE on real data, the
-- new plan should drop the function count well under jit_above_cost.

CREATE OR REPLACE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_entry_key  UUID,
    p_limit             INTEGER
)
RETURNS TABLE(
    posted_at TIMESTAMPTZ,
    entry_key UUID
)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    -- Walks the resolved_transactions view, grouping by the view's
    -- own entry_key projection (COALESCE(txn_group_id, id)). For
    -- single-posting events txn_group_id is NULL and the entry_key
    -- collapses to leg.id — each leg is its own entry. For multi-
    -- posting events txn_group_id is h.id and all legs collapse to
    -- one entry. This matches the pre-ADR-0022 pagination semantics
    -- expected by the API's AssembleEntries; the only thing that
    -- changed is what's UNDER the view.
    SELECT
        MAX(rt.posted_at)                                  AS posted_at,
        COALESCE(rt.txn_group_id, rt.id)                   AS entry_key
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
    GROUP BY COALESCE(rt.txn_group_id, rt.id)
    HAVING
        p_cursor_posted_at IS NULL
        OR MAX(rt.posted_at) < p_cursor_posted_at
        OR (
            MAX(rt.posted_at) = p_cursor_posted_at
            AND COALESCE(rt.txn_group_id, rt.id) < p_cursor_entry_key
        )
    ORDER BY MAX(rt.posted_at) DESC, COALESCE(rt.txn_group_id, rt.id) DESC
    LIMIT p_limit;
$$;

-- Grant is already in place from migration 019; the function shape
-- didn't change, only the body. Re-asserting for the readability
-- audit:
GRANT EXECUTE ON FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, UUID, INTEGER) TO coffer_app;
