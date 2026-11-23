using Dapper;
using Npgsql;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The memo and tag placement invariants over one ledger, read by the importer's tests
/// over their fixtures and by the full Demo's test over every row it holds, the rows its
/// examples add included.
/// </summary>
/// <remarks>
/// One source for both test projects (linked into the importer's), so a rule added for one
/// holds for the other: two copies of an invariant drift, and the copy that drifted is the
/// one nobody is looking at. It depends only on Npgsql and Dapper.
/// </remarks>
internal static class MemoPlacementInvariants
{
    /// <summary>
    /// One row per broken (transaction, rule): a one-posting transaction with notes, an
    /// actioned multi-posting event with a value on its investment side (its brokerage and
    /// that brokerage's holdings account), a blank stored memo. A transaction is named by
    /// its external id, else its id.
    /// </summary>
    public static async Task<List<string>> ViolationsAsync(NpgsqlConnection conn, Guid ledgerId) =>
        (await conn.QueryAsync<string>("""
            WITH shape AS (
                SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
                  FROM txn_legs l WHERE l.ledger_id = @L GROUP BY l.header_id
            )
            SELECT coalesce(h.external_id, h.id::text) || ': one-posting header notes'
              FROM txn_headers h JOIN shape s ON s.header_id = h.id
             WHERE h.ledger_id = @L AND s.postings = 1
               AND (h.memo IS NOT NULL OR EXISTS (SELECT 1 FROM txn_header_tags x WHERE x.header_id = h.id))
            UNION ALL
            SELECT coalesce(h.external_id, h.id::text) || ': investment side carries a value'
              FROM txn_headers h JOIN shape s ON s.header_id = h.id
              JOIN txn_legs l ON l.header_id = h.id
             WHERE h.ledger_id = @L AND h.action IS NOT NULL AND s.postings > 1
               AND (l.account_id = h.originating_account_id
                    OR l.account_id = (SELECT b.holdings_account_id FROM accounts b WHERE b.id = h.originating_account_id))
               AND (l.leg_memo IS NOT NULL OR EXISTS (SELECT 1 FROM txn_leg_tags x WHERE x.leg_id = l.id))
            UNION ALL
            SELECT coalesce(h.external_id, h.id::text) || ': blank memo'
              FROM txn_headers h
             WHERE h.ledger_id = @L
               AND (h.memo ~ @Blank
                    OR EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id AND l.leg_memo ~ @Blank));
            """, new { L = ledgerId, Blank = BlankPattern }).ConfigureAwait(false)).ToList();

    /// <summary>Empty or only .NET white space (<see cref="char.IsWhiteSpace(char)"/>), as a
    /// PostgreSQL regex: the control range and U+0085 written as the regex's own escapes, the
    /// other spaces as the characters themselves.</summary>
    internal const string BlankPattern =
        "^[\\u0009-\\u000D \\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$";
}
