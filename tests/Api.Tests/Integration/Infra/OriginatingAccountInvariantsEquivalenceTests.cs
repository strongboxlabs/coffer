using System.Globalization;
using System.Text;

using Npgsql;

using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The set-based originating-account check (<see cref="OriginatingAccountInvariants.ViolationsSql"/>)
/// names exactly the (header, clause) rows the correlated text it replaced named, on a
/// ledger that breaks every clause in every way the shapes allow.
/// </summary>
/// <remarks>
/// <para>The fixture is a grid, not a list of cases: every header shape below, each
/// owned in turn by every account of the ledger and by none. That covers each clause
/// passing and failing for each reason it can fail (no owner with legs, an owner on a
/// legless header, an owner on no leg, a holdings account or a category owning an
/// event, a brokerage on one posting only, the destination of an in-kind transfer, an
/// account on one line of a split), across legless headers, one-posting and
/// multi-posting headers, events of every family and both forms of in-kind transfer.
/// A second ledger holds the same grid, so a text that leaked across ledgers would
/// differ.</para>
/// <para>The same grid also holds migration 263's end assertion to the check, clause
/// by clause, so the upgrade and the import cannot drift apart.</para>
/// <para>The headers are written in SQL because most of them are states no writer
/// produces: they are the check's input domain, and a check is judged on the states
/// that break it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OriginatingAccountInvariantsEquivalenceTests : DatabaseTest
{
    public OriginatingAccountInvariantsEquivalenceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>
    /// REFERENCE ONLY: the correlated text the set-based one replaced, verbatim. It looks
    /// each header up in materialised sets once per header, which grows with the square
    /// of the ledger, so nothing but this test may run it.
    /// </summary>
    private const string ReplacedCorrelatedViolationsSql = """
        WITH shape AS (
            SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
              FROM txn_legs l
             WHERE l.ledger_id = @LedgerId
             GROUP BY l.header_id
        ), on_every AS (
            SELECT l.header_id, l.account_id
              FROM txn_legs l
              JOIN shape s ON s.header_id = l.header_id
             GROUP BY l.header_id, l.account_id, s.postings
            HAVING count(DISTINCT l.posting_index) = s.postings
        ), sources AS (
            SELECT x.header_id, x.account_id
              FROM public.transfer_shares_source_brokerages(@LedgerId) x
        )
        SELECT h.id AS header_id, v.clause
          FROM txn_headers h
          LEFT JOIN shape s ON s.header_id = h.id
         CROSS JOIN LATERAL (
                SELECT '(a)' AS clause
                 WHERE (h.originating_account_id IS NULL) = (s.header_id IS NOT NULL)
                    OR (h.originating_account_id IS NOT NULL AND NOT EXISTS (
                           SELECT 1 FROM txn_legs l
                            WHERE l.header_id = h.id AND l.account_id = h.originating_account_id))
                UNION ALL
                SELECT '(b)'
                 WHERE h.action IS NOT NULL AND h.action <> 'transfer_shares'
                   AND s.header_id IS NOT NULL
                   AND NOT EXISTS (
                           SELECT 1 FROM on_every e JOIN accounts a ON a.id = e.account_id
                            WHERE e.header_id = h.id AND e.account_id = h.originating_account_id
                              AND a.holdings_account_id IS NOT NULL)
                UNION ALL
                SELECT '(c)'
                 WHERE h.action = 'transfer_shares'
                   AND s.header_id IS NOT NULL
                   AND NOT EXISTS (
                           SELECT 1 FROM sources x
                            WHERE x.header_id = h.id AND x.account_id = h.originating_account_id)
                UNION ALL
                SELECT '(d)'
                 WHERE h.action IS NULL
                   AND s.postings > 1
                   AND EXISTS (SELECT 1 FROM on_every e WHERE e.header_id = h.id)
                   AND NOT EXISTS (SELECT 1 FROM on_every e
                                    WHERE e.header_id = h.id AND e.account_id = h.originating_account_id)
              ) v
         WHERE h.ledger_id = @LedgerId
        """;

    /// <summary>
    /// REFERENCE ONLY: migration 263's end-assertion counts as the correlated queries it
    /// replaced computed them, verbatim but for each <c>INTO</c> becoming a column of one
    /// row. Like the check above, it looks headers up per header, so nothing but this
    /// test may run it. It reads <c>mig263_sources</c>, which the test builds as 263
    /// does.
    /// </summary>
    private const string ReplacedUpgradeAssertionCountsSql = """
        SELECT
          (SELECT count(*)
             FROM txn_headers h
            WHERE (h.originating_account_id IS NULL
                   AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id))
               OR (h.originating_account_id IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM txn_legs l
                                    WHERE l.header_id = h.id
                                      AND l.account_id = h.originating_account_id))) AS a,
          (WITH shape AS (
               SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
                 FROM txn_legs l GROUP BY l.header_id
           ), on_every AS (
               SELECT l.header_id, l.account_id
                 FROM txn_legs l
                 JOIN shape s ON s.header_id = l.header_id
                GROUP BY l.header_id, l.account_id, s.postings
               HAVING count(DISTINCT l.posting_index) = s.postings
           )
           SELECT count(*)
             FROM txn_headers h
            WHERE h.action IS NOT NULL AND h.action <> 'transfer_shares'
              AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id)
              AND NOT EXISTS (SELECT 1
                                FROM on_every e
                                JOIN accounts a ON a.id = e.account_id
                               WHERE e.header_id = h.id
                                 AND e.account_id = h.originating_account_id
                                 AND a.holdings_account_id IS NOT NULL)) AS b,
          (SELECT count(*)
             FROM txn_headers h
            WHERE h.action = 'transfer_shares'
              AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id)
              AND NOT EXISTS (SELECT 1 FROM mig263_sources s
                               WHERE s.header_id = h.id AND s.account_id = h.originating_account_id)) AS c,
          (WITH shape AS (
               SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
                 FROM txn_legs l GROUP BY l.header_id
           ), on_every AS (
               SELECT l.header_id, l.account_id
                 FROM txn_legs l
                 JOIN shape s ON s.header_id = l.header_id
                WHERE s.postings > 1
                GROUP BY l.header_id, l.account_id, s.postings
               HAVING count(DISTINCT l.posting_index) = s.postings
           )
           SELECT count(*)
             FROM txn_headers h
            WHERE h.action IS NULL
              AND EXISTS (SELECT 1 FROM on_every e WHERE e.header_id = h.id)
              AND NOT EXISTS (SELECT 1 FROM on_every e
                               WHERE e.header_id = h.id AND e.account_id = h.originating_account_id)) AS d
        """;

    /// <summary>One leg: account key, posting, amount, whether it carries shares (and how many).</summary>
    private readonly record struct Leg(string Account, int Posting, decimal Amount, decimal? Shares = null, string? Role = null);

    private sealed record Shape(string Name, string? Action, Leg[] Legs);

    private static readonly string[] Accounts =
    [
        "BankA", "BankB", "CatX", "CatY", "CatInc",
        "HoldP", "HoldQ", "HoldShared", "BrokP", "BrokQ", "BrokR", "BrokS",
    ];

    private static Leg Sec(string account, int posting, decimal amount, decimal? shares = null) =>
        new(account, posting, amount, shares, "security");

    private static readonly Shape[] Shapes =
    [
        new("legless, no action", null, []),
        new("legless event", "buy", []),
        new("legless in-kind transfer", "transfer_shares", []),
        new("one-split to a category", null, [new("BankA", 0, -10m), new("CatX", 0, 10m)]),
        new("transfer between banks", null, [new("BankA", 0, -10m), new("BankB", 0, 10m)]),
        new("split, the bank on every line", null,
            [new("BankA", 0, -6m), new("CatX", 0, 6m), new("BankA", 1, -4m), new("CatY", 1, 4m)]),
        new("split, every line to one category", null,
            [new("BankB", 0, 3m), new("CatInc", 0, -3m), new("BankB", 1, 2m), new("CatInc", 1, -2m)]),
        new("split, no account on every line", null,
            [new("BankA", 0, 5m), new("CatX", 0, -5m), new("BankB", 1, -7m), new("CatY", 1, 7m)]),
        new("split over posting indexes with a gap", null,
            [new("BankA", 0, -6m), new("CatX", 0, 6m), new("BankA", 2, -4m), new("CatY", 2, 4m)]),
        new("split of three lines, one category on two", null,
            [new("BankA", 0, -1m), new("CatX", 0, 1m), new("BankA", 1, -2m), new("CatY", 1, 2m),
             new("BankA", 2, -3m), new("CatX", 2, 3m)]),
        new("buy", "buy", [Sec("HoldP", 0, 400m, 8m), Sec("BrokP", 0, -400m)]),
        new("buy_xfr with a fee, funded by a second brokerage", "buyx",
            [Sec("HoldP", 0, 1000m, 20m), Sec("BrokP", 0, -1000m),
             new("BrokP", 1, -10m, null, "fee"), new("CatX", 1, 10m, null, "fee"),
             new("BrokP", 2, 1010m, null, "transfer"), new("BrokQ", 2, -1010m, null, "transfer")]),
        new("event with no brokerage leg", "buyx", [Sec("HoldP", 0, 900m, 30m), Sec("CatX", 0, -900m)]),
        new("dividend", "dividend_cash",
            [new("BrokP", 0, 3m, null, "income"), new("CatInc", 0, -3m, null, "income")]),
        new("cash transfer between brokerages", "transfer",
            [new("BrokP", 0, 100m, null, "transfer"), new("BrokQ", 0, -100m, null, "transfer")]),
        new("event with each brokerage on one posting only", "misc",
            [new("BrokP", 0, 5m, null, "transfer"), new("CatX", 0, -5m, null, "transfer"),
             new("BrokQ", 1, -5m, null, "transfer"), new("CatY", 1, 5m, null, "transfer")]),
        new("in kind, Q to P, paired by posting", "transfer_shares",
            [Sec("HoldQ", 0, -500m, -10m), Sec("BrokQ", 0, 0m), Sec("HoldP", 1, 500m, 10m), Sec("BrokP", 1, 0m)]),
        new("in kind, Q to P, one posting", "transfer_shares",
            [Sec("HoldQ", 0, -500m, -10m), Sec("BrokQ", 0, 0m), Sec("HoldP", 0, 500m, 10m), Sec("BrokP", 0, 0m)]),
        new("in kind, S to R over one holdings account", "transfer_shares",
            [Sec("HoldShared", 0, -500m, -10m), Sec("BrokS", 0, 0m),
             Sec("HoldShared", 1, 500m, 10m), Sec("BrokR", 1, 0m)]),
        new("in kind, no posting pairs a brokerage with its holdings", "transfer_shares",
            [Sec("HoldQ", 0, -500m, -10m), Sec("HoldP", 0, 500m, 10m), Sec("BrokQ", 1, 0m), Sec("BrokP", 1, 0m)]),
        new("in kind, no shares leave", "transfer_shares",
            [Sec("HoldP", 0, 500m, 10m), Sec("BrokP", 0, 0m)]),
        new("in kind, shares leave two brokerages", "transfer_shares",
            [Sec("HoldQ", 0, -500m, -10m), Sec("BrokQ", 0, 0m), Sec("HoldP", 1, -300m, -5m), Sec("BrokP", 1, 0m)]),
        new("in kind, a source on two postings", "transfer_shares",
            [Sec("HoldQ", 0, -500m, -10m), Sec("BrokQ", 0, 0m), Sec("HoldQ", 1, -300m, -5m), Sec("BrokQ", 1, 0m),
             Sec("HoldP", 2, 800m, 15m), Sec("BrokP", 2, 0m)]),
    ];

    [Fact]
    public async Task The_set_based_check_names_the_rows_the_correlated_check_named()
    {
        var cs = Db.ServiceConnectionString;
        var a = await BuildGridAsync(cs);
        var b = await BuildGridAsync(cs);

        foreach (var (ledgerId, headers) in new[] { a, b })
        {
            var replaced = await RowsAsync(cs, ReplacedCorrelatedViolationsSql, ledgerId);
            var current = await RowsAsync(cs, OriginatingAccountInvariants.ViolationsSql, ledgerId);

            Assert.Equal(replaced, current);

            // Not vacuous: every clause breaks, some headers keep every clause, and no row
            // names a header of another ledger.
            foreach (var clause in new[] { "(a)", "(b)", "(c)", "(d)" })
                Assert.Contains(current, r => r.EndsWith(clause, StringComparison.Ordinal));
            var broken = current.Select(r => Guid.Parse(r[..36])).ToHashSet();
            Assert.Contains(headers, h => !broken.Contains(h));
            Assert.All(broken, h => Assert.Contains(h, headers));
        }
    }

    [Fact]
    public async Task The_plan_runs_no_subquery_per_header_and_the_replaced_one_did()
    {
        var cs = Db.ServiceConnectionString;
        var (ledgerId, _) = await BuildGridAsync(cs);

        Assert.Empty(await OriginatingAccountCheck.PerRowSubplansAsync(cs, ledgerId));
        // The plan check can fail: the text it replaced looks each header up per row.
        Assert.NotEmpty(await OriginatingAccountCheck.PerRowSubplansAsync(cs, ledgerId, ReplacedCorrelatedViolationsSql));
    }

    /// <summary>
    /// Migration 263's end assertion counts, clause by clause, the headers the check
    /// names, over the same grid: the upgrade and the import judge a ledger alike.
    /// </summary>
    /// <remarks>
    /// Run on a database of its own at head, by re-running 263 over two grids. The
    /// backfill before the assertion fills an owner wherever a header with legs has
    /// none, so the grids leave that one case out; every other header reaches the
    /// assertion as built, and the upgrade must refuse with the check's counts.
    /// </remarks>
    [Fact]
    public async Task The_upgrade_assertion_counts_each_clause_as_the_check_does()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig263-grid-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig263_invariants_grid");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageAll(work));

        var grids = new[] { await BuildGridAsync(cs, ownerlessWithLegs: false), await BuildGridAsync(cs, ownerlessWithLegs: false) };
        var rows = new List<string>();
        foreach (var (ledgerId, _) in grids)
            rows.AddRange(await RowsAsync(cs, OriginatingAccountInvariants.ViolationsSql, ledgerId));
        int Count(string clause) => rows.Count(r => r.EndsWith(clause, StringComparison.Ordinal));
        Assert.All(new[] { "(a)", "(b)", "(c)", "(d)" }, c => Assert.True(Count(c) > 0, $"no header breaks {c}"));

        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync();
            await using var forget = new NpgsqlCommand(
                "DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%263_originating_account_tag_name_key_and_saved_report_repoint.sql'",
                connection);
            Assert.Equal(1, await forget.ExecuteNonQueryAsync());
        }
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => UpgradeRehearsal.RunStaged(cs, work)));

        var message = string.Join(" || ", Chain(ex).Select(e => e.Message));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture,
            $"(a) not on a leg, or NULL with legs: {Count("(a)")}; (b) actioned event not owned by a brokerage "
            + $"on every posting: {Count("(b)")}; (c) transfer_shares not owned by its source brokerage: {Count("(c)")}; "
            + $"(d) multi-split not owned by an account on every line: {Count("(d)")}."), message, StringComparison.Ordinal);

        static IEnumerable<Exception> Chain(Exception e)
        {
            for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
        }
    }

    /// <summary>
    /// Migration 263's one-pass end assertion, run as the migration states it, counts
    /// each clause as the correlated queries it replaced did and as the check does, over
    /// grids that include the case a re-run of 263 cannot reach: a header with legs and
    /// no owner, which its backfill fills before the assertion runs.
    /// </summary>
    /// <remarks>
    /// On a database of its own at head, so the whole-table counts cover the grids
    /// alone. The assertion's text is read out of the migration file, between its
    /// <c>WITH per_account AS (</c> and <c>FROM facts f;</c>, with its <c>INTO</c>
    /// removed; a change to those markers fails here rather than testing a stale copy.
    /// </remarks>
    [Fact]
    public async Task The_upgrade_assertion_counts_as_the_queries_it_replaced_and_the_check()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig263-assert-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig263_assertion_side_by_side");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageAll(work));

        var grids = new[] { await BuildGridAsync(cs), await BuildGridAsync(cs) };
        var rows = new List<string>();
        foreach (var (ledgerId, _) in grids)
            rows.AddRange(await RowsAsync(cs, OriginatingAccountInvariants.ViolationsSql, ledgerId));
        long Count(string clause) => rows.Count(r => r.EndsWith(clause, StringComparison.Ordinal));
        var fromCheck = new[] { Count("(a)"), Count("(b)"), Count("(c)"), Count("(d)") };
        Assert.All(fromCheck, n => Assert.True(n > 0, "a clause no header breaks proves nothing"));

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using (var sources = new NpgsqlCommand("""
            CREATE TEMP TABLE mig263_sources AS
            SELECT s.header_id, s.account_id
              FROM public.ledgers g
             CROSS JOIN LATERAL public.transfer_shares_source_brokerages(g.id) s;
            """, connection))
            await sources.ExecuteNonQueryAsync();

        var replaced = await CountsAsync(connection, ReplacedUpgradeAssertionCountsSql);
        var current = await CountsAsync(connection, UpgradeAssertionCountsSql());

        Assert.Equal(replaced, current);
        Assert.Equal(fromCheck, current);

        static async Task<long[]> CountsAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return [reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)];
        }
    }

    /// <summary>The counting query of migration 263's end assertion, as the file states it.</summary>
    private static string UpgradeAssertionCountsSql()
    {
        var script = File.ReadAllText(Path.Combine(
            Coffer.Api.Migrations.MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            "263_originating_account_tag_name_key_and_saved_report_repoint.sql"));
        var block = script.IndexOf("DO $assert$", StringComparison.Ordinal);
        Assert.True(block >= 0, "263 has no DO $assert$ block");
        var start = script.IndexOf("WITH per_account AS (", block, StringComparison.Ordinal);
        const string end = "FROM facts f;";
        var stop = script.IndexOf(end, block, StringComparison.Ordinal);
        Assert.True(start > block && stop > start, "263's end assertion no longer has the shape this test reads");

        var counting = script[start..(stop + end.Length - 1)];
        var into = System.Text.RegularExpressions.Regex.Matches(counting, @"^\s*INTO v_a, v_b, v_c, v_d\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Single(into);
        return counting.Remove(into[0].Index, into[0].Length);
    }

    private static async Task<List<string>> RowsAsync(string cs, string sql, Guid ledgerId)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT v.header_id, v.clause FROM ({sql}) v", connection);
        command.Parameters.AddWithValue("LedgerId", ledgerId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetGuid(0)} {reader.GetString(1)}");
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>
    /// A new ledger holding every shape once per owner: every account, and none (left
    /// out on a header with legs when <paramref name="ownerlessWithLegs"/> is false).
    /// </summary>
    private static async Task<(Guid LedgerId, HashSet<Guid> Headers)> BuildGridAsync(
        string cs, bool ownerlessWithLegs = true)
    {
        var ledger = (LedgerId: Guid.NewGuid(), Name: $"Ledger {Guid.NewGuid():N}");
        var ids = Accounts.ToDictionary(k => k, _ => Guid.NewGuid());
        var security = Guid.NewGuid();
        var headers = new HashSet<Guid>();

        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"""
            INSERT INTO ledgers (id, name) VALUES ('{ledger.LedgerId}', '{ledger.Name}');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, is_system,
                                  holdings_account_id)
            VALUES ('{ids["BankA"]}', '{ledger.LedgerId}', 'Account A', 'bank', NULL, 'USD', FALSE, NULL),
                   ('{ids["BankB"]}', '{ledger.LedgerId}', 'Account B', 'bank', NULL, 'USD', FALSE, NULL),
                   ('{ids["CatX"]}', '{ledger.LedgerId}', 'Category X', 'category', 'expense', 'USD', FALSE, NULL),
                   ('{ids["CatY"]}', '{ledger.LedgerId}', 'Category Y', 'category', 'expense', 'USD', FALSE, NULL),
                   ('{ids["CatInc"]}', '{ledger.LedgerId}', 'Category Income', 'category', 'income', 'USD', FALSE, NULL),
                   ('{ids["HoldP"]}', '{ledger.LedgerId}', 'Holdings P', 'investment', NULL, 'USD', TRUE, NULL),
                   ('{ids["HoldQ"]}', '{ledger.LedgerId}', 'Holdings Q', 'investment', NULL, 'USD', TRUE, NULL),
                   ('{ids["HoldShared"]}', '{ledger.LedgerId}', 'Holdings R', 'investment', NULL, 'USD', TRUE, NULL);
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id)
            VALUES ('{ids["BrokP"]}', '{ledger.LedgerId}', 'Brokerage P', 'investment', 'USD', '{ids["HoldP"]}'),
                   ('{ids["BrokQ"]}', '{ledger.LedgerId}', 'Brokerage Q', 'investment', 'USD', '{ids["HoldQ"]}'),
                   ('{ids["BrokR"]}', '{ledger.LedgerId}', 'Brokerage R', 'investment', 'USD', '{ids["HoldShared"]}'),
                   ('{ids["BrokS"]}', '{ledger.LedgerId}', 'Brokerage S', 'investment', 'USD', '{ids["HoldShared"]}');
            INSERT INTO securities (id, ledger_id, name) VALUES ('{security}', '{ledger.LedgerId}', 'Security A');

            """);

        var owners = new string?[] { null }.Concat(Accounts).ToList();
        foreach (var shape in Shapes)
        {
            foreach (var owner in owners)
            {
                if (owner is null && shape.Legs.Length > 0 && !ownerlessWithLegs) continue;
                var header = Guid.NewGuid();
                headers.Add(header);
                var action = shape.Action is null ? "NULL" : $"'{shape.Action}'";
                var ownerSql = owner is null ? "NULL" : $"'{ids[owner]}'";
                sql.Append(CultureInfo.InvariantCulture, $"""
                    INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, created_at,
                                             action, originating_account_id)
                    VALUES ('{header}', '{ledger.LedgerId}', 'manual', TIMESTAMPTZ '2026-03-02 12:00:00+00',
                            TIMESTAMPTZ '2026-03-02 12:00:00+00', TIMESTAMPTZ '2026-03-02 12:00:00+00',
                            {action}, {ownerSql});

                    """);
                foreach (var leg in shape.Legs)
                {
                    var shares = leg.Shares is { } q
                        ? $"'{security}', {q.ToString(CultureInfo.InvariantCulture)}, 50"
                        : "NULL, NULL, NULL";
                    var role = leg.Role is null ? "NULL" : $"'{leg.Role}'";
                    sql.Append(CultureInfo.InvariantCulture, $"""
                        INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                                              security_id, quantity, unit_price, posting_role)
                        VALUES ('{Guid.NewGuid()}', '{header}', '{ledger.LedgerId}', '{ids[leg.Account]}',
                                {leg.Posting}, {leg.Amount.ToString(CultureInfo.InvariantCulture)},
                                {shares}, {role});

                        """);
                }
            }
        }

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql.ToString(), connection);
        await command.ExecuteNonQueryAsync();
        // Raw SQL reaches no writer: store the presentation as the upgrade's backfill does.
        await Coffer.Api.Migrations.LegNotesBackfill.RunAsync(cs, ledger.LedgerId, CancellationToken.None);
        return (ledger.LedgerId, headers);
    }
}
