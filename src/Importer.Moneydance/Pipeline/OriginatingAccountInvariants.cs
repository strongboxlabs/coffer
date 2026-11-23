namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// The originating-account invariants of migration 263, over one ledger, as a query
/// that names each header breaking one and the clause it breaks.
/// </summary>
/// <remarks>
/// <para>The clauses, each over every header of the ledger:</para>
/// <list type="bullet">
///   <item><description>(a) the value is the account of a leg of its header, and NULL
///   exactly when the header has no legs;</description></item>
///   <item><description>(b) an actioned event other than an in-kind transfer is owned by
///   a brokerage that carries a leg in every posting;</description></item>
///   <item><description>(c) an in-kind transfer is owned by its source brokerage, as
///   <c>transfer_shares_source_brokerages</c> defines it;</description></item>
///   <item><description>(d) an action-less header of several postings on which some
///   account carries a leg in every posting is owned by one of those
///   accounts.</description></item>
/// </list>
/// <para>The import check runs it after every import, and the API tests run the same
/// text after their writers and seeds, so the two cannot drift apart. The source
/// brokerage is the database's own definition, the one migration 263 backfills and
/// asserts with, so neither copy restates it.</para>
/// <para>Runs as the role that owns the schema (the import's, and the tests' service
/// connection): the source-brokerage function is not granted to the request role.</para>
/// <para>Set-based, because it runs over a whole ledger: each fact a clause reads is
/// aggregated once per header (or per header and account) and joined to the header
/// by key, and the clauses are then plain tests of the joined row. A clause written
/// as a subquery per header over a materialised set scans that set once per header,
/// which grows with the square of the ledger. The import pays that inside its own
/// transaction, on its import connection (whose command timeout is minutes, not
/// seconds), and twice when the check fails; a test seed pays it under Npgsql's
/// default timeout.</para>
/// <para>Two guards, each for one way the cost can turn quadratic. A subquery the
/// planner evaluates per row shows in the plan's shape at any size, so the stress
/// seed's invariant check refuses one on the smoke seed of the ordinary suite. A join
/// the planner chooses to run as a nested loop over an unindexed set is a cost-based
/// choice that appears only where row estimates are large and wrong, so no small
/// ledger can show it; the stress lane's cost test times the check at the default
/// stress scale against a fixed bound instead.</para>
/// </remarks>
public static class OriginatingAccountInvariants
{
    /// <summary>
    /// One row per (header, broken clause). Parameter <c>@LedgerId</c>; columns
    /// <c>header_id uuid</c> and <c>clause text</c>, the clause's letter in brackets.
    /// </summary>
    /// <remarks>
    /// Legs are read by their own <c>ledger_id</c>: the composite foreign key on
    /// <c>(header_id, ledger_id)</c> puts every leg in its header's ledger.
    /// </remarks>
    public const string ViolationsSql = """
        WITH per_account AS (
            -- Each account on a header's legs, and how many postings it has a leg in.
            SELECT l.header_id, l.account_id, count(DISTINCT l.posting_index) AS postings_on
              FROM txn_legs l
             WHERE l.ledger_id = @LedgerId
             GROUP BY l.header_id, l.account_id
        ), shape AS (
            -- Each header with legs, and its posting count.
            SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
              FROM txn_legs l
             WHERE l.ledger_id = @LedgerId
             GROUP BY l.header_id
        ), widest AS (
            -- The most postings one account of the header has a leg in: the posting
            -- count exactly when some account carries a leg in every posting.
            SELECT p.header_id, max(p.postings_on) AS most_on
              FROM per_account p
             GROUP BY p.header_id
        ), sources AS (
            SELECT DISTINCT x.header_id, x.account_id
              FROM public.transfer_shares_source_brokerages(@LedgerId) x
        ), facts AS (
            -- One row per header: its shape, and where its owner stands in it.
            SELECT h.id AS header_id,
                   h.action,
                   h.originating_account_id IS NOT NULL         AS has_owner,
                   s.header_id IS NOT NULL                      AS has_legs,
                   COALESCE(s.postings, 0)                      AS postings,
                   COALESCE(w.most_on = s.postings, FALSE)      AS some_on_every,
                   o.header_id IS NOT NULL                      AS owner_on_leg,
                   COALESCE(o.postings_on = s.postings, FALSE)  AS owner_on_every,
                   a.holdings_account_id IS NOT NULL            AS owner_is_brokerage,
                   x.header_id IS NOT NULL                      AS owner_is_source
              FROM txn_headers h
              LEFT JOIN shape s       ON s.header_id = h.id
              LEFT JOIN widest w      ON w.header_id = h.id
              LEFT JOIN per_account o ON o.header_id = h.id AND o.account_id = h.originating_account_id
              LEFT JOIN accounts a    ON a.id = h.originating_account_id
              LEFT JOIN sources x     ON x.header_id = h.id AND x.account_id = h.originating_account_id
             WHERE h.ledger_id = @LedgerId
        )
        SELECT f.header_id, v.clause
          FROM facts f
         CROSS JOIN LATERAL (VALUES
                ('(a)', f.has_owner <> f.has_legs
                        OR (f.has_owner AND NOT f.owner_on_leg)),
                ('(b)', f.action IS NOT NULL AND f.action <> 'transfer_shares' AND f.has_legs
                        AND NOT (f.owner_on_every AND f.owner_is_brokerage)),
                ('(c)', f.action IS NOT NULL AND f.action = 'transfer_shares' AND f.has_legs
                        AND NOT f.owner_is_source),
                ('(d)', f.action IS NULL AND f.postings > 1 AND f.some_on_every
                        AND NOT f.owner_on_every)
              ) AS v(clause, broken)
         WHERE v.broken
        """;
}
