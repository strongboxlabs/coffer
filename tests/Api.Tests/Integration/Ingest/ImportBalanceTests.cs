using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// Balance-invariant coverage for EVERY ingest write path: the OFX/QFX
/// and QIF file importers and the SimpleFIN feed pull. All three converge
/// on <c>IngestOrchestrator</c>, which writes through EF
/// <c>SaveChangesAsync</c> so <see cref="Coffer.Api.Db.LegDerivedRecomputeInterceptor"/>
/// recomputes <c>txn_header_account_balances</c> — but each path has a
/// distinct front end and dedup key, so each is covered independently.
///
/// <para>Two invariants per provider:</para>
/// <list type="number">
///   <item><description><b>Accrual</b> — after an import/sync the running
///   <c>balance_after</c> on the bound account is exact (the recompute fires
///   on the ingest write path).</description></item>
///   <item><description><b>No double-count on re-ingest</b> — importing /
///   syncing the same payload twice leaves the header count AND the balances
///   unchanged. The existing ingest tests pin the header-count dedup; this
///   pins the BALANCE, which had no oracle (the gap the PR #196 audit
///   flagged).</description></item>
/// </list>
///
/// <para>Plus one investment-import invariant: an imported investment row
/// moves CASH and stages the wire's share/price into <c>ingest_*</c> fields,
/// but creates NO <c>holdings</c>/<c>lots</c> — the holdings model only
/// materialises when the user resolves the row in the editor ("importers
/// report the feed, not a cash model", ADR-0042). Hand-computed absolute
/// oracles, atomic per-test ledger.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ImportBalanceTests
{
    private readonly PostgresFixture _fixture;

    public ImportBalanceTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static MultipartFormDataContent FileUpload(
        string body, string fileName, Guid accountId, string providerAccountId)
    {
        var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(stream, "file", fileName);
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent(providerAccountId), "providerAccountId");
        return content;
    }

    /// <summary>The running <c>balance_after</c> on <paramref name="accountId"/>,
    /// ordered by the header's posted date — the imported account's ledger
    /// column as a reader would see it.</summary>
    private async Task<List<decimal>> BalancesByPostedAtAsync(Guid ledgerId, Guid accountId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.AccountId == accountId)
            .Join(
                db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId),
                b => b.HeaderId, h => h.Id, (b, h) => new { h.PostedAt, b.BalanceAfter })
            .OrderBy(x => x.PostedAt)
            .Select(x => x.BalanceAfter)
            .ToListAsync();
    }

    // =================================================================
    // OFX / QFX file import
    // =================================================================

    // OFX 1.x (SGML) bank statement: -12.34 (Jan 5) then +2500.00 (Jan 15).
    private const string OfxBank = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII

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
        <CURDEF>USD
        <BANKACCTFROM>
        <BANKID>999999999
        <ACCTID>12345
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260105
        <TRNAMT>-12.34
        <FITID>IMPBAL-OFX-1
        <NAME>STARBUCKS
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20260115
        <TRNAMT>2500.00
        <FITID>IMPBAL-OFX-2
        <NAME>PAYROLL
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public async Task Ofx_import_accrues_running_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(OfxBank, "statement.qfx", bank.Id, "999999999:12345"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>())!.TransactionsForReview);

        // -12.34, then -12.34 + 2500 = 2487.66.
        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }

    [Fact]
    public async Task Ofx_reimport_does_not_double_count_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        for (var i = 0; i < 2; i++)
        {
            var resp = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
                FileUpload(OfxBank, "statement.qfx", bank.Id, "999999999:12345"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // FITID dedup: still two headers, balances unchanged — not doubled.
        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }

    // =================================================================
    // QIF file import
    // =================================================================

    // Bank QIF: T is already signed (debit negative). -12.34 then +2500.00.
    private const string QifBank = """
        !Type:Bank
        D01/05/2026
        T-12.34
        PSTARBUCKS
        ^
        D01/15/2026
        T2500.00
        PPAYROLL
        ^
        """;

    // Single-buy investment QIF (workplace-plan shape): cash out -500.00.
    private const string QifSingleBuy = """
        !Type:Invst
        D01/05/2026
        NBuy
        YGROWTH FUND(AAAA)
        I100.00000
        Q5.000
        U500.00
        T500.00
        MContribution
        ^
        """;

    [Fact]
    public async Task Qif_import_accrues_running_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(QifBank, "statement.qif", bank.Id, "qif"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>())!.TransactionsForReview);

        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }

    [Fact]
    public async Task Qif_reimport_does_not_double_count_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        for (var i = 0; i < 2; i++)
        {
            var resp = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
                FileUpload(QifBank, "statement.qif", bank.Id, "qif"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // Synthetic-external-id dedup (SHA-1 of account + row fields):
        // balances unchanged on the second import.
        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }

    [Fact]
    public async Task Investment_import_moves_cash_but_creates_no_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(QifSingleBuy, "401k.qif", brokerage.Id, "qif"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, (await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>())!.TransactionsForReview);

        // Cash leg accrues the wire amount on the brokerage account.
        Assert.Equal(new[] { -500.00m }, await BalancesByPostedAtAsync(ledger.LedgerId, brokerage.Id));

        // But the import stages share/price into ingest_* fields only — it
        // does NOT open a holding. Holdings materialise on editor resolution.
        await using var db = _fixture.NewDbContext();
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;
        var holdings = await db.Holdings.AsNoTracking()
            .CountAsync(h => h.AccountId == holdingsAccountId);
        Assert.Equal(0, holdings);
        var lots = await db.Lots.AsNoTracking()
            .CountAsync(l => l.LedgerId == ledger.LedgerId);
        Assert.Equal(0, lots);
    }

    // =================================================================
    // SimpleFIN feed pull
    // =================================================================

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }

    private static SimpleFinClient ClientWithStubHandler(string accountsBody)
    {
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        return new SimpleFinClient(new HttpClient(new StubHandler(req =>
            req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accountsBody) })));
    }

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // sf-1 Checking: -12.34 (earlier) then +2500.00 (later).
    private const string SimpleFinBody = """
        {"connections":[
          {"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}
        ],"errlist":[],"accounts":[
          {"id":"sf-1","conn_id":"c-A","name":"Checking","currency":"USD","balance":"100.00","transactions":[
            {"id":"impbal-sf-1","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false},
            {"id":"impbal-sf-2","posted":1715100000,"amount":"2500.00","description":"PAYROLL","pending":false}
          ]}
        ]}
        """;

    /// <summary>Connect a stubbed SimpleFIN feed and bind a fresh bank
    /// account to <c>sf-1</c>, ready to sync.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger, FeedConnectionSummary Connection, Coffer.Api.Db.Entities.AccountRow Bank, HttpClient Client)>
        ConnectAndMapAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var keys = _fixture.NewLedgerKeyService();
        await using (var seed = _fixture.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(SimpleFinBody));
        var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        var connection = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var bank = await ledger.AddBankAccountAsync("checking");
        var mapResp = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-1",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapResp.StatusCode);

        return (factory, ledger, connection, bank, client);
    }

    [Fact]
    public async Task Simplefin_sync_accrues_running_balances()
    {
        var (factory, ledger, connection, bank, client) = await ConnectAndMapAsync();
        await using var _ = factory;
        using var __ = client;

        var sync = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(2, (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!.TransactionsForReview);

        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }

    [Fact]
    public async Task Simplefin_resync_does_not_double_count_balances()
    {
        var (factory, ledger, connection, bank, client) = await ConnectAndMapAsync();
        await using var _ = factory;
        using var __ = client;

        for (var i = 0; i < 2; i++)
        {
            var sync = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        }

        // FITID dedup on re-sync: balances unchanged, not doubled.
        Assert.Equal(new[] { -12.34m, 2487.66m }, await BalancesByPostedAtAsync(ledger.LedgerId, bank.Id));
    }
}
