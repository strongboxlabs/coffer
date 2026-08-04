using System.Net;
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
/// End-to-end checks for ADR-0031 Phase 3c — the orchestrator's
/// brokerage hints persistence. When a sync row's description matches
/// the SimpleFinDescriptionClassifier patterns, the orchestrator
/// writes <c>txn_headers.ingest_action_hint</c>; when the classifier
/// also produced a ticker AND a mapping exists in
/// <c>provider_security_mappings</c>, it writes
/// <c>txn_headers.ingest_security_id</c>. Phase 3d picks these up
/// in the editor's pre-fill flow.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class IngestBrokerageHintsTests
{
    private readonly PostgresFixture _fixture;

    public IngestBrokerageHintsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ----- test infrastructure -----

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private static SimpleFinClient ClientWithStubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new SimpleFinClient(new HttpClient(new StubHandler(handler)));

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

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

    /// <summary>
    /// Create a synthetic ledger, seal it with a wrapped LEK, stand up
    /// an ApiFactory that stubs SimpleFIN to return the supplied
    /// accounts payload, then run the Connect flow. Returns the wired
    /// pieces so the test can map a brokerage + trigger sync.
    /// </summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger, FeedConnectionSummary Connection)>
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
        var summary = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        return (factory, ledger, summary);
    }

    /// <summary>
    /// Bind a synthetic account to a SimpleFIN external id via the
    /// public mapping endpoint. AccountRow's binding fields are
    /// init-only on the entity; we go through the API to flip them.
    /// </summary>
    private static async Task BindAccountAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId,
        Guid connectionId, string externalId)
    {
        var resp = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connectionId,
                SimpleFinAccountId = externalId,
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ----- the tests -----

    [Fact]
    public async Task Sync_buy_description_persists_IngestActionHint_buy()
    {
        // Classifier-matching description with a ticker; no security
        // mapping seeded — so IngestActionHint surfaces but
        // ingest_security_id (view-resolved per ADR-0038) stays null.
        // The editor's pre-fill flow (Phase 3d) lets the user pick
        // the security manually + records the mapping on save.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Brokerage A","org_id":"brokerage-a","sfin_url":"https://sfin/vg"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Brokerage",
               "currency":"USD","balance":"10000.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-300.00",
                 "description":"YOU BOUGHT ACME INDEX FUND S&P 500 ETF (ETFA) (Cash) Cash","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var brokerage = await setup.Ledger.AddInvestmentAccountAsync("Brokerage A");
        await BindAccountAsync(client, setup.Ledger, brokerage.Id, setup.Connection.Id, "sf-1");

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .FirstOrDefaultAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                      && h.ExternalId == "fitid-A");
        Assert.NotNull(header);
        Assert.Equal("buy", header!.IngestActionHint);
        Assert.True(header.NeedsReview);
        // Action stays null — the row is bank-shape (cash-flow →
        // Uncategorized) until the editor upgrades it.
        Assert.Null(header.Action);
        // No mapping seeded → resolved_transactions returns null
        // for ingest_security_id on every leg of this header.
        var resolvedIds = await db.ResolvedTransactions.AsNoTracking()
            .Where(r => r.HeaderId == header.Id)
            .Select(r => r.IngestSecurityId)
            .ToListAsync();
        Assert.NotEmpty(resolvedIds);
        Assert.All(resolvedIds, id => Assert.Null(id));
    }

    [Fact]
    public async Task Sync_buy_description_with_existing_mapping_resolves_security_id_via_view()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Brokerage A","org_id":"brokerage-a","sfin_url":"https://sfin/vg"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Brokerage",
               "currency":"USD","balance":"10000.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-300.00",
                 "description":"YOU BOUGHT ACME INDEX (ETFA) Cash","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var brokerage = await setup.Ledger.AddInvestmentAccountAsync("Brokerage A");
        await BindAccountAsync(client, setup.Ledger, brokerage.Id, setup.Connection.Id, "sf-1");

        // Pre-seed the (simplefin, ETFA) → security mapping. After
        // sync the resolved view should expose the security_id even
        // though no header column stores it (ADR-0038).
        var vooId = await setup.Ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await using (var seed = _fixture.NewDbContext())
        {
            seed.ProviderSecurityMappings.Add(new ProviderSecurityMappingRow
            {
                Id = Guid.NewGuid(),
                LedgerId = setup.Ledger.LedgerId,
                ProviderKey = "simplefin",
                ProviderSecurityId = "ETFA",
                SecurityId = vooId,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .FirstOrDefaultAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                      && h.ExternalId == "fitid-A");
        Assert.NotNull(header);
        Assert.Equal("buy", header!.IngestActionHint);
        Assert.Equal("ETFA", header.IngestSecurityTickerHint);
        var resolvedIds = await db.ResolvedTransactions.AsNoTracking()
            .Where(r => r.HeaderId == header.Id)
            .Select(r => r.IngestSecurityId)
            .ToListAsync();
        Assert.NotEmpty(resolvedIds);
        Assert.All(resolvedIds, id => Assert.Equal(vooId, id));
    }

    [Fact]
    public async Task Sync_non_classifier_description_leaves_hints_null()
    {
        // Regression guard: bank-style descriptions (no "YOU BOUGHT"
        // / "DIVIDEND" / etc. prefix) keep both hints null, preserving
        // the Phase 2 behavior for non-brokerage rows. Existing
        // FeedConnections* tests cover bank cases; this asserts the
        // hint columns specifically stay null.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank","org_id":"b","sfin_url":"https://sfin/b"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-12.34",
                 "description":"STARBUCKS COFFEE PURCHASE","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var checking = await setup.Ledger.AddBankAccountAsync("Checking");
        await BindAccountAsync(client, setup.Ledger, checking.Id, setup.Connection.Id, "sf-1");

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .FirstOrDefaultAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                      && h.ExternalId == "fitid-A");
        Assert.NotNull(header);
        Assert.Null(header!.IngestActionHint);
        Assert.Null(header.IngestSecurityTickerHint);
    }
}
