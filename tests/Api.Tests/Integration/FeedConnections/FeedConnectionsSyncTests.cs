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
/// End-to-end checks for the slice-2b sync flow:
///   <c>POST /api/ledgers/{ledgerId}/feed-connections/{connectionId}/sync</c>
/// + <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping</c>.
/// Stub the SimpleFIN backend; assert (a) unmapped accounts come
/// back for the wizard, (b) FITID-dedup against existing
/// <c>txn_headers</c> rows, (c) unmatched rows land directly in
/// <c>txn_headers</c> with <c>needs_review=true</c> idempotently
/// across re-sync (slice 2c eliminated the legacy
/// <c>pending_transactions</c> staging table), and (d) the mapping
/// endpoint binds an existing Coffer account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FeedConnectionsSyncTests
{
    private readonly PostgresFixture _fixture;

    public FeedConnectionsSyncTests(PostgresFixture fixture)
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

    /// <summary>Run a Connect + Sync flow against the same stubbed
    /// SimpleFIN. The stub responds to POST (claim) with a fake
    /// access URL and to GET (/accounts) with the supplied
    /// transaction payload.</summary>
    private async Task<(ApiFactory Factory, SyntheticLedger Ledger, FeedConnectionSummary Connection)>
        ConnectAsync(string accountsBody)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        // Patch on a wrapped LEK so the seal step in the connect
        // path succeeds (the synthetic-ledger helper doesn't run the
        // API's CreateWithOwnerAsync path).
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

    [Fact]
    public async Task First_sync_upserts_every_SimpleFIN_account_into_the_connection_directory()
    {
        // Slice 2c.4: the bank-side account directory is now
        // persisted in `feed_connection_accounts`; the SPA reads
        // it via GET /feed-connections/{cid}/accounts. After a
        // first sync against a 2-account feed, that endpoint
        // returns both rows with null bindings (no mappings yet).
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[]},
              {"id":"sf-2","conn_id":"c-A","name":"Savings",
               "currency":"USD","balance":"500.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;

        Assert.Equal(2, result.AccountsDiscovered);
        Assert.Equal(0, result.TransactionsForReview);
        Assert.Equal(0, result.TransactionsStillPending);
        Assert.Equal(0, result.AlreadyKnown);

        // GET the per-connection accounts list — backs the unified
        // accounts panel. Both SimpleFIN accounts surface; neither
        // is bound to a Coffer account.
        var dir = await client.GetFromJsonAsync<List<FeedConnectionAccountDto>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/accounts");
        Assert.NotNull(dir);
        Assert.Equal(2, dir!.Count);
        Assert.Contains(dir, a => a.SimpleFinAccountId == "sf-1"
                                 && a.Name == "Checking"
                                 && a.BoundLedgerAccountId == null);
        Assert.Contains(dir, a => a.SimpleFinAccountId == "sf-2"
                                 && a.Name == "Savings"
                                 && a.BoundLedgerAccountId == null);
    }

    [Fact]
    public async Task Sync_posted_rows_land_in_txn_headers_with_needs_review()
    {
        // Slice 2c: bank-posted rows go straight into txn_headers
        // with needs_review=true (modern aggregator pattern). Both
        // rows below are pending=false on the wire, so both should
        // be in the register flagged for review after the sync.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false},
                {"id":"fitid-B","posted":1715100000,"amount":"-5.00","description":"BUS","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        // Seed a Coffer account + bind it to sf-1.
        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        var mapResp = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });
        Assert.Equal(HttpStatusCode.NoContent, mapResp.StatusCode);

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(1, result.AccountsDiscovered);
        Assert.Equal(2, result.TransactionsForReview);
        Assert.Equal(0, result.TransactionsStillPending);
        Assert.Equal(0, result.AlreadyKnown);

        // DB verification: two txn_headers, both flagged for
        // review, neither bank-pending. Symmetric posting wired to
        // Uncategorized (lazy-created on first row).
        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == setup.Ledger.LedgerId
                        // Mig 107: origin is icon-level; SimpleFIN
                        // identification moved to provider_key.
                        && h.ProviderKey == "simplefin")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(2, headers.Count);
        Assert.All(headers, h => Assert.True(h.NeedsReview));
        Assert.All(headers, h => Assert.False(h.IsPending));
        Assert.Equal("fitid-A", headers[0].ExternalId);
        // OFX-protocol columns are NOT written by SimpleFIN (mig 105).
        // SimpleFIN ids are not OFX FITIDs; SimpleFIN org_id is not
        // an OFX FI_ID. Future OFX/QFX direct importers populate these.
        Assert.Null(headers[0].OnlineMatchFitid);
        Assert.Null(headers[0].OnlineMatchFiId);
        Assert.Equal("COFFEE", headers[0].Payee);

        // Both legs of fitid-A: source on the bank account
        // (negative) + counterparty on Uncategorized (positive).
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headers[0].Id)
            .OrderBy(l => l.Amount)
            .ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.Equal(-12.34m, legs[0].Amount);
        Assert.Equal(bank.Id, legs[0].AccountId);
        Assert.Equal(12.34m, legs[1].Amount);

        // Uncategorized was lazy-created exactly once for the
        // ledger, marked is_system=true so account-tree pickers
        // can filter it.
        var uncategorized = await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.Id == legs[1].AccountId);
        Assert.Equal("Uncategorized", uncategorized.Name);
        Assert.True(uncategorized.IsSystem);
        Assert.Equal("category", uncategorized.AccountType);
        Assert.Equal("expense", uncategorized.CategoryKind);

    }

    [Fact]
    public async Task Sync_pending_rows_land_in_txn_headers_with_is_pending_and_needs_review()
    {
        // Slice 2c: bank-pending rows (SimpleFIN pending=true) also
        // go into txn_headers — unified data model — with BOTH
        // is_pending and needs_review set. They show in the
        // register at their date position with a pending badge.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[
                {"id":"fitid-P","posted":1715000000,"amount":"-9.99","description":"PENDING COFFEE","pending":true}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(0, result.TransactionsForReview);
        Assert.Equal(1, result.TransactionsStillPending);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == setup.Ledger.LedgerId
                              && h.ExternalId == "fitid-P");
        Assert.True(header.IsPending);
        Assert.True(header.NeedsReview);
    }

    [Fact]
    public async Task Re_sync_of_same_FITID_is_idempotent()
    {
        // Slice 2c: re-sync of the same payload finds the FITID
        // already in txn_headers and skips. No duplicate rows.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        var first = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var firstResult = (await first.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(1, firstResult.TransactionsForReview);

        var second = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var secondResult = (await second.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(0, secondResult.TransactionsForReview);
        Assert.Equal(1, secondResult.AlreadyKnown);

        await using var db = _fixture.NewDbContext();
        Assert.Equal(1, await db.TxnHeaders
            .CountAsync(h => h.ExternalId == "fitid-A"
                             && h.LedgerId == setup.Ledger.LedgerId));
    }

    [Fact]
    public async Task Delete_then_resync_does_not_reinsert_same_FITID()
    {
        // Regression for mig 105's motivating bug: a user "deletes"
        // a SimpleFIN-synced row, the very next sync (same FITID)
        // re-inserts it as a new row.
        //
        // The hidden chain was:
        //   1. SimpleFIN ingest used to write the FITID into
        //      online_match_fitid, leaving external_id NULL.
        //   2. DeleteAsync picks soft-hide vs hard-delete via
        //      external_id IS NULL → SimpleFIN rows took the
        //      hard-delete branch, vanishing from the DB.
        //   3. Next sync dedup looked at online_match_fitid; the row
        //      was gone → treated as new → inserted again.
        //
        // After mig 105: SimpleFIN id → external_id, dedup keys off
        // external_id scoped by origin, DELETE goes through the
        // soft-hide branch (external_id is NOT NULL), and the
        // re-sync finds the soft-hidden row and alreadyKnowns it.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[
                {"id":"fitid-Z","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false}
              ]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        // 1st sync — row lands.
        var first = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<SyncResultDto>())!.TransactionsForReview);

        Guid landedId;
        await using (var db = _fixture.NewDbContext())
        {
            var landed = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                  && h.ExternalId == "fitid-Z");
            // Mig 107: origin is icon-level; SimpleFIN-specific
            // detail moved to provider_key.
            Assert.Equal("online_import", landed.Origin);
            Assert.Equal("simplefin", landed.ProviderKey);
            Assert.Null(landed.OnlineMatchFitid); // mig 105: OFX columns NOT written
            Assert.Null(landed.OnlineMatchFiId);
            landedId = landed.Id;
        }

        // 2. User deletes the row. external_id is set → soft-hide
        // branch returns 200 with `{"outcome":"soft-hidden"}`.
        var del = await client.DeleteAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/transactions/{landedId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var outcome = await del.Content.ReadFromJsonAsync<DeleteTransactionResponse>();
        Assert.Equal("soft-hidden", outcome!.Kind);
        await using (var verify = _fixture.NewDbContext())
        {
            var hidden = await verify.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.Id == landedId);
            Assert.True(hidden.IsHidden);
            Assert.Equal("fitid-Z", hidden.ExternalId); // preserved
        }

        // 3. Re-sync with the SAME FITID. Dedup finds the
        // soft-hidden row by (ledger, origin, external_id) and
        // skips. No new row.
        var second = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var secondResult = (await second.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(0, secondResult.TransactionsForReview);
        Assert.Equal(1, secondResult.AlreadyKnown);

        await using (var verify = _fixture.NewDbContext())
        {
            var count = await verify.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                 && h.ExternalId == "fitid-Z");
            Assert.Equal(1, count); // still exactly one row
        }
    }

    [Fact]
    public async Task Promote_on_clear_flips_is_pending_and_updates_leg_amounts()
    {
        // Slice 2c: a previously-pending FITID re-arriving with
        // pending=false flips is_pending=false on the existing row
        // (not a new insert) and rewrites both leg amounts (banks
        // adjust the cleared amount vs the pending hold, e.g.
        // restaurant tip). needs_review is NOT cleared — the
        // user's workflow state is independent of bank-side state.
        var setup = await ConnectAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[
                {"id":"fitid-X","posted":1715000000,"amount":"-30.00","description":"DINER HOLD","pending":true}
              ]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        // First sync — bank-pending row lands.
        var first = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var firstResult = (await first.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal(1, firstResult.TransactionsStillPending);

        await using (var db = _fixture.NewDbContext())
        {
            var pending = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                  && h.ExternalId == "fitid-X");
            Assert.True(pending.IsPending);
        }

        // Second sync — same FITID, now pending=false, amount
        // bumped from $30 to $35 (tip). Stub a new handler so the
        // second GET returns the cleared payload.
        await using var promoteFactory = new ApiFactory(_fixture).WithoutDevAuth()
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
        using var promoteClient = await AuthedClientAsync(promoteFactory, setup.Ledger);
        var second = await promoteClient.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        var secondResult = (await second.Content.ReadFromJsonAsync<SyncResultDto>())!;
        // The promote-on-clear path counts under TransactionsForReview
        // (one row landed in the "for review" register surface this
        // run, even though it's an update not an insert).
        Assert.Equal(1, secondResult.TransactionsForReview);
        Assert.Equal(0, secondResult.TransactionsStillPending);

        await using var verify = _fixture.NewDbContext();
        var promoted = await verify.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == setup.Ledger.LedgerId
                              && h.ExternalId == "fitid-X");
        Assert.False(promoted.IsPending);
        Assert.True(promoted.NeedsReview); // user workflow state untouched
        var legs = await verify.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == promoted.Id)
            .OrderBy(l => l.Amount)
            .ToListAsync();
        Assert.Equal(-35.00m, legs[0].Amount);
        Assert.Equal(35.00m, legs[1].Amount);
    }

    [Fact]
    public async Task Patch_with_approve_clears_needs_review_on_a_synced_row()
    {
        // Slice 2c.6a: the dedicated POST /approve endpoint was
        // collapsed into PATCH with `approve: true`. This test
        // covers the sync-side integration — a bank-feed row that
        // landed with needs_review=true gets cleared via PATCH.
        var setup = await ConnectAsync("""
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[
                {"id":"fitid-A","posted":1715000000,"amount":"-12.34","description":"COFFEE","pending":false}
              ]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });
        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        Guid headerId;
        await using (var db = _fixture.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == setup.Ledger.LedgerId
                                  && h.ExternalId == "fitid-A");
            headerId = header.Id;
            Assert.True(header.NeedsReview);
        }

        var approve = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/transactions/{headerId}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest { Approve = true }),
            });
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        await using (var verify = _fixture.NewDbContext())
        {
            var header = await verify.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.Id == headerId);
            Assert.False(header.NeedsReview);
        }

        // Idempotent — second PATCH with approve=true returns 204 too.
        var again = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/transactions/{headerId}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest { Approve = true }),
            });
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Patch_with_approve_422_when_header_belongs_to_another_ledger()
    {
        // Cross-ledger guard: alice's header id can't be approved
        // through bob's ledger scope (slice 2c.6a — same guard
        // applies via PATCH as the prior POST /approve enforced).
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("alice-checking");
        var aliceCat = await alice.AddCategoryAsync("food");
        var (legId, _) = await alice.AddTransactionPairAsync(
            aliceBank.Id, aliceCat.Id, -12.34m,
            new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc),
            payee: "Coffee");
        var aliceHeaderId = await alice.ResolveHeaderIdAsync(legId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{bob.LedgerId}/transactions/{aliceHeaderId}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest { Approve = true }),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("transaction-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    // Removed in mig 105. This test asserted that a SimpleFIN sync
    // would dedup against a manual row whose online_match_fitid had
    // been hand-stamped to equal the incoming SimpleFIN transaction
    // id. That scenario doesn't reflect real provider behaviour:
    // SimpleFIN ids are proprietary strings (`TRN-<uuid>` etc.), not
    // OFX FITIDs, so the equality never holds in production.
    // Cross-source dedup against MD's preserved OFX state is a real
    // need that belongs to the OFX/QFX direct importers (which DO
    // write OFX FITIDs natively into online_match_fitid). They will
    // ship with their own cross-source dedup test. SimpleFIN dedup
    // is now origin-scoped on external_id — covered by
    // Re_sync_of_same_FITID_is_idempotent above and the
    // delete-then-sync regression test in BalanceMergeHideSyncTests.

    [Fact]
    public async Task Sync_403_flips_connection_to_needs_reauth_and_returns_typed_status()
    {
        // Defensive-API contract end-to-end: a 403 on the v2
        // /accounts call must NOT bubble as a 422, must flip
        // feed_connections.status='needs_reauth', and must return
        // a typed SyncResultDto so the SPA can render a re-connect
        // CTA instead of a generic error toast.
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

        // First request (POST = claim) succeeds; second (GET =
        // /accounts on connect-time probe) and third (GET =
        // /accounts on sync) both return 403.
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
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var summary = (await createResp.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var sync = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{summary.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal("needs_reauth", result.ConnectionStatus);
        Assert.Equal(0, result.AccountsDiscovered);

        // DB verification: the row's status really flipped.
        await using var db = _fixture.NewDbContext();
        var stored = await db.FeedConnections
            .Where(c => c.Id == summary.Id)
            .Select(c => c.Status)
            .SingleAsync();
        Assert.Equal("needs_reauth", stored);
    }

    [Fact]
    public async Task Sync_surfaces_errlist_entries_alongside_success_counts()
    {
        // SimpleFIN v2 partial-failure: accounts may sync cleanly
        // while errlist[] carries per-connection messages (e.g.
        // "Bank A is in maintenance"). The endpoint must forward
        // both verbatim so the SPA shows the partial-failure banner
        // next to the success summary.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[
              {"code":"fi.maintenance","msg":"Bank A maintenance window","conn_id":"c-A"}
            ],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal("active", result.ConnectionStatus);
        Assert.Single(result.Errors);
        Assert.Equal("fi.maintenance", result.Errors[0].Code);
        Assert.Equal("Bank A maintenance window", result.Errors[0].Message);
        Assert.Equal("c-A", result.Errors[0].SimpleFinConnectionId);
    }

    [Fact]
    public async Task Patch_feed_mapping_422_when_connection_belongs_to_another_ledger()
    {
        // alice connects; bob (different ledger) tries to map an
        // account onto alice's connection id. Cross-ledger guard
        // must reject.
        var alice = (await ConnectAsync("""{"connections":[],"errlist":[],"accounts":[]}""")).Connection;
        var bobLedger = await SyntheticLedger.CreateAsync(_fixture);
        var bobAccount = await bobLedger.AddBankAccountAsync("bob-checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"connections":[],"errlist":[],"accounts":[]}""") }));
        using var bobClient = await AuthedClientAsync(factory, bobLedger);

        var response = await bobClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{bobLedger.LedgerId}/accounts/{bobAccount.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = alice.Id,        // alice's connection id
                    SimpleFinAccountId = "sf-1",
                }),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-mapping-connection-mismatch",
            doc.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // GET /api/ledgers/{ledgerId}/feed-connections/{connectionId}/accounts
    // (slice 2c.4 — unified accounts list, backs the bank-feeds panel)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Connection_accounts_list_reflects_binding_after_patch_mapping()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[]},
              {"id":"sf-2","conn_id":"c-A","name":"Savings",
               "currency":"USD","balance":"500.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        var mapResp = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });
        Assert.Equal(HttpStatusCode.NoContent, mapResp.StatusCode);

        var dir = await client.GetFromJsonAsync<List<FeedConnectionAccountDto>>(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/accounts");
        Assert.NotNull(dir);
        Assert.Equal(2, dir!.Count);

        var checking = dir.Single(a => a.SimpleFinAccountId == "sf-1");
        Assert.Equal(bank.Id, checking.BoundLedgerAccountId);
        Assert.Equal("checking", checking.BoundLedgerAccountName);

        var savings = dir.Single(a => a.SimpleFinAccountId == "sf-2");
        Assert.Null(savings.BoundLedgerAccountId);
        Assert.Null(savings.BoundLedgerAccountName);
    }

    [Fact]
    public async Task Connection_accounts_list_422_for_unknown_connection()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}") }));
        using var client = await AuthedClientAsync(factory, ledger);

        var unknownConnectionId = Guid.NewGuid();
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{unknownConnectionId}/accounts");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-not-found",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Connection_accounts_list_422_when_connection_belongs_to_another_ledger()
    {
        // alice owns the connection; bob is a different ledger asking
        // for it via the path that swaps the ledgerId. The
        // BelongsToLedgerAsync guard must reject (RLS already hides
        // the row, but the explicit guard surfaces a typed code).
        var alice = (await ConnectAsync("""{"connections":[],"errlist":[],"accounts":[]}""")).Connection;
        var bobLedger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => ClientWithStubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}") }));
        using var bobClient = await AuthedClientAsync(factory, bobLedger);

        var response = await bobClient.GetAsync(
            $"/api/ledgers/{bobLedger.LedgerId}/feed-connections/{alice.Id}/accounts");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("feed-connection-not-found",
            doc.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // DELETE /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping
    // (slice 2c.4 — explicit unmap, paired with PATCH bind)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_feed_mapping_clears_the_binding_columns()
    {
        var setup = await ConnectAsync("""
            {"connections":[],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        var bind = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });
        Assert.Equal(HttpStatusCode.NoContent, bind.StatusCode);

        var unmap = await client.DeleteAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping");
        Assert.Equal(HttpStatusCode.NoContent, unmap.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.Null(row.FeedConnectionId);
        Assert.Null(row.ExternalId);
    }

    [Fact]
    public async Task Delete_feed_mapping_is_idempotent_on_an_already_unmapped_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_feed_mapping_422_for_unknown_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{Guid.NewGuid()}/feed-mapping");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_feed_mapping_422_when_account_belongs_to_another_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceAccount = await alice.AddBankAccountAsync("alice-checking");
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.DeleteAsync(
            $"/api/ledgers/{bob.LedgerId}/accounts/{aliceAccount.Id}/feed-mapping");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------
    // Directory upsert idempotency — re-syncs bump last_seen_at on the
    // same (feed_connection_id, external_id) row, never create dupes.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Subsequent_sync_upserts_directory_idempotently_and_bumps_last_seen_at()
    {
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"100.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        DateTime firstSeen;
        await using (var db = _fixture.NewDbContext())
        {
            var row = await db.FeedConnectionAccounts.AsNoTracking()
                .SingleAsync(a => a.FeedConnectionId == setup.Connection.Id
                                  && a.ExternalId == "sf-1");
            firstSeen = row.LastSeenAt;
        }

        // Re-sync. Same external_id, same connection — upsert path
        // must update the existing row, not insert a duplicate.
        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        await using var db2 = _fixture.NewDbContext();
        var rows = await db2.FeedConnectionAccounts.AsNoTracking()
            .Where(a => a.FeedConnectionId == setup.Connection.Id)
            .ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].LastSeenAt >= firstSeen,
            "last_seen_at must advance (or hold) across re-syncs.");
    }

    // ------------------------------------------------------------------
    // Per-account sync watermark (slice 2c.5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sync_advances_watermark_for_mapped_accounts_only()
    {
        // Two SimpleFIN accounts; only one is mapped on the Coffer
        // side. After a successful sync, only the mapped account's
        // accounts.last_simplefin_sync_at should advance — unmapped
        // accounts have no Coffer row to write through to.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[],"accounts":[
              {"id":"sf-mapped","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]},
              {"id":"sf-unmapped","conn_id":"c-A","name":"Savings",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-mapped",
                }),
            });

        var before = DateTime.UtcNow;
        var sync = await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        await using var db = _fixture.NewDbContext();
        var bankRow = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.NotNull(bankRow.LastSimpleFinSyncAt);
        Assert.True(bankRow.LastSimpleFinSyncAt >= before,
            "Mapped account's watermark must advance to at least the moment the sync started.");
    }

    [Fact]
    public async Task Sync_does_not_advance_watermark_for_account_in_errlist()
    {
        // SimpleFIN returns 200 with a per-account error in errlist
        // tagging the mapped account by its external id. The
        // server-side run completes as "partial"; the affected
        // account's watermark must NOT advance so the next sync
        // retries the same window.
        const string body = """
            {"connections":[
              {"conn_id":"c-A","name":"Bank A","org_id":"banka",
               "sfin_url":"https://sfin/banka"}
            ],"errlist":[
              {"code":"sync.unavailable","msg":"Maintenance window.",
               "conn_id":"c-A","account_id":"sf-broken"}
            ],"accounts":[
              {"id":"sf-ok","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """;
        var setup = await ConnectAsync(body);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var ok = await setup.Ledger.AddBankAccountAsync("ok");
        var broken = await setup.Ledger.AddBankAccountAsync("broken");
        foreach (var (acct, sfinId) in new[] { (ok, "sf-ok"), (broken, "sf-broken") })
        {
            await client.SendAsync(new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{acct.Id}/feed-mapping")
                {
                    Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                    {
                        FeedConnectionId = setup.Connection.Id,
                        SimpleFinAccountId = sfinId,
                    }),
                });
        }

        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        await using var db = _fixture.NewDbContext();
        var okRow = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == ok.Id);
        var brokenRow = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == broken.Id);
        Assert.NotNull(okRow.LastSimpleFinSyncAt);
        Assert.Null(brokenRow.LastSimpleFinSyncAt);
    }

    [Fact]
    public async Task Unmap_clears_per_account_watermark()
    {
        // Re-mapping should start a fresh 90-day window. Leaving an
        // old watermark on the row would silently narrow the new
        // binding's first sync.
        var setup = await ConnectAsync("""
            {"connections":[],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        // Drive a sync so the watermark gets set.
        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);

        // Now unmap and verify the watermark is cleared.
        await client.DeleteAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping");

        await using var db = _fixture.NewDbContext();
        var row = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.Null(row.FeedConnectionId);
        Assert.Null(row.ExternalId);
        Assert.Null(row.LastSimpleFinSyncAt);
    }

    // ------------------------------------------------------------------
    // PATCH /accounts/{id}/sync-from-date (slice 2c.5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PatchSyncFromDate_sets_the_per_account_watermark()
    {
        var setup = await ConnectAsync("""
            {"connections":[],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        var chosen = new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/sync-from-date",
            new PatchAccountSyncFromDateRequest { SyncFromDate = chosen });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.Equal(chosen, row.LastSimpleFinSyncAt);
    }

    [Fact]
    public async Task PatchSyncFromDate_null_clears_the_watermark()
    {
        var setup = await ConnectAsync("""
            {"connections":[],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        // Sync first so the watermark gets set, then clear it.
        await client.PostAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/feed-connections/{setup.Connection.Id}/sync",
            content: null);
        await using (var db = _fixture.NewDbContext())
        {
            var pre = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
            Assert.NotNull(pre.LastSimpleFinSyncAt);
        }

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/sync-from-date",
            new PatchAccountSyncFromDateRequest { SyncFromDate = null });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db2 = _fixture.NewDbContext();
        var row = await db2.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.Null(row.LastSimpleFinSyncAt);
    }

    [Fact]
    public async Task PatchSyncFromDate_422_for_future_date()
    {
        var setup = await ConnectAsync("""
            {"connections":[],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking",
               "currency":"USD","balance":"0.00","transactions":[]}
            ]}
            """);
        await using var factory = setup.Factory;
        using var client = await AuthedClientAsync(factory, setup.Ledger);

        var bank = await setup.Ledger.AddBankAccountAsync("checking");
        await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
            {
                Content = JsonContent.Create(new PatchAccountFeedMappingRequest
                {
                    FeedConnectionId = setup.Connection.Id,
                    SimpleFinAccountId = "sf-1",
                }),
            });

        var future = DateTime.UtcNow.AddDays(7);
        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{setup.Ledger.LedgerId}/accounts/{bank.Id}/sync-from-date",
            new PatchAccountSyncFromDateRequest { SyncFromDate = future });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sync-from-date-in-future",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PatchSyncFromDate_422_for_unmapped_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/sync-from-date",
            new PatchAccountSyncFromDateRequest
            {
                SyncFromDate = DateTime.UtcNow.AddDays(-30),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-bound-to-feed",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PatchSyncFromDate_422_when_account_belongs_to_another_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceAccount = await alice.AddBankAccountAsync("alice-checking");
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.PatchAsJsonAsync(
            $"/api/ledgers/{bob.LedgerId}/accounts/{aliceAccount.Id}/sync-from-date",
            new PatchAccountSyncFromDateRequest
            {
                SyncFromDate = DateTime.UtcNow.AddDays(-30),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }
}
