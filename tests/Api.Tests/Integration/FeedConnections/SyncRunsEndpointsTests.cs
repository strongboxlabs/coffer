using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.FeedConnections;

/// <summary>
/// Integration coverage for the sync activity log (slice 2c.1):
/// every Sync now click writes a <c>sync_runs</c> row with the
/// right status + counters, errors persist to
/// <c>sync_run_errors</c>, promote-on-clear events persist to
/// <c>sync_run_promotions</c>, and the two GET endpoints surface
/// the audit trail with cross-ledger RLS enforced.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SyncRunsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public SyncRunsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }

    private static SimpleFinClient ClientWithStubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new SimpleFinClient(new HttpClient(new StubHandler(handler)));

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Connect + map an account + sync against the supplied
    /// /accounts payload. Returns enough handles to drive the
    /// follow-up assertions on the sync activity log.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger,
                       FeedConnectionSummary Connection, AccountRow Bank)>
        ConnectMapSyncAsync(string accountsBody, string simpleFinAccountId)
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

        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(accessUrl) }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(accountsBody) }));
        var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        var connection = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var bank = await ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = connection.Id,
                    SimpleFinAccountId = simpleFinAccountId,
                }),
            });
        return (factory, ledger, connection, bank);
    }

    [Fact]
    public async Task Sync_writes_a_sync_runs_row_with_terminal_status_and_counters()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false},
                {"id":"fitid-B","posted":1715100000,"amount":"-5.00","description":"BUS","pending":true}
              ]}
            ]}
            """;
        var setup = await ConnectMapSyncAsync(body, "sf-1");
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        var list = await client.GetFromJsonAsync<List<SyncRunSummary>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/?connectionId={setup.Connection.Id}");
        Assert.NotNull(list);
        Assert.Single(list!);
        var run = list![0];
        Assert.Equal("completed", run.Status);
        Assert.Equal(setup.Connection.Id, run.FeedConnectionId);
        Assert.Equal(2, run.TxnsFetched);
        Assert.Equal(2, run.TxnsInserted);
        Assert.Equal(0, run.TxnsPromoted);
        Assert.Equal(1, run.TxnsStillPending);
        Assert.Equal(0, run.TxnsAlreadyKnown);
        Assert.NotNull(run.CompletedAt);
        Assert.NotNull(run.TriggeredByUserId);
        Assert.Equal(0, run.ErrorCount);
        Assert.Equal(0, run.PromotionCount);
    }

    [Fact]
    public async Task Sync_with_errlist_marks_status_partial_and_persists_errors()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[
              {"code":"fi.maintenance","msg":"Bank A maintenance","conn_id":"c-A"}
            ],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectMapSyncAsync(body, "sf-1");
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        var list = (await client.GetFromJsonAsync<List<SyncRunSummary>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/?connectionId={setup.Connection.Id}"))!;
        var run = Assert.Single(list);
        Assert.Equal("partial", run.Status);
        Assert.Equal(1, run.ErrorCount);

        var detail = (await client.GetFromJsonAsync<SyncRunDetail>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/{run.Id}"))!;
        Assert.Single(detail.Errors);
        Assert.Equal("fi.maintenance", detail.Errors[0].Code);
        Assert.Equal("Bank A maintenance", detail.Errors[0].Message);
        Assert.Equal("c-A", detail.Errors[0].SimpleFinConnectionId);
    }

    [Fact]
    public async Task Sync_403_records_needs_reauth_status_on_the_run()
    {
        // 403 path — first sync after mapping returns Forbidden;
        // the run row should land in needs_reauth + the connection
        // status flip remains.
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
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(accessUrl) }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = await AuthedClientAsync(factory, ledger);
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        var summary = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{summary.Id}/sync",
            content: null);

        var list = (await client.GetFromJsonAsync<List<SyncRunSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/sync-runs/?connectionId={summary.Id}"))!;
        var run = Assert.Single(list);
        Assert.Equal("needs_reauth", run.Status);
    }

    [Fact]
    public async Task Promote_on_clear_writes_a_sync_run_promotions_row()
    {
        // Two-stage sync: first pending, then posted. The second run
        // is a promote-on-clear with the bank changing the amount.
        var setup = await ConnectMapSyncAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[
                {"id":"fitid-X","posted":1715000000,"amount":"-30.00","description":"DINER HOLD","pending":true}
              ]}
            ]}
            """, "sf-1");
        await using var factory1 = setup.Factory;
        using var client1 = await AuthedClientAsync(factory1, setup.Ledger);
        await client1.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        // Second sync — same FITID cleared at $35.
        await using var factory2 = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@bridge.simplefin.org/access/x") }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("""
                            {"connections":[
                              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
                               "sfin_url":"https://sfin/banka"}
                            ],"errlist":[],"accounts":[
                              {"id":"sf-1","conn_id":"c-A","name":"Checking",
                               "currency":"USD","balance":"-35.00","transactions":[
                                {"id":"fitid-X","posted":1715000000,"amount":"-35.00","description":"DINER","pending":false}
                              ]}
                            ]}
                            """) }));
        using var client2 = await AuthedClientAsync(factory2, setup.Ledger);
        await client2.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        // Both runs visible; the second has a promotion event.
        var list = (await client2.GetFromJsonAsync<List<SyncRunSummary>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/?connectionId={setup.Connection.Id}"))!;
        Assert.Equal(2, list.Count);
        var promoteRun = list[0]; // newest first
        Assert.Equal(1, promoteRun.TxnsPromoted);
        Assert.Equal(1, promoteRun.PromotionCount);

        var detail = (await client2.GetFromJsonAsync<SyncRunDetail>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/{promoteRun.Id}"))!;
        var promo = Assert.Single(detail.Promotions);
        Assert.Equal(-30.00m, promo.WasAmount);
        Assert.Equal(-35.00m, promo.BecameAmount);
    }

    [Fact]
    public async Task Sync_runs_list_is_RLS_scoped_to_visible_ledgers()
    {
        // alice syncs, bob can't list her runs even via her ledger id.
        var alice = await ConnectMapSyncAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """, "sf-1");
        await using var aliceFactory = alice.Factory;
        using var aliceClient = await AuthedClientAsync(aliceFactory, alice.Ledger);
        await aliceClient.PostAsync(
            $"/api/ledgers/{alice.Ledger.LedgerId}/feed-connections/{alice.Connection.Id}/sync",
            content: null);

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var bobFactory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(bobFactory, bob);

        var resp = await bobClient.GetAsync(
            $"/api/ledgers/{alice.Ledger.LedgerId}/sync-runs/?connectionId={alice.Connection.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sync_run_detail_422_when_run_belongs_to_a_different_ledger()
    {
        // alice has a run; bob tries to fetch its detail under his
        // ledger scope. The cross-ledger ledger_id filter in the
        // repo rejects with sync-run-not-in-ledger.
        var alice = await ConnectMapSyncAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """, "sf-1");
        await using var aliceFactory = alice.Factory;
        using var aliceClient = await AuthedClientAsync(aliceFactory, alice.Ledger);
        await aliceClient.PostAsync(
            $"/api/ledgers/{alice.Ledger.LedgerId}/feed-connections/{alice.Connection.Id}/sync",
            content: null);
        var aliceRun = (await aliceClient.GetFromJsonAsync<List<SyncRunSummary>>(
            $"/api/ledgers/{alice.Ledger.LedgerId}/sync-runs/?connectionId={alice.Connection.Id}"))!;
        var runId = aliceRun.Single().Id;

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var bobFactory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(bobFactory, bob);

        var resp = await bobClient.GetAsync(
            $"/api/ledgers/{bob.LedgerId}/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("sync-run-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }
}
