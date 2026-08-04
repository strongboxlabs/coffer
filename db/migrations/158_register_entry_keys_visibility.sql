-- ADR-0072 D1: give register_entry_keys a visibility flag so a "Hidden" register
-- view can page soft-hidden rows (is_hidden = TRUE). Body is IDENTICAL to the
-- mig 109 definition except the hidden predicate:
--     rt.is_hidden = FALSE   ->   rt.is_hidden = p_hidden
-- The new 7th arg defaults FALSE, so every existing 6-arg call (the normal
-- visible register) is byte-for-byte unchanged; the Hidden view opts in by
-- passing TRUE.

DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_seq        BIGINT,
    p_direction         TEXT,
    p_limit             INTEGER,
    p_hidden            BOOLEAN DEFAULT FALSE
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
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER, BOOLEAN)
    TO coffer_app;
