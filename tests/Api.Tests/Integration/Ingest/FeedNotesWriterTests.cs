using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// A feed row's memo is the synced account's own, written through the write
/// primitive on insert and by the resync's payee/memo backfill; a resync never
/// overwrites a memo the register already shows.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FeedNotesWriterTests : DatabaseTest
{
    public FeedNotesWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static string Body(string transactionJson) => $$"""
        {"connections":[
          {"conn_id":"c-N","name":"Bank N","org_id":"bankn","sfin_url":"https://sfin/bankn"}
        ],"errlist":[],"accounts":[
          {"id":"sf-n","conn_id":"c-N","name":"Checking","currency":"USD","balance":"100.00","transactions":[
            {{transactionJson}}
          ]}
        ]}
        """;

    private const string WithPayee =
        """{"id":"t-1","posted":1715000000,"amount":"-42.50","payee":"Corner Cafe","description":"CORNER CAFE 0042"}""";

    private const string WithoutPayee =
        """{"id":"t-1","posted":1715000000,"amount":"-42.50","description":"CORNER CAFE 0042"}""";

    private sealed record Feed(SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client, Guid ConnectionId, Guid Bank) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }

        public async Task SyncAsync()
        {
            var resp = await Client.PostAsync(
                $"/api/ledgers/{Ledger.LedgerId}/feed-connections/{ConnectionId}/sync", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    private async Task<Feed> FeedAsync(Func<string> transaction, NotesTableCommandCounter? counter = null)
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
            .WithService<SimpleFinClient>(_ => StubClient(() => Body(transaction())));
        if (counter is not null)
            factory = factory.WithServices(s => s.ConfigureDbContext<Coffer.Api.Db.AppDbContext>(o => o.AddInterceptors(counter)));
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

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
        return new Feed(ledger, factory, client, connection.Id, bank.Id);
    }

    private async Task<(Guid Id, string? Payee, string? HeaderMemo, string? BankMemo, string? OtherMemo)> RowAsync(Feed feed)
    {
        await using var db = Db.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.LedgerId == feed.Ledger.LedgerId);
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == header.Id).ToListAsync();
        return (header.Id, header.Payee, header.Memo,
            legs.Single(l => l.AccountId == feed.Bank).LegMemo,
            legs.Single(l => l.AccountId != feed.Bank).LegMemo);
    }

    [Fact]
    public async Task A_new_feed_rows_memo_is_the_synced_accounts_own()
    {
        await using var feed = await FeedAsync(() => WithPayee);
        await feed.SyncAsync();

        var row = await RowAsync(feed);
        Assert.Equal("Corner Cafe", row.Payee);
        Assert.Null(row.HeaderMemo);
        Assert.Equal("CORNER CAFE 0042", row.BankMemo);
        Assert.Null(row.OtherMemo);
        await OriginatingAccountCheck.AssertHoldsAsync(Db, feed.Ledger.LedgerId);
    }

    [Fact]
    public async Task The_resync_backfill_writes_the_description_as_the_synced_accounts_memo()
    {
        // A row synced before the feed sent payees: the description is its payee and it
        // has no memo. When the payee arrives, the backfill splits them.
        var transaction = WithoutPayee;
        await using var feed = await FeedAsync(() => transaction);
        await feed.SyncAsync();
        var before = await RowAsync(feed);
        Assert.Equal(("CORNER CAFE 0042", null), (before.Payee, before.BankMemo));

        transaction = WithPayee;
        await feed.SyncAsync();

        var after = await RowAsync(feed);
        Assert.Equal("Corner Cafe", after.Payee);
        Assert.Null(after.HeaderMemo);
        Assert.Equal("CORNER CAFE 0042", after.BankMemo);
    }

    /// <summary>
    /// A pre-split row edited before the payee arrived (its category only) has originals
    /// holding the description as payee and no memo. The backfill restates them with the
    /// new payee and memo as it restates the header (ADR-0114 D15), so payee recall reads
    /// the row as it reads one with no originals.
    /// </summary>
    [Fact]
    public async Task The_resync_backfill_restates_originals_that_still_hold_the_pre_split_values()
    {
        var transaction = WithoutPayee;
        await using var feed = await FeedAsync(() => transaction);
        await feed.SyncAsync();
        var row = await RowAsync(feed);
        var snacks = await feed.Ledger.AddCategoryAsync("Snacks");
        Guid bankLeg;
        await using (var db = Db.NewDbContext())
            bankLeg = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == row.Id && l.AccountId == feed.Bank)).Id;
        var recategorize = await feed.Client.PatchAsJsonAsync($"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{row.Id}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = feed.Bank,
                    Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = snacks.Id, Amount = -42.50m }],
                },
            });
        Assert.True(recategorize.IsSuccessStatusCode, await recategorize.Content.ReadAsStringAsync());
        var captured = await OriginalsAsync(row.Id);
        Assert.Equal(("CORNER CAFE 0042", (string?)null), (captured.Payee, captured.Memo));

        transaction = WithPayee;
        await feed.SyncAsync();

        var after = await RowAsync(feed);
        Assert.Equal(("Corner Cafe", "CORNER CAFE 0042"), (after.Payee, after.BankMemo));
        var originals = await OriginalsAsync(row.Id);
        Assert.Equal(("Corner Cafe", "CORNER CAFE 0042"), (originals.Payee, originals.Memo));
    }

    /// <summary>Originals that hold anything but the values the backfill replaces are the
    /// feed's own, and the backfill leaves them as they are.</summary>
    [Fact]
    public async Task The_resync_backfill_leaves_originals_that_hold_other_values()
    {
        var transaction = WithPayee;
        await using var feed = await FeedAsync(() => transaction);
        await feed.SyncAsync();
        var row = await RowAsync(feed);
        // The user gives the row the pre-split shape: the description as payee, no memo.
        var edit = await feed.Client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{row.Id}")
        {
            Content = new StringContent(
                $$$"""{"payee":"CORNER CAFE 0042","notes":{"side":"{{{feed.Bank}}}","memo":null}}""",
                Encoding.UTF8, "application/json"),
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        Assert.Equal(("CORNER CAFE 0042", (string?)null), ((await RowAsync(feed)).Payee, (await RowAsync(feed)).BankMemo));

        transaction = """{"id":"t-1","posted":1715000000,"amount":"-42.50","payee":"Cafe Corner","description":"CORNER CAFE 0042"}""";
        await feed.SyncAsync();

        Assert.Equal("Cafe Corner", (await RowAsync(feed)).Payee);
        var originals = await OriginalsAsync(row.Id);
        Assert.Equal(("Corner Cafe", "CORNER CAFE 0042"), (originals.Payee, originals.Memo));
    }

    private async Task<Coffer.Api.Db.Entities.TxnHeaderOriginalRow> OriginalsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaderOriginals.AsNoTracking().SingleAsync(o => o.HeaderId == headerId);
    }

    [Fact]
    public async Task A_resync_leaves_a_memo_the_register_shows_alone()
    {
        // The row still looks pre-split (the payee is the raw description), but the
        // synced account's register already shows a memo, inherited from the other
        // side: the backfill must not write over it.
        var transaction = WithoutPayee;
        await using var feed = await FeedAsync(() => transaction);
        await feed.SyncAsync();
        var row = await RowAsync(feed);
        await using (var db = Db.NewDbContext())
        {
            var other = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == row.Id && l.AccountId != feed.Bank);
            await feed.Ledger.SetSideMemoAsync(other.Id, other.AccountId, "split the bill", feed.Bank);
        }

        transaction = WithPayee;
        await feed.SyncAsync();

        var after = await RowAsync(feed);
        Assert.Equal("CORNER CAFE 0042", after.Payee);   // untouched: the backfill did not fire
        Assert.Null(after.BankMemo);
        Assert.Equal("split the bill", after.OtherMemo);
    }

    [Fact]
    public async Task A_resync_after_the_user_edits_the_memo_keeps_the_edit()
    {
        await using var feed = await FeedAsync(() => WithPayee);
        await feed.SyncAsync();
        var row = await RowAsync(feed);

        var patch = await feed.Client.PatchAsJsonAsync($"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{row.Id}",
            new PatchTransactionRequest { Notes = new TransactionNotesRequest { Side = feed.Bank, Memo = "coffee with a friend" } });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        await feed.SyncAsync();

        var after = await RowAsync(feed);
        Assert.Equal("coffee with a friend", after.BankMemo);
        // The feed's memo was captured as the original on the first edit.
        await using var db = Db.NewDbContext();
        Assert.Equal("CORNER CAFE 0042", await db.TxnHeaderOriginals.AsNoTracking()
            .Where(o => o.HeaderId == row.Id).Select(o => o.Memo).SingleAsync());
    }

    [Fact]
    public async Task A_split_sent_without_notes_captures_the_feeds_memo_first()
    {
        // A split moves the memo to the header and a collapse joins it with the line's,
        // so a reshape rewrites memos even when no notes are sent.
        await using var feed = await FeedAsync(() => WithPayee);
        await feed.SyncAsync();
        var row = await RowAsync(feed);
        var tips = await feed.Ledger.AddCategoryAsync("Tips");
        Guid bankLeg, otherAccount;
        await using (var db = Db.NewDbContext())
        {
            var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == row.Id).ToListAsync();
            bankLeg = legs.Single(l => l.AccountId == feed.Bank).Id;
            otherAccount = legs.Single(l => l.AccountId != feed.Bank).AccountId;
        }

        var split = await feed.Client.PatchAsJsonAsync($"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{row.Id}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = feed.Bank,
                    Items =
                    [
                        new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = otherAccount, Amount = -40m },
                        new TransactionPosting { CounterpartyAccountId = tips.Id, Amount = -2.50m },
                    ],
                },
            });
        Assert.True(split.IsSuccessStatusCode, await split.Content.ReadAsStringAsync());

        await using var check = Db.NewDbContext();
        Assert.Equal("CORNER CAFE 0042", await check.TxnHeaderOriginals.AsNoTracking()
            .Where(o => o.HeaderId == row.Id).Select(o => o.Memo).SingleAsync());
    }

    [Fact]
    public async Task A_resync_loads_and_stores_notes_in_the_same_round_trips_however_many_rows_it_backfills()
    {
        async Task<int> NotesTableCommandsOnResyncAsync(int rows)
        {
            string Many(bool withPayee) => string.Join(",", Enumerable.Range(1, rows).Select(i => withPayee
                ? $$"""{"id":"t-{{i}}","posted":{{1715000000 + i}},"amount":"-{{i}}.50","payee":"Cafe {{i}}","description":"CAFE {{i}} RAW"}"""
                : $$"""{"id":"t-{{i}}","posted":{{1715000000 + i}},"amount":"-{{i}}.50","description":"CAFE {{i}} RAW"}"""));
            var withPayee = false;
            var counter = new NotesTableCommandCounter();
            await using var feed = await FeedAsync(() => Many(withPayee), counter);
            await feed.SyncAsync();

            withPayee = true;
            counter.Count = 0;
            await feed.SyncAsync();
            var count = counter.Count;

            await using var db = Db.NewDbContext();
            var bankMemos = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == feed.Ledger.LedgerId && l.AccountId == feed.Bank)
                .Select(l => l.LegMemo).ToListAsync();
            Assert.Equal(rows, bankMemos.Count);
            Assert.All(bankMemos, m => Assert.Matches("^CAFE [0-9]+ RAW$", m!));
            return count;
        }

        Assert.Equal(await NotesTableCommandsOnResyncAsync(2), await NotesTableCommandsOnResyncAsync(6));
    }

    /// <summary>Counts the commands that touch a table the notes load reads: the legs,
    /// the accounts' facts and both tag pairing tables.</summary>
    private sealed class NotesTableCommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private static readonly string[] Tables = ["txn_header_tags", "txn_leg_tags", "txn_legs", "accounts"];
        private int _count;

        public int Count { get => Volatile.Read(ref _count); set => Volatile.Write(ref _count, value); }

        private void Seen(System.Data.Common.DbCommand command)
        {
            if (Tables.Any(t => System.Text.RegularExpressions.Regex.IsMatch(command.CommandText, $"\\b{t}\\b")))
                Interlocked.Increment(ref _count);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Seen(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
