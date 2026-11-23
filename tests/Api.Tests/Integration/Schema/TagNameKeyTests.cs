using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// <c>tag_name_key</c> (migration 263), the one definition of "the same tag name", and
/// the database facts it depends on.
/// </summary>
/// <remarks>
/// <para><b>The collation is part of the rule.</b> lower() follows the database
/// collation, so a database created under another one would fold names differently —
/// silently, with every lookup still "working". The pin below makes that a red test
/// instead. The vectors record what the key IS under the shipped collation, the cases
/// where folding rules differ (dotted capital I, sharp s, final sigma) included.</para>
///
/// <para><b>The trim is .NET's.</b> Writers store <c>string.Trim()</c>'s result, so the
/// key must strip exactly <c>char.IsWhiteSpace</c>'s set — no more (a zero-width space
/// is not white space to .NET) and no less (a no-break or ideographic space is). Checked
/// over every UTF-16 code unit, not a sample.</para>
///
/// <para>Vectors spell non-ASCII characters as <c>&lt;hhhh&gt;</c> code points, so the
/// source shows what each one is (several are invisible, two are line breaks).</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class TagNameKeyTests : DatabaseTest
{
    public TagNameKeyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // The postgres:16 image's initdb default, which the shipped compose file does not
    // override (POSTGRES_INITDB_ARGS sets only the auth methods).
    private const string ShippedCollate = "en_US.utf8";
    private const string ShippedCtype = "en_US.utf8";
    private const string ShippedProvider = "c";   // libc

    [Fact]
    public async Task The_database_collation_is_the_shipped_one()
    {
        var row = await RowAsync(Db.ServiceConnectionString, """
            SELECT datcollate, datctype, datlocprovider::text
              FROM pg_database WHERE datname = current_database()
            """);
        Assert.Equal((ShippedCollate, ShippedCtype, ShippedProvider),
            ((string)row[0], (string)row[1], (string)row[2]));
    }

    public static TheoryData<string, string> Vectors => new()
    {
        { "Work", "work" },
        { "WORK TRIP", "work trip" },
        { "  Work  ", "work" },
        { "<0009>Work<000A>", "work" },
        { "<00A0>Work<00A0>", "work" },
        { "<3000>Work<3000>", "work" },
        { "<2028>Work<2029><0085>", "work" },
        // Inner white space is part of the name.
        { "Wo<00A0>rk  Trip", "wo<00A0>rk  trip" },
        // Not white space to .NET, so not trimmed.
        { "<200B>Work<FEFF>", "<200B>work<FEFF>" },
        // Folding cases, as the shipped collation answers them.
        { "<0130>stanbul", "istanbul" },
        { "STRASSE", "strasse" },
        { "Stra<00DF>e", "stra<00DF>e" },
        { "<1E9E>", "<00DF>" },
        { "<039F><0394><039F><03A3>", "<03BF><03B4><03BF><03C3>" },
        { "", "" },
        { " <0009><3000> ", "" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Each_vector_has_its_recorded_key(string name, string expected)
    {
        var key = await ScalarAsync(Db.ServiceConnectionString, "SELECT tag_name_key(@n)", U(name));
        Assert.Equal(expected, Spell((string)key!));
    }

    [Fact]
    public async Task Null_has_no_key()
    {
        Assert.Null(await ScalarAsync(Db.ServiceConnectionString, "SELECT tag_name_key(NULL)", null));
    }

    [Fact]
    public async Task The_trim_strips_exactly_dotnet_white_space()
    {
        // Every UTF-16 code unit a text value can hold (no NUL, no lone surrogate).
        var units = Enumerable.Range(1, 0xFFFF)
            .Where(c => c < 0xD800 || c > 0xDFFF)
            .Select(c => (char)c)
            .ToArray();
        var probes = units.Select(c => $"{c}x{c}").ToArray();

        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT k.key = 'x' FROM tag_name_keys(@p) WITH ORDINALITY AS k(name, key, ord) ORDER BY k.ord",
            connection);
        command.Parameters.AddWithValue("p", probes);
        var trimmed = new List<bool>(probes.Length);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) trimmed.Add(reader.GetBoolean(0));

        Assert.Equal(units.Length, trimmed.Count);
        var disagreements = units
            .Select((c, i) => (c, sql: trimmed[i], dotnet: char.IsWhiteSpace(c)))
            .Where(x => x.sql != x.dotnet)
            .Select(x => $"U+{(int)x.c:X4}: SQL trims={x.sql}, char.IsWhiteSpace={x.dotnet}")
            .ToList();
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
        // The set is not empty, or the check above would pass on a key that trims nothing.
        Assert.Equal(units.Count(char.IsWhiteSpace), trimmed.Count(t => t));
        Assert.True(trimmed.Count(t => t) > 20);
    }

    [Fact]
    public async Task The_functions_are_pure_inlinable_and_granted_to_the_request_role()
    {
        var rows = await RowsAsync(Db.SuperuserConnectionString, """
            SELECT p.proname, p.provolatile::text, p.proisstrict, p.proparallel::text,
                   p.proconfig IS NULL,
                   -- Granted by name, not through PUBLIC's default: 254 stopped default
                   -- grants to coffer_app, and an explicit one survives a later REVOKE
                   -- FROM PUBLIC.
                   EXISTS (SELECT 1 FROM aclexplode(p.proacl) a
                            WHERE a.grantee = 'coffer_app'::regrole AND a.privilege_type = 'EXECUTE'),
                   p.prosrc
              FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace
               AND p.proname IN ('tag_name_key', 'tag_name_keys')
             ORDER BY p.proname
            """);
        Assert.Equal(2, rows.Count);
        foreach (var r in rows)
        {
            var name = (string)r[0];
            Assert.True((string)r[1] == "i", $"{name} must be IMMUTABLE");
            Assert.True((string)r[3] == "s", $"{name} must be PARALLEL SAFE");
            // A SET clause stops the planner inlining a SQL function.
            Assert.True((bool)r[4], $"{name} must carry no SET clause");
            Assert.True((bool)r[5], $"coffer_app must be able to EXECUTE {name}");
        }
        // STRICT on the scalar (NULL has no key); NOT on the set-returning one, which
        // the planner would otherwise refuse to inline.
        Assert.True((bool)rows[0][2], "tag_name_key must be STRICT");
        Assert.False((bool)rows[1][2], "tag_name_keys must not be STRICT");

        // The key's body calls pg_catalog functions only, so a restricted search_path
        // (the one maintenance operations use on an index expression) means the same.
        var calls = FunctionCall().Matches((string)rows[0][6]).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.StartsWith("pg_catalog.", c, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"([\w.]+)\s*\(")]
    private static partial Regex FunctionCall();

    /// <summary>
    /// The round-trip spike's outcome (migration 263): a request's names are keyed by
    /// <c>tag_name_keys</c> and joined to the ledger's tags on the key, in ONE statement
    /// composed in LINQ. An EF primitive collection cannot do this — EF evaluates a
    /// projection of a parameter array on the client, where the key function throws —
    /// so the row source is a function.
    /// </summary>
    [Fact]
    public async Task A_request_is_keyed_and_matched_in_one_statement()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var work = await ledger.AddBareTagAsync("Work");
        string[] names = [U("work<00A0>"), "Travel", "WORK"];

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var query =
            from k in db.TagNameKeys(names)
            from t in db.Tags
                .Where(t => t.LedgerId == ledger.LedgerId && AppDbContext.TagNameKey(t.Name) == k.Key)
                .DefaultIfEmpty()
            select new { k.Name, k.Key, TagId = (Guid?)t!.Id };

        var sql = query.ToQueryString();
        Assert.Contains("FROM tag_name_keys(@names)", sql, StringComparison.Ordinal);
        Assert.Contains("tag_name_key(t0.name) = t.key", sql, StringComparison.Ordinal);
        // PropagatesNullability: NOT NULL names need no NULL arm in the comparison.
        Assert.DoesNotContain("IS NULL", sql, StringComparison.Ordinal);

        var rows = (await query.ToListAsync())
            .OrderBy(r => Array.IndexOf(names, r.Name))
            .Select(r => (r.Name, r.Key, r.TagId))
            .ToList();
        Assert.Equal([(names[0], "work", (Guid?)work), ("Travel", "travel", null), ("WORK", "work", work)], rows);
    }

    [Fact]
    public async Task The_key_list_function_is_inlined_into_the_caller()
    {
        // Inlined, it is a scan of unnest() the planner can see through; a STRICT or
        // SET-carrying definition would show up as an opaque function scan instead.
        var plan = await PlanAsync(Db.ServiceConnectionString,
            "EXPLAIN (VERBOSE, COSTS OFF) SELECT * FROM tag_name_keys(ARRAY['a', 'B'])");
        Assert.DoesNotContain("tag_name_keys", plan, StringComparison.Ordinal);
        Assert.Contains("unnest", plan, StringComparison.Ordinal);
    }

    // ----- helpers -----

    [GeneratedRegex("<([0-9A-F]{4})>")]
    private static partial Regex CodePoint();

    /// <summary>"<c>&lt;00A0&gt;</c>" to the character it names.</summary>
    private static string U(string spelled) =>
        CodePoint().Replace(spelled, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    /// <summary>The inverse of <see cref="U"/>, so a failure prints what it compared.</summary>
    private static string Spell(string value) =>
        string.Concat(value.Select(c => c < 0x20 || c > 0x7E
            ? $"<{(int)c:X4}>"
            : c.ToString()));

    private static async Task<object?> ScalarAsync(string cs, string sql, string? parameter)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@n", StringComparison.Ordinal))
            command.Parameters.AddWithValue("n", (object?)parameter ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<object[]> RowAsync(string cs, string sql) => (await RowsAsync(cs, sql)).Single();

    private static async Task<List<object[]>> RowsAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string> PlanAsync(string cs, string sql) =>
        string.Join("\n", (await RowsAsync(cs, sql)).Select(r => (string)r[0]));
}
