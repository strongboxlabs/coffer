using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.FeedConnections;

/// <summary>
/// Server-side concurrency control for SimpleFIN sync (slice 2c.2).
/// The UNIQUE partial index <c>uq_sync_runs_one_running_per_connection</c>
/// (migration 040) enforces at-most-one running sync per connection
/// at the database layer. Per project memory
/// feedback_server_side_concurrency, the SPA's mapBusy / per-row
/// disable is UX clarity only — the API must own the race.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SyncConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public SyncConcurrencyTests(PostgresFixture fixture)
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

    /// <summary>Connect a ledger to a stubbed SimpleFIN that
    /// returns the supplied body on /accounts.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger,
                       FeedConnectionSummary Connection)>
        ConnectAsync(string accountsBody)
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
        return (factory, ledger, connection);
    }

    [Fact]
    public async Task Concurrent_sync_requests_serialize_one_succeeds_others_get_422_feed_sync_in_progress()
    {
        // Stub a SimpleFIN /accounts that blocks until released, so
        // we can guarantee both requests are in flight at the same
        // time. The blocking handler holds the FIRST request open
        // for ~500ms; the SECOND request races on the
        // sync_runs INSERT during that window.
        var gate = new TaskCompletionSource<bool>();
        var firstRequestSeen = new TaskCompletionSource<bool>();
        var releaseFirst = new TaskCompletionSource<bool>();
        var requestCount = 0;

        var setup = await ConnectAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[]}
            """);

        // Replace the SimpleFIN client with one that gates GET
        // /accounts so we can hold the first sync's HTTP call open.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(
                new HttpClient(new GatedHandler(
                    async req =>
                    {
                        if (req.Method == HttpMethod.Post)
                        {
                            return new HttpResponseMessage(HttpStatusCode.OK)
                                { Content = new StringContent(
                                    "https://u:p@bridge.simplefin.org/access/x") };
                        }
                        var count = Interlocked.Increment(ref requestCount);
                        if (count == 1)
                        {
                            firstRequestSeen.TrySetResult(true);
                            await releaseFirst.Task.ConfigureAwait(false);
                        }
                        return new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new StringContent("""
                                {"connections":[
                                  {"conn_id":"c-A","name":"Bank A","org_id":"banka",
                                   "sfin_url":"https://sfin/banka"}
                                ],"errlist":[],"accounts":[]}
                                """) };
                    }))));

        using var clientA = await AuthedClientAsync(factory, setup.Ledger);
        using var clientB = await AuthedClientAsync(factory, setup.Ledger);

        var firstSyncTask = clientA.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        // Wait until the first sync has actually started its
        // SimpleFIN call — at this point the sync_runs row is
        // already INSERTed with status='running'. A second sync
        // attempt would race on the partial UNIQUE index.
        await firstRequestSeen.Task;
        var secondSync = await clientB.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondSync.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await secondSync.Content.ReadAsStringAsync());
        Assert.Equal("feed-sync-in-progress",
            doc.RootElement.GetProperty("code").GetString());

        // Release the first sync so it completes cleanly.
        releaseFirst.SetResult(true);
        var firstResp = await firstSyncTask;
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);

        // Exactly one sync_runs row landed (the rejected second
        // attempt never wrote one).
        await using var db = _fixture.NewDbContext();
        var runs = await db.LedgerOperations.AsNoTracking()
            .Where(r => r.FeedConnectionId == setup.Connection.Id)
            .ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal("completed", run.Status);
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task Stale_running_run_is_reaped_before_a_new_sync_starts()
    {
        // Slice 2c.2 lazy reaper: a sync_runs row stranded in
        // `running` longer than 10 minutes (e.g. process crash
        // mid-sync) would otherwise permanently block syncs for
        // this connection via the partial UNIQUE index. The next
        // sync sweeps it into `failed` before its own INSERT.
        var setup = await ConnectAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[]}
            """);

        // Seed a stale `running` row 15 minutes old.
        Guid staleRunId;
        await using (var seed = _fixture.NewDbContext())
        {
            var row = new LedgerOperationRow
            {
                Id = Guid.NewGuid(),
                LedgerId = setup.Ledger.LedgerId,
                Family = "ingest",
                ProviderKey = "simplefin",
                TriggeredVia = "manual",
                FeedConnectionId = setup.Connection.Id,
                Status = "running",
                StartedAt = DateTime.UtcNow.AddMinutes(-15),
            };
            seed.LedgerOperations.Add(row);
            await seed.SaveChangesAsync();
            staleRunId = row.Id;
        }

        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);
        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        // The stale row was reaped to `failed`; a fresh `completed`
        // row exists for this attempt.
        await using var db = _fixture.NewDbContext();
        var stale = await db.LedgerOperations.AsNoTracking()
            .SingleAsync(r => r.Id == staleRunId);
        Assert.Equal("failed", stale.Status);
        Assert.NotNull(stale.CompletedAt);
        Assert.Contains("Interrupted", stale.ErrorMessage);

        var live = await db.LedgerOperations.AsNoTracking()
            .CountAsync(r => r.FeedConnectionId == setup.Connection.Id
                          && r.Status == "completed");
        Assert.Equal(1, live);
    }

    [Fact]
    public async Task Subsequent_sync_uses_a_tighter_start_date()
    {
        // Slice 2c.5 smart start-date is per-account: first sync
        // (account.last_simplefin_sync_at NULL) asks for the 90-day
        // max; the second sync (watermark just set) asks for
        // watermark - 7d. The captured query strings prove the
        // second sync's start-date sits strictly closer to now.
        var capturedStartDates = new List<long>();
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
        // Body returns one SimpleFIN account so the connection has
        // something to map against (slice 2c.5 needs a mapped account
        // for the watermark to advance).
        const string bodyWithAccount = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """;
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(
                new HttpClient(new RecordingHandler(req =>
                {
                    if (req.Method == HttpMethod.Get)
                    {
                        var q = System.Web.HttpUtility.ParseQueryString(
                            req.RequestUri!.Query);
                        var startDateRaw = q["start-date"];
                        if (long.TryParse(startDateRaw, out var sd))
                            capturedStartDates.Add(sd);
                    }
                    return req.Method == HttpMethod.Post
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new StringContent(accessUrl) }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new StringContent(bodyWithAccount) };
                }))));
        using var client = await AuthedClientAsync(factory, ledger);
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        var connection = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        // Slice 2c.5: bind a Coffer account to sf-1 BEFORE the syncs
        // so the watermark math has a per-account anchor. Without a
        // mapping, both syncs would fall back to the 90-day floor.
        var bank = await ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        // The connect-time probe is fire-and-forget for institution
        // name — count only sync-time GETs by clearing.
        capturedStartDates.Clear();

        await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync",
            content: null);
        await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync",
            content: null);

        Assert.Equal(2, capturedStartDates.Count);
        // First sync (watermark NULL) asks for ~89 days back.
        var first = DateTimeOffset.FromUnixTimeSeconds(capturedStartDates[0]);
        // First-sync window = MaxWindowDays - WindowSafetyMargin
        // = 90 days - 1 day = 89 days. Asserts strictly < 90 so the
        // SimpleFIN cap-boundary regression can't sneak back (a 1-hour
        // margin shipped first and still tripped the warning on real
        // bridges — see WindowSafetyMargin xmldoc).
        var firstAgo = (DateTimeOffset.UtcNow - first).TotalDays;
        Assert.InRange(firstAgo, 88.5, 89.5);
        // Second sync (watermark ~now) asks for ~7 days back
        // — strictly closer to now than the first.
        var second = DateTimeOffset.FromUnixTimeSeconds(capturedStartDates[1]);
        Assert.True(second > first,
            $"Second sync's start-date ({second:o}) should be later than the first's ({first:o}).");
        Assert.InRange((DateTimeOffset.UtcNow - second).TotalDays, 6, 8);
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _impl;
        public GatedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _impl(request);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
