using System.Globalization;
using System.Text;
using System.Text.Json;

using Npgsql;

using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Money;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// The SQL minor-units function (migration 301, ADR-0114 D15) is a frozen copy of the
/// ISO 4217 data file the API and the SPA compile in, and gives the same places as
/// <see cref="CurrencyMinorUnits.PlacesOf"/> for every code.
/// </summary>
/// <remarks>
/// <para>The function is only a copy: a change to the data file needs a new migration
/// that issues it again, and this guard fails until one does. It reads the file from
/// disk, not through the API's embedded copy, so it compares the SQL with the file the
/// SPA ships too.</para>
/// <para>Every entry is probed one by one. A CASE that lists the 0- and 3-place codes
/// with ELSE 2 also answers 2 for a code it forgot to list, so a sample would pass a
/// function missing a 0-place code; and the 4-place fund units and the N.A. entries
/// must answer 2, which only a per-code probe sees.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MinorUnitsSqlGuardTests : DatabaseTest
{
    public MinorUnitsSqlGuardTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Every_code_in_the_data_file_gets_the_places_the_api_gives_it()
    {
        var file = ReadDataFile();
        // Floor: a parse that read nothing would make every check below vacuous.
        Assert.True(file.Count >= 150, $"read only {file.Count} code(s) from the data file");
        Assert.Equal(CurrencyMinorUnits.ListOne.Count, file.Count);

        var codes = file.Select(e => e.Code).ToArray();
        var sql = await PlacesAsync(codes);
        Assert.Equal(file.Count, sql.Count);

        var wrong = new List<string>();
        foreach (var (code, exponent) in file)
        {
            // The rule, stated on its own: the exponent when it is at most the ceiling,
            // else (an N.A. entry, CLF, UYW) the 2 a refused code keeps.
            var expected = exponent is { } e && e <= CurrencyMinorUnits.MaxPlaces ? e : 2;
            var api = CurrencyMinorUnits.PlacesOf(code);
            if (api != expected) wrong.Add($"{code}: PlacesOf gives {api}, the rule gives {expected}");
            if (sql[code] != expected) wrong.Add($"{code}: currency_minor_units gives {sql[code]}, the rule gives {expected}");
        }

        Assert.True(wrong.Count == 0,
            "The SQL minor-units function disagrees with the data file: " + string.Join("; ", wrong)
            + ". Issue the function again in a new migration with this body:\n" + Render(file));
    }

    [Fact]
    public async Task The_codes_an_account_can_no_longer_take_get_two_places()
    {
        var file = ReadDataFile();
        var refused = file.Where(e => e.Exponent is null or > CurrencyMinorUnits.MaxPlaces)
            .Select(e => e.Code).ToList();
        // The fund units with four places and the N.A. entries are all on the list.
        Assert.Contains("CLF", refused);
        Assert.Contains("UYW", refused);
        Assert.Contains(file, e => e.Exponent is null);

        var sql = await PlacesAsync(refused);
        Assert.All(refused, code => Assert.Equal(2, sql[code]));
    }

    [Fact]
    public async Task An_unknown_empty_or_null_code_gets_two_places_and_a_lower_case_one_matches_the_api()
    {
        await using var connection = Db.OpenServiceConnection();

        // QQQ is on no ISO list.
        Assert.False(CurrencyMinorUnits.ListOne.ContainsKey("QQQ"));
        Assert.Equal(2, await ScalarAsync(connection, "SELECT currency_minor_units('QQQ')"));
        Assert.Equal(2, await ScalarAsync(connection, "SELECT currency_minor_units('')"));
        // Not STRICT: NULL answers 2, never NULL. ROUND(x, NULL) would put NULL into a
        // NOT NULL realized_gains column.
        Assert.Equal(2, await ScalarAsync(connection, "SELECT currency_minor_units(NULL)"));

        // Stored codes are upper case; a lower-case one gets whatever the API's lookup
        // gives it, so the two never disagree on a stored value.
        foreach (var code in new[] { "jpy", "bhd", "usd" })
        {
            Assert.Equal(
                CurrencyMinorUnits.PlacesOf(code),
                await ScalarAsync(connection, $"SELECT currency_minor_units('{code}')"));
        }
    }

    [Fact]
    public async Task The_function_is_one_immutable_parallel_safe_overload_that_is_not_strict()
    {
        await using var connection = Db.OpenServiceConnection();
        await using var command = new NpgsqlCommand("""
            SELECT p.provolatile::text, p.proparallel::text, p.proisstrict,
                   pg_get_function_identity_arguments(p.oid), pg_get_function_result(p.oid)
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'public' AND p.proname = 'currency_minor_units'
            """, connection);
        var rows = new List<(string Volatile, string Parallel, bool Strict, string Args, string Result)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4)));
        }

        var row = Assert.Single(rows);
        Assert.Equal("i", row.Volatile);
        Assert.Equal("s", row.Parallel);
        Assert.False(row.Strict);
        Assert.Equal("p_code text", row.Args);
        Assert.Equal("integer", row.Result);

        // The request role calls it through the two realized-gains functions.
        Assert.Equal(true, await ScalarObjectAsync(connection,
            "SELECT has_function_privilege('coffer_app', 'currency_minor_units(text)', 'EXECUTE')"));
    }

    /// <summary>Every code of the data file and its exponent, null for an N.A. entry.</summary>
    private static List<(string Code, int? Exponent)> ReadDataFile()
    {
        var path = Path.Combine(RepoRoot(), "src", "Web", "src", "lib", "currencyMinorUnits.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("minorUnits").EnumerateObject()
            .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.Null ? (int?)null : p.Value.GetInt32()))
            .ToList();
    }

    /// <summary>
    /// The function's CASE body for <paramref name="file"/>: what a migration that issues
    /// it again writes, printed when the guard fails. In the test project, because
    /// src/Api writes no raw SQL.
    /// </summary>
    private static string Render(IReadOnlyList<(string Code, int? Exponent)> file)
    {
        static string List(IEnumerable<string> codes) =>
            string.Join(", ", codes.Order(StringComparer.Ordinal).Select(c => $"'{c}'"));

        var sb = new StringBuilder();
        sb.AppendLine("    SELECT CASE");
        sb.Append("        WHEN p_code IN (").Append(List(file.Where(e => e.Exponent == 0).Select(e => e.Code))).AppendLine(") THEN 0");
        sb.Append("        WHEN p_code IN (").Append(List(file.Where(e => e.Exponent == 3).Select(e => e.Code))).AppendLine(") THEN 3");
        sb.AppendLine("        ELSE 2");
        sb.Append("    END");
        return sb.ToString();
    }

    private async Task<Dictionary<string, int>> PlacesAsync(IReadOnlyCollection<string> codes)
    {
        await using var connection = Db.OpenServiceConnection();
        await using var command = new NpgsqlCommand(
            "SELECT c, currency_minor_units(c) FROM unnest(@codes) AS c", connection);
        command.Parameters.AddWithValue("codes", codes.ToArray());
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    private static async Task<int> ScalarAsync(NpgsqlConnection connection, string sql) =>
        Convert.ToInt32(await ScalarObjectAsync(connection, sql), CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarObjectAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
