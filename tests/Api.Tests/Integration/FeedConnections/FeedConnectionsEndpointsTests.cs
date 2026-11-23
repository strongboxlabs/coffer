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
public sealed class FeedConnectionsEndpointsTests : DatabaseTest
{
    public FeedConnectionsEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
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
        new SimpleFinClient(new HttpClient(new StubHandler(handler)), TimeProvider.System);

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

        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using var db = Db.NewDbContext();
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
        var keys = Db.NewLedgerKeyService();
        var opened = keys.Open(ledgerRow.WrappedLek!, row.AccessUrlCiphertext!);
        Assert.Equal(accessUrl, Encoding.UTF8.GetString(opened));
    }

    [Fact]
    public async Task Post_returns_422_when_setup_token_is_missing()
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // Deliberately don't patch on a LEK — leaves wrapped_lek NULL.
        const string accessUrl = "https://u:p@bridge.simplefin.org/access/xyz";
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using var db = Db.NewDbContext();
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
        var keys = Db.NewLedgerKeyService();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using (var db = Db.NewDbContext())
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-not-found",
            doc.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // An account's currency must match what is imported into it
    // (ADR-0114 D13): the bind refuses a feed account in another
    // currency, and the sync refuses a binding the bind could not judge.
    // ------------------------------------------------------------------

    /// <summary>A feed with one account in each of two currencies and one that reports
    /// none, each carrying one posted row.</summary>
    private const string TwoCurrencyFeed = """
        {"connections":[
          {"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}
        ],"errlist":[],"accounts":[
          {"id":"sf-eur","conn_id":"c-A","name":"Account E",
           "currency":"EUR","balance":"0.00","transactions":[
            {"id":"fitid-eur","posted":1715000000,"amount":"-10.00","description":"Row E","pending":false}
          ]},
          {"id":"sf-usd","conn_id":"c-A","name":"Account U",
           "currency":"usd","balance":"0.00","transactions":[
            {"id":"fitid-usd","posted":1715000000,"amount":"-10.00","description":"Row U","pending":false}
          ]},
          {"id":"sf-none","conn_id":"c-A","name":"Account N",
           "balance":"0.00","transactions":[]}
        ]}
        """;

    /// <summary>A connection on a ledger whose SimpleFIN answers every fetch with
    /// <paramref name="accountsBody"/>.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger, Guid ConnectionId)> ConnectAsync(
        string accountsBody)
    {
        var ledger = await SyntheticLedgerWithLekAsync();
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accountsBody) }));
        using var client = await AuthedClientAsync(factory, ledger);
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") });
        created.EnsureSuccessStatusCode();
        var connection = (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        return (factory, ledger, connection.Id);
    }

    private static Task<HttpResponseMessage> BindAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, Guid connectionId, string externalId) =>
        client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connectionId,
                SimpleFinAccountId = externalId,
            }),
        });

    private async Task<string?> BoundExternalIdAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId).Select(a => a.ExternalId).SingleAsync();
    }

    [Fact]
    public async Task Binding_a_feed_account_in_another_currency_is_refused_and_one_in_the_accounts_own_binds()
    {
        var setup = await ConnectAsync(TwoCurrencyFeed);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);
        var usdAccount = await setup.Ledger.AddBankAccountAsync("account-a");
        var eurAccount = await setup.Ledger.AddBankAccountAsync("account-b", currencyCode: "EUR");
        var otherUsdAccount = await setup.Ledger.AddBankAccountAsync("account-c");

        // A first sync fills the connection's directory, each feed account with its currency.
        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.ConnectionId}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var refused = await BindAsync(client, setup.Ledger, usdAccount.Id, setup.ConnectionId, "sf-eur");
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{(int)refused.StatusCode}: {refusedBody}");
        using (var doc = JsonDocument.Parse(refusedBody))
        {
            Assert.Equal("feed-account-currency-mismatch", doc.RootElement.GetProperty("code").GetString());
            var detail = doc.RootElement.GetProperty("detail").GetString()!;
            Assert.Contains("EUR", detail, StringComparison.Ordinal);
            Assert.Contains("USD", detail, StringComparison.Ordinal);
        }
        Assert.Null(await BoundExternalIdAsync(usdAccount.Id));

        // The anchor: the same feed account binds to an account in its own currency.
        var bound = await BindAsync(client, setup.Ledger, eurAccount.Id, setup.ConnectionId, "sf-eur");
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        Assert.Equal("sf-eur", await BoundExternalIdAsync(eurAccount.Id));

        // A feed account that reports its code in another case is the same currency.
        Assert.Equal(HttpStatusCode.NoContent,
            (await BindAsync(client, setup.Ledger, otherUsdAccount.Id, setup.ConnectionId, "sf-usd")).StatusCode);

        // A feed account that reports no currency is not judged.
        Assert.Equal(HttpStatusCode.NoContent,
            (await BindAsync(client, setup.Ledger, usdAccount.Id, setup.ConnectionId, "sf-none")).StatusCode);
        Assert.Equal("sf-none", await BoundExternalIdAsync(usdAccount.Id));
    }

    /// <summary>
    /// A binding the bind could not judge, because the directory did not yet know the
    /// feed account's currency, is judged at sync: that account's pull fails with an
    /// error naming both currencies and none of its rows lands, while an account bound in
    /// its own currency on the same connection imports as before (ADR-0114 D13).
    /// </summary>
    [Fact]
    public async Task A_binding_in_another_currency_made_before_the_directory_knew_it_fails_that_accounts_pull()
    {
        var setup = await ConnectAsync(TwoCurrencyFeed);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);
        var usdAccount = await setup.Ledger.AddBankAccountAsync("account-a");
        var otherUsdAccount = await setup.Ledger.AddBankAccountAsync("account-b");

        // No sync has run, so the directory is empty and the bind has nothing to judge.
        Assert.Equal(HttpStatusCode.NoContent,
            (await BindAsync(client, setup.Ledger, usdAccount.Id, setup.ConnectionId, "sf-eur")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await BindAsync(client, setup.Ledger, otherUsdAccount.Id, setup.ConnectionId, "sf-usd")).StatusCode);

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.ConnectionId}/sync", content: null);
        var syncBody = await sync.Content.ReadAsStringAsync();
        Assert.True(sync.StatusCode == HttpStatusCode.OK, $"{(int)sync.StatusCode}: {syncBody}");
        var result = JsonSerializer.Deserialize<SyncResultDto>(syncBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var error = Assert.Single(result.Errors);
        Assert.Equal("feed-account-currency-mismatch", error.Code);
        Assert.Contains("EUR", error.Message, StringComparison.Ordinal);
        Assert.Contains("USD", error.Message, StringComparison.Ordinal);
        // The anchor: the account bound in its own currency imported its row.
        Assert.Equal(1, result.TransactionsForReview);

        await using var db = Db.NewDbContext();
        var landed = await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == setup.Ledger.LedgerId)
            .Select(l => l.AccountId)
            .ToListAsync();
        Assert.DoesNotContain(usdAccount.Id, landed);
        Assert.Contains(otherUsdAccount.Id, landed);
        // The failed account keeps its watermark, so a fixed binding fetches the same window.
        Assert.Null(await db.Accounts.AsNoTracking()
            .Where(a => a.Id == usdAccount.Id).Select(a => a.LastSimpleFinSyncAt).SingleAsync());
        // The directory still records what the feed reports.
        Assert.Equal("EUR", await db.FeedConnectionAccounts.AsNoTracking()
            .Where(d => d.FeedConnectionId == setup.ConnectionId && d.ExternalId == "sf-eur")
            .Select(d => d.Currency).SingleAsync());
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using var db = Db.NewDbContext();
        var row = await db.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
        row.WrappedLek = keys.CreateWrappedLek();
        row.LekKekId = keys.CurrentKekId;
        row.LekCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ledger;
    }
}
