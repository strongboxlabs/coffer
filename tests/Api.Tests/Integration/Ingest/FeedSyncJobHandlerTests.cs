using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Ingest;
using Coffer.Api.Scheduling;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// Scheduled feed sync: what it touches, what it refuses to touch, and what it reports.
/// </summary>
/// <remarks>
/// The interesting cases are all about UNATTENDED running. A human pressing "sync" can see
/// the result and decide what to do; a daily job cannot, so every state it can land in has
/// to resolve to something better than "try the same broken thing again tomorrow".
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FeedSyncJobHandlerTests
{
    private readonly PostgresFixture _fixture;

    public FeedSyncJobHandlerTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) => _impl = impl;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }

    private const string Accounts = """
        {"connections":[{"conn_id":"c-1","name":"Test Bank","org_id":"testbank","sfin_url":"https://sfin/test"}],"errlist":[],"accounts":[{"id":"a-1","conn_id":"c-1","name":"Checking","currency":"USD","balance":"0.00","transactions":[]}]}
        """;

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static SimpleFinClient StubClient(HashSet<string> fetched, string? faultOn = null)
        => new(new HttpClient(new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "https://u:p@bridge.simplefin.org/simplefin/access/" + url.Split('/')[^1]),
                };

            fetched.Add(url);
            if (faultOn is not null && url.Contains(faultOn, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream is down"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Accounts),
            };
        })));

    private async Task<SyntheticLedger> LedgerWithLekAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var keys = _fixture.NewLedgerKeyService();
        await using var db = _fixture.NewDbContext();
        var row = await db.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
        row.WrappedLek = keys.CreateWrappedLek();
        row.LekKekId = keys.CurrentKekId;
        row.LekCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ledger;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> AddConnectionAsync(
        HttpClient client, Guid ledgerId, string name)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/feed-connections",
            new CreateFeedConnectionRequest
            {
                SetupToken = SetupTokenFor("https://bridge.simplefin.org/simplefin/claim/" + name),
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<FeedConnectionSummary>();
        return body!.Id;
    }

    private async Task SetStatusAsync(Guid connectionId, string status)
    {
        await using var db = _fixture.NewDbContext();
        await db.FeedConnections
            .Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status));
    }

    private static async Task<JobRunOutcome> RunHandlerAsync(
        ApiFactory factory, PostgresFixture fixture, Guid ledgerId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
            .Single(h => h.JobType == JobTypes.FeedSync);

        // The SERVICE-role context, exactly as SchedulerService supplies. Resolving
        // AppDbContext from the scope instead would give the app-role context with no
        // app.user_id, RLS would hide every connection, and the handler would report a
        // clean run over nothing — which is precisely the bug the first version of these
        // tests found.
        await using var db = fixture.NewServiceDbContext();
        return await handler.RunAsync(
            db, ledgerId, userId, new JobRunClock(DateTime.UtcNow, "UTC"), default);
    }

    [Fact]
    public async Task A_connection_awaiting_reconnection_is_not_retried()
    {
        // The defect that makes autorun unsafe without this. needs_reauth means the bank
        // revoked consent: only a person can fix it. Retrying daily fails forever AND
        // re-stamps last_synced_at each time, so the UI shows a recent sync for stale
        // data — and five such days trip the five-strike auto-disable, taking the WORKING
        // connections down with the broken one.
        var ledger = await LedgerWithLekAsync();
        var fetched = new HashSet<string>(StringComparer.Ordinal);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(fetched));
        using var client = await AuthedClientAsync(factory, ledger);

        var dead = await AddConnectionAsync(client, ledger.LedgerId, "dead");
        await AddConnectionAsync(client, ledger.LedgerId, "alive");
        await SetStatusAsync(dead, "needs_reauth");

        fetched.Clear();
        var outcome = await RunHandlerAsync(factory, _fixture, ledger.LedgerId, ledger.UserId);

        // The dead one was never contacted...
        Assert.DoesNotContain(fetched, u => u.Contains("dead", StringComparison.Ordinal));
        // ...the live one was...
        Assert.Contains(fetched, u => u.Contains("alive", StringComparison.Ordinal));
        // ...and the job is healthy, because a connection awaiting a human is not the
        // schedule failing. Marking the switch down daily for it would teach its owner
        // to ignore the switch.
        Assert.Equal(JobRunResult.Ok, outcome.Result);
    }

    [Fact]
    public async Task Every_connection_awaiting_reconnection_is_degraded()
    {
        // Nothing was refreshed at all, so the switch must not stay green — the same
        // shape as a quote refresh where every provider was down. "No exception was
        // thrown" is not a success criterion.
        var ledger = await LedgerWithLekAsync();
        var fetched = new HashSet<string>(StringComparer.Ordinal);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(fetched));
        using var client = await AuthedClientAsync(factory, ledger);

        var only = await AddConnectionAsync(client, ledger.LedgerId, "only");
        await SetStatusAsync(only, "needs_reauth");

        var outcome = await RunHandlerAsync(factory, _fixture, ledger.LedgerId, ledger.UserId);

        Assert.Equal(JobRunResult.Degraded, outcome.Result);
        Assert.Contains("reconnect", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_faulting_bank_degrades_the_run_without_stopping_the_others()
    {
        var ledger = await LedgerWithLekAsync();
        var fetched = new HashSet<string>(StringComparer.Ordinal);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(fetched, faultOn: "broken"));
        using var client = await AuthedClientAsync(factory, ledger);

        await AddConnectionAsync(client, ledger.LedgerId, "broken");
        await AddConnectionAsync(client, ledger.LedgerId, "healthy");

        fetched.Clear();
        var outcome = await RunHandlerAsync(factory, _fixture, ledger.LedgerId, ledger.UserId);

        Assert.Equal(JobRunResult.Degraded, outcome.Result);
        // The healthy bank was still contacted — degraded is not "gave up".
        Assert.Contains(fetched, u => u.Contains("healthy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ledger_with_no_connections_is_not_a_problem()
    {
        // A schedule can be enabled before any bank is connected. Reporting that as a
        // failure would mark the switch down over a state nobody needs to fix.
        var ledger = await LedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient([]));

        var outcome = await RunHandlerAsync(factory, _fixture, ledger.LedgerId, ledger.UserId);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
    }

    [Fact]
    public async Task A_scheduled_run_is_recorded_as_scheduled_not_manual()
    {
        // triggered_via was hardcoded "manual". The Activity timeline reads it, so every
        // scheduled run would have claimed a person pressed the button.
        var ledger = await LedgerWithLekAsync();
        var fetched = new HashSet<string>(StringComparer.Ordinal);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(fetched));
        using var client = await AuthedClientAsync(factory, ledger);

        await AddConnectionAsync(client, ledger.LedgerId, "bank");
        await RunHandlerAsync(factory, _fixture, ledger.LedgerId, ledger.UserId);

        await using var read = _fixture.NewDbContext();
        var triggers = await read.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledger.LedgerId && o.Family == "ingest")
            .Select(o => o.TriggeredVia)
            .ToListAsync();

        Assert.Contains("scheduled", triggers);
    }
}
