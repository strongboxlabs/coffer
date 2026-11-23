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
/// <c>sync_run_promotions</c>, and the run-detail endpoint surfaces
/// the audit trail with cross-ledger RLS enforced. Runs are found the
/// way Settings → Activity finds them, through <c>/ledger-operations</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SyncRunsEndpointsTests : DatabaseTest
{
    public SyncRunsEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
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

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The ledger's SimpleFIN runs, newest first, as Settings → Activity lists
    /// them, each read back through the detail endpoint its row opens.</summary>
    private static async Task<List<SyncRunDetail>> RunsAsync(HttpClient client, Guid ledgerId)
    {
        var listed = (await client.GetFromJsonAsync<List<LedgerOperationSummaryDto>>(
            $"/api/ledgers/{ledgerId}/ledger-operations?provider=simplefin"))!;
        var runs = new List<SyncRunDetail>();
        foreach (var op in listed)
        {
            runs.Add((await client.GetFromJsonAsync<SyncRunDetail>(
                $"/api/ledgers/{ledgerId}/sync-runs/{op.Id}"))!);
        }
        return runs;
    }

    /// <summary>Connect + map an account + sync against the supplied
    /// /accounts payload. Returns enough handles to drive the
    /// follow-up assertions on the sync activity log.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger,
                       FeedConnectionSummary Connection, AccountRow Bank)>
        ConnectMapSyncAsync(string accountsBody, string simpleFinAccountId)
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

        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        var factory = new ApiFactory(Db).WithoutDevAuth()
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

        var run = Assert.Single(await RunsAsync(client, setup.Ledger.LedgerId)).Summary;
        Assert.Equal("completed", run.Status);
        // The Activity list names the connection too, so several connections' runs can be
        // told apart there.
        var listed = (await client.GetFromJsonAsync<List<LedgerOperationSummaryDto>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/ledger-operations?provider=simplefin"))!;
        Assert.Equal(setup.Connection.Id, Assert.Single(listed).FeedConnectionId);
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

        var detail = Assert.Single(await RunsAsync(client, setup.Ledger.LedgerId));
        Assert.Equal("partial", detail.Summary.Status);
        Assert.Equal(1, detail.Summary.ErrorCount);
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
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
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

        var run = Assert.Single(await RunsAsync(client, ledger.LedgerId)).Summary;
        Assert.Equal("needs_reauth", run.Status);
        Assert.Equal(summary.Id, run.FeedConnectionId);
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

        // Second sync — same FITID cleared at a larger amount than the hold.
        await using var factory2 = new ApiFactory(Db).WithoutDevAuth()
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
        var runs = await RunsAsync(client2, setup.Ledger.LedgerId);
        Assert.Equal(2, runs.Count);
        var detail = runs[0]; // newest first
        Assert.Equal(1, detail.Summary.TxnsPromoted);
        Assert.Equal(1, detail.Summary.PromotionCount);
        var promo = Assert.Single(detail.Promotions);
        Assert.Equal(-30.00m, promo.WasAmount);
        Assert.Equal(-35.00m, promo.BecameAmount);
    }

    /// <summary>
    /// A promotion names the currency its two figures are in, the synced account's
    /// (ADR-0114 D15). The ledgers report in a third currency, so neither a constant nor
    /// the reporting currency passes.
    /// </summary>
    [Fact]
    public async Task A_promotion_names_the_currency_of_the_account_it_was_synced_into()
    {
        var yen = await PromotionOnAsync(accountCurrency: "JPY", pending: "-3000", cleared: "-3500");
        Assert.Equal("JPY", yen.CurrencyCode);
        Assert.Equal(-3500m, yen.BecameAmount);

        var dollars = await PromotionOnAsync(accountCurrency: "USD", pending: "-30.00", cleared: "-35.00");
        Assert.Equal("USD", dollars.CurrencyCode);
        Assert.Equal(-35.00m, dollars.BecameAmount);
    }

    /// <summary>A fresh ledger reporting in EUR, one bank account in
    /// <paramref name="accountCurrency"/> bound to a feed account in the same currency, a
    /// pending row synced and then cleared at another figure: the run's one
    /// promotion.</summary>
    private async Task<SyncRunPromotionDto> PromotionOnAsync(string accountCurrency, string pending, string cleared)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        var isPending = true;
        string Body() => $$"""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking","currency":"{{accountCurrency}}","balance":"0","transactions":[
                {"id":"fitid-C","posted":1715000000,"amount":"{{(isPending ? pending : cleared)}}","description":"HOLD","pending":{{(isPending ? "true" : "false")}}}
              ]}
            ]}
            """;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin/access/abc") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body()) }));
        using var client = await AuthedClientAsync(factory, ledger);
        var connection = (await (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking", currencyCode: accountCurrency);
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-1",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        var syncUrl = $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(syncUrl, content: null)).StatusCode);
        isPending = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(syncUrl, content: null)).StatusCode);

        var runs = await RunsAsync(client, ledger.LedgerId);
        return Assert.Single(runs[0].Promotions);
    }

    [Fact]
    public async Task Sync_run_detail_is_refused_under_a_ledger_the_caller_cannot_see()
    {
        // alice syncs, bob can't read her run even via her ledger id.
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
        var runId = Assert.Single(await RunsAsync(aliceClient, alice.Ledger.LedgerId)).Summary.Id;

        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var bobFactory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(bobFactory, bob);

        var resp = await bobClient.GetAsync(
            $"/api/ledgers/{alice.Ledger.LedgerId}/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// Settings → Activity opens a quote refresh's row through the same detail read as
    /// a sync's, so the read serves a run of any family: a quote run's errors come back
    /// with their code and message. The run is written the shape
    /// <c>QuoteOrchestrator.WriteRunAsync</c> writes one: no feed connection, no
    /// failure message, errors with no SimpleFIN ids.
    /// </summary>
    [Fact]
    public async Task Run_detail_serves_a_quote_refresh_with_its_errors()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var runId = Guid.NewGuid();
        await using (var db = ledger.NewDbContext())
        {
            db.LedgerOperations.Add(new LedgerOperationRow
            {
                Id = runId,
                LedgerId = ledger.LedgerId,
                Family = "quote",
                ProviderKey = "quote-refresh",
                TriggeredVia = "manual",
                Status = "partial",
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = DateTime.UtcNow,
            });
            db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = runId,
                LedgerId = ledger.LedgerId,
                Code = Coffer.Api.Quotes.Frankfurter.FrankfurterRatesClient.FetchFailedCode,
                Message = "EUR/USD: HTTP 503 from the exchange-rate service.",
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var detail = (await resp.Content.ReadFromJsonAsync<SyncRunDetail>())!;
        Assert.Equal("partial", detail.Summary.Status);
        Assert.Null(detail.Summary.FeedConnectionId);
        Assert.Null(detail.Summary.ErrorMessage);
        Assert.Equal(1, detail.Summary.ErrorCount);
        var error = Assert.Single(detail.Errors);
        Assert.Equal("fx-fetch-failed", error.Code);
        Assert.Equal("EUR/USD: HTTP 503 from the exchange-rate service.", error.Message);
        Assert.Null(error.SimpleFinConnectionId);
        Assert.Empty(detail.Promotions);
    }

    /// <summary>
    /// The path the Activity log reads end to end: a sync fetches a brokerage the feed
    /// names no currency for and that is mapped to no Coffer account, the run that follows
    /// the sync reads its holdings and fails them, and opening that run names the source
    /// and the account by the feed's own name and institution, not by the feed's id.
    /// </summary>
    [Fact]
    public async Task The_run_after_a_sync_names_the_source_and_the_account_of_a_holdings_error()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking","currency":"USD","balance":"0.00","transactions":[]},
              {"id":"sf-2","conn_id":"c-A","name":"Brokerage","balance":"6750.00","balance-date":1779537600,
               "holdings":[{"id":"HOL-1","symbol":"ETFA","shares":"10","market_value":"6750"}],
               "transactions":[]}
            ]}
            """;
        var setup = await ConnectMapSyncAsync(body, "sf-1");
        var investments = await setup.Ledger.AddInvestmentAccountAsync("Investments");
        var securityId = await setup.Ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await setup.Ledger.AddHoldingAsync(investments.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var listed = (await client.GetFromJsonAsync<List<LedgerOperationSummaryDto>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/ledger-operations"))!;
        var quoteRun = Assert.Single(listed, o => o.Family == "quote");
        Assert.Equal("post-sync", quoteRun.TriggeredVia);
        var detail = (await client.GetFromJsonAsync<SyncRunDetail>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/sync-runs/{quoteRun.Id}"))!;
        var error = Assert.Single(detail.Errors);
        Assert.Equal("currency-missing", error.Code);
        Assert.Equal("simplefin-holdings", error.ProviderKey);
        Assert.Equal("sf-2", error.SimpleFinAccountId);
        Assert.Equal("Brokerage · Bank A", error.AccountLabel);
        Assert.DoesNotContain("sf-2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the detail read calls the account an error names: the Coffer account the feed
    /// account is mapped to, else the feed's name and institution, else nothing (the SPA
    /// then shows the id). An error with no account id has no label, and the source key
    /// is returned as stored.
    /// </summary>
    [Fact]
    public async Task Run_detail_names_each_errors_account_by_mapping_then_by_the_feeds_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var mappedAccount = await ledger.AddBankAccountAsync("Everyday checking");
        var connectionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await using (var db = ledger.NewDbContext())
        {
            db.FeedConnections.Add(new FeedConnectionRow
            {
                Id = connectionId, LedgerId = ledger.LedgerId, Provider = "simplefin", Status = "active",
            });
            db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
            {
                Id = Guid.NewGuid(), FeedConnectionId = connectionId, LedgerId = ledger.LedgerId,
                ExternalId = "sf-mapped", Name = "Feed checking", OrgName = "Bank A",
                LastSeenAt = DateTime.UtcNow,
            });
            db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
            {
                Id = Guid.NewGuid(), FeedConnectionId = connectionId, LedgerId = ledger.LedgerId,
                ExternalId = "sf-unmapped", Name = "Feed brokerage", OrgName = "Bank A",
                LastSeenAt = DateTime.UtcNow,
            });
            db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
            {
                Id = Guid.NewGuid(), FeedConnectionId = connectionId, LedgerId = ledger.LedgerId,
                ExternalId = "sf-no-org", Name = "Feed savings",
                LastSeenAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = ledger.NewDbContext())
        {
            var bound = await new Coffer.Api.Db.Repositories.AccountsRepository(
                    db, new Coffer.Api.Db.Repositories.LegDerivedRecomputeService(db), TimeProvider.System)
                .BindFeedMappingAsync(ledger.LedgerId, mappedAccount.Id, connectionId, "sf-mapped");
            Assert.Equal(Coffer.Api.Db.Repositories.AccountsRepository.BindFeedMappingResult.Ok, bound.Result);
        }
        await using (var db = ledger.NewDbContext())
        {
            db.LedgerOperations.Add(new LedgerOperationRow
            {
                Id = runId, LedgerId = ledger.LedgerId, Family = "quote", ProviderKey = "quote-refresh",
                TriggeredVia = "manual", Status = "partial",
                StartedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow,
            });
            // Written in this order, one second apart, so the read's order is known.
            var at = DateTime.UtcNow.AddSeconds(-10);
            foreach (var (accountId, n) in new (string? AccountId, int N)[]
                     { ("sf-mapped", 0), ("sf-unmapped", 1), ("sf-no-org", 2), ("sf-gone", 3), (null, 4) })
            {
                db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
                {
                    Id = Guid.NewGuid(), LedgerOperationId = runId, LedgerId = ledger.LedgerId,
                    Code = "currency-missing", Message = $"Problem {n}",
                    ProviderKey = "simplefin-holdings", SimpleFinAccountId = accountId,
                    CreatedAt = at.AddSeconds(n),
                });
            }
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var detail = (await client.GetFromJsonAsync<SyncRunDetail>(
            $"/api/ledgers/{ledger.LedgerId}/sync-runs/{runId}"))!;

        Assert.Equal(
            new string?[] { "Everyday checking", "Feed brokerage · Bank A", "Feed savings", null, null },
            detail.Errors.Select(e => e.AccountLabel).ToArray());
        Assert.All(detail.Errors, e => Assert.Equal("simplefin-holdings", e.ProviderKey));
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
        var runId = Assert.Single(await RunsAsync(aliceClient, alice.Ledger.LedgerId)).Summary.Id;

        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var bobFactory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(bobFactory, bob);

        var resp = await bobClient.GetAsync(
            $"/api/ledgers/{bob.LedgerId}/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("sync-run-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }
}
