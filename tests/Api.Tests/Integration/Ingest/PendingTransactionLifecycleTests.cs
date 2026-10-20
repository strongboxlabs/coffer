using System.Net;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// A PENDING transaction, and the moment it stops being one (dimension 14).
/// </summary>
/// <remarks>
/// <para>The fixture audit filed this under transaction shape and scored the
/// test-built column "~". Measuring found something more specific: no import
/// path can produce it at all. <c>TransactionMapper</c>,
/// <c>InvestmentTransactionMapper</c> and <c>ReminderMapper</c> each hard-code
/// <c>IsPending: false</c>, so a Moneydance fixture is structurally incapable
/// of carrying one — the same shape of answer as <c>reconciling</c>, which the
/// importer also cannot emit. <b>The only writer that ever sets it true is the
/// feed ingest path</b>, from a provider's own pending flag.</para>
///
/// <para>So the gap is not a fixture's. <c>SimpleFinClientTests</c> already
/// proves the wire's <c>pending</c> parses; nothing downstream of that proved
/// anything. What was untested is the whole reason the column exists: a
/// transaction arrives pending, the bank posts it a day later under the SAME
/// provider id, and the row has to FLIP rather than duplicate.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PendingTransactionLifecycleTests
{
    private readonly PostgresFixture _fixture;

    public PendingTransactionLifecycleTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The same transaction id, twice: pending, then posted.</summary>
    private static string Body(bool pending) => $$"""
        {"connections":[
          {"conn_id":"c-P","name":"Bank P","org_id":"bankp","sfin_url":"https://sfin/bankp"}
        ],"errlist":[],"accounts":[
          {"id":"sf-p","conn_id":"c-P","name":"Checking","currency":"USD","balance":"100.00","transactions":[
            {"id":"pending-1","posted":1715000000,"amount":"-42.50","description":"HOLD AT PUMP","pending":{{(pending ? "true" : "false")}}}
          ]}
        ]}
        """;

    [Fact]
    public async Task A_pending_transaction_lands_pending_then_flips_when_the_bank_posts_it()
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

        // One mutable body so the SECOND sync can answer differently to the
        // first — which is the whole scenario. A fixed stub could only ever
        // test one half of a lifecycle.
        var pending = true;
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(() => Body(pending)));
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") });
        var connection = (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var bank = await ledger.AddBankAccountAsync("checking");
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-p",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        async Task SyncAsync()
        {
            var resp = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync",
                content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        async Task<(int Count, bool IsPending)> StateAsync()
        {
            await using var db = _fixture.NewDbContext();
            var rows = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .ToListAsync();
            return (rows.Count, rows.Count == 1 && rows[0].IsPending);
        }

        await SyncAsync();
        var afterFirst = await StateAsync();
        Assert.Equal(1, afterFirst.Count);
        Assert.True(afterFirst.IsPending,
            "a transaction the bank reported as pending was stored as posted");

        // The bank posts it. Same provider id, same amount.
        pending = false;
        await SyncAsync();
        var afterSecond = await StateAsync();

        // ONE row, not two. The dedup key and the pending flip are the same
        // mechanism, and getting the first right while getting the second
        // wrong would leave the ledger with a permanent phantom hold.
        Assert.Equal(1, afterSecond.Count);
        Assert.False(afterSecond.IsPending,
            "the bank posted it, but the row is still flagged pending");
    }

    private static SimpleFinClient StubClient(Func<string> body)
    {
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        return new SimpleFinClient(new HttpClient(new StubHandler(req =>
            req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) })), TimeProvider.System);
    }

    private static string SetupToken(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

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
}
