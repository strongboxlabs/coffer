using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// End-to-end brokerage CSV import through the Fidelity shim (ADR-0098).
/// </summary>
/// <remarks>
/// <para>
/// The shim's own unit tests assert on the QIF it produces, and the provider's assert on
/// the <c>FileResult</c> it returns. Both still pass if the orchestrator drops the
/// provenance trail for every row, because neither asserts on what reaches the
/// database. That is this file's job: it reads columns back out of
/// <c>txn_headers</c>, so a break anywhere between the CSV and the row is visible here.
/// </para>
/// <para>
/// The fixture is SYNTHETIC. Invented funds, invented tickers, invented digits. It mirrors
/// a real "Activity &amp; Orders" export in SHAPE only: leading blank lines, the header at
/// an unpredictable offset, Price before Quantity, a zero Quantity on cash rows, an
/// <c>as of</c> date inside the action sentence, and legal prose with commas and quotes
/// trailing the data.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FidelityIngestTests : DatabaseTest
{
    public FidelityIngestTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Header =
        "Run Date,Action,Symbol,Description,Type,Price ($),Quantity,Commission ($),"
        + "Fees ($),Accrued Interest ($),Amount ($),Cash Balance ($),Settlement Date";

    private const string Trailer =
        "\n"
        + "The data and information in this spreadsheet is provided to you solely for your use\n"
        + "\"informational purposes only, and is not intended to provide advice, nor should it\"\n"
        + "Date downloaded 09/11/2026 3:21 pm\n";

    /// <summary>
    /// Four rows, each carrying one thing that has broken before.
    /// </summary>
    private static string ActivityCsv() =>
        "\n\n" + Header + "\n"
        // A fractional share count finer than four decimals.
        + "09/02/2026,YOU BOUGHT SAMPLE GROWTH FUND (SMPLX),SMPLX,SAMPLE GROWTH FUND,Cash,"
        + "13.0645,0.000765432,,,,-0.01,999.99,\n"
        // A TAX-named fund, which a substring test reads as a fee. Reaches Buy only by the
        // sign fallback: the sentence contains neither BUY nor BOUGHT.
        + "09/03/2026,PURCHASE INTO CORE ACCOUNT SAMPLE TAX-EXEMPT MONEY MARKET (SMTXX),"
        + "SMTXX,SAMPLE TAX-EXEMPT MONEY MARKET,Cash,1.00,500,,,,-500.00,499.99,\n"
        // A genuine fee: pure cash, explicit zero quantity.
        + "09/04/2026,FOREIGN TAX WITHHELD SAMPLE GROWTH FUND (SMPLX),SMPLX,"
        + "SAMPLE GROWTH FUND,Cash,,0,,,,-1.23,498.76,\n"
        // The date that is not the Run Date.
        + "09/08/2026,REINVESTMENT as of 09/05/2026 SAMPLE GROWTH FUND (SMPLX),SMPLX,"
        + "SAMPLE GROWTH FUND,Cash,10.00,1.5,,,,-15.00,483.76,\n"
        + Trailer;

    private static MultipartFormDataContent Upload(Guid accountId) => Upload(accountId, ActivityCsv());

    private static MultipartFormDataContent Upload(Guid accountId, string csv)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(body, "file", "History_for_Account_X.csv");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent("fidelity"), "providerAccountId");
        return content;
    }

    /// <summary>
    /// The Fidelity action sentence, wherever it landed.
    /// </summary>
    /// <remarks>
    /// On a SHARE row the QIF provider makes the security name the payee (mirroring OFX)
    /// and the memo carries the sentence; on a cash row there is no security, so the
    /// sentence is the payee. Tests match on the sentence, so they must look in both.
    /// </remarks>
    /// <remarks>
    /// The memo of an ingested row is the account imported into's own, on its leg.
    /// </remarks>
    private static async Task<Func<Coffer.Api.Db.Entities.TxnHeaderRow, string>> SentenceAsync(
        Coffer.Api.Db.AppDbContext db, Guid accountId)
    {
        var memos = await db.TxnLegs.AsNoTracking()
            .Where(l => l.AccountId == accountId && l.LegMemo != null)
            .Select(l => new { l.HeaderId, l.LegMemo })
            .ToDictionaryAsync(l => l.HeaderId, l => l.LegMemo!);
        return h => memos.GetValueOrDefault(h.Id) ?? h.Payee ?? string.Empty;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Every_imported_row_keeps_both_halves_of_the_conversion_in_the_database()
    {
        // ADR-0098 promises that a converted format does not lose its trail: each row
        // carries the CSV line it came from AND the QIF record it became. OFX and QIF
        // both pass a null RawProviderPayload, so this is the file provider that proves
        // the file-import path writes it rather than discarding it.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(4, headers.Count);

        foreach (var header in headers)
        {
            Assert.NotNull(header.ProviderRawPayload);
            using var trail = JsonDocument.Parse(header.ProviderRawPayload!);
            Assert.Equal(
                "fidelity-activity-csv", trail.RootElement.GetProperty("source").GetString());

            // Both halves, and each actually populated — a trail carrying two nulls would
            // satisfy "the property exists" while answering no question at all.
            var csv = trail.RootElement.GetProperty("csv").GetString();
            var qif = trail.RootElement.GetProperty("qif").GetString();
            Assert.False(string.IsNullOrWhiteSpace(csv));
            Assert.False(string.IsNullOrWhiteSpace(qif));
            Assert.Contains("2026", csv);
            Assert.Contains("D09/", qif);
        }

        // And the halves belong to the SAME row, rather than every row carrying row one's.
        var trails = headers
            .Select(h => JsonDocument.Parse(h.ProviderRawPayload!).RootElement
                .GetProperty("csv").GetString())
            .ToList();
        Assert.Equal(4, trails.Distinct().Count());
    }

    [Fact]
    public async Task A_fractional_share_count_reaches_the_row_without_being_rounded()
    {
        // ingest_shares is NUMERIC(28,8) and the quantity it grows into is NUMERIC(25,12),
        // both widened deliberately for fractional shares (mig 043). Formatting shares as
        // money in the QIF projection rounded this to three decimals before anything could
        // object.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();
        var sentenceOf = await SentenceAsync(db, brokerage.Id);
        var row = Assert.Single(headers, h => sentenceOf(h).Contains("YOU BOUGHT"));

        // Eight decimals, not the file's nine: ingest_shares is NUMERIC(28,8) and the ninth
        // decimal is the COLUMN's to drop. That is the whole point — the value is rounded
        // ONCE, at the destination's scale, instead of twice. Formatting shares as money
        // in the QIF projection rounded it to three decimals first, an overstatement that no
        // later stage could see, let alone undo.
        Assert.Equal(0.00076543m, row.IngestShares);
        Assert.NotEqual(0.001m, row.IngestShares);
        Assert.Equal("buy", row.IngestActionHint);
    }

    [Fact]
    public async Task A_tax_named_fund_is_bought_not_expensed()
    {
        // The Action cell is a sentence containing the FUND'S NAME, so a substring test
        // for "TAX" reads the security. This row posted as a miscellaneous expense — cash
        // with the wrong sign, and its units never entered the position.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();
        var sentenceOf = await SentenceAsync(db, brokerage.Id);

        var purchase = Assert.Single(headers, h => sentenceOf(h).Contains("PURCHASE INTO CORE"));
        Assert.Equal("buy", purchase.IngestActionHint);
        Assert.Equal(500m, purchase.IngestShares);

        // The neighbouring row IS a genuine withholding and must stay one: a pure cash
        // row, no shares. Without it this test would pass on a rule that simply never
        // fires.
        var withheld = Assert.Single(headers, h => sentenceOf(h).Contains("FOREIGN TAX WITHHELD"));
        Assert.Equal("misc", withheld.IngestActionHint);
        Assert.Null(withheld.IngestShares);
    }

    [Fact]
    public async Task The_as_of_date_in_the_sentence_wins_over_the_run_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();
        var sentenceOf = await SentenceAsync(db, brokerage.Id);
        var reinvest = Assert.Single(headers, h => sentenceOf(h).Contains("REINVESTMENT"));

        // Run Date says 09/08; the sentence says it happened on 09/05.
        Assert.Equal(new DateOnly(2026, 9, 5), DateOnly.FromDateTime(reinvest.PostedAt));
    }

    [Fact]
    public async Task A_cash_cell_finer_than_the_currency_is_rounded_once_at_the_write_and_kept_whole_in_the_originals()
    {
        // The shim hands the cell on exactly, so the ingest write is the one rounding
        // (ADR-0114 D15): the leg takes the account currency's places, half away from
        // zero, and the originals keep the cell's figure in the row's stored sign, after
        // QIF's signing by action, with the rounded mark.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var csv = "\n\n" + Header + "\n"
            + "09/02/2026,YOU BOUGHT SAMPLE GROWTH FUND (SMPLX),SMPLX,SAMPLE GROWTH FUND,Cash,"
            + "25.00,10,,,,-250.123456,749.876544,\n"
            + "09/03/2026,YOU SOLD SAMPLE GROWTH FUND (SMPLX),SMPLX,SAMPLE GROWTH FUND,Cash,"
            + "25.00,-4,,,,100.004999,849.881543,\n"
            + Trailer;
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id, csv));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToDictionaryAsync(h => h.IngestActionHint!, h => h.Id);
        Assert.Equal(2, headers.Count);

        async Task<(decimal Leg, Coffer.Api.Db.Entities.TxnHeaderOriginalRow? Originals)> ReadAsync(Guid headerId) => (
            await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId && l.AccountId == brokerage.Id)
                .Select(l => l.Amount).SingleAsync(),
            await db.TxnHeaderOriginals.AsNoTracking().SingleOrDefaultAsync(o => o.HeaderId == headerId));

        var (buyLeg, buyOriginals) = await ReadAsync(headers["buy"]);
        Assert.Equal(-250.12m, buyLeg);
        Assert.NotNull(buyOriginals);
        Assert.Equal("-250.123456", buyOriginals!.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(buyOriginals.RoundedAtImport);

        var (sellLeg, sellOriginals) = await ReadAsync(headers["sell"]);
        Assert.Equal(100.00m, sellLeg);
        Assert.NotNull(sellOriginals);
        Assert.Equal("100.004999", sellOriginals!.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(sellOriginals.RoundedAtImport);
    }

    [Fact]
    public async Task A_fee_finer_than_four_places_reaches_its_carrier_as_the_file_sent_it()
    {
        // ingest_fee is an as-sent carrier: the editor rounds its Fee prefill from it once,
        // at the account currency's places (ADR-0114 D14, D15). A column that held only
        // four places rounded the cell first, so the prefill was a second rounding of a
        // figure the file never stated, and could land a minor unit away from the cell's.
        // The cash amount fits the currency, so nothing else on the row is rounded.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var csv = "\n\n" + Header + "\n"
            + "09/02/2026,YOU BOUGHT SAMPLE GROWTH FUND (SMPLX),SMPLX,SAMPLE GROWTH FUND,Cash,"
            + "25.00,10,,1.00495,,-251.00,749.00,\n"
            + Trailer;
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id, csv));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity");
        Assert.Equal("buy", row.IngestActionHint);
        Assert.Equal("1.00495", row.IngestFee?.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The register row carries it the same way, through the views the column feeds.
        var viewFee = await db.ResolvedTransactions.AsNoTracking()
            .Where(rv => rv.HeaderId == row.Id && rv.AccountId == brokerage.Id)
            .Select(rv => rv.IngestFee)
            .SingleAsync();
        Assert.Equal("1.00495", viewFee?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
