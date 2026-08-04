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
/// Slice 2c.3: per-account sync (<c>POST /accounts/{id}/sync</c>)
/// + ledger-wide sync-all (<c>POST /sync-all</c>). Asserts:
/// <list type="bullet">
///   <item><description>Per-account sync narrows the SimpleFIN
///   call via <c>?account=</c> and only the requested account's
///   transactions land.</description></item>
///   <item><description>Per-account sync 422s when the account
///   isn't bound to a feed.</description></item>
///   <item><description>Sync-all aggregates results across every
///   connection on the ledger; per-connection failures don't
///   cascade.</description></item>
/// </list>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PerAccountAndSyncAllTests
{
    private readonly PostgresFixture _fixture;

    public PerAccountAndSyncAllTests(PostgresFixture fixture)
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> CapturedQueries { get; } = new();
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri is { } uri)
                CapturedQueries.Add(uri.Query);
            return Task.FromResult(_impl(request));
        }
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

    [Fact]
    public async Task Per_account_sync_passes_account_filter_to_SimpleFIN_and_lands_only_that_accounts_rows()
    {
        // SimpleFIN feed has two accounts; per-account sync should
        // pass `?account=sf-A` and land only sf-A's transactions.
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

        const string accessUrl = "https://u:p@bridge.simplefin.org/access/x";
        var recording = new RecordingHandler(req => req.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(accessUrl) }
            : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""
                    {"connections":[
                      {"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}
                    ],"errlist":[],"accounts":[
                      {"id":"sf-A","conn_id":"c-A","name":"Checking",
                       "currency":"USD","balance":"0.00","transactions":[
                        {"id":"fitid-A","posted":1715000000,"amount":"-10.00","description":"A1","pending":false}
                      ]}
                    ]}
                    """) });
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(new HttpClient(recording)));
        using var client = await AuthedClientAsync(factory, ledger);

        // Connect + map bank → sf-A.
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
                    SimpleFinAccountId = "sf-A",
                }),
            });
        recording.CapturedQueries.Clear();

        // Per-account sync — narrows to bank.Id's bound sf-A.
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(1, result.TransactionsForReview);

        // The SimpleFIN query carries `account=sf-A`.
        var query = Assert.Single(recording.CapturedQueries);
        Assert.Contains("account=sf-A", query, StringComparison.Ordinal);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId)
            .ToListAsync();
        Assert.Single(headers);
        Assert.Equal("fitid-A", headers[0].ExternalId);
    }

    [Fact]
    public async Task Per_account_sync_422s_with_account_not_bound_to_feed_when_account_has_no_mapping()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("unbound");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("account-not-bound-to-feed",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sync_all_walks_every_connection_and_aggregates_results_into_one_response()
    {
        // Two SimpleFIN connections on one ledger; first returns
        // a clean account, second returns 403 (needs_reauth). The
        // aggregate response carries both outcomes; hadAnyFailure
        // is true because of the needs_reauth.
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

        // Per-call routing: claim POST → access URL; first GET
        // → success body; second GET → 403. We track GET count
        // to disambiguate which connection is being synced.
        var getCount = 0;
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
            {
                if (req.Method == HttpMethod.Post)
                {
                    // Stable access URL per claim. Two POSTs are
                    // expected — one per connection-create.
                    return new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(
                            "https://u:p@bridge.simplefin.org/access/x") };
                }
                getCount++;
                // GETs from connect-probe + the two syncs interleave.
                // For simplicity, every GET against the first
                // 2 calls (probes) returns a tiny body, and from
                // the 3rd onwards we route by accessUrl content.
                // Simpler: just return success for the first sync
                // GET and 403 for the second sync GET. Use req's
                // path + a header-less heuristic = call order.
                if (getCount > 2)
                {
                    // 3rd GET = first connection's sync; 4th GET
                    // = second connection's sync.
                    if (getCount == 4)
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""
                        {"connections":[],"errlist":[],"accounts":[]}
                        """) };
            }));
        using var client = await AuthedClientAsync(factory, ledger);

        var c1 = (await (await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/1") }))
                .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var c2 = (await (await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/2") }))
                .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/sync-all",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var agg = (await resp.Content.ReadFromJsonAsync<SyncAllResultDto>())!;
        Assert.Equal(2, agg.Connections.Count);
        Assert.True(agg.HadAnyFailure);

        // Lookup by connection id since the order matches
        // ListByLedgerAsync's "most-recently-synced first" sort —
        // which on first-sync flips both rows around.
        var byId = agg.Connections.ToDictionary(e => e.ConnectionId);
        var firstEntry = byId[c1.Id];
        var secondEntry = byId[c2.Id];

        // Exactly one of (result, failureCode) is non-null on
        // each entry — both connections completed (one as
        // needs_reauth, one clean). hadAnyFailure picks up the
        // needs_reauth via connectionStatus.
        Assert.NotNull(firstEntry.Result);
        Assert.NotNull(secondEntry.Result);
        // One of the two completed with needs_reauth.
        var statuses = new[] { firstEntry.Result!.ConnectionStatus, secondEntry.Result!.ConnectionStatus };
        Assert.Contains("needs_reauth", statuses);
        Assert.Contains("active", statuses);
    }
}
