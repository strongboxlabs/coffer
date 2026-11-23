using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Import;

/// <summary>
/// End-to-end coverage for the Moneydance import endpoints.
/// </summary>
/// <remarks>
/// <para>
/// The point of these is LIFETIME, not mapping (the mappers are covered in
/// Importer.Moneydance.Tests). An <c>MdExport</c> owns the parsed
/// <c>JsonDocument</c> and every <c>MdItem</c> is a view into it, so the document
/// has to stay alive for exactly as long as the import reads from it — and the
/// in-app import is fire-and-forget: <c>ImportJobRunner</c> hands the export to a
/// <c>Task.Run</c> that outlives the HTTP request. Disposing in the endpoint (the
/// obvious <c>using</c>) would tear the document out mid-import, and NOTHING else
/// in the suite would catch it: it fails only on the real background path.
/// </para>
/// <para>
/// The fixture is the generated Demo's three-month span
/// (<see cref="DemoTestExports.Tiny"/>) — a real export that is known to survive
/// the whole pipeline, so a failure here is about lifetime rather than about
/// hand-written JSON being subtly invalid.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MoneydanceImportEndpointTests : DatabaseTest
{
    public MoneydanceImportEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Preview_reports_item_counts_and_writes_nothing()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var content = Upload(ledgerName: null);
        var response = await client.PostAsync("/api/imports/moneydance/preview", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Preview owns the document for the duration of the request and disposes it
        // before returning — so the DTO must already be materialized. A lazy
        // projection over the items would throw ObjectDisposedException here.
        Assert.Equal(DemoItemCount, body.RootElement.GetProperty("totalItems").GetInt32());
        // ADR-0114 D7: the wizard's default reporting currency is the file's base currency.
        Assert.Equal("USD", body.RootElement.GetProperty("baseCurrencyCode").GetString());
    }

    /// <summary>A synthetic file whose base currency is EUR, holding one account in EUR
    /// and one in USD; <paramref name="extra"/> adds items.</summary>
    private static string EuroExport(string extra = "") => $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-eur","currid":"EUR","name":"Euro","dec":"2","isbase":"1"},
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-eur"},
            {"obj_type":"acct","id":"a-eu","type":"b","name":"Account A","parentid":"a-root","currid":"c-eur","sbal":"0"},
            {"obj_type":"acct","id":"a-us","type":"b","name":"Account B","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-cat","type":"e","name":"Category A","parentid":"a-root","currid":"c-eur"}{{extra}}
          ]
        }
        """;

    [Fact]
    public async Task A_start_with_no_reporting_currency_takes_the_files_base_currency()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var preview = Upload(ledgerName: null, EuroExport());
        var previewed = await client.PostAsync("/api/imports/moneydance/preview", preview);
        Assert.Equal(HttpStatusCode.OK, previewed.StatusCode);
        using (var body = JsonDocument.Parse(await previewed.Content.ReadAsStringAsync()))
            Assert.Equal("EUR", body.RootElement.GetProperty("baseCurrencyCode").GetString());

        using var content = Upload("Import base currency " + Guid.NewGuid().ToString("N")[..8], EuroExport());
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var (state, ledgerId, error) = await PollToCompletionAsync(client, startedBody.RootElement.GetProperty("jobId").GetString()!);

        Assert.Equal("succeeded", state);
        Assert.Null(error);
        await using var db = Db.NewDbContext();
        var ledger = Guid.Parse(ledgerId!);
        Assert.Equal("EUR", await db.Ledgers.Where(l => l.Id == ledger).Select(l => l.CurrencyCode).SingleAsync());
        // Each account in its own Moneydance currency.
        var currencies = await db.Accounts.Where(a => a.LedgerId == ledger && a.ExternalId != null)
            .ToDictionaryAsync(a => a.ExternalId!, a => a.CurrencyCode);
        Assert.Equal("EUR", currencies["a-eu"]);
        Assert.Equal("USD", currencies["a-us"]);
        Assert.Equal("EUR", currencies["c-cat"]);
    }

    [Theory]
    [InlineData(""",{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","sbal":"0"}""", "'Account C' has no currency")]
    [InlineData(""",{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","currid":"c-gone","sbal":"0"}""", "'Account C' names a currency")]
    // ADR-0114 D15: the file keeps more places for a code than Coffer does.
    [InlineData(""",{"obj_type":"curr","id":"c-fine","currid":"GBP","name":"Pound","dec":"3"},{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","currid":"c-fine","sbal":"0"}""",
        "'Account C' is in 'Pound', whose amounts the file keeps at 3 decimal places, and Coffer keeps GBP at 2")]
    // ADR-0114 D15: a code Coffer does not accept for an account.
    [InlineData(""",{"obj_type":"curr","id":"c-unit","currid":"CLF","name":"Unit","dec":"2"},{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","currid":"c-unit","sbal":"0"}""",
        "'Account C' is in 'Unit', whose code 'CLF' is not a currency Coffer accepts for an account")]
    // ADR-0114 D15: a loan's escrow finer than its currency.
    [InlineData(""",{"obj_type":"acct","id":"a-bad","type":"o","name":"Account C","parentid":"a-root","currid":"c-eur","sbal":"0","int_rate":"4.0","num_payments":"360","pmts_per_year":"12","init_principal":"10000000","escrow_payment":"12.345"}""",
        "'Account C' has a loan escrow payment with 3 decimal places, and Coffer keeps EUR at 2")]
    public async Task An_export_whose_currencies_cannot_be_read_is_refused_by_the_preview_and_the_start(
        string extra, string expected)
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var preview = Upload(ledgerName: null, EuroExport(extra));
        var previewed = await client.PostAsync("/api/imports/moneydance/preview", preview);
        Assert.Equal("import-currency-unresolved", await CodeOf(previewed));
        Assert.Contains(expected, await DetailOf(previewed), StringComparison.Ordinal);

        var name = "Import refused currency " + Guid.NewGuid().ToString("N")[..8];
        using var content = Upload(name, EuroExport(extra));
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal("import-currency-unresolved", await CodeOf(started));
        Assert.Contains(expected, await DetailOf(started), StringComparison.Ordinal);

        await using var db = Db.NewDbContext();
        Assert.False(await db.Ledgers.AnyAsync(l => l.Name == name));
        // A refused start never took the import: its memory slot is free again.
        Assert.False(factory.Services.GetRequiredService<Coffer.Api.Import.ImportMemoryGate>().IsHeld);
    }

    // ADR-0114 D7, D15: a category is stamped with the reporting currency, and a posting
    // between two categories has no real side, so its legs carry the primary category's own
    // Moneydance amount at that category's own places. A category kept at more places than
    // the reporting currency would store a figure that currency cannot hold.
    private const string DinarCategory = """
        ,{"obj_type":"curr","id":"c-kwd","currid":"KWD","name":"Dinar","dec":"3"}
        ,{"obj_type":"acct","id":"c-kat","type":"e","name":"Category K","parentid":"a-root","currid":"c-kwd"}
        """;

    [Fact]
    public async Task A_category_kept_at_more_places_than_the_reporting_currency_is_refused_when_it_pays_another_category()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var extra = DinarCategory + """
            ,{"obj_type":"txn","id":"t-1","acctid":"c-kat","dt":"20260115","desc":"Move","0.id":"s-1","0.acctid":"c-cat","0.pamt":"12345","0.samt":"-12345"}
            """;
        const string expected =
            "The category 'Category K' moves money to another category in 'Dinar' at 3 decimal places, "
            + "and Coffer keeps a category in the ledger's reporting currency, EUR, at 2.";

        using var preview = Upload(ledgerName: null, EuroExport(extra));
        var previewed = await client.PostAsync("/api/imports/moneydance/preview", preview);
        Assert.Equal("import-currency-unresolved", await CodeOf(previewed));
        Assert.Contains(expected, await DetailOf(previewed), StringComparison.Ordinal);

        var name = "Import category places " + Guid.NewGuid().ToString("N")[..8];
        using var content = Upload(name, EuroExport(extra));
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal("import-currency-unresolved", await CodeOf(started));
        Assert.Contains(expected, await DetailOf(started), StringComparison.Ordinal);

        await using var db = Db.NewDbContext();
        Assert.False(await db.Ledgers.AnyAsync(l => l.Name == name));
    }

    [Fact]
    public async Task A_category_kept_at_more_places_imports_when_it_only_meets_real_accounts()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        // The category's line is read at the real side's places, so its own never reach a
        // stored figure.
        var extra = DinarCategory + """
            ,{"obj_type":"txn","id":"t-1","acctid":"a-eu","dt":"20260115","desc":"Spend","0.id":"s-1","0.acctid":"c-kat","0.pamt":"-1234","0.samt":"12345"}
            """;

        using var content = Upload("Import category real side " + Guid.NewGuid().ToString("N")[..8], EuroExport(extra));
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var (state, ledgerId, error) = await PollToCompletionAsync(client, startedBody.RootElement.GetProperty("jobId").GetString()!);

        Assert.Equal("succeeded", state);
        Assert.Null(error);
        await using var db = Db.NewDbContext();
        var ledger = Guid.Parse(ledgerId!);
        var category = await db.Accounts.Where(a => a.LedgerId == ledger && a.ExternalId == "c-kat").SingleAsync();
        Assert.Equal("EUR", category.CurrencyCode);
        var leg = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.LedgerId == ledger && l.AccountId == category.Id);
        Assert.Equal(12.34m, leg.Amount);
    }

    [Fact]
    public async Task A_category_that_pays_another_is_checked_against_the_reporting_currency_the_import_was_given()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        // Both categories at the euro's 2 places, which fit the base currency and not the yen's 0.
        var extra = """
            ,{"obj_type":"acct","id":"c-cat2","type":"e","name":"Category B","parentid":"a-root","currid":"c-eur"}
            ,{"obj_type":"txn","id":"t-1","acctid":"c-cat","dt":"20260115","desc":"Move","0.id":"s-1","0.acctid":"c-cat2","0.pamt":"1234","0.samt":"-1234"}
            """;
        const string expected =
            "The category 'Category A' moves money to another category in 'Euro' at 2 decimal places, "
            + "and Coffer keeps a category in the ledger's reporting currency, JPY, at 0.";

        using var atBase = Upload(ledgerName: null, EuroExport(extra));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/imports/moneydance/preview", atBase)).StatusCode);

        using var preview = Upload(ledgerName: null, EuroExport(extra));
        preview.Add(new StringContent("JPY"), "currencyCode");
        var previewed = await client.PostAsync("/api/imports/moneydance/preview", preview);
        Assert.Equal("import-currency-unresolved", await CodeOf(previewed));
        Assert.Contains(expected, await DetailOf(previewed), StringComparison.Ordinal);

        var name = "Import chosen currency places " + Guid.NewGuid().ToString("N")[..8];
        using var content = Upload(name, EuroExport(extra));
        content.Add(new StringContent("JPY"), "currencyCode");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal("import-currency-unresolved", await CodeOf(started));
        Assert.Contains(expected, await DetailOf(started), StringComparison.Ordinal);

        await using var db = Db.NewDbContext();
        Assert.False(await db.Ledgers.AnyAsync(l => l.Name == name));
    }

    [Fact]
    public async Task An_export_with_no_base_currency_is_refused_rather_than_read_as_dollars()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var preview = Upload(ledgerName: null, EuroExport().Replace(",\"isbase\":\"1\"", "", StringComparison.Ordinal));
        var previewed = await client.PostAsync("/api/imports/moneydance/preview", preview);

        Assert.Equal("import-currency-unresolved", await CodeOf(previewed));
        Assert.Contains("no currency as its base currency", await DetailOf(previewed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_import_creates_its_ledger_in_the_reporting_currency_the_wizard_chose()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var content = Upload("Import currency " + Guid.NewGuid().ToString("N")[..8]);
        content.Add(new StringContent(" eur "), "currencyCode");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());

        var (state, ledgerId, error) = await PollToCompletionAsync(client, startedBody.RootElement.GetProperty("jobId").GetString()!);

        Assert.Equal("succeeded", state);
        Assert.Null(error);
        await using var db = Db.NewDbContext();
        var ledger = Guid.Parse(ledgerId!);
        Assert.Equal("EUR", await db.Ledgers.Where(l => l.Id == ledger).Select(l => l.CurrencyCode).SingleAsync());
    }

    [Fact]
    public async Task An_import_with_a_currency_that_is_not_one_is_refused_before_it_starts()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        using var preview = Upload(ledgerName: null);
        preview.Add(new StringContent("EURO"), "currencyCode");
        Assert.Equal("ledger-currency-invalid", await CodeOf(await client.PostAsync("/api/imports/moneydance/preview", preview)));

        using var content = Upload("Import bad currency");
        content.Add(new StringContent("EURO"), "currencyCode");
        Assert.Equal("ledger-currency-invalid", await CodeOf(await client.PostAsync("/api/imports/moneydance", content)));
    }

    [Fact]
    public async Task Start_hands_the_export_to_the_background_run_and_imports_it()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        var ledgerName = "Import lifetime " + Guid.NewGuid().ToString("N")[..8];
        using var content = Upload(ledgerName);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrEmpty(jobId));

        var (state, ledgerId, error) = await PollToCompletionAsync(client, jobId!);

        // "failed" with an ObjectDisposedException in the logs is the shape this
        // test exists to catch, so surface the error rather than asserting bare.
        Assert.Equal("succeeded", state);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(ledgerId), "a successful import must report its new ledger");

        // The import stores the memo-and-tags presentation inside its own transaction:
        // the new ledger's legs are presented, each as the rule computes it.
        await using var db = Db.NewDbContext();
        var ledger = Guid.Parse(ledgerId!);
        Assert.Empty(await Coffer.Api.Db.LegNotes.DifferencesAsync(db, ledger, CancellationToken.None));
        Assert.True(await StoredNotesEquivalence.PresentedLegsAsync(db, ledger) > 0,
            "the imported ledger has no presented leg, so nothing was compared");
    }

    [Fact]
    public async Task An_import_that_drops_a_transaction_warns_in_the_server_log()
    {
        // The job status that carries the warnings to the wizard is held in memory
        // only, so each failed check is also logged by name, with the job and the
        // ledger it wrote.
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(Db)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        using var content = Upload("Dropped " + Guid.NewGuid().ToString("N")[..8], DroppingExport);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = body.RootElement.GetProperty("jobId").GetString()!;

        var (state, ledgerId, _) = await PollToCompletionAsync(client, jobId);

        Assert.Equal("succeeded", state);
        var warning = Assert.Single(logs.Lines, l =>
            l.Category == typeof(Coffer.Api.Import.ImportJobRunner).FullName && l.Level == LogLevel.Warning);
        Assert.Contains(jobId, warning.Message);
        Assert.Contains(ledgerId!, warning.Message);
        Assert.Contains("no-dropped-transactions", warning.Message);
    }

    [Fact]
    public async Task An_import_that_passes_validation_logs_no_warning()
    {
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(Db)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        using var content = Upload("Clean " + Guid.NewGuid().ToString("N")[..8], CleanExport);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var (state, _, _) = await PollToCompletionAsync(client, body.RootElement.GetProperty("jobId").GetString()!);

        Assert.Equal("succeeded", state);
        Assert.Contains(logs.Lines, l =>
            l.Category == typeof(Coffer.Api.Import.ImportJobRunner).FullName && l.Level == LogLevel.Information);
        Assert.DoesNotContain(logs.Lines, l =>
            l.Category == typeof(Coffer.Api.Import.ImportJobRunner).FullName && l.Level >= LogLevel.Warning);
    }

    /// <summary>A bank transaction to a category: maps cleanly, every check passes.</summary>
    private const string CleanExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"}
          ]
        }
        """;

    /// <summary>The clean export plus a buy whose security the export does not hold, which
    /// the importer cannot map and drops.</summary>
    private const string DroppingExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","dt":"20260102",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-2","0.acctid":"a-unknown-security","0.invest.splittype":"sec",
             "0.samt":"100000","0.pamt":"-10000"}
          ]
        }
        """;

    // --- what a finished import reports to the wizard -------------------------------

    [Fact]
    public async Task A_finished_import_reports_each_failed_check_and_the_transactions_it_did_not_import()
    {
        using var body = await ImportToSucceededAsync("Warned " + Guid.NewGuid().ToString("N")[..8], WarningExport);
        var root = body.RootElement;

        // One entry per failed check, in the validator's order, each with the sentence the
        // person importing reads, not the importer's own account of the failure.
        var warnings = root.GetProperty("warnings").EnumerateArray()
            .Select(w => (Text(w, "check"), Text(w, "message")))
            .ToList();
        Assert.Equal(
            new (string?, string?)[]
            {
                ("header-balance",
                    "1 transaction has lines that do not add up to zero, so the balances of the accounts "
                    + "it touches may be off."),
                ("no-dropped-transactions", "3 transactions were not imported."),
            },
            warnings);

        // Every transaction not imported, in export order, with what identifies it.
        var skipped = root.GetProperty("skippedTransactions").EnumerateArray().ToList();
        Assert.Equal(3, skipped.Count);
        AssertSkipped(skipped[0], "2026-01-02", "UnknownSecurity", security: null, ticker: null, shares: 10m);
        AssertSkipped(skipped[1], "2026-01-03", "UnknownXferAccount", security: "Fund A", ticker: "FUND", shares: 2.5m);
        AssertSkipped(skipped[2], date: null, "UnknownBrokerageAccount", security: null, ticker: null, shares: 0.75m);
        Assert.Equal(0, root.GetProperty("skippedTransactionsNotListed").GetInt32());
    }

    [Fact]
    public async Task A_finished_import_lists_the_bank_transactions_it_did_not_import()
    {
        // The bank route's skips reach the wizard as the investment route's do: each one
        // listed with its date and reason, and counted by the failed check.
        using var body = await ImportToSucceededAsync("Bank skips " + Guid.NewGuid().ToString("N")[..8], BankSkipsExport);
        var root = body.RootElement;

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("no-dropped-transactions", Text(warning, "check"));
        Assert.Equal("3 transactions were not imported.", Text(warning, "message"));

        var skipped = root.GetProperty("skippedTransactions").EnumerateArray().ToList();
        Assert.Equal(3, skipped.Count);
        AssertSkipped(skipped[0], "2026-01-02", "UnknownSplitAccount", security: null, ticker: null, shares: null);
        AssertSkipped(skipped[1], "2026-01-03", "NoSplits", security: null, ticker: null, shares: null);
        AssertSkipped(skipped[2], "2026-01-05", "DuplicateFitid", security: null, ticker: null, shares: null);
        Assert.Equal(0, root.GetProperty("skippedTransactionsNotListed").GetInt32());
    }

    /// <summary>The clean export plus a bank transaction to a category the export does not
    /// hold, one with no lines, and a second carrying the first FITID's identity. None of
    /// the three is imported.</summary>
    private const string BankSkipsExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-unknown","acctid":"a-bank","desc":"Shop","dt":"20260102",
             "0.id":"s-2","0.acctid":"c-missing","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-empty","acctid":"a-bank","desc":"Shop","dt":"20260103"},
            {"obj_type":"txn","id":"t-fit-1","acctid":"a-bank","desc":"Shop","dt":"20260104",
             "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1",
             "0.id":"s-3","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-fit-2","acctid":"a-bank","desc":"Shop","dt":"20260105",
             "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1",
             "0.id":"s-4","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"}
          ]
        }
        """;

    [Fact]
    public async Task A_clean_import_reports_no_warnings_and_no_skipped_transactions()
    {
        using var body = await ImportToSucceededAsync("Clean " + Guid.NewGuid().ToString("N")[..8], CleanExport);
        var root = body.RootElement;

        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, root.GetProperty("skippedTransactions").GetArrayLength());
        Assert.Equal(0, root.GetProperty("skippedTransactionsNotListed").GetInt32());
    }

    [Fact]
    public async Task The_skipped_list_stops_at_its_cap_and_counts_the_rest()
    {
        const int beyond = 2;
        const int cap = Coffer.Api.Import.ImportJobOutcome.SkippedTransactionsListed;

        using var body = await ImportToSucceededAsync(
            "Capped " + Guid.NewGuid().ToString("N")[..8], ManyDroppedExport(cap + beyond));
        var root = body.RootElement;

        var skipped = root.GetProperty("skippedTransactions").EnumerateArray().ToList();
        Assert.Equal(cap, skipped.Count);
        // The first ones are listed, in export order; the rest are only counted.
        AssertSkipped(skipped[0], "2026-01-01", "UnknownSecurity", security: null, ticker: null, shares: 1m);
        AssertSkipped(skipped[^1], "2026-01-01", "UnknownSecurity", security: null, ticker: null, shares: cap);
        Assert.Equal(beyond, root.GetProperty("skippedTransactionsNotListed").GetInt32());
        // The check still counts every one of them.
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("no-dropped-transactions", Text(warning, "check"));
        Assert.Equal($"{cap + beyond} transactions were not imported.", Text(warning, "message"));
    }

    private static void AssertSkipped(
        JsonElement row, string? date, string reason, string? security, string? ticker, decimal? shares)
    {
        Assert.Equal(
            new[] { "date", "reason", "security", "ticker", "shares" },
            row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(date, Text(row, "date"));
        Assert.Equal(reason, Text(row, "reason"));
        Assert.Equal(security, Text(row, "security"));
        Assert.Equal(ticker, Text(row, "ticker"));
        var value = row.GetProperty("shares");
        Assert.Equal(shares, value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal());
    }

    /// <summary>Import <paramref name="json"/> and return the settled job status, which
    /// must be a success: a failed run is reported with the errors it logged.</summary>
    private async Task<JsonDocument> ImportToSucceededAsync(string ledgerName, string json)
    {
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(Db)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        using var content = Upload(ledgerName, json);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString()!;

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var response = await client.GetAsync($"/api/imports/moneydance/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = Text(body.RootElement, "state");
            if (state == "running")
            {
                body.Dispose();
                await Task.Delay(250);
                continue;
            }
            if (state != "succeeded")
            {
                body.Dispose();
                Assert.Fail($"The import ended '{state}': "
                    + string.Join(" || ", logs.Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message)));
            }
            return body;
        }

        Assert.Fail("The import job never left the 'running' state.");
        return null!;
    }

    /// <summary>
    /// The clean export plus: a transfer between two accounts in one currency whose two
    /// sides differ, which is imported but does not balance; a buy of a security the export
    /// does not hold; a buy funded from an account the export does not hold; and a buy
    /// in a brokerage the export does not hold, whose date is not a calendar date. The
    /// three buys are not imported.
    /// </summary>
    private const string WarningExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-save","type":"b","name":"Savings","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-2","acctid":"a-bank","desc":"Move","dt":"20260101",
             "0.id":"s-2","0.acctid":"a-save","0.samt":"1000","0.pamt":"-900"},
            {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","dt":"20260102",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-3","0.acctid":"a-unknown-security","0.invest.splittype":"sec",
             "0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"txn","id":"t-buyx","acctid":"a-brk","desc":"Buy","dt":"20260103",
             "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr",
             "0.id":"s-4","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"25000","0.pamt":"-2500",
             "1.id":"s-5","1.acctid":"a-unknown-account","1.invest.splittype":"xfr","1.samt":"-2500","1.pamt":"2500"},
            {"obj_type":"txn","id":"t-undated","acctid":"a-unknown-brokerage","desc":"Buy","dt":"0",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-6","0.acctid":"a-unknown-security","0.invest.splittype":"sec",
             "0.samt":"7500","0.pamt":"-750"}
          ]
        }
        """;

    /// <summary>A brokerage and <paramref name="count"/> buys of a security the export
    /// does not hold, buy <c>i</c> of <c>i</c> shares, none of which can be imported.</summary>
    private static string ManyDroppedExport(int count)
    {
        var buys = Enumerable.Range(1, count).Select(i =>
            $$"""
            {"obj_type":"txn","id":"t-{{i}}","acctid":"a-brk","desc":"Buy","dt":"20260101",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-{{i}}","0.acctid":"a-unknown-security","0.invest.splittype":"sec",
             "0.samt":"{{i * 10000}}","0.pamt":"-{{i * 100}}"}
            """);
        return $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {{string.Join(",\n", buys)}}
              ]
            }
            """;
    }

    // --- one export in memory at a time, and a size cap (FU-092) ---------------------

    [Fact]
    public async Task While_another_export_is_held_previews_and_starts_are_refused_before_reading_it()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<Coffer.Api.Import.ImportMemoryGate>();
        using (var held = gate.TryEnter())
        {
            Assert.NotNull(held);
            using var preview = Upload(ledgerName: null);
            Assert.Equal("import-already-running",
                await CodeOf(await client.PostAsync("/api/imports/moneydance/preview", preview)));
            using var start = Upload("Refused " + Guid.NewGuid().ToString("N")[..8]);
            Assert.Equal("import-already-running",
                await CodeOf(await client.PostAsync("/api/imports/moneydance", start)));
        }

        // Released, the next one goes through.
        using var again = Upload(ledgerName: null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/imports/moneydance/preview", again)).StatusCode);
    }

    [Fact]
    public async Task A_finished_import_releases_its_memory_slot()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        using var content = Upload("Slot release " + Guid.NewGuid().ToString("N")[..8]);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());

        var (state, _, _) = await PollToCompletionAsync(client, body.RootElement.GetProperty("jobId").GetString()!);

        Assert.Equal("succeeded", state);
        // Read at once: the slot is released before the job reports its end.
        var gate = factory.Services.GetRequiredService<Coffer.Api.Import.ImportMemoryGate>();
        Assert.False(gate.IsHeld, "a finished import must release the slot, or no export could be imported again");
    }

    [Fact]
    public async Task An_import_reports_its_end_only_once_its_activity_row_is_written_and_its_slot_is_free()
    {
        // The wizard polls the job until it leaves "running" and then acts on it: it opens
        // the new ledger or Activity, or starts the next import. So nothing of the run may
        // follow its terminal state. The save of the import's ledger_operations row is held
        // here so that the order is observable rather than a race.
        var hold = new HoldMoneydanceOperationSave();
        await using var factory = new ApiFactory(Db).WithServices(s =>
        {
            s.RemoveAll<ServiceDbContextFactory>();
            s.AddSingleton(Db.NewServiceFactoryWith(hold));
        });
        using var client = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<Coffer.Api.Import.ImportMemoryGate>();

        using var content = Upload("Ordered " + Guid.NewGuid().ToString("N")[..8], CleanExport);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString()!;

        try
        {
            await hold.Reached.WaitAsync(TimeSpan.FromSeconds(60));

            // The ledger is committed and its activity row is not: the job is still running.
            Assert.Equal("running", await StateOfAsync(client, jobId));
        }
        finally
        {
            hold.Release();
        }

        var (state, ledgerId, error) = await PollToCompletionAsync(client, jobId);
        Assert.Equal("succeeded", state);
        Assert.Null(error);

        // Read at once, with no wait: the run's work is done when its end is reported.
        Assert.False(gate.IsHeld, "the job reported its end while its memory slot was still held");
        await using var db = Db.NewDbContext();
        var ledger = Guid.Parse(ledgerId!);
        var row = Assert.Single(await db.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledger)
            .ToListAsync());
        Assert.Equal(Coffer.Api.Db.Repositories.LedgerOperationsRepository.MoneydanceImportProviderKey, row.ProviderKey);
        Assert.Equal("completed", row.Status);
    }

    /// <summary>Holds the save of a Moneydance import's <c>ledger_operations</c> row until
    /// <see cref="Release"/>; every other save passes.</summary>
    private sealed class HoldMoneydanceOperationSave : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the held save has been reached.</summary>
        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LedgerOperationRow>().Any(e =>
                    e.State == EntityState.Added
                    && e.Entity.ProviderKey == Coffer.Api.Db.Repositories.LedgerOperationsRepository.MoneydanceImportProviderKey))
            {
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> StateOfAsync(HttpClient client, string jobId)
    {
        var response = await client.GetAsync($"/api/imports/moneydance/{jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Text(body.RootElement, "state");
    }

    [Fact]
    public async Task An_export_over_the_upload_cap_is_refused()
    {
        await using var factory = new ApiFactory(Db).WithConfig("Api:Import:MaxUploadMb", "1");
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[3 * 1024 * 1024]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "too-big.json");

        var response = await client.PostAsync("/api/imports/moneydance/preview", content);

        Assert.Equal("import-too-large", await CodeOf(response));
        // And the slot it took is free again.
        Assert.False(factory.Services.GetRequiredService<Coffer.Api.Import.ImportMemoryGate>().IsHeld);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Text(doc.RootElement, "code");
    }

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Text(doc.RootElement, "detail") ?? string.Empty;
    }

    private static async Task<(string? State, string? LedgerId, string? Error)> PollToCompletionAsync(
        HttpClient client, string jobId)
    {
        // The import is a background task, so this is a genuine wait, not a
        // convenience. The sample imports in a few seconds; the ceiling is only
        // here so a hang fails
        // the test instead of hanging the lane.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var response = await client.GetAsync($"/api/imports/moneydance/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = body.RootElement.GetProperty("state").GetString();
            if (state != "running")
            {
                return (state,
                    Text(body.RootElement, "ledgerId"),
                    Text(body.RootElement, "error"));
            }

            await Task.Delay(250);
        }

        Assert.Fail("The import job never left the 'running' state.");
        return (null, null, null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Counted from the bytes this test uploads, not written down as a number.
    /// The assertion is about preview COUNTING the file it was given; the file's
    /// size is not the subject, and a literal here would make every regeneration
    /// of the sample look like a preview bug.
    /// </summary>
    private static int DemoItemCount
    {
        get
        {
            using var document = JsonDocument.Parse(DemoExportBytes());
            return document.RootElement.GetProperty("all_items").GetArrayLength();
        }
    }

    private static MultipartFormDataContent Upload(string? ledgerName, string? json = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(json is null ? DemoExportBytes() : System.Text.Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "demo-export.json");
        if (ledgerName is not null)
            content.Add(new StringContent(ledgerName), "ledgerName");
        return content;
    }

    /// <summary>
    /// The generated Demo's three-month span, from the same generator the Demo
    /// ledger is built with, so the two cannot drift.
    /// </summary>
    private static byte[] DemoExportBytes() => DemoTestExports.TinyBytes;
}
