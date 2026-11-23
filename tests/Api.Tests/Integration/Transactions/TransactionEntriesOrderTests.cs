using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The ledger-wide entries (<c>GET /transactions/entries</c>) and a register
/// (<c>GET /transactions?account_id=</c>) order entries by one key: on a ledger with one
/// real account the entries are that register's first page, entry for entry; with
/// several, each account's entries keep its register's order and the accounts interleave
/// newest first.
/// </summary>
/// <remarks>
/// The ledgers come in through the Moneydance import, the product path that brings in a
/// transaction entered from a category: a category-only header, which is an entry on no
/// real account, and a split from a category with two lines on one real account, two
/// entries there under one header and one sequence.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TransactionEntriesOrderTests : DatabaseTest
{
    public TransactionEntriesOrderTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ---- one account: the entries are its register --------------------------------

    /// <summary>
    /// With one real account, every first page of the entries, at each limit up to past
    /// the end, is that account's register page of the same limit: the same entries in
    /// the same order, each written the same, and more to follow exactly when the
    /// register has an older page.
    /// </summary>
    [Fact]
    public async Task With_one_account_each_first_page_of_entries_is_its_registers_first_page()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await ImportLedgerAsync(client, OneAccountExport);
        var accounts = await AccountIdsAsync(client, ledgerId);
        var accountA = accounts["Account A"];
        var categoryA = accounts["Category A"];

        // Premises: the register holds every shape the order has to agree on.
        var register = await RegisterPageAsync(client, ledgerId, accountA, limit: 100);
        var rows = Entries(register);
        Assert.Equal(OneAccountEntries, rows.Count);
        var split = await HeaderOfAsync(ledgerId, "t-split");
        Assert.Single(rows, e => e.IsGroup && e.HeaderId == split);
        var fromCategory = await HeaderOfAsync(ledgerId, "t-from-category");
        Assert.Equal(2, rows.Count(e => !e.IsGroup && e.HeaderId == fromCategory));
        Assert.True(rows.GroupBy(e => e.PostedAt).Max(g => g.Count()) >= 3, "no day holds three entries");
        var categoryOnly = await HeaderOfAsync(ledgerId, "t-category-only");
        Assert.DoesNotContain(rows, e => e.HeaderId == categoryOnly);
        // ...which is a real row, on its categories' registers.
        Assert.Contains(Entries(await RegisterPageAsync(client, ledgerId, categoryA, limit: 100)),
            e => e.HeaderId == categoryOnly);

        for (var limit = 1; limit <= OneAccountEntries + 1; limit++)
        {
            var page = await EntriesPageAsync(client, ledgerId, $"limit={limit}");
            var listed = Listed(page);
            var registerPage = await RegisterPageAsync(client, ledgerId, accountA, limit);
            Assert.All(listed, e => Assert.Equal(accountA, e.AccountId));
            Assert.Equal(Entries(registerPage).Select(e => e.Raw), listed.Select(e => e.Entry.Raw));
            Assert.Equal(registerPage.GetProperty("cursorForOlder").ValueKind != JsonValueKind.Null,
                page.GetProperty("hasMore").GetBoolean());
        }
    }

    // ---- two accounts: each keeps its register's order, interleaved newest first ----

    /// <summary>
    /// With two real accounts, the entries of each are its register's, in its order;
    /// together they run newest first by date and then by the transaction's sequence;
    /// and every shorter first page is the start of the longest one.
    /// </summary>
    [Fact]
    public async Task With_two_accounts_each_keeps_its_registers_order_and_they_interleave_newest_first()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await ImportLedgerAsync(client, TwoAccountExport);
        var accounts = await AccountIdsAsync(client, ledgerId);
        Guid[] real = [accounts["Account A"], accounts["Account B"]];

        var all = await EntriesPageAsync(client, ledgerId, "limit=50");
        Assert.False(all.GetProperty("hasMore").GetBoolean());
        var listed = Listed(all);

        var total = 0;
        foreach (var account in real)
        {
            var own = Entries(await RegisterPageAsync(client, ledgerId, account, limit: 100));
            total += own.Count;
            Assert.Equal(own.Select(e => e.Raw), listed.Where(e => e.AccountId == account).Select(e => e.Entry.Raw));
        }
        Assert.Equal(total, listed.Count);

        // Premise: the accounts interleave, so the merged order is not one register after
        // the other, and a transfer between them is an entry on each.
        var accountsInOrder = listed.Select(e => e.AccountId).ToList();
        Assert.True(Enumerable.Range(1, accountsInOrder.Count - 2)
                .Any(i => accountsInOrder[i - 1] == accountsInOrder[i + 1] && accountsInOrder[i] != accountsInOrder[i - 1]),
            "the two accounts' entries do not interleave");
        var transfer = await HeaderOfAsync(ledgerId, "t-transfer");
        Assert.Equal(real.Order(), listed.Where(e => e.Entry.HeaderId == transfer).Select(e => e.AccountId).Order());

        // Newest first: by date, then by the transaction's sequence.
        var seqOf = await SeqsAsync(ledgerId);
        var keys = listed.Select(e => (e.Entry.PostedAt, Seq: seqOf[e.Entry.HeaderId])).ToList();
        for (var i = 1; i < keys.Count; i++)
            Assert.True(keys[i - 1].CompareTo(keys[i]) >= 0, $"entry {i} sorts after entry {i - 1}: {keys[i - 1]} then {keys[i]}");

        for (var limit = 1; limit <= listed.Count; limit++)
        {
            var page = await EntriesPageAsync(client, ledgerId, $"limit={limit}");
            Assert.Equal(listed.Take(limit).Select(e => (e.AccountId, e.Entry.Raw)),
                Listed(page).Select(e => (e.AccountId, e.Entry.Raw)));
            Assert.Equal(limit < listed.Count, page.GetProperty("hasMore").GetBoolean());
        }
    }

    // ---- read from the newest headers, the page is the same -------------------------

    /// <summary>
    /// The page read from the newest headers in batches is the page read in one
    /// statement over every leg, the same entries in the same order and the same
    /// <c>hasMore</c>, for every direction, with and without a today, at limits from one
    /// to the ceiling.
    /// </summary>
    /// <remarks>
    /// The newest headers, after the today and on it, are transactions between
    /// categories, more of them than the first batch of any limit up to five reads, so
    /// those pages are found only by reading further batches; the newest real entries are
    /// inflows, so an outflow page reads further still. Transfers between the accounts go
    /// both ways, one split has two opposite-sign lines on the other account and one from
    /// a category has two lines on an account, so a header holds several entries.
    /// </remarks>
    [Fact]
    public async Task The_page_read_from_the_newest_headers_is_the_page_read_in_one_statement()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await ImportLedgerAsync(client, BatchedExport);
        var accounts = await AccountIdsAsync(client, ledgerId);

        // Premise: more transactions between categories are newer than every real entry,
        // with no today and with it, than a first batch of up to six headers reads.
        var categories = new[] { accounts["Category A"], accounts["Category B"] };
        await using (var db = Db.NewDbContext())
        {
            var newest = await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId)
                .OrderByDescending(h => h.PostedAt).ThenByDescending(h => h.Seq)
                .Select(h => new { h.Id, h.PostedAt, OnCategoriesOnly = db.TxnLegs.Where(l => l.HeaderId == h.Id).All(l => categories.Contains(l.AccountId)) })
                .ToListAsync();
            Assert.True(newest.TakeWhile(h => h.OnCategoriesOnly).Count() >= 6, "the newest headers are not between categories");
            Assert.True(newest.Where(h => h.PostedAt < BatchedTodayCut).TakeWhile(h => h.OnCategoriesOnly).Count() >= 6,
                "the newest headers up to the today are not between categories");
        }

        var cells = 0;
        var nonEmpty = 0;
        foreach (var direction in new[] { TransactionDirection.All, TransactionDirection.Inflow, TransactionDirection.Outflow })
        foreach (var today in new DateOnly?[] { null, BatchedToday })
        foreach (var limit in new[] { 1, 2, 3, 4, 5, 8, 13, 21, 34, TransactionsEndpoints.EntriesMaxLimit })
        {
            var oneStatement = await PageAsync(ledgerId, direction, today, limit, byHeader: false);
            var byHeader = await PageAsync(ledgerId, direction, today, limit, byHeader: true);
            Assert.True(oneStatement == byHeader,
                $"{direction}, today {today?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}, limit {limit}:\n"
                + $"one statement {oneStatement}\nby header     {byHeader}");
            cells++;
            if (oneStatement.Contains("\"entries\":[{", StringComparison.Ordinal)) nonEmpty++;
        }
        Assert.Equal(60, cells);
        Assert.Equal(cells, nonEmpty);
    }

    /// <summary>One page of the ledger-wide entries, read by header or in one statement, as the API would write it.</summary>
    private async Task<string> PageAsync(Guid ledgerId, TransactionDirection direction, DateOnly? today, int limit, bool byHeader)
    {
        await using var db = Db.NewDbContext();
        var page = await new RegisterRepository(db, TimeProvider.System)
            .GetLedgerPageAsync(ledgerId, needsReview: false, direction, today, limit, byHeader, CancellationToken.None);
        return JsonSerializer.Serialize(page, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- fixtures ---------------------------------------------------------------------

    private static readonly DateOnly BatchedToday = new(2026, 1, 20);

    /// <summary>The first instant after <see cref="BatchedToday"/>.</summary>
    private static readonly DateTime BatchedTodayCut = new(2026, 1, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two real accounts and three categories. After the today: rows on both accounts and
    /// a transfer between them, then six transactions between categories, the newest of
    /// all. On the today: an inflow, then six more between categories. Before it, newest
    /// first: three inflows, a split from a category with two lines on one account, a
    /// split with two opposite-sign lines on the other account, a transfer back, outflows
    /// on both, a split onto two categories, one more between categories, and a few older
    /// rows.
    /// </summary>
    private static readonly string BatchedExport = Export(
    [
        Account("a-a", "b", "Account A"),
        Account("a-b", "b", "Account B"),
        Account("c-a", "e", "Category A"),
        Account("c-b", "e", "Category B"),
        Account("c-c", "i", "Category C"),
        Txn("t-old-out", "a-a", "20260101", "Payee A", ("c-a", -100)),
        Txn("t-old-in", "a-b", "20260102", "Payee B", ("c-c", 3000)),
        Txn("t-old-between", "c-a", "20260103", "Payee C", ("c-b", -200)),
        Txn("t-split", "a-a", "20260104", "Payee D", ("c-a", -300), ("c-b", -400)),
        .. Enumerable.Range(5, 9).Select(d => Txn($"t-a-out-{d}", "a-a", $"202601{d:D2}", "Payee E", ("c-a", -(500 + d)))),
        .. new[] { 8, 10, 12 }.Select(d => Txn($"t-b-out-{d}", "a-b", $"202601{d:D2}", "Payee F", ("c-b", -(700 + d)))),
        Txn("t-transfer-back", "a-b", "20260114", "Payee G", ("a-a", -1500)),
        Txn("t-two-lines", "a-a", "20260115", "Payee H", ("a-b", -600), ("a-b", 100)),
        Txn("t-from-category", "c-a", "20260116", "Payee I", ("a-a", 200), ("a-a", -500)),
        Txn("t-in-a", "a-a", "20260117", "Payee J", ("c-c", 1200)),
        Txn("t-in-b", "a-b", "20260118", "Payee K", ("c-c", 1300)),
        Txn("t-in-a-2", "a-a", "20260119", "Payee L", ("c-c", 1400)),
        Txn("t-today-in", "a-b", "20260120", "Payee R", ("c-c", 1100)),
        .. Enumerable.Range(1, 6).Select(i => Txn($"t-today-between-{i}", "c-a", "20260120", "Payee M", ("c-b", -(10 * i)))),
        Txn("t-later-out", "a-a", "20260121", "Payee N", ("c-a", -800)),
        Txn("t-later-in", "a-b", "20260122", "Payee O", ("c-c", 900)),
        Txn("t-later-transfer", "a-a", "20260123", "Payee P", ("a-b", -1000)),
        .. Enumerable.Range(25, 6).Select(d => Txn($"t-later-between-{d}", "c-a", $"202601{d:D2}", "Payee Q", ("c-b", -(20 * d)))),
    ]);

    /// <summary>The entries <see cref="OneAccountExport"/> puts on its one real account.</summary>
    private const int OneAccountEntries = 8;

    /// <summary>
    /// One real account and three categories: plain rows, three of them and a split on
    /// one day; a split entered from a category with two lines on the account that day
    /// too; and a transfer between two categories, which no real account shows.
    /// </summary>
    private static readonly string OneAccountExport = Export(
        Account("a-a", "b", "Account A"),
        Account("c-a", "e", "Category A"),
        Account("c-b", "e", "Category B"),
        Account("c-c", "i", "Category C"),
        Txn("t-oldest", "a-a", "20260102", "Payee A", ("c-a", -100)),
        Txn("t-plain", "a-a", "20260103", "Payee B", ("c-a", -1000)),
        Txn("t-split", "a-a", "20260104", "Payee C", ("c-a", -300), ("c-b", -400)),
        Txn("t-income", "a-a", "20260104", "Payee D", ("c-c", 2500)),
        Txn("t-from-category", "c-a", "20260104", "Payee E", ("a-a", 200), ("a-a", -500)),
        Txn("t-same-day", "a-a", "20260104", "Payee F", ("c-b", -50)),
        Txn("t-category-only", "c-a", "20260105", "Payee G", ("c-b", -700)),
        Txn("t-newest", "a-a", "20260106", "Payee H", ("c-b", -900)));

    /// <summary>
    /// Two real accounts and two categories: rows on each on the same days, a transfer
    /// between them, a split from one with two lines of opposite sign on the other, a
    /// split onto two categories, and a transfer between the categories.
    /// </summary>
    private static readonly string TwoAccountExport = Export(
        Account("a-a", "b", "Account A"),
        Account("a-b", "b", "Account B"),
        Account("c-a", "e", "Category A"),
        Account("c-b", "e", "Category B"),
        Txn("t-b-oldest", "a-b", "20260109", "Payee A", ("c-b", 4000)),
        Txn("t-a-plain", "a-a", "20260110", "Payee B", ("c-a", -1000)),
        Txn("t-b-plain", "a-b", "20260110", "Payee C", ("c-a", -2000)),
        Txn("t-transfer", "a-a", "20260110", "Payee D", ("a-b", -3000)),
        Txn("t-b-split", "a-b", "20260110", "Payee E", ("c-a", -100), ("c-b", -200)),
        Txn("t-two-lines", "a-a", "20260111", "Payee F", ("a-b", -600), ("a-b", 100)),
        Txn("t-category-only", "c-a", "20260111", "Payee G", ("c-b", -700)),
        Txn("t-a-newest", "a-a", "20260112", "Payee H", ("c-b", -50)));

    private static string Export(params string[] items) =>
        $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {{string.Join(",\n    ", items)}}
          ]
        }
        """;

    /// <summary>An account under the root: <paramref name="type"/> <c>b</c> a bank, <c>e</c> an expense category, <c>i</c> an income one.</summary>
    private static string Account(string id, string type, string name) =>
        $$"""{"obj_type":"acct","id":"{{id}}","type":"{{type}}","name":"{{name}}","parentid":"a-root","currid":"c-usd","sbal":"0"}""";

    /// <summary>
    /// A transaction entered from <paramref name="home"/>, one split per line: each line's
    /// amount on the home in minor units, the opposite on the line's account.
    /// </summary>
    private static string Txn(string id, string home, string date, string payee, params (string Account, long OnHome)[] lines)
    {
        var splits = lines.Select((l, i) => string.Create(CultureInfo.InvariantCulture,
            $"\"{i}.id\":\"{id}-s{i}\",\"{i}.acctid\":\"{l.Account}\",\"{i}.samt\":\"{-l.OnHome}\",\"{i}.pamt\":\"{l.OnHome}\""));
        return $$"""{"obj_type":"txn","id":"{{id}}","acctid":"{{home}}","desc":"{{payee}}","dt":"{{date}}",{{string.Join(",", splits)}}}""";
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>Import <paramref name="json"/> through the Moneydance import route and return the new ledger, once the run has succeeded.</summary>
    private static async Task<Guid> ImportLedgerAsync(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "export.json");
        content.Add(new StringContent("Ledger " + Guid.NewGuid().ToString("N")[..8]), "ledgerName");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.True(started.StatusCode == HttpStatusCode.OK, $"import: {(int)started.StatusCode} {await started.Content.ReadAsStringAsync()}");
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString()!;

        // The import is a background run, so this is a real wait; the ceiling makes a hang fail.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var status = JsonDocument.Parse(await client.GetStringAsync($"/api/imports/moneydance/{jobId}"));
            var state = status.RootElement.GetProperty("state").GetString();
            if (state == "running")
            {
                await Task.Delay(250);
                continue;
            }
            Assert.True(state == "succeeded", $"the import ended '{state}': {status.RootElement.GetRawText()}");
            return Guid.Parse(status.RootElement.GetProperty("ledgerId").GetString()!);
        }
        Assert.Fail("The import never left the 'running' state.");
        return Guid.Empty;
    }

    private static async Task<Dictionary<string, Guid>> AccountIdsAsync(HttpClient client, Guid ledgerId)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/accounts?includeInactive=true"));
        return doc.RootElement.EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid(), StringComparer.Ordinal);
    }

    private async Task<Guid> HeaderOfAsync(Guid ledgerId, string externalId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == externalId)).Id;
    }

    private async Task<Dictionary<Guid, long>> SeqsAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId)
            .ToDictionaryAsync(h => h.Id, h => h.Seq);
    }

    private static async Task<JsonElement> RegisterPageAsync(HttpClient client, Guid ledgerId, Guid accountId, int limit)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/ledgers/{ledgerId}/transactions?account_id={accountId}&limit={limit}"));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> EntriesPageAsync(HttpClient client, Guid ledgerId, string query)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/transactions/entries?{query}"));
        return doc.RootElement.Clone();
    }

    /// <summary>One register entry as the API wrote it, with the header and date its row carries.</summary>
    private sealed record Entry(string Raw, bool IsGroup, Guid HeaderId, DateTime PostedAt)
    {
        public static Entry Of(JsonElement entry)
        {
            var isGroup = entry.GetProperty("kind").GetString() == "group";
            var row = isGroup ? entry.GetProperty("legs")[0] : entry.GetProperty("txn");
            return new Entry(entry.GetRawText(), isGroup, row.GetProperty("headerId").GetGuid(),
                row.GetProperty("postedAt").GetDateTime());
        }
    }

    private sealed record LedgerEntry(Guid AccountId, Entry Entry);

    private static List<Entry> Entries(JsonElement registerPage) =>
        [.. registerPage.GetProperty("entries").EnumerateArray().Select(Entry.Of)];

    private static List<LedgerEntry> Listed(JsonElement entriesPage) =>
        [.. entriesPage.GetProperty("entries").EnumerateArray()
            .Select(e => new LedgerEntry(e.GetProperty("accountId").GetGuid(), Entry.Of(e.GetProperty("entry"))))];
}
