using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// An ingested amount finer than its account's currency is rounded to the currency's
/// places, half away from zero, and the row's originals keep the bank's figure with the
/// rounded mark (ADR-0114 D15): SimpleFIN, OFX, QIF and CSV alike. A figure that fits,
/// trailing zeros included, is written as sent and seeds nothing.
/// </summary>
/// <remarks>
/// Synthetic ledgers with whole-unit, two-place and three-place accounts, written through
/// the sync and import endpoints and read back from the tables.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class IngestRoundingTests : DatabaseTest
{
    public IngestRoundingTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Posted = DateTimeOffset.FromUnixTimeSeconds(1715000000).UtcDateTime;
    private static readonly DateTime Transacted = DateTimeOffset.FromUnixTimeSeconds(1714900000).UtcDateTime;

    // ----- SimpleFIN -----

    /// <summary>A feed row whose figure is finer than its account's currency: both legs
    /// take the rounded figure, and the originals keep the figure as sent, with the payee,
    /// dates, check number and feed memo the row was written with.</summary>
    [Theory]
    [InlineData("JPY", "-1234.5", "-1235")]
    [InlineData("USD", "-12.345", "-12.35")]
    [InlineData("KWD", "-1.2345", "-1.235")]
    public async Task A_feed_figure_finer_than_its_currency_is_rounded_and_seeds_the_bank_figure(
        string currency, string sent, string written)
    {
        var feed = await FeedAsync(currency, $$"""
            {"id":"t-1","posted":1715000000,"transacted_at":1714900000,"amount":"{{sent}}","payee":"Corner Cafe","description":"CORNER CAFE 0042","pending":false}
            """);

        var header = await HeaderAsync(feed.Ledger, "t-1");
        Assert.True(header.NeedsReview);
        var legs = await LegsAsync(header.Id);
        Assert.Equal(D(written), legs.Single(l => l.AccountId == feed.Bank).Amount);
        Assert.Equal(-D(written), legs.Single(l => l.AccountId != feed.Bank).Amount);

        var originals = await OriginalsAsync(header.Id);
        Assert.NotNull(originals);
        Assert.Equal(sent, originals!.Amount?.ToString(CultureInfo.InvariantCulture));
        Assert.True(originals.RoundedAtImport);
        Assert.Equal("Corner Cafe", originals.Payee);
        Assert.Equal("CORNER CAFE 0042", originals.Memo);
        Assert.Equal(Posted, originals.PostedAt);
        Assert.Equal(Transacted, originals.TransactedAt);
        Assert.Equal(header.CheckNumber, originals.CheckNumber);
        Assert.True(await HasOverridesAsync(header.Id));
    }

    /// <summary>A figure that fits its currency, a trailing zero past its places
    /// included, is written as sent and seeds no originals; a three-decimal figure in a
    /// three-place currency fits.</summary>
    [Theory]
    [InlineData("JPY", "-1234.0")]
    [InlineData("USD", "-12.340")]
    [InlineData("KWD", "-1.234")]
    public async Task A_feed_figure_that_fits_its_currency_seeds_nothing(string currency, string sent)
    {
        var feed = await FeedAsync(currency, $$"""
            {"id":"t-1","posted":1715000000,"amount":"{{sent}}","payee":"Corner Cafe","description":"CORNER CAFE 0042","pending":false}
            """);

        var header = await HeaderAsync(feed.Ledger, "t-1");
        var legs = await LegsAsync(header.Id);
        Assert.Equal(D(sent), legs.Single(l => l.AccountId == feed.Bank).Amount);
        Assert.Null(await OriginalsAsync(header.Id));
        Assert.False(await HasOverridesAsync(header.Id));
    }

    [Fact]
    public async Task A_pending_feed_row_that_rounds_is_seeded_at_insert()
    {
        var feed = await FeedAsync("USD", """
            {"id":"t-p","posted":1715000000,"amount":"-7.125","payee":"Corner Cafe","description":"CORNER CAFE 0042","pending":true}
            """);

        var header = await HeaderAsync(feed.Ledger, "t-p");
        Assert.True(header.IsPending);
        Assert.Equal(-7.13m, (await LegsAsync(header.Id)).Single(l => l.AccountId == feed.Bank).Amount);
        var originals = await OriginalsAsync(header.Id);
        Assert.Equal(-7.125m, originals?.Amount);
        Assert.True(originals!.RoundedAtImport);
    }

    // ----- file imports -----

    [Fact]
    public async Task An_OFX_figure_finer_than_its_currency_is_rounded_and_seeds_the_bank_figure()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var resp = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            Upload(OfxStatement("USD", ("FITID-R", "-12.345"), ("FITID-F", "-5.000")), "statement.qfx",
                bank.Id, "999999999:1234567890"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var rounded = await HeaderAsync(ledger, "FITID-R");
        Assert.Equal(-12.35m, (await LegsAsync(rounded.Id)).Single(l => l.AccountId == bank.Id).Amount);
        Assert.Equal(12.35m, (await LegsAsync(rounded.Id)).Single(l => l.AccountId != bank.Id).Amount);
        var originals = await OriginalsAsync(rounded.Id);
        Assert.Equal(-12.345m, originals?.Amount);
        Assert.True(originals!.RoundedAtImport);
        Assert.Equal("SAMPLE STORE", originals.Payee);
        Assert.Equal("SAMPLE STORE 0042", originals.Memo);
        Assert.Equal(rounded.PostedAt, originals.PostedAt);

        var fits = await HeaderAsync(ledger, "FITID-F");
        Assert.Equal(-5.000m, (await LegsAsync(fits.Id)).Single(l => l.AccountId == bank.Id).Amount);
        Assert.Null(await OriginalsAsync(fits.Id));
    }

    [Fact]
    public async Task A_QIF_figure_finer_than_its_currency_is_rounded_in_the_rows_stored_sign()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var plan = await ledger.AddInvestmentAccountAsync("plan");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        const string bankQif = "!Type:Bank\nD01/05/2026\nT-12.345\nPSAMPLE STORE\nMSAMPLE STORE 0042\n^\n";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            Upload(bankQif, "export.qif", bank.Id, "qif"))).StatusCode);
        // A withdrawal is signed by its action: the file's figure is a magnitude.
        const string planQif = "!Type:Invst\nD02/15/2026\nNWithdrwX\nT100.005\nMWithdrawal\n^\n";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            Upload(planQif, "export.qif", plan.Id, "qif"))).StatusCode);

        var bankRow = await SingleHeaderOnAsync(ledger, bank.Id);
        Assert.Equal(-12.35m, (await LegsAsync(bankRow)).Single(l => l.AccountId == bank.Id).Amount);
        Assert.Equal(-12.345m, (await OriginalsAsync(bankRow))?.Amount);

        var planRow = await SingleHeaderOnAsync(ledger, plan.Id);
        Assert.Equal(-100.01m, (await LegsAsync(planRow)).Single(l => l.AccountId == plan.Id).Amount);
        var planOriginals = await OriginalsAsync(planRow);
        Assert.Equal(-100.005m, planOriginals?.Amount);
        Assert.True(planOriginals!.RoundedAtImport);
    }

    /// <summary>A CSV figure is kept in the row's stored sign: after the layout inverts
    /// it, and after debit and credit columns sign it.</summary>
    [Fact]
    public async Task A_CSV_figure_finer_than_its_currency_is_rounded_in_the_rows_stored_sign()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var card = await ledger.AddBankAccountAsync("card");
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        const string inverted = """
            version: 1
            delimiter: comma
            header_rows: 0
            date:
              column: 1
              format: yyyy-MM-dd
            payee:
              column: 2
            amount:
              shape: signed
              column: 3
              invert: true
            """;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            CsvUpload("2026-09-02,SAMPLE STORE,12.345\n", card.Id, inverted))).StatusCode);

        const string debitCredit = """
            version: 1
            delimiter: comma
            header_rows: 0
            date:
              column: 1
              format: yyyy-MM-dd
            payee:
              column: 2
            amount:
              shape: debit_credit
              debit_column: 3
              credit_column: 4
            """;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            CsvUpload("2026-09-02,SAMPLE STORE,7.125,\n2026-09-04,SAMPLE PAYER,,23.505\n", bank.Id, debitCredit))).StatusCode);

        var cardRow = await SingleHeaderOnAsync(ledger, card.Id);
        Assert.Equal(-12.35m, (await LegsAsync(cardRow)).Single(l => l.AccountId == card.Id).Amount);
        Assert.Equal(-12.345m, (await OriginalsAsync(cardRow))?.Amount);

        await using var db = Db.NewDbContext();
        var bankRows = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.OriginatingAccountId == bank.Id)
            .ToDictionaryAsync(h => h.Payee!, h => h.Id);
        Assert.Equal(-7.13m, (await LegsAsync(bankRows["SAMPLE STORE"])).Single(l => l.AccountId == bank.Id).Amount);
        Assert.Equal(-7.125m, (await OriginalsAsync(bankRows["SAMPLE STORE"]))?.Amount);
        Assert.Equal(23.51m, (await LegsAsync(bankRows["SAMPLE PAYER"])).Single(l => l.AccountId == bank.Id).Amount);
        Assert.Equal(23.505m, (await OriginalsAsync(bankRows["SAMPLE PAYER"]))?.Amount);
    }

    // ----- helpers -----

    private sealed record Feed(SyntheticLedger Ledger, Guid Bank);

    /// <summary>A ledger with one bank account in <paramref name="currency"/>, bound to a
    /// feed account in the same currency, synced once with <paramref name="transaction"/>.</summary>
    private async Task<Feed> FeedAsync(string currency, string transaction)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        var body = $$"""
            {"connections":[
              {"conn_id":"c-R","name":"Bank R","org_id":"bankr","sfin_url":"https://sfin/bankr"}
            ],"errlist":[],"accounts":[
              {"id":"sf-r","conn_id":"c-R","name":"Checking","currency":"{{currency}}","balance":"0","transactions":[
                {{transaction}}
              ]}
            ]}
            """;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(new HttpClient(new StubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin/access/abc") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })), TimeProvider.System));
        using var client = await ClientAsync(factory, ledger);
        var connection = (await (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking", currencyCode: currency);
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-r",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        var sync = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        return new Feed(ledger, bank.Id);
    }

    private static string OfxStatement(string currency, params (string Fitid, string Amount)[] rows)
    {
        var transactions = string.Concat(rows.Select(r => $"""
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20260105
            <TRNAMT>{r.Amount}
            <FITID>{r.Fitid}
            <NAME>SAMPLE STORE
            <MEMO>SAMPLE STORE 0042
            </STMTTRN>

            """));
        return $"""
            OFXHEADER:100
            DATA:OFXSGML
            VERSION:102
            SECURITY:NONE
            ENCODING:USASCII
            CHARSET:1252
            COMPRESSION:NONE
            OLDFILEUID:NONE
            NEWFILEUID:NONE

            <OFX>
            <SIGNONMSGSRSV1>
            <SONRS>
            <STATUS><CODE>0<SEVERITY>INFO</STATUS>
            <DTSERVER>20260201120000
            <LANGUAGE>ENG
            </SONRS>
            </SIGNONMSGSRSV1>
            <BANKMSGSRSV1>
            <STMTTRNRS>
            <TRNUID>0
            <STATUS><CODE>0<SEVERITY>INFO</STATUS>
            <STMTRS>
            <CURDEF>{currency}
            <BANKACCTFROM>
            <BANKID>999999999
            <ACCTID>1234567890
            <ACCTTYPE>CHECKING
            </BANKACCTFROM>
            <BANKTRANLIST>
            <DTSTART>20260101
            <DTEND>20260131
            {transactions}</BANKTRANLIST>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            </OFX>
            """;
    }

    private static MultipartFormDataContent Upload(string body, string fileName, Guid accountId, string providerAccountId)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent(providerAccountId), "providerAccountId");
        return content;
    }

    private static MultipartFormDataContent CsvUpload(string body, Guid accountId, string mappingYaml)
    {
        var content = Upload(body, "statement.csv", accountId, "csv");
        content.Add(new StringContent(mappingYaml), "mappingYaml");
        return content;
    }

    private async Task<TxnHeaderRow> HeaderAsync(SyntheticLedger ledger, string externalId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.ExternalId == externalId);
    }

    private async Task<Guid> SingleHeaderOnAsync(SyntheticLedger ledger, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.OriginatingAccountId == accountId)
            .Select(h => h.Id)
            .SingleAsync();
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private async Task<TxnHeaderOriginalRow?> OriginalsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaderOriginals.AsNoTracking().SingleOrDefaultAsync(o => o.HeaderId == headerId);
    }

    /// <summary>The view's <c>has_overrides</c>, which no API response carries.</summary>
    private async Task<bool> HasOverridesAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.Database
            .SqlQuery<bool>($"""SELECT bool_or(has_overrides) AS "Value" FROM resolved_transactions WHERE header_id = {headerId}""")
            .SingleAsync();
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string SetupToken(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
