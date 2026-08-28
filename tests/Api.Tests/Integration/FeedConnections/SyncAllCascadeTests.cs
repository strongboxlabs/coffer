using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.FeedConnections;

/// <summary>
/// One unreachable bank must not silence every other connection on the ledger.
/// </summary>
/// <remarks>
/// <para>
/// <c>SyncAllAsync</c>'s own doc comment promises this: "per-connection failures
/// shouldn't cascade — a 403 on one bank just records needs_reauth for that connection
/// and the loop continues." A <c>SimpleFinException</c> — unreachable host, non-2xx,
/// malformed payload — escaped <c>RunPullAsync</c> and unwound the whole loop, so every
/// connection after the failing one was silently never attempted. The response carried no
/// entry for them at all, so nothing said they had been skipped.
/// </para>
/// <para>
/// This matters more once sync is scheduled: five days of one flaky bank would trip the
/// five-strike auto-disable and stop the ledger's feed entirely, with the evidence in a
/// container log.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SyncAllCascadeTests
{
    private readonly PostgresFixture _fixture;

    public SyncAllCascadeTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>A ledger with a wrapped LEK, which a feed connection needs to seal its
    /// access URL. Same shape as FeedConnectionsEndpointsTests' own helper.</summary>
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) => _impl = impl;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private const string Accounts = """
        {"connections":[{"conn_id":"c-1","name":"Test Bank","org_id":"testbank","sfin_url":"https://sfin/test"}],"errlist":[],"accounts":[{"id":"a-1","conn_id":"c-1","name":"Checking","currency":"USD","balance":"0.00","transactions":[]}]}
        """;

    /// <summary>
    /// A stub where the claim/probe always works, but a LATER /accounts fetch for the
    /// nominated access URL faults — which is what an unreachable bank looks like.
    /// </summary>
    private static SimpleFinClient ClientFaultingOn(string faultingAccessUrlFragment, HashSet<string> seen)
        => new(new HttpClient(new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;

            if (req.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // The claim returns the access URL derived from the claim path, so
                    // each connection ends up with a distinguishable access URL.
                    Content = new StringContent(
                        "https://u:p@bridge.simplefin.org/simplefin/access/"
                        + url.Split('/')[^1]),
                };

            seen.Add(url);

            if (url.Contains(faultingAccessUrlFragment, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream is down"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Accounts),
            };
        })));

    [Fact]
    public async Task A_faulting_bank_does_not_skip_the_other_connections()
    {
        var ledger = await LedgerWithLekAsync();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientFaultingOn("broken", seen));
        using var client = await AuthedClientAsync(factory, ledger);

        // Two connections. The first one's access URL contains "broken", so its /accounts
        // fetch faults; the second must still be attempted.
        foreach (var name in new[] { "broken", "healthy" })
        {
            var created = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest
                {
                    SetupToken = SetupTokenFor("https://bridge.simplefin.org/simplefin/claim/" + name),
                });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        seen.Clear();

        var response = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/sync-all", content: null);

        // Not a 500. The whole request used to fail on the first faulting bank.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = body.GetProperty("connections").EnumerateArray().ToList();

        // BOTH connections have an entry — the second one used to be missing entirely,
        // so nothing even said it had been skipped.
        Assert.Equal(2, entries.Count);
        Assert.True(body.GetProperty("hadAnyFailure").GetBoolean());

        // And the healthy bank was genuinely contacted, not just reported on.
        Assert.Contains(seen, u => u.Contains("healthy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_single_faulting_sync_is_a_business_error_not_a_500()
    {
        var ledger = await LedgerWithLekAsync();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientFaultingOn("broken", seen));
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest
            {
                SetupToken = SetupTokenFor("https://bridge.simplefin.org/simplefin/claim/broken"),
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = await created.Content.ReadFromJsonAsync<FeedConnectionSummary>();

        var response = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection!.Id}/sync",
            content: null);

        // The orchestrator's comment claimed this already mapped to 422. Nothing mapped
        // it: SimpleFinException reached UseExceptionHandler and the caller got a 500,
        // which invited the operator to look for a bug on this side of the wire.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("feed-sync-provider-fault", problem.GetProperty("code").GetString());
    }
}
