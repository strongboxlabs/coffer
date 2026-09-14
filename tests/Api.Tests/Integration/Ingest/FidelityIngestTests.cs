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
/// the <c>FileResult</c> it returns. Both passed while the orchestrator dropped the
/// provenance trail on the floor for every row — the gap being that NOTHING asserted on
/// what reaches the database. That is this file's job: it reads columns back out of
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
public sealed class FidelityIngestTests
{
    private readonly PostgresFixture _fixture;

    public FidelityIngestTests(PostgresFixture fixture) => _fixture = fixture;

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
        + "10.1975,0.000980392,,,,-0.01,999.99,\n"
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

    private static MultipartFormDataContent Upload(Guid accountId)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(ActivityCsv()));
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
    private static string Sentence(Coffer.Api.Db.Entities.TxnHeaderRow h) =>
        h.Memo ?? h.Payee ?? string.Empty;

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Every_imported_row_keeps_both_halves_of_the_conversion_in_the_database()
    {
        // ADR-0098 promises that a converted format does not lose its trail: each row
        // carries the CSV line it came from AND the QIF record it became. The provider
        // built that trail and the file-import path then discarded it, because no file
        // provider had ever populated RawProviderPayload before this one — OFX and QIF
        // both pass null, so the orchestrator's insert simply had no such line.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
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
        // money in the QIF projection rounded this to 0.001 before anything could object.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();
        var row = Assert.Single(headers, h => Sentence(h).Contains("YOU BOUGHT"));

        // 0.00098039, not 0.000980392: ingest_shares is NUMERIC(28,8) and the ninth
        // decimal is the COLUMN's to drop. That is the whole point — the value is rounded
        // ONCE, at the destination's scale, instead of twice. Formatting shares as money
        // in the QIF projection rounded it to 0.001 first, a 2% overstatement that no
        // later stage could see, let alone undo.
        Assert.Equal(0.00098039m, row.IngestShares);
        Assert.NotEqual(0.001m, row.IngestShares);
        Assert.Equal("buy", row.IngestActionHint);
    }

    [Fact]
    public async Task A_tax_named_fund_is_bought_not_expensed()
    {
        // The Action cell is a sentence containing the FUND'S NAME, so a substring test
        // for "TAX" reads the security. This row posted as a miscellaneous expense — cash
        // with the wrong sign, and the 500 units never entered the position.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();

        var purchase = Assert.Single(headers, h => Sentence(h).Contains("PURCHASE INTO CORE"));
        Assert.Equal("buy", purchase.IngestActionHint);
        Assert.Equal(500m, purchase.IngestShares);

        // The neighbouring row IS a genuine withholding and must stay one: a pure cash
        // row, no shares. Without it this test would pass on a rule that simply never
        // fires.
        var withheld = Assert.Single(headers, h => Sentence(h).Contains("FOREIGN TAX WITHHELD"));
        Assert.Equal("misc", withheld.IngestActionHint);
        Assert.Null(withheld.IngestShares);
    }

    [Fact]
    public async Task The_as_of_date_in_the_sentence_wins_over_the_run_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/fidelity/import", Upload(brokerage.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-fidelity")
            .ToListAsync();
        var reinvest = Assert.Single(headers, h => Sentence(h).Contains("REINVESTMENT"));

        // Run Date says 09/08; the sentence says it happened on 09/05.
        Assert.Equal(new DateOnly(2026, 9, 5), DateOnly.FromDateTime(reinvest.PostedAt));
    }
}
