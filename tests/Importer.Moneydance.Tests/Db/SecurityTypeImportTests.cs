using Dapper;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// A security's vehicle type and class come from the type-<c>s</c> security account,
/// where Moneydance writes <c>sec_type</c>, <c>sec_subtype</c> and <c>sec_exchange</c>
/// (ADR-0016, amendment of 2026-11-18). Reading them from the <c>curr</c> item, as the
/// importer once did, left every real export's securities unclassified.
/// </summary>
/// <remarks>
/// Run through the whole import, per ticker, because the check that reports an
/// unrecognised code is part of what the person importing sees. The export puts a
/// decoy <c>sec_type</c> on two <c>curr</c> items that disagrees with their accounts,
/// so reading the curr again fails both on the value and on the unclassified one.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class SecurityTypeImportTests : DatabaseTest
{
    public SecurityTypeImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>
    /// One security per row of the code table, an unknown code, one with no code, and
    /// one held in three accounts: the first carries only an exchange, the second a
    /// full set, which wins whole, and a third disagrees with it and loses.
    /// </summary>
    private const string Export = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","isbase":"1","dec":"2"},
            {"obj_type":"curr","id":"c-cd","currid":"^CDX","name":"Sec CD","type":"s","ticker":"CDX","dec":"2"},
            {"obj_type":"curr","id":"c-bnd","currid":"^BND","name":"Sec Bond","type":"s","ticker":"BNDX","dec":"3",
             "sec_type":"3"},
            {"obj_type":"curr","id":"c-mf","currid":"^MF","name":"Sec Fund","type":"s","ticker":"MFX","dec":"4"},
            {"obj_type":"curr","id":"c-mm","currid":"^MM","name":"Sec Money Market","type":"s","ticker":"MMX","dec":"4"},
            {"obj_type":"curr","id":"c-fetf","currid":"^FE","name":"Sec Fund ETF","type":"s","ticker":"FETF","dec":"4"},
            {"obj_type":"curr","id":"c-stk","currid":"^STK","name":"Sec Stock","type":"s","ticker":"STKX","dec":"4"},
            {"obj_type":"curr","id":"c-setf","currid":"^SE","name":"Sec Stock ETF","type":"s","ticker":"SETF","dec":"4"},
            {"obj_type":"curr","id":"c-opt","currid":"^OPT","name":"Sec Option","type":"s","ticker":"OPTX","dec":"4"},
            {"obj_type":"curr","id":"c-odd","currid":"^ODD","name":"Sec Odd","type":"s","ticker":"ODDX","dec":"4"},
            {"obj_type":"curr","id":"c-none","currid":"^NON","name":"Sec Untyped","type":"s","ticker":"NONX","dec":"4",
             "sec_type":"3","sec_exchange":"CURR"},
            {"obj_type":"curr","id":"c-two","currid":"^TWO","name":"Sec Twice Held","type":"s","ticker":"TWOX","dec":"4"},

            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-ira","type":"v","name":"Retirement","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"s-cd","type":"s","name":"Sec CD","parentid":"a-brk","currid":"c-cd","sec_type":"0"},
            {"obj_type":"acct","id":"s-bnd","type":"s","name":"Sec Bond","parentid":"a-brk","currid":"c-bnd",
             "sec_type":"1","sec_subtype":"Income","sec_exchange":"NYSE"},
            {"obj_type":"acct","id":"s-mf","type":"s","name":"Sec Fund","parentid":"a-brk","currid":"c-mf",
             "sec_type":"2","sec_subtype":"Balanced / Targeted"},
            {"obj_type":"acct","id":"s-mm","type":"s","name":"Sec Money Market","parentid":"a-brk","currid":"c-mm",
             "sec_type":"2","sec_subtype":"Money Market"},
            {"obj_type":"acct","id":"s-fetf","type":"s","name":"Sec Fund ETF","parentid":"a-brk","currid":"c-fetf",
             "sec_type":"2","sec_subtype":"Exchange Traded Fund","sec_exchange":"NYSE"},
            {"obj_type":"acct","id":"s-stk","type":"s","name":"Sec Stock","parentid":"a-brk","currid":"c-stk",
             "sec_type":"3","sec_subtype":"U.S. Stocks","sec_exchange":"NASDAQ"},
            {"obj_type":"acct","id":"s-setf","type":"s","name":"Sec Stock ETF","parentid":"a-brk","currid":"c-setf",
             "sec_type":"3","sec_subtype":"Exchange Traded Fund"},
            {"obj_type":"acct","id":"s-opt","type":"s","name":"Sec Option","parentid":"a-brk","currid":"c-opt","sec_type":"4"},
            {"obj_type":"acct","id":"s-odd","type":"s","name":"Sec Odd","parentid":"a-brk","currid":"c-odd","sec_type":"7"},
            {"obj_type":"acct","id":"s-none","type":"s","name":"Sec Untyped","parentid":"a-brk","currid":"c-none"},
            {"obj_type":"acct","id":"s-two-a","type":"s","name":"Sec Twice Held","parentid":"a-brk","currid":"c-two",
             "sec_type":"","sec_exchange":"EXCH-A"},
            {"obj_type":"acct","id":"s-two-b","type":"s","name":"Sec Twice Held","parentid":"a-ira","currid":"c-two",
             "sec_type":"2","sec_subtype":"Exchange Traded Fund","sec_exchange":"NYSE"},
            {"obj_type":"acct","id":"s-two-c","type":"s","name":"Sec Twice Held","parentid":"a-ira","currid":"c-two",
             "sec_type":"3","sec_subtype":"U.S. Stocks","sec_exchange":"NASDAQ"}
          ]
        }
        """;

    [Fact]
    public async Task Each_security_is_classified_from_its_security_account()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, Export);

        var rows = (await conn.QueryAsync<(string Ticker, string? AssetClass, string? VehicleType,
                string? Source, string? Exchange)>(
            """
            SELECT ticker, asset_class, vehicle_type, classification_source, exchange
              FROM securities WHERE ledger_id = @L ORDER BY ticker;
            """, new { L = result.LedgerId }))
            .ToDictionary(r => r.Ticker);

        Assert.Equal(11, rows.Count);
        Assert.Equal(("cash", "cd"), (rows["CDX"].AssetClass, rows["CDX"].VehicleType));
        Assert.Equal(("fixed_income", "bond"), (rows["BNDX"].AssetClass, rows["BNDX"].VehicleType));
        Assert.Equal(((string?)null, "mutual_fund"), (rows["MFX"].AssetClass, rows["MFX"].VehicleType));
        Assert.Equal(("cash", "money_market"), (rows["MMX"].AssetClass, rows["MMX"].VehicleType));
        Assert.Equal(((string?)null, "etf"), (rows["FETF"].AssetClass, rows["FETF"].VehicleType));
        Assert.Equal(("equity", "stock"), (rows["STKX"].AssetClass, rows["STKX"].VehicleType));
        Assert.Equal(((string?)null, "etf"), (rows["SETF"].AssetClass, rows["SETF"].VehicleType));
        Assert.Equal(("alternative", "option"), (rows["OPTX"].AssetClass, rows["OPTX"].VehicleType));
        Assert.Equal(((string?)null, "other"), (rows["ODDX"].AssetClass, rows["ODDX"].VehicleType));

        // No code on its account: unclassified, whatever its curr carries.
        Assert.Equal(((string?)null, (string?)null, (string?)null, (string?)null),
            (rows["NONX"].AssetClass, rows["NONX"].VehicleType, rows["NONX"].Source, rows["NONX"].Exchange));

        // All three from one account: the first, in item order, with a type. The
        // exchange of the typeless first account is not mixed in.
        Assert.Equal(((string?)null, "etf", "NYSE"),
            (rows["TWOX"].AssetClass, rows["TWOX"].VehicleType, rows["TWOX"].Exchange));

        Assert.Equal("NYSE", rows["BNDX"].Exchange);
        Assert.Equal("NASDAQ", rows["STKX"].Exchange);
        Assert.All(rows.Values.Where(r => r.VehicleType is not null), r => Assert.Equal("import", r.Source));

        // The unknown code is reported, not dropped.
        var check = result.Validation.Checks.Single(c => c.Name == "security-types-recognised");
        Assert.False(check.Passed);
        Assert.Equal("1 security(ies) with an unrecognised sec_type code (7) imported with vehicle type Other",
            check.Message);
        Assert.Contains("imported with vehicle type Other", check.Summary);
    }

    [Fact]
    public async Task Every_code_known_passes_the_check()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, Export.Replace("\"sec_type\":\"7\"", "\"sec_type\":\"4\"", StringComparison.Ordinal));

        Assert.True(result.Validation.Checks.Single(c => c.Name == "security-types-recognised").Passed);
        Assert.Equal("option", await conn.ExecuteScalarAsync<string?>(
            "SELECT vehicle_type FROM securities WHERE ledger_id = @L AND ticker = 'ODDX';",
            new { L = result.LedgerId }));
    }

    /// <summary>
    /// The generated Demo (its short span, which keeps every security account) writes
    /// the codes only on its type-<c>s</c> accounts, as Moneydance does, and its
    /// <c>curr</c> items carry none: each ticker gets the vehicle its type codes name
    /// only if the importer reads the accounts.
    /// </summary>
    [Fact]
    public async Task Each_generated_Demo_security_is_classified_from_its_security_account()
    {
        await using var conn = Db.OpenConnection();
        using var export = DemoTestExports.ShortExport();
        var result = await ImportAsync(conn, export);

        var rows = (await conn.QueryAsync<(string? Ticker, string Name, string? AssetClass, string? VehicleType, string? Exchange)>(
            "SELECT ticker, name, asset_class, vehicle_type, exchange FROM securities WHERE ledger_id = @L;",
            new { L = result.LedgerId }))
            .ToDictionary(r => r.Ticker ?? "(none)", StringComparer.Ordinal);

        Assert.Equal(9, rows.Count);
        foreach (var etf in new[] { "VTI", "VXUS", "BND", "VNQ" })
            Assert.Equal(((string?)null, "etf"), (rows[etf].AssetClass, rows[etf].VehicleType));
        Assert.Equal(((string?)null, "mutual_fund"), (rows["VFFVX"].AssetClass, rows["VFFVX"].VehicleType));
        Assert.Equal(("cash", "money_market"), (rows["VMFXX"].AssetClass, rows["VMFXX"].VehicleType));
        foreach (var stock in new[] { "NVDA", "MSFT" })
            Assert.Equal(("equity", "stock"), (rows[stock].AssetClass, rows[stock].VehicleType));
        Assert.Equal("mutual_fund", rows["(none)"].VehicleType);
        Assert.Null(rows["(none)"].Exchange);
        Assert.All(rows.Where(r => r.Key != "(none)"), r => Assert.Contains(r.Value.Exchange, new[] { "NYSE", "NASDAQ" }));

        Assert.True(result.Validation.Checks.Single(c => c.Name == "security-types-recognised").Passed);
    }

    private static async Task<MoneydanceImportResult> ImportAsync(NpgsqlConnection conn, string json)
    {
        using var export = MdItemReader.ReadString(json);
        return await ImportAsync(conn, export);
    }

    private static async Task<MoneydanceImportResult> ImportAsync(NpgsqlConnection conn, MdExport export)
    {
        var owner = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });
        return await new MoneydanceImportService().ImportAsync(
            conn, export, ledgerName: $"Security types {Guid.NewGuid():N}", ownerUserId: owner,
            progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
    }
}
