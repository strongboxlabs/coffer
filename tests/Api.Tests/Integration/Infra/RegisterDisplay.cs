using System.Globalization;

using Npgsql;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// What every register of a ledger shows for each transaction's memo and tags, read
/// two ways: as the release before 264 read it (its view and its renderers), and as
/// the stored presentation 264 fills. The upgrade tests compare the two, row by row.
/// </summary>
/// <remarks>
/// <para><b>One key per register row.</b> <c>"{transaction} {account} {slot}"</c>: the
/// transaction by its external id (else its id), the account by its external id (else
/// its name), and the slot: <c>row</c> for a one-posting transaction's row, <c>parent</c>
/// and <c>line{n}</c> for a split's own register, <c>line{n}</c> for a line seen from
/// another account, <c>event</c> for a brokerage's one row per investment transaction.
/// Holdings accounts have no register and are left out; templates are not in a register.</para>
/// <para><b>Before 264</b> (read on a database at 263 or earlier): a row showed its own
/// leg's memo, else the header's (<c>resolved_transactions.memo</c>), and the header's
/// tags; a split's own register showed the header's memo and tags on the parent and each
/// line's own memo, no tags, on its line (the old bank row renderer); a line seen from
/// another account showed its row's memo and the header's tags; a brokerage showed one
/// event row with its first leg's row memo and the header's tags (the old investment
/// projection). A split's own register was the one with a leg in every posting.</para>
/// <para><b>After 264</b>: the stored presentation of each leg (<c>notes_memo</c>, the
/// shown tag rows); a split's parent shows the transaction's memo and tags
/// (<c>notes_header_memo</c>, the transaction-level tag rows); a split's own register is
/// its originating account's.</para>
/// </remarks>
public static class RegisterDisplay
{
    private sealed record Leg(
        string Header, string Account, int Posting, int Postings, bool OwnsEveryPosting, bool IsBrokerage,
        string? Memo, string? LegMemo, string? HeaderMemo, string Tags, string? Kind, string? NotesHeaderMemo,
        string TransactionTags);

    /// <summary>The release before 264's read: needs the view's <c>memo</c> and <c>tags</c>, which 266 drops.</summary>
    public static async Task<SortedDictionary<string, string>> BeforeAsync(string cs, Guid ledgerId)
    {
        var legs = await ReadAsync(cs, """
            SELECT COALESCE(h.external_id, h.id::text), COALESCE(a.external_id, a.name), r.leg_index,
                   r.header_total_postings, r.account_postings_on_header = r.header_total_postings,
                   a.holdings_account_id IS NOT NULL,
                   r.memo, r.leg_memo, r.header_memo,
                   array_to_string(ARRAY(SELECT x FROM unnest(r.tags) x ORDER BY x COLLATE "C"), ','),
                   NULL::text, NULL::text, ''
              FROM resolved_transactions r
              JOIN txn_headers h ON h.id = r.header_id
              JOIN accounts a ON a.id = r.account_id
             WHERE h.ledger_id = @L
               AND NOT EXISTS (SELECT 1 FROM accounts b WHERE b.holdings_account_id = a.id)
            """, ledgerId);
        var shown = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in legs.GroupBy(l => (l.Header, l.Account)))
        {
            var first = g.OrderBy(l => l.Posting).First();
            if (first.IsBrokerage)
                shown[$"{g.Key.Header} {g.Key.Account} event"] = Value(first.Memo, first.Tags);
            else if (first.Postings == 1)
                shown[$"{g.Key.Header} {g.Key.Account} row"] = Value(first.Memo, first.Tags);
            else if (first.OwnsEveryPosting)
            {
                shown[$"{g.Key.Header} {g.Key.Account} parent"] = Value(first.HeaderMemo, first.Tags);
                foreach (var l in g) shown[$"{g.Key.Header} {g.Key.Account} line{l.Posting}"] = Value(l.LegMemo, "");
            }
            else
                foreach (var l in g) shown[$"{g.Key.Header} {g.Key.Account} line{l.Posting}"] = Value(l.Memo, l.Tags);
        }
        return shown;
    }

    /// <summary>The stored presentation's read, on a database at 264 or later.</summary>
    public static async Task<SortedDictionary<string, string>> AfterAsync(string cs, Guid ledgerId)
    {
        var legs = await ReadAsync(cs, """
            SELECT COALESCE(h.external_id, h.id::text), COALESCE(a.external_id, a.name), l.posting_index,
                   (SELECT count(DISTINCT x.posting_index)::int FROM txn_legs x WHERE x.header_id = h.id),
                   FALSE, a.holdings_account_id IS NOT NULL,
                   l.notes_memo, NULL::text, NULL::text,
                   COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                               FROM txn_leg_note_tags n JOIN tags t ON t.id = n.tag_id
                              WHERE n.leg_id = l.id AND n.shown), ''),
                   l.notes_kind, l.notes_header_memo,
                   COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                               FROM txn_leg_note_tags n JOIN tags t ON t.id = n.tag_id
                              WHERE n.leg_id = l.id AND n.transaction_level), '')
              FROM txn_legs l
              JOIN live_txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE h.ledger_id = @L
               AND NOT EXISTS (SELECT 1 FROM accounts b WHERE b.holdings_account_id = a.id)
            """, ledgerId);
        var shown = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in legs.GroupBy(l => (l.Header, l.Account)))
        {
            var first = g.OrderBy(l => l.Posting).First();
            if (first.IsBrokerage)
                shown[$"{g.Key.Header} {g.Key.Account} event"] = Value(first.Memo, first.Tags);
            else if (first.Postings == 1)
                shown[$"{g.Key.Header} {g.Key.Account} row"] = Value(first.Memo, first.Tags);
            else if (first.Kind == "SplitSide")
            {
                shown[$"{g.Key.Header} {g.Key.Account} parent"] = Value(first.NotesHeaderMemo, first.TransactionTags);
                foreach (var l in g) shown[$"{g.Key.Header} {g.Key.Account} line{l.Posting}"] = Value(l.Memo, l.Tags);
            }
            else
                foreach (var l in g) shown[$"{g.Key.Header} {g.Key.Account} line{l.Posting}"] = Value(l.Memo, l.Tags);
        }
        return shown;
    }

    /// <summary>Every row whose display differs, one line each: <c>key: before → after</c>.</summary>
    public static SortedSet<string> Differences(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in before.Keys.Union(after.Keys))
        {
            var b = before.GetValueOrDefault(key, "(no row)");
            var a = after.GetValueOrDefault(key, "(no row)");
            if (!string.Equals(b, a, StringComparison.Ordinal)) lines.Add($"{key}: {b} → {a}");
        }
        return lines;
    }

    private static string Value(string? memo, string tags) =>
        $"{(memo is null ? "—" : "'" + memo + "'")} [{tags}]";

    private static async Task<List<Leg>> ReadAsync(string cs, string sql, Guid ledgerId)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("L", ledgerId);
        await using var reader = await command.ExecuteReaderAsync();
        var legs = new List<Leg>();
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (await reader.ReadAsync())
            legs.Add(new Leg(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetBoolean(4), reader.GetBoolean(5),
                Text(6), Text(7), Text(8), reader.GetString(9), Text(10), Text(11), reader.GetString(12)));
        return legs;
    }
}
