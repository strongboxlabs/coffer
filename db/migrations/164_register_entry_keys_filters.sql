-- 164 — register filtering + search (server-side).
--
-- The register is a sliding window (useWindowedRegister, ~1000 rows, keyset
-- cursor). Client-side filtering only sees the loaded window and trips the
-- edge-load-suppression (#322), so any filter that must match across full
-- history has to push into register_entry_keys so the cursor walks ONLY
-- matching entries. This extends mig 158 with filter predicates + folds the
-- status tabs (previously client-side) server-side.
--
-- Predicates go in the WHERE (per-leg) BEFORE the GROUP BY. entry_key is
-- computed from view-precomputed counts (account_postings_on_header /
-- header_total_postings), which are constant across an entry's legs and
-- unaffected by dropping non-matching legs — so a per-leg filter yields the
-- correct "entry appears iff ANY of its legs match" semantics while the keyset
-- walk stays intact. Entry-constant fields (posted_at, header net amount,
-- payee, status, is_hidden) filter identically whether read per-leg or per-entry.
--
-- New args all default NULL/FALSE ⇒ a no-op filter, so the plain register is
-- byte-for-byte unchanged. Status: 'cleared'/'uncleared'/'scheduled'/
-- 'needs_review' map to resolved_transactions predicates mirroring the SPA's
-- resolveRowStatus precedence (scheduled = posted_at > today; pending rows only
-- show under 'all'). 'all' and 'hidden' pass p_status NULL (hidden via p_hidden).
-- p_today is the caller's LOCAL calendar date (the SPA computes "scheduled" off
-- the user's local date, not the server's UTC date); falls back to CURRENT_DATE.

DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER, BOOLEAN);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_seq        BIGINT,
    p_direction         TEXT,
    p_limit             INTEGER,
    p_hidden            BOOLEAN DEFAULT FALSE,
    p_search            TEXT    DEFAULT NULL,
    p_date_from         DATE    DEFAULT NULL,
    p_date_to           DATE    DEFAULT NULL,
    p_amount_min        NUMERIC DEFAULT NULL,
    p_amount_max        NUMERIC DEFAULT NULL,
    p_security_id       UUID    DEFAULT NULL,
    p_tag               TEXT    DEFAULT NULL,
    p_category_id       UUID    DEFAULT NULL,
    p_status            TEXT    DEFAULT NULL,
    p_today             DATE    DEFAULT NULL
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
        WHERE rt.is_hidden = p_hidden
          AND rt.is_merged_into IS NULL
          AND (p_account_id IS NULL OR rt.account_id = p_account_id)
          AND (
              p_account_id IS NOT NULL
              OR EXISTS (
                  SELECT 1 FROM accounts a
                  WHERE a.id = rt.account_id AND a.ledger_id = p_ledger_id
              )
          )
          -- Filters (mig 164). Each is a no-op when its arg is NULL.
          AND (p_date_from IS NULL OR rt.posted_at::date >= p_date_from)
          AND (p_date_to   IS NULL OR rt.posted_at::date <= p_date_to)
          AND (p_amount_min IS NULL
               OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) >= p_amount_min)
          AND (p_amount_max IS NULL
               OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) <= p_amount_max)
          AND (p_security_id IS NULL OR rt.security_id = p_security_id)
          AND (p_category_id IS NULL OR rt.counterparty_account_id = p_category_id)
          AND (p_tag IS NULL OR p_tag = ANY(rt.tags))
          AND (p_search IS NULL OR (
                  rt.payee ILIKE '%' || p_search || '%'
               OR rt.memo ILIKE '%' || p_search || '%'
               OR rt.check_number ILIKE '%' || p_search || '%'
               OR rt.counterparty_account_name ILIKE '%' || p_search || '%'
               OR EXISTS (SELECT 1 FROM unnest(rt.tags) tg WHERE tg ILIKE '%' || p_search || '%')
          ))
          AND (
                  p_status IS NULL
               OR (p_status = 'needs_review' AND rt.needs_review = TRUE)
               OR (p_status = 'scheduled'
                     AND rt.posted_at::date > COALESCE(p_today, CURRENT_DATE))
               OR (p_status = 'cleared'
                     AND rt.status = 'cleared'
                     AND rt.is_pending = FALSE
                     AND rt.posted_at::date <= COALESCE(p_today, CURRENT_DATE))
               OR (p_status = 'uncleared'
                     AND rt.status = 'uncleared'
                     AND rt.is_pending = FALSE
                     AND rt.posted_at::date <= COALESCE(p_today, CURRENT_DATE))
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
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER, BOOLEAN,
        TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE)
    TO coffer_app;
