using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.FeedConnections;

/// <summary>
/// End-to-end checks for
/// <c>POST /api/ledgers/{ledgerId}/feed-connections</c> — Phase 5
/// slice 1, SimpleFIN setup-token exchange. The real SimpleFIN
/// network is stubbed via a delegating <see cref="HttpMessageHandler"/>
/// plugged behind the real <see cref="SimpleFinClient"/> — so the
/// integration test exercises the entire endpoint + client + crypto
/// stack and only intercepts the actual network call. Pins:
///
///   * Sealed access URL is stored in the DB, never plaintext.
///   * The per-ledger LEK round-trips the sealed bytes back to the
///     original URL.
///   * Empty setup token → 422 with a typed code.
///   * Stranger ledger → 422 ledger-not-visible.
///   * SimpleFIN protocol failure → 422 (not 500) with an
///     actionable code.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FeedConnectionsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public FeedConnectionsEndpointsTests(PostgresFixture fixture)
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

    /// <summary>Stub HttpMessageHandler that the real
    /// <see cref="SimpleFinClient"/> sits on top of in these tests.
    /// The delegate decides what to return for each request — the
    /// caller chooses between "happy path" and "protocol failure".</summary>
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

    /// <summary>Build the canned setup-token (base64url of the
    /// claim URL) the user would paste from simplefin.org/setup.</summary>
    private static string SetupTokenFor(string claimUrl)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    [Fact]
    public async Task Post_exchanges_setup_token_and_returns_a_sealed_connection()
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        const string claimUrl = "https://bridge.simplefin.org/simplefin/claim/abc";
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/xyz";

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri == claimUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(accessUrl),
                    };
                }
                // GET /accounts probe — v2.0.0 shape, with the
                // institution name on the top-level connections[]
                // entry (the pre-v2 nested account.org block is
                // gone).
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"connections":[{"conn_id":"c-1","name":"Test Bank","org_id":"testbank","sfin_url":"https://sfin/test"}],"errlist":[],"accounts":[{"id":"a-1","conn_id":"c-1","name":"Checking","currency":"USD","balance":"0.00","transactions":[]}]}
                        """),
                };
            }));
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor(claimUrl) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FeedConnectionSummary>();
        Assert.NotNull(body);
        Assert.Equal(ledger.LedgerId, body!.LedgerId);
        Assert.Equal("simplefin", body.Provider);
        Assert.Equal("Test Bank", body.InstitutionName);
        Assert.Equal("active", body.Status);

        // Core security property: the access URL is sealed in the
        // DB, never stored plaintext.
        await using var db = _fixture.NewDbContext();
        var row = await db.FeedConnections.AsNoTracking()
            .SingleAsync(r => r.Id == body.Id);
        Assert.NotNull(row.AccessUrlCiphertext);
        var asString = Encoding.UTF8.GetString(row.AccessUrlCiphertext!);
        Assert.DoesNotContain(accessUrl, asString, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge.simplefin.org", asString, StringComparison.Ordinal);

        // Round-trip: the same per-ledger LEK should decrypt the
        // ciphertext back to the original access URL.
        var ledgerRow = await db.Ledgers.AsNoTracking()
            .SingleAsync(l => l.Id == ledger.LedgerId);
        var keys = _fixture.NewLedgerKeyService();
        var opened = keys.Open(ledgerRow.WrappedLek!, row.AccessUrlCiphertext!);
        Assert.Equal(accessUrl, Encoding.UTF8.GetString(opened));
    }

    [Fact]
    public async Task Post_returns_422_when_setup_token_is_missing()
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            // Stub never gets called — request rejected before the
            // exchange. Returns a valid-looking response just in
            // case the gate slips.
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("https://u:p@host/access/x"),
                }));
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-setup-token-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_returns_422_ledger_not_visible_for_a_stranger()
    {
        var alice = await SyntheticLedgerWithLekAsync();
        var bob = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("https://u:p@host/access/x"),
                }));
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.PostAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/claim") });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_surfaces_SimpleFin_failure_as_422_with_actionable_code()
    {
        // Backing handler returns 403 — SimpleFIN's response when
        // the setup token is already-consumed or expired. The real
        // SimpleFinClient maps this to SimpleFinException; the
        // endpoint then maps the exception to 422 with a typed
        // code so the SPA can render "generate a fresh token."
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/claim") });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-setup-token-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_lazy_backfills_LEK_on_pre_035_ledger()
    {
        // Pre-035 ledger (no wrapped_lek). On first secret-write
        // the endpoint generates a fresh LEK transparently, wraps
        // it with the current master KEK, persists, then proceeds
        // with the seal. ADR-0026 lazy-backfill path.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        // Deliberately don't patch on a LEK — leaves wrapped_lek NULL.
        const string accessUrl = "https://u:p@bridge.simplefin.org/access/xyz";
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(accessUrl) }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("""{"accounts":[]}""") }));
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/claim") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Ledger row now carries a wrapped LEK + kek_id.
        await using var db = _fixture.NewDbContext();
        var ledgerRow = await db.Ledgers.AsNoTracking()
            .SingleAsync(l => l.Id == ledger.LedgerId);
        Assert.NotNull(ledgerRow.WrappedLek);
        Assert.NotNull(ledgerRow.LekKekId);
        Assert.NotNull(ledgerRow.LekCreatedAt);

        // Sanity: the freshly-backfilled LEK actually unwraps the
        // sealed access URL (uses the same test master KEK).
        var conn = await response.Content.ReadFromJsonAsync<FeedConnectionSummary>();
        var connRow = await db.FeedConnections.AsNoTracking()
            .SingleAsync(c => c.Id == conn!.Id);
        var keys = _fixture.NewLedgerKeyService();
        var opened = keys.Open(ledgerRow.WrappedLek!, connRow.AccessUrlCiphertext!);
        Assert.Equal(accessUrl, Encoding.UTF8.GetString(opened));
    }

    [Fact]
    public async Task Get_lists_connections_for_the_ledger_ordered_by_recency()
    {
        // Two connections, one with a newer LastSyncedAt — assert
        // order. Also pins that per-user RLS scope holds: a stranger
        // sees an empty list, not the alice rows.
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@host/access/a") }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("""{"accounts":[{"org":{"name":"A"}}]}""") }));
        using var client = await AuthedClientAsync(factory, ledger);

        // Two POSTs — distinct setup tokens (claim URLs differ) so
        // SimpleFinClient doesn't error on duplicate exchange.
        var first = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c1") });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c2") });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Touch the second connection's last_synced_at so we can
        // assert ordering. Direct DB write — slice 2b will own this
        // path via the sync worker, but here we just need a stable
        // signal for the ordering assertion.
        var firstId = (await first.Content.ReadFromJsonAsync<FeedConnectionSummary>())!.Id;
        var secondId = (await second.Content.ReadFromJsonAsync<FeedConnectionSummary>())!.Id;
        await using (var db = _fixture.NewDbContext())
        {
            var row = await db.FeedConnections.SingleAsync(r => r.Id == secondId);
            row.LastSyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var listResp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/feed-connections");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = (await listResp.Content.ReadFromJsonAsync<FeedConnectionSummary[]>())!;
        Assert.Equal(2, list.Length);
        Assert.Equal(secondId, list[0].Id);     // synced more recently
        Assert.Equal(firstId, list[1].Id);
    }

    [Fact]
    public async Task Get_returns_422_ledger_not_visible_for_stranger()
    {
        var alice = await SyntheticLedgerWithLekAsync();
        var bob = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.GetAsync(
            $"/api/ledgers/{alice.LedgerId}/feed-connections");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_removes_a_connection()
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("https://u:p@host/access/a") }));
        using var client = await AuthedClientAsync(factory, ledger);

        var create = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        var summary = await create.Content.ReadFromJsonAsync<FeedConnectionSummary>();

        var del = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{summary!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Gone from the list.
        var list = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections"))
            .Content.ReadFromJsonAsync<FeedConnectionSummary[]>())!;
        Assert.Empty(list);
    }

    [Fact]
    public async Task Delete_returns_422_feed_connection_not_found_for_unknown_id()
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-not-found",
            doc.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// SyntheticLedger creates the ledger via raw EF, which doesn't
    /// run the API's <c>LedgersRepository.CreateWithOwnerAsync</c> — so
    /// no LEK is generated. Patch one on with the test fixture's
    /// deterministic <c>LedgerKeyService</c> so the endpoint's seal
    /// step succeeds for the happy-path / per-user-scoping tests.
    /// </summary>
    private async Task<SyntheticLedger> SyntheticLedgerWithLekAsync()
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
}
