using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A save that carries a header date on the row's stored day keeps the stored
/// timestamp; a date on another day is applied as sent.
/// </summary>
/// <remarks>
/// <para>The editors send a date as midnight UTC of the picked day. A row whose
/// <c>posted_at</c> carries a time of day (every feed row) would otherwise have
/// that time overwritten by an accept or a save that never touched the date,
/// and re-sort among its same-day neighbours.</para>
///
/// <para>Every comparison is between values read back from the database, i.e. at
/// <c>timestamptz</c>'s microsecond scale, never against an in-memory value of
/// finer scale.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HeaderDateSameDayTests : DatabaseTest
{
    public HeaderDateSameDayTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // A feed transaction posted at a time of day (SimpleFIN sends epoch seconds).
    private const string FeedBody = """
        {"connections":[
          {"conn_id":"c-N","name":"Bank N","org_id":"bankn","sfin_url":"https://sfin/bankn"}
        ],"errlist":[],"accounts":[
          {"id":"sf-n","conn_id":"c-N","name":"Checking","currency":"USD","balance":"100.00","transactions":[
            {"id":"t-1","posted":1715000000,"amount":"-42.50","payee":"Corner Cafe","description":"CORNER CAFE 0042"}
          ]}
        ]}
        """;

    private static string MidnightUtc(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'00:00:00.000'Z'", CultureInfo.InvariantCulture);

    private sealed record Feed(SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client, Guid Bank) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<Feed> FeedAsync()
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

        var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(() => FeedBody));
        var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") });
        var connection = (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking");
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-n",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        var sync = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        return new Feed(ledger, factory, client, bank.Id);
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private sealed record Stored(Guid Id, DateTime PostedAt, DateTime TransactedAt, bool NeedsReview);

    private async Task<Stored> HeaderAsync(Guid ledgerId, Guid? headerId = null)
    {
        await using var db = Db.NewDbContext();
        var h = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(x => x.LedgerId == ledgerId && (headerId == null || x.Id == headerId));
        Assert.NotNull(h.TransactedAt);
        return new Stored(h.Id, h.PostedAt, h.TransactedAt.Value, h.NeedsReview);
    }

    private async Task<(DateTime PostedAt, DateTime TransactedAt)?> OriginalAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var o = await db.TxnHeaderOriginals.AsNoTracking()
            .Where(x => x.HeaderId == headerId)
            .Select(x => new { x.PostedAt, x.TransactedAt })
            .SingleOrDefaultAsync();
        if (o is null) return null;
        Assert.NotNull(o.PostedAt);
        Assert.NotNull(o.TransactedAt);
        return (o.PostedAt.Value, o.TransactedAt.Value);
    }

    private static HttpRequestMessage PatchJson(string url, string json) =>
        new(HttpMethod.Patch, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // -- bank ---------------------------------------------------------------

    [Fact]
    public async Task Accepting_a_feed_row_on_its_own_day_keeps_the_stored_timestamps_and_captures_nothing()
    {
        await using var feed = await FeedAsync();
        var before = await HeaderAsync(feed.Ledger.LedgerId);
        // Premise: a feed row awaiting review whose timestamp is not midnight.
        Assert.True(before.NeedsReview);
        Assert.NotEqual(TimeSpan.Zero, before.PostedAt.ToUniversalTime().TimeOfDay);

        var day = MidnightUtc(before.PostedAt);
        var response = await feed.Client.SendAsync(PatchJson(
            $"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{before.Id}",
            $$"""{"postedAt": "{{day}}", "transactedAt": "{{MidnightUtc(before.TransactedAt)}}", "approve": true}"""));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await HeaderAsync(feed.Ledger.LedgerId, before.Id);
        Assert.False(after.NeedsReview);
        Assert.Equal(before.PostedAt.Ticks, after.PostedAt.Ticks);
        Assert.Equal(before.TransactedAt.Ticks, after.TransactedAt.Ticks);
        // An unchanged date is not an edit, so there is nothing to capture.
        Assert.Null(await OriginalAsync(before.Id));
    }

    [Fact]
    public async Task Saving_a_feed_row_with_the_editors_full_body_on_its_own_day_keeps_the_stored_timestamps()
    {
        // The editor sends every header field on every save. The payee it carries
        // captures originals (existence, not difference), and what is captured
        // must show the date as unchanged.
        await using var feed = await FeedAsync();
        var before = await HeaderAsync(feed.Ledger.LedgerId);

        var response = await feed.Client.SendAsync(PatchJson(
            $"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{before.Id}",
            $$"""
            {"payee": "Corner Cafe", "checkNumber": null,
             "postedAt": "{{MidnightUtc(before.PostedAt)}}", "transactedAt": "{{MidnightUtc(before.TransactedAt)}}",
             "approve": true}
            """));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await HeaderAsync(feed.Ledger.LedgerId, before.Id);
        Assert.Equal(before.PostedAt.Ticks, after.PostedAt.Ticks);
        Assert.Equal(before.TransactedAt.Ticks, after.TransactedAt.Ticks);
        var original = await OriginalAsync(before.Id);
        Assert.NotNull(original);
        Assert.Equal(after.PostedAt.Ticks, original!.Value.PostedAt.Ticks);
        Assert.Equal(after.TransactedAt.Ticks, original.Value.TransactedAt.Ticks);
    }

    [Fact]
    public async Task Saving_a_feed_row_on_another_day_applies_the_request_and_captures_the_feed_date()
    {
        await using var feed = await FeedAsync();
        var before = await HeaderAsync(feed.Ledger.LedgerId);

        var moved = DateTime.SpecifyKind(before.PostedAt.ToUniversalTime().Date.AddDays(3), DateTimeKind.Utc);
        var response = await feed.Client.SendAsync(PatchJson(
            $"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{before.Id}",
            $$"""{"postedAt": "{{MidnightUtc(moved)}}", "approve": true}"""));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await HeaderAsync(feed.Ledger.LedgerId, before.Id);
        Assert.Equal(moved, after.PostedAt.ToUniversalTime());
        var original = await OriginalAsync(before.Id);
        Assert.NotNull(original);
        Assert.Equal(before.PostedAt.Ticks, original!.Value.PostedAt.Ticks);
    }

    // -- investment ---------------------------------------------------------

    private sealed record Event(SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client,
        Guid Brokerage, Guid Security, Guid HeaderId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }

        public PatchInvestmentTransactionRequest Buy(DateTime postedAt, DateTime? transactedAt = null) => new()
        {
            BrokerageAccountId = Brokerage,
            PostedAt = postedAt,
            TransactedAt = transactedAt,
            Action = "buy",
            SecurityId = Security,
            Shares = 10m,
            Price = 100m,
            Amount = 1000m,
            Approve = true,
        };
    }

    private async Task<Event> EventAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("SDAY", ticker: "SDAY");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = await AuthedClientAsync(factory, ledger);

        // A time of day with sub-second digits, at the column's microsecond scale.
        var postedAt = new DateTime(2026, 5, 4, 14, 37, 21, DateTimeKind.Utc).AddTicks(1_234_560);
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();
        return new Event(ledger, factory, client, brokerage.Id, securityId, headerId);
    }

    [Fact]
    public async Task Saving_an_investment_event_on_its_own_day_keeps_the_stored_timestamps()
    {
        await using var ev = await EventAsync();
        var before = await HeaderAsync(ev.Ledger.LedgerId, ev.HeaderId);
        Assert.NotEqual(TimeSpan.Zero, before.PostedAt.ToUniversalTime().TimeOfDay);

        var day = DateTime.SpecifyKind(before.PostedAt.ToUniversalTime().Date, DateTimeKind.Utc);
        var patch = await ev.Client.PatchAsJsonAsync(
            $"/api/ledgers/{ev.Ledger.LedgerId}/investment-transactions/{ev.HeaderId}",
            ev.Buy(day, transactedAt: day));
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var after = await HeaderAsync(ev.Ledger.LedgerId, ev.HeaderId);
        Assert.Equal(before.PostedAt.Ticks, after.PostedAt.Ticks);
        Assert.Equal(before.TransactedAt.Ticks, after.TransactedAt.Ticks);
        // The investment PATCH replaces the whole event and captures on every
        // save; what it captures must show the date as unchanged.
        if (await OriginalAsync(ev.HeaderId) is { } original)
            Assert.Equal(after.PostedAt.Ticks, original.PostedAt.Ticks);
    }

    [Fact]
    public async Task Saving_an_investment_event_on_another_day_applies_the_request()
    {
        await using var ev = await EventAsync();
        var before = await HeaderAsync(ev.Ledger.LedgerId, ev.HeaderId);

        var moved = DateTime.SpecifyKind(before.PostedAt.ToUniversalTime().Date.AddDays(-2), DateTimeKind.Utc);
        var patch = await ev.Client.PatchAsJsonAsync(
            $"/api/ledgers/{ev.Ledger.LedgerId}/investment-transactions/{ev.HeaderId}",
            ev.Buy(moved));
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var after = await HeaderAsync(ev.Ledger.LedgerId, ev.HeaderId);
        Assert.Equal(moved, after.PostedAt.ToUniversalTime());
        Assert.Equal(moved, after.TransactedAt.ToUniversalTime());
        var original = await OriginalAsync(ev.HeaderId);
        Assert.NotNull(original);
        Assert.Equal(before.PostedAt.Ticks, original!.Value.PostedAt.Ticks);
    }

    // -- harness ------------------------------------------------------------

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
