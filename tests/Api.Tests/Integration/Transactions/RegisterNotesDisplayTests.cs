using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The memo and tags the register, the reports and the transaction listing show, read
/// through <see cref="MemoTagRules"/> in the database, against the same rule run in
/// memory (<see cref="MemoTagRules.PresentInMemory{TTag}"/>) over the stored shape.
/// </summary>
/// <remarks>
/// <para>One ledger holds every shape with its notes placed by the write primitive
/// (the side-aware seeds), so every stored state is one a writer produces. Every
/// assertion reads what the API answers: the register page, the report summary, the
/// budget drill-down; the listing is read through its repository, which the MCP tool
/// calls as it is.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RegisterNotesDisplayTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public RegisterNotesDisplayTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    // =====================================================================
    // The register page
    // =====================================================================

    [Fact]
    public async Task Every_register_row_shows_what_the_rule_presents_for_its_leg()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;

        await using var db = Db.NewDbContext();
        var collation = await CollationOrderAsync(db, ledger);
        // The pair the fixture carries sorts one way in the database, the other way
        // by an ordinal comparison, so the order check below can fail.
        Assert.NotEqual(new[] { "a-c", "ab" }.Order(StringComparer.OrdinalIgnoreCase), collation.Order(["a-c", "ab"]));
        var failures = new List<string>();
        var compared = 0;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var seenLegs = new HashSet<Guid>();
        foreach (var account in s.Accounts)
        {
            var page = await PageAsync(client, ledger, account.Id);
            foreach (var row in RowsOf(page))
            {
                compared++;
                kinds.Add(row.NotesKind);
                var expected = await ledger.PresentAsync(row.Id);
                var collapsed = account.AccountType == "investment";
                var diff = Diff(expected, row, collapsed);
                if (diff is not null)
                    failures.Add($"{account.Name} row {row.Id.ToString()[..8]} ({row.Payee}, {row.NotesKind}): {diff}");
                // Every tag list is in the order the database sorts names in.
                foreach (var (field, names) in new[] { ("tags", row.Tags), ("inheritedTags", row.InheritedTags), ("headerTags", row.HeaderTags ?? []) })
                    if (!names.SequenceEqual(collation.Order(names)))
                        failures.Add($"{account.Name} row {row.Id.ToString()[..8]} {field} [{string.Join(",", names)}] " +
                                     $"is not in the database's order [{string.Join(",", collation.Order(names))}]");
                // The raw stored values are not on the row at all (AnnotationContractTests).
            }
            seenLegs.UnionWith(RowsOf(page).Select(r => r.Id));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
        // Every kind is in the ledger, and every leg on a non-investment account is a row.
        Assert.Equal(
            ["eventCounterpartOne", "eventOpposite", "eventSide", "onePosting", "splitOpposite", "splitSide"],
            kinds.Order(StringComparer.Ordinal));
        var bankLegs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledger.LedgerId && db.Accounts.Any(a => a.Id == l.AccountId && a.AccountType != "investment"))
            .Select(l => l.Id).ToListAsync();
        Assert.All(bankLegs, id => Assert.Contains(id, seenLegs));
        Assert.True(compared > 60, $"only {compared} rows compared");
        _output.WriteLine($"{compared} rows compared");
    }

    [Fact]
    public async Task The_stored_notes_read_as_the_rule_computes_them_for_every_shape()
    {
        // Every leg, from every perspective a reader takes: what the readers read of
        // what the writers stored, against the rule run over the loaded rows from a
        // perspective picked in C#. The stored rows were written by the API's own saves
        // and the side-aware seeds, never by hand.
        var (ledger, client, factory, _) = await EveryShapeAsync();
        await using var __ = factory;
        using var ___ = client;
        await using var db = Db.NewDbContext();

        await StoredNotesEquivalence.AssertEquivalentAsync(db, ledger.LedgerId);
        await StoredNotesEquivalence.AssertFiltersEquivalentAsync(db, ledger.LedgerId, StoredNotesEquivalence.FilterSample.All);
        Assert.Empty(await LegNotes.DifferencesAsync(db, ledger.LedgerId, CancellationToken.None));
    }

    [Fact]
    public async Task The_entry_a_save_returns_shows_the_same_notes_as_the_page()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;
        await using var db = Db.NewDbContext();
        var repository = new RegisterRepository(db, TimeProvider.System);

        foreach (var account in s.Accounts.Where(a => a.AccountType != "investment"))
        {
            var page = await PageAsync(client, ledger, account.Id);
            // Every entry a header has here, in the page's order, with the same notes.
            foreach (var header in page.Entries.GroupBy(e => (e.Txn ?? e.Legs![0]).HeaderId))
            {
                var saved = await repository.GetEntriesForHeaderAsync(header.Key, account.Id);
                Assert.Equal(header.SelectMany(EntryRows).Select(r => $"{r.Id}|{Notes(r)}"),
                    saved.SelectMany(EntryRows).Select(r => $"{r.Id}|{Notes(r)}"));
            }
        }

        static IEnumerable<RegisterRowDto> EntryRows(RegisterEntryDto e) => e.Txn is { } txn ? [txn] : e.Legs ?? [];

        static string Notes(RegisterRowDto r) =>
            $"{r.Memo}|{string.Join(",", r.Tags)}|{r.MemoInherited}|{r.TagsInherited}|{r.InheritedMemo}|" +
            $"{string.Join(",", r.InheritedTags)}|{r.InheritFromAccountId}|{r.HeaderMemo}|" +
            $"{(r.HeaderTags is null ? "-" : string.Join(",", r.HeaderTags))}|{r.NotesKind}|{r.IsReadOnly}";
    }

    [Fact]
    public async Task The_page_reads_no_annotation_column_of_the_view()
    {
        // What the rule shows is the only memo and tag source: the rows' statement
        // reads none of the view's memo and tag columns, and so never pays for them.
        var (ledger, client, factory, s) = await EveryShapeAsync(notes: false);
        await using var _ = factory;
        using var __ = client;
        var explain = new ExplainCapture { Selects = sql => sql.Contains("array_agg", StringComparison.Ordinal) };
        await using var logged = new ApiFactory(Db).WithoutDevAuth().WithServices(explain.Register);
        using var loggedClient = await AuthedClientAsync(logged, ledger);

        explain.Enabled = true;
        await PageAsync(loggedClient, ledger, s.BankA.Id);
        var rows = Assert.Single(explain.Drain());
        // Every read of the view in the statement, under whatever name it is given.
        var aliases = System.Text.RegularExpressions.Regex.Matches(rows.Sql, @"resolved_transactions AS (\w+)")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(aliases);
        Assert.DoesNotMatch(@"resolved_transactions(?! AS \w+)", rows.Sql);
        foreach (var alias in aliases)
            Assert.DoesNotMatch($@"\b{alias}\.(memo|tags|leg_memo|header_memo)\b", rows.Sql);
        // It reads the view's other columns, and the rule's stored result.
        Assert.Contains(aliases, alias => System.Text.RegularExpressions.Regex.IsMatch(rows.Sql, $@"\b{alias}\.payee\b"));
        Assert.Contains(aliases, alias => System.Text.RegularExpressions.Regex.IsMatch(rows.Sql, $@"\b{alias}\.notes_memo\b"));
        Assert.Contains("txn_leg_note_tags", rows.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_rail_and_counts_agree_on_inherited_values()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A one-split whose category side inherits the bank side's memo and tag, a
        // one-split the category side tagged itself, and a split whose transaction
        // memo and tag its opposite side never shows. Each line of the split goes to
        // its own category, so a header and an entry are one and the same here.
        var inherited = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee North", (catA.Id, -10m));
        await Notes(ledger, inherited, bank.Id, "trip note", ["trip"], bank.Id);
        var own = await CreateBankAsync(client, ledger, bank.Id, Day(4, 1), "Payee South", (catA.Id, -20m));
        await Notes(ledger, own, catA.Id, null, ["trip"], bank.Id);
        var split = await CreateBankAsync(client, ledger, bank.Id, Day(5, 1), "Payee East", (catA.Id, -5m), (catB.Id, -7m));
        await Notes(ledger, split, bank.Id, "trip whole", ["trip"], bank.Id);
        await CreateBankAsync(client, ledger, bank.Id, Day(6, 1), "Payee West", (catA.Id, -30m));

        var categoryLegs = await LegIdsAsync(inherited, catA.Id);
        foreach (var query in new[] { "&tag=trip", "&search=trip" })
        {
            var page = await PageAsync(client, ledger, catA.Id, query);
            var rows = RowsOf(page).ToList();
            // What the category register shows: the inherited one-split and its own one.
            Assert.Equal(2, page.Entries.Count);
            var row = Assert.Single(rows, r => r.Id == categoryLegs[0]);
            if (query == "&tag=trip")
            {
                Assert.Equal(["trip"], row.Tags);
                Assert.True(row.TagsInherited);
            }
            else
            {
                Assert.Equal("trip note", row.Memo);
                Assert.True(row.MemoInherited);
            }
            // The split's line on Category A shows neither the transaction memo nor its tag.
            Assert.DoesNotContain(rows, r => r.HeaderId == split);

            var buckets = await GetJsonAsync<List<IndexBucketDto>>(client,
                $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={catA.Id}{query}");
            var counts = await GetJsonAsync<RegisterStatusCounts>(client,
                $"/api/ledgers/{ledger.LedgerId}/transactions/status-counts?account_id={catA.Id}{query}");
            Assert.Equal(page.Entries.Count, buckets.Sum(b => b.Count));
            Assert.Equal(page.Entries.Count, counts.All);
        }

        // The split side counts the transaction's tag for the whole transaction.
        var bankPage = await PageAsync(client, ledger, bank.Id, "&tag=trip");
        Assert.Contains(bankPage.Entries, e => e.Kind == "group" && e.GroupId == split);
        var bankBuckets = await GetJsonAsync<List<IndexBucketDto>>(client,
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bank.Id}&tag=trip");
        Assert.Equal(bankPage.Entries.Count, bankBuckets.Sum(b => b.Count));
    }

    [Fact]
    public async Task The_chip_the_rail_the_counts_and_the_page_count_the_same_entries_for_every_shape()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;
        // A category with a descendant and a split with a line on each: one header,
        // two entries in the parent's register read with its descendants.
        var child = await ledger.AddCategoryAsync("Category F", parentId: s.CatA.Id);
        var family = await CreateBankAsync(client, ledger, s.BankA.Id, Day(4, 2), "Payee Family", (s.CatA.Id, -71m), (child.Id, -72m));
        await Notes(ledger, family, s.BankA.Id, "family whole", ["t-family"], s.BankA.Id);
        var hidden = await CreateBankAsync(client, ledger, s.BankA.Id, Day(4, 3), "Payee Hidden", (s.CatA.Id, -73m), (s.CatA.Id, -74m));
        await ledger.HideTransactionAsync(hidden);

        var registers = s.Accounts.Select(a => (a.Name, a.Id, Scope: ""))
            .Append(("Category A and below", s.CatA.Id, "&include_subcategories=true"))
            .ToList();
        var filters = new[] { "", "&tag=t-sym-line", "&search=Family", "&search=Payee", "&date_from=2026-03-01&date_to=2026-03-31" };
        var failures = new List<string>();
        var moreEntriesThanHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, account, scope) in registers)
        foreach (var filter in filters)
        {
            var query = scope + filter;
            var entries = await AllEntriesAsync(client, ledger, account, query);
            var hiddenEntries = await AllEntriesAsync(client, ledger, account, query + "&hidden=true");
            var buckets = await GetJsonAsync<List<IndexBucketDto>>(client,
                $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={account}{query}");
            var counts = await GetJsonAsync<RegisterStatusCounts>(client,
                $"/api/ledgers/{ledger.LedgerId}/transactions/status-counts?account_id={account}{query}");
            var cell = $"{name}{query}";

            // The chip is the rail's total; each month's bucket is that month's entries.
            var byMonth = entries.GroupBy(e => $"{PostedOf(e):yyyy-MM}").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var rail = buckets.ToDictionary(b => b.YearMonth, b => b.Count, StringComparer.Ordinal);
            if (buckets.Sum(b => b.Count) != entries.Count)
                failures.Add($"{cell}: chip {buckets.Sum(b => b.Count)}, page {entries.Count}");
            if (!rail.OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(byMonth.OrderBy(kv => kv.Key, StringComparer.Ordinal)))
                failures.Add($"{cell}: rail [{string.Join(",", rail.Select(kv => $"{kv.Key}={kv.Value}"))}], " +
                             $"page [{string.Join(",", byMonth.Select(kv => $"{kv.Key}={kv.Value}"))}]");
            if (counts.All != entries.Count || counts.Hidden != hiddenEntries.Count)
                failures.Add($"{cell}: counts {counts.All} / hidden {counts.Hidden}, page {entries.Count} / hidden {hiddenEntries.Count}");
            if (entries.Count > entries.Select(HeaderOf).Distinct().Count())
                moreEntriesThanHeaders.Add(cell);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
        // The shapes where an entry is not a header are in the matrix: the symmetric
        // shape's lines and a category read with its descendants.
        Assert.Contains("Category A", moreEntriesThanHeaders);
        Assert.Contains("Category A and below&include_subcategories=true&search=Family", moreEntriesThanHeaders);
        Assert.Contains("Category A&search=Payee", moreEntriesThanHeaders);

        static DateTime PostedOf(RegisterEntryDto e) => (e.Txn ?? e.Legs![0]).PostedAt;
        static Guid HeaderOf(RegisterEntryDto e) => (e.Txn ?? e.Legs![0]).HeaderId;
    }

    /// <summary>Every entry of a register under a filter, walked page by page to its end.</summary>
    private static async Task<List<RegisterEntryDto>> AllEntriesAsync(HttpClient client, SyntheticLedger ledger, Guid account, string query)
    {
        var entries = new List<RegisterEntryDto>();
        string? cursor = null;
        for (var pageNo = 0; pageNo < 200; pageNo++)
        {
            var page = await GetJsonAsync<RegisterPage>(client,
                $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={account}&limit=7{query}" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            entries.AddRange(page.Entries);
            cursor = page.CursorForOlder;
            if (cursor is null) return entries;
        }
        throw new InvalidOperationException("the register did not end");
    }

    // =====================================================================
    // Reports
    // =====================================================================

    [Fact]
    public async Task Every_tag_cell_counts_each_posting_as_its_real_account_shows_it()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;

        var expected = await ReportOracleAsync(ledger);
        var report = await SummaryAsync(client, ledger, "&groupBy=Tag");

        var cells = report.Rows.ToDictionary(r => r.GroupName, r => r.Amount, StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var (tag, amount) in expected.ByTag)
            if (!cells.TryGetValue(tag, out var actual) || actual != amount)
                failures.Add($"{tag}: {(cells.TryGetValue(tag, out var a) ? a : null)}, expected {amount}");
        foreach (var name in cells.Keys.Where(k => k != "(untagged)" && !expected.ByTag.ContainsKey(k)))
            failures.Add($"{name}: a cell the rule gives no posting");
        if (cells.GetValueOrDefault("(untagged)") != expected.Untagged)
            failures.Add($"(untagged): {cells.GetValueOrDefault("(untagged)")}, expected {expected.Untagged}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Tagged plus untagged is the total: every posting is in a tag's cell or in
        // "(untagged)", never both, never neither.
        var total = (await SummaryAsync(client, ledger, "")).Total;
        Assert.Equal(total, expected.Tagged + expected.Untagged);
        Assert.NotEqual(0m, expected.Tagged);
        Assert.NotEqual(0m, expected.Untagged);

        // The tag filter keeps exactly the postings a tag's cell counts.
        await using var db = Db.NewDbContext();
        foreach (var (tag, amount) in expected.ByTag)
        {
            var tagId = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId && t.Name == tag)
                .Select(t => t.Id).SingleAsync();
            Assert.Equal(amount, (await SummaryAsync(client, ledger, $"&tagId={tagId}")).Total);
        }
    }

    [Fact]
    public async Task A_report_counts_the_transactions_tags_per_line_and_never_a_category_side_override()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A split: the transaction tag counts for both lines' money, the first line's
        // own tag for that line only. The same tag on the transaction and a line
        // counts that line once.
        var split = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee A", (catA.Id, -60m), (catB.Id, -40m));
        await Notes(ledger, split, bank.Id, null, ["whole"], bank.Id,
            new LineIntent<string>(0, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set(["line", "whole"])));
        // A one-split the bank side tagged: its category inherits it.
        var bankSide = await CreateBankAsync(client, ledger, bank.Id, Day(3, 2), "Payee B", (catA.Id, -7m));
        await Notes(ledger, bankSide, bank.Id, null, ["entered"], bank.Id);
        // A one-split only the category side tagged: the real side inherits it, so it counts.
        var categorySide = await CreateBankAsync(client, ledger, bank.Id, Day(3, 3), "Payee C", (catA.Id, -3m));
        await Notes(ledger, categorySide, catA.Id, null, ["category-only"], bank.Id);
        // Both sides tagged: the real side's own counts, the category side's never.
        var both = await CreateBankAsync(client, ledger, bank.Id, Day(3, 4), "Payee D", (catB.Id, -2m));
        await Notes(ledger, both, bank.Id, null, ["entered"], bank.Id);
        await Notes(ledger, both, catB.Id, null, ["override"], bank.Id);
        // A split whose line keeps a category-side override from before the split.
        var kept = await SplitKeepingCategoryOverrideAsync(client, ledger, bank.Id, catA.Id, catB.Id, "override");
        // A split entered from a category to two real accounts: each real account is
        // its opposite side, so it shows its own value, else that line's; the
        // transaction's tags reach no real register and never count.
        var bank2 = await ledger.AddBankAccountAsync("Account B");
        var owned = await ImportFromCategoryAsync(ledger, catA.Id, Day(3, 5), "Payee E", (bank.Id, 30m), (bank2.Id, 20m));
        await Notes(ledger, owned, catA.Id, "owned whole", ["owned-whole"], catA.Id,
            new LineIntent<string>(0, FieldEdit<string>.Set("owned line"), FieldEdit<IReadOnlyList<string>>.Set(["owned-line"])),
            new LineIntent<string>(1, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set(["owned-line-two"])));
        // A posting between two categories: each leg counts as its own category shows it.
        var between = await ImportFromCategoryAsync(ledger, catA.Id, Day(3, 6), "Payee F", (catB.Id, 5m));
        await Notes(ledger, between, catA.Id, null, ["between-a"], catA.Id);
        await Notes(ledger, between, catB.Id, null, ["between-b"], catA.Id);

        var cells = (await SummaryAsync(client, ledger, "&groupBy=Tag", measure: "Spending")).Rows
            .ToDictionary(r => r.GroupName, r => r.Amount, StringComparer.Ordinal);

        Assert.Equal(100m, cells["whole"]);
        Assert.Equal(60m, cells["line"]);
        Assert.Equal(9m, cells["entered"]);
        Assert.Equal(3m, cells["category-only"]);
        Assert.False(cells.ContainsKey("override"));
        Assert.Equal(30m, cells["owned-line"]);
        Assert.Equal(20m, cells["owned-line-two"]);
        Assert.False(cells.ContainsKey("owned-whole"));
        Assert.Equal(5m, cells["between-a"]);
        Assert.Equal(-5m, cells["between-b"]);
        // The kept split's lines carry no tag a report counts, and nothing else is untagged.
        Assert.Equal(kept.Total, cells["(untagged)"]);

        // Grouped by the account each posting pairs with, a leg of a posting between two
        // categories is filed under the other category and counted as that category
        // shows the posting: Category A's leg goes under Category B with Category B's tag.
        await using var db = Db.NewDbContext();
        var tagIds = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.Ordinal);
        var underB = Assert.Single((await SummaryAsync(client, ledger,
            $"&groupBy=Account&tagId={tagIds["between-b"]}", measure: "Spending")).Rows);
        Assert.Equal((catB.Id, 5m), (underB.GroupId!.Value, underB.Amount));
        var underA = Assert.Single((await SummaryAsync(client, ledger,
            $"&groupBy=Account&tagId={tagIds["between-a"]}", measure: "Spending")).Rows);
        Assert.Equal((catA.Id, -5m), (underA.GroupId!.Value, underA.Amount));
        // Grouped by category, each leg is counted as its own category shows it.
        var ownB = Assert.Single((await SummaryAsync(client, ledger,
            $"&groupBy=Category&tagId={tagIds["between-b"]}", measure: "Spending")).Rows);
        Assert.Equal((catB.Id, -5m), (ownB.GroupId!.Value, ownB.Amount));
    }

    // =====================================================================
    // The transaction listing (MCP list_transactions, the budget drill-down)
    // =====================================================================

    [Fact]
    public async Task Every_listed_line_shows_what_the_register_of_the_leg_in_scope_shows()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;
        await using var db = Db.NewDbContext();
        var reporting = new ReportingRepository(db, TimeProvider.System);
        var legs = await LegsAsync(ledger);
        var collation = await CollationOrderAsync(db, ledger);
        var failures = new List<string>();
        var compared = new Dictionary<string, int>(StringComparer.Ordinal);

        StoredLeg LegOf(TransactionLine line) =>
            legs.Single(l => l.HeaderId == line.HeaderId && l.AccountId == line.AccountId && l.Amount == line.Amount
                             && l.OtherAccountId == line.CounterpartyAccountId);

        // Each line against the register of the leg it is presented from.
        async Task CompareAsync(string scope, IEnumerable<TransactionLine> lines, Func<StoredLeg, Guid> seenFrom)
        {
            foreach (var line in lines)
            {
                var leg = LegOf(line);
                compared[scope] = compared.GetValueOrDefault(scope) + 1;
                var shown = await ledger.PresentAsync(seenFrom(leg));
                var transactionMemo = shown.Presentation.Kind == LegKind.SplitSide ? shown.Presentation.InPlayHeaderMemo : null;
                var tags = collation.Order(shown.InPlayTags);
                if (line.Memo != shown.Memo || line.TransactionMemo != transactionMemo || !line.Tags.SequenceEqual(tags))
                    failures.Add($"{scope} {line.Payee} on {line.AccountName}: memo '{line.Memo}' / '{line.TransactionMemo}' " +
                                 $"tags [{string.Join(",", line.Tags)}], expected '{shown.Memo}' / '{transactionMemo}' " +
                                 $"[{string.Join(",", tags)}]");
            }
        }

        // Scoped to an account: that account's register.
        foreach (var account in s.Accounts)
        {
            var scoped = await reporting.ListTransactionsAsync(new TransactionQuery { LedgerId = ledger.LedgerId, AccountId = account.Id, Limit = 500 });
            await CompareAsync("account", scoped.Lines, leg => leg.Id);
        }

        // Without an account nothing is in scope: each money-side line from its own
        // register, and a line between two real accounts from the posting's outflow
        // leg, so both lines of a transfer read the same.
        // A zero amount has no outflow leg: the leg on the side the header belongs to,
        // the side whose register groups by it (the zero-amount test pins the
        // originating side on transfers entered from each account).
        var headersSide = new HashSet<Guid>();
        foreach (var leg in legs.Where(l => l.OtherIsReal && l.Amount == 0m))
            if ((await ledger.PresentAsync(leg.OtherId!.Value)).Presentation.GroupsByHeader)
                headersSide.Add(leg.OtherId!.Value);
        Guid Outflow(StoredLeg leg) =>
            leg.OtherIsReal && (leg.Amount > 0m || (leg.Amount == 0m && headersSide.Contains(leg.OtherId!.Value)))
                ? leg.OtherId!.Value
                : leg.Id;
        var all = await reporting.ListTransactionsAsync(new TransactionQuery { LedgerId = ledger.LedgerId, Limit = 500 });
        await CompareAsync("ledger", all.Lines.Where(l => !LegOf(l).OtherIsReal), Outflow);
        var realToReal = all.Lines.Where(l => LegOf(l).OtherIsReal).ToList();
        await CompareAsync("ledger, real to real", realToReal, Outflow);
        // The fixture tells the two readings apart: some inflow line shows something
        // other than its own register.
        var differs = 0;
        foreach (var leg in realToReal.Select(LegOf).Where(l => l.Amount > 0m))
            if (Notes(await ledger.PresentAsync(leg.Id)) != Notes(await ledger.PresentAsync(Outflow(leg))))
                differs++;
        Assert.True(differs > 0, "no line between two real accounts reads differently from its outflow leg");

        // The budget drill-down: the money side of a category's lines, each presented
        // as the category it pairs with shows it, the register the drill-down is of.
        var budget = await GetJsonAsync<TransactionLinesResult>(client,
            $"/api/ledgers/{ledger.LedgerId}/budget/transactions?month=2026-03&categoryId={s.CatA.Id}&limit=200");
        Assert.All(budget.Lines, l => Assert.Equal(3, l.PostedAt.Month));
        Assert.Equal(
            legs.Where(l => l.AccountType != "category" && l.OtherAccountId == s.CatA.Id).Select(l => l.Id).Order(),
            budget.Lines.Select(LegOf).Select(l => l.Id).Order());
        await CompareAsync("budget", budget.Lines, leg => leg.OtherId!.Value);
        // The same scope through the listing's category filter.
        var byCategory = await reporting.ListTransactionsAsync(new TransactionQuery { LedgerId = ledger.LedgerId, CategoryId = s.CatA.Id, Limit = 500 });
        await CompareAsync("category", byCategory.Lines, leg => leg.OtherId!.Value);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
        Assert.True(compared["account"] > 60, $"only {compared["account"]} lines compared");
        Assert.True(compared["ledger, real to real"] > 4, $"only {compared["ledger, real to real"]} real-to-real lines compared");
        Assert.True(compared["budget"] > 4, $"only {compared["budget"]} budget lines compared");

        static string Notes(ShownNotes<string> n) =>
            $"{n.Memo}|{(n.Presentation.Kind == LegKind.SplitSide ? n.Presentation.InPlayHeaderMemo : null)}|" +
            string.Join(",", n.InPlayTags.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Both_lines_of_a_zero_amount_transfer_read_from_the_originating_side()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var first = await ledger.AddBankAccountAsync("Account A");
        var second = await ledger.AddBankAccountAsync("Account B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Entered from each account in turn, so the account order cannot stand in for
        // the originating side: one of the two transfers originates on the account the
        // database orders last.
        await using var db = Db.NewDbContext();
        var lower = await db.Accounts.AsNoTracking().Where(a => a.Id == first.Id || a.Id == second.Id)
            .OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
        var higher = lower == first.Id ? second.Id : first.Id;
        var entries = new List<(Guid Header, Guid Entered, string Side, int Day)>();
        foreach (var (entered, other, side, day) in new[] { (lower, higher, "lower", 1), (higher, lower, "higher", 2) })
        {
            var header = await CreateBankAsync(client, ledger, entered, Day(3, day), "Payee Zero", (other, 0m));
            await Notes(ledger, header, lower, $"lower note {day}", [$"t-lower-{day}"], entered);
            await Notes(ledger, header, higher, $"higher note {day}", [$"t-higher-{day}"], entered);
            Assert.Equal(entered, await db.TxnHeaders.AsNoTracking().Where(h => h.Id == header)
                .Select(h => h.OriginatingAccountId).SingleAsync());
            entries.Add((header, entered, side, day));
        }

        var reporting = new ReportingRepository(db, TimeProvider.System);
        var lines = (await reporting.ListTransactionsAsync(new TransactionQuery { LedgerId = ledger.LedgerId, Limit = 500 })).Lines;
        foreach (var (header, entered, side, day) in entries)
        {
            var mine = lines.Where(l => l.HeaderId == header).ToList();
            Assert.Equal(new[] { first.Id, second.Id }.Order(), mine.Select(l => l.AccountId).Order());
            var originatingLeg = Assert.Single(await LegIdsAsync(header, entered));
            var shown = await ledger.PresentAsync(originatingLeg);
            Assert.Equal($"{side} note {day}", shown.Memo);
            Assert.All(mine, l =>
            {
                Assert.Equal(shown.Memo, l.Memo);
                Assert.Null(l.TransactionMemo);
                Assert.Equal(shown.InPlayTags.Order(StringComparer.Ordinal), l.Tags.Order(StringComparer.Ordinal));
            });
        }
    }

    [Fact]
    public async Task A_listing_finds_a_line_by_what_its_register_shows_and_never_by_the_transaction_level_on_the_opposite_side()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var oneSplit = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee North", (catA.Id, -10m));
        await Notes(ledger, oneSplit, bank.Id, "entered note", ["entered"], bank.Id);
        var split = await CreateBankAsync(client, ledger, bank.Id, Day(3, 2), "Payee South", (catA.Id, -5m), (catB.Id, -7m));
        await Notes(ledger, split, bank.Id, "whole note", ["whole"], bank.Id,
            new LineIntent<string>(0, FieldEdit<string>.Set("line note"), FieldEdit<IReadOnlyList<string>>.Leave));

        await using var db = Db.NewDbContext();
        var reporting = new ReportingRepository(db, TimeProvider.System);
        var tags = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId).ToDictionaryAsync(t => t.Name, t => t.Id);
        async Task<List<(Guid Header, Guid Account)>> FindAsync(Guid? account, string? text = null, string? tag = null) =>
            [.. (await reporting.ListTransactionsAsync(new TransactionQuery
            {
                LedgerId = ledger.LedgerId, AccountId = account, Text = text, TagId = tag is null ? null : tags[tag],
            })).Lines.Select(l => (l.HeaderId, l.AccountId))];

        // The category inherits the bank side's memo and tag, and is found by them.
        Assert.Equal([(oneSplit, catA.Id)], await FindAsync(catA.Id, text: "entered note"));
        Assert.Equal([(oneSplit, catA.Id)], await FindAsync(catA.Id, tag: "entered"));
        // The split side is found by the transaction memo and tag and by the line's own.
        Assert.Equal(2, (await FindAsync(bank.Id, text: "whole note")).Count(x => x.Header == split));
        Assert.Equal(2, (await FindAsync(bank.Id, tag: "whole")).Count(x => x.Header == split));
        Assert.Equal([(split, bank.Id)], (await FindAsync(bank.Id, text: "line note")).Where(x => x.Header == split));
        // The opposite side shows the line's own memo, never the transaction's.
        Assert.Equal([(split, catA.Id)], await FindAsync(catA.Id, text: "line note"));
        Assert.Empty(await FindAsync(catA.Id, text: "whole note"));
        Assert.Empty(await FindAsync(catB.Id, text: "whole note"));
        Assert.Empty(await FindAsync(catA.Id, tag: "whole"));
        // With no account the money side is read: the split's two bank lines.
        Assert.Equal(2, (await FindAsync(null, tag: "whole")).Count(x => x.Header == split && x.Account == bank.Id));

        var bankLines = (await reporting.ListTransactionsAsync(new TransactionQuery { LedgerId = ledger.LedgerId, AccountId = bank.Id }))
            .Lines.Where(l => l.HeaderId == split).OrderBy(l => l.Amount).ToList();
        Assert.All(bankLines, l => Assert.Equal("whole note", l.TransactionMemo));
        Assert.All(bankLines, l => Assert.Contains("whole", l.Tags));
        Assert.Contains(bankLines, l => l.Memo == "line note");
        Assert.Contains(bankLines, l => l.Memo is null);
    }

    // =====================================================================
    // Tag and text filters, side by side
    // =====================================================================

    /// <summary>
    /// The shapes where a tag filter reads the row's own leg, the posting's other leg, or
    /// both: a one-split whose bank side and category side carry different tags, a
    /// transfer whose two sides carry different tags, a transfer and a one-split with the
    /// same tag on both sides, and a split with a transaction tag and a line tag.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, HttpClient Client, ApiFactory Factory, SideBySide Shape)> SideBySideAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = await AuthedClientAsync(factory, ledger);

        var oneSplit = await CreateBankAsync(client, ledger, bankA.Id, Day(4, 1), "Payee One", (catA.Id, -10m));
        await Notes(ledger, oneSplit, bankA.Id, "bank side memo", ["bank-only"], bankA.Id);
        await Notes(ledger, oneSplit, catA.Id, "category side memo", ["category-only"], bankA.Id);
        var transfer = await CreateBankAsync(client, ledger, bankA.Id, Day(4, 2), "Payee Gamma", (bankB.Id, -20m));
        await Notes(ledger, transfer, bankA.Id, "alpha memo", ["a-side"], bankA.Id);
        await Notes(ledger, transfer, bankB.Id, "beta memo", ["b-side"], bankA.Id);
        var bothTransfer = await CreateBankAsync(client, ledger, bankA.Id, Day(4, 3), "Payee Both", (bankB.Id, -30m));
        await Notes(ledger, bothTransfer, bankA.Id, null, ["both"], bankA.Id);
        await Notes(ledger, bothTransfer, bankB.Id, null, ["both"], bankA.Id);
        var bothOneSplit = await CreateBankAsync(client, ledger, bankB.Id, Day(4, 4), "Payee Both", (catB.Id, -5m));
        await Notes(ledger, bothOneSplit, bankB.Id, null, ["both"], bankB.Id);
        await Notes(ledger, bothOneSplit, catB.Id, null, ["both"], bankB.Id);
        var split = await CreateBankAsync(client, ledger, bankA.Id, Day(4, 5), "Payee Split", (catA.Id, -60m), (catB.Id, -40m));
        await Notes(ledger, split, bankA.Id, "whole memo", ["whole"], bankA.Id,
            new LineIntent<string>(0, FieldEdit<string>.Set("line memo"), FieldEdit<IReadOnlyList<string>>.Set(["line"])));
        return (ledger, client, factory,
            new SideBySide(bankA.Id, bankB.Id, catA.Id, catB.Id, oneSplit, transfer, bothTransfer, bothOneSplit, split));
    }

    private sealed record SideBySide(
        Guid BankA, Guid BankB, Guid CatA, Guid CatB,
        Guid OneSplit, Guid Transfer, Guid BothTransfer, Guid BothOneSplit, Guid Split);

    /// <summary>The legs whose stored presentation has <paramref name="tag"/> in play, by header and account.</summary>
    private async Task<HashSet<(Guid Header, Guid Account)>> InPlayLegsAsync(SyntheticLedger ledger, string tag)
    {
        await using var db = Db.NewDbContext();
        var rows = await (
            from t in db.TxnLegNoteTags.AsNoTracking()
            join g in db.Tags.AsNoTracking() on t.TagId equals g.Id
            join l in db.TxnLegs.AsNoTracking() on t.LegId equals l.Id
            where g.LedgerId == ledger.LedgerId && g.Name == tag && t.InPlay
            select new { l.HeaderId, l.AccountId }).ToListAsync();
        return [.. rows.Select(r => (r.HeaderId, r.AccountId))];
    }

    /// <summary>
    /// Each listed line by its header, account and amount, with how often it is listed:
    /// a line listed twice counts two.
    /// </summary>
    private static List<string> Keys(IEnumerable<TransactionLine> lines) =>
        [.. lines.Select(l => Key(l.HeaderId, l.AccountId, l.Amount)).Order(StringComparer.Ordinal)];

    private static List<string> Ordered(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];

    private static string Key(Guid header, Guid account, decimal amount) =>
        $"{header}|{account}|{amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The lines a listing in <paramref name="scope"/> must return, by the in-memory rule:
    /// each line presented from the leg the scope reads it from (the account's own; with
    /// nothing in scope, a line between two real accounts from the outflow leg), kept
    /// when <paramref name="keep"/> holds for what that leg shows.
    /// </summary>
    private async Task<List<string>> ExpectedLinesAsync(
        SyntheticLedger ledger, Guid? account, Func<StoredLeg, ShownNotes<string>, bool> keep)
    {
        var expected = new List<string>();
        foreach (var leg in await LegsAsync(ledger))
        {
            if (account is { } scoped ? leg.AccountId != scoped : leg.AccountType == "category") continue;
            var seenFrom = account is null && leg.OtherIsReal && leg.Amount > 0m ? leg.OtherId!.Value : leg.Id;
            if (keep(leg, await ledger.PresentAsync(seenFrom)))
                expected.Add(Key(leg.HeaderId, leg.AccountId, leg.Amount));
        }
        return [.. expected.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_tag_filter_keeps_each_line_and_posting_by_the_side_it_reads_and_lists_it_once()
    {
        var (ledger, client, factory, s) = await SideBySideAsync();
        await using var _ = factory;
        using var __ = client;
        await using var db = Db.NewDbContext();
        var reporting = new ReportingRepository(db, TimeProvider.System);
        var tags = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.Ordinal);
        async Task<List<string>> ListAsync(string tag, Guid? account = null) =>
            Keys((await reporting.ListTransactionsAsync(new TransactionQuery
            {
                LedgerId = ledger.LedgerId, AccountId = account, TagId = tags[tag], Limit = 500,
            })).Lines);

        // The fixture holds each case it claims, in the stored presentation.
        Assert.Equal([(s.OneSplit, s.BankA)], await InPlayLegsAsync(ledger, "bank-only"));
        Assert.Equal([(s.OneSplit, s.CatA)], await InPlayLegsAsync(ledger, "category-only"));
        Assert.Equal([(s.Transfer, s.BankA)], await InPlayLegsAsync(ledger, "a-side"));
        Assert.Equal([(s.Transfer, s.BankB)], await InPlayLegsAsync(ledger, "b-side"));
        Assert.Equal(
            new HashSet<(Guid, Guid)> { (s.BothTransfer, s.BankA), (s.BothTransfer, s.BankB), (s.BothOneSplit, s.BankB), (s.BothOneSplit, s.CatB) },
            await InPlayLegsAsync(ledger, "both"));
        Assert.Contains((s.Split, s.BankA), await InPlayLegsAsync(ledger, "whole"));
        Assert.Contains((s.Split, s.BankA), await InPlayLegsAsync(ledger, "line"));

        // Every tag, every scope: exactly the lines the rule keeps, each once.
        var failures = new List<string>();
        foreach (var tag in tags.Keys)
            foreach (var account in new Guid?[] { null, s.BankA, s.BankB, s.CatA, s.CatB })
            {
                var expected = await ExpectedLinesAsync(ledger, account, (leg, shown) => shown.InPlayTags.Contains(tag, StringComparer.Ordinal));
                var actual = await ListAsync(tag, account);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    failures.Add($"tag {tag} in {(account is null ? "the ledger" : account.ToString())}: " +
                                 $"[{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");
            }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // The cases by name. With nothing in scope, the bank line of a one-split reads
        // its own leg: the bank side's tag keeps it, the category side's does not.
        Assert.Equal([Key(s.OneSplit, s.BankA, -10m)], await ListAsync("bank-only"));
        Assert.Empty(await ListAsync("category-only"));
        // A transfer reads the outflow leg for both lines: the inflow line is kept by a
        // tag only on the other side, and never by its own.
        Assert.Equal(Ordered([Key(s.Transfer, s.BankA, -20m), Key(s.Transfer, s.BankB, 20m)]), await ListAsync("a-side"));
        Assert.Empty(await ListAsync("b-side"));
        // In its own register the inflow line reads its own leg.
        Assert.Equal([Key(s.Transfer, s.BankB, 20m)], await ListAsync("b-side", s.BankB));
        Assert.Empty(await ListAsync("a-side", s.BankB));
        // On both sides: each line once.
        Assert.Equal(
            Ordered([Key(s.BothTransfer, s.BankA, -30m), Key(s.BothTransfer, s.BankB, 30m), Key(s.BothOneSplit, s.BankB, -5m)]),
            await ListAsync("both"));
        // A split: the transaction's tag keeps both lines, a line's own keeps that line.
        Assert.Equal(Ordered([Key(s.Split, s.BankA, -60m), Key(s.Split, s.BankA, -40m)]), await ListAsync("whole"));
        Assert.Equal([Key(s.Split, s.BankA, -60m)], await ListAsync("line"));

        // Reports read each category posting from the real account it pairs with: the
        // other leg. A tag on both sides counts the posting once.
        async Task<decimal> SpendingAsync(string tag) =>
            (await SummaryAsync(client, ledger, $"&tagId={tags[tag]}", measure: "Spending")).Total!.Value;
        Assert.Equal(10m, await SpendingAsync("bank-only"));
        Assert.Equal(0m, await SpendingAsync("category-only"));
        Assert.Equal(5m, await SpendingAsync("both"));
        Assert.Equal(100m, await SpendingAsync("whole"));
        Assert.Equal(60m, await SpendingAsync("line"));
        var byTag = await ReportOracleAsync(ledger);
        foreach (var (tag, id) in tags)
            Assert.Equal(byTag.ByTag.GetValueOrDefault(tag),
                (await SummaryAsync(client, ledger, $"&tagId={id}")).Total);
    }

    [Fact]
    public async Task A_text_search_finds_a_memo_shown_from_either_side_and_a_payee()
    {
        var (ledger, client, factory, s) = await SideBySideAsync();
        await using var _ = factory;
        using var __ = client;
        await using var db = Db.NewDbContext();
        var reporting = new ReportingRepository(db, TimeProvider.System);
        async Task<List<string>> FindAsync(string text, Guid? account = null) =>
            Keys((await reporting.ListTransactionsAsync(new TransactionQuery
            {
                LedgerId = ledger.LedgerId, AccountId = account, Text = text, Limit = 500,
            })).Lines);

        // The transfer's memos sit one on each side.
        Assert.Equal("alpha memo", (await ledger.PresentAsync(Assert.Single(await LegIdsAsync(s.Transfer, s.BankA)))).Memo);
        Assert.Equal("beta memo", (await ledger.PresentAsync(Assert.Single(await LegIdsAsync(s.Transfer, s.BankB)))).Memo);

        static bool Has(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
        var headers = await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(h => h.Id, h => h.Payee);
        var failures = new List<string>();
        foreach (var text in new[] { "alpha", "BETA", "bank side", "category side", "whole memo", "line memo", "Gamma", "Payee", "memo", "none such" })
            foreach (var account in new Guid?[] { null, s.BankA, s.BankB, s.CatA, s.CatB })
            {
                var expected = await ExpectedLinesAsync(ledger, account, (leg, shown) =>
                    Has(headers[leg.HeaderId], text) || Has(shown.Memo, text) || Has(shown.Presentation.InPlayHeaderMemo, text));
                var actual = await FindAsync(text, account);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    failures.Add($"text '{text}' in {(account is null ? "the ledger" : account.ToString())}: " +
                                 $"[{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");
            }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // The cases by name: with nothing in scope both lines of the transfer read the
        // outflow side, so its memo finds both and the inflow side's memo neither; in
        // the inflow account's own register its memo finds its line.
        Assert.Equal(Ordered([Key(s.Transfer, s.BankA, -20m), Key(s.Transfer, s.BankB, 20m)]), await FindAsync("alpha"));
        Assert.Empty(await FindAsync("beta"));
        Assert.Equal([Key(s.Transfer, s.BankB, 20m)], await FindAsync("beta", s.BankB));
        // A payee, which no memo holds, finds every line of its transaction.
        Assert.Equal(2, (await FindAsync("Gamma")).Count);
        // The category side's own memo is found in its own register, never from the bank's.
        Assert.Equal([Key(s.OneSplit, s.CatA, 10m)], await FindAsync("category side", s.CatA));
        Assert.Empty(await FindAsync("category side", s.BankA));
    }

    [Fact]
    public async Task Tag_and_text_filtered_pages_join_up_across_page_boundaries()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var cat = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // On one day, so the order falls to the header's sequence, the posting and the
        // leg: transfers list two lines that tie on everything but the leg. Untagged
        // transactions sit between the tagged ones.
        for (var i = 0; i < 5; i++)
        {
            var transfer = await CreateBankAsync(client, ledger, bankA.Id, Day(5, 1), $"Payee T{i}", (bankB.Id, -(10m + i)));
            await Notes(ledger, transfer, bankA.Id, $"paged memo {i}", ["paged"], bankA.Id);
            await CreateBankAsync(client, ledger, bankA.Id, Day(5, 1), $"Payee N{i}", (cat.Id, -(50m + i)));
            var split = await CreateBankAsync(client, ledger, bankA.Id, Day(5, 1), $"Payee S{i}", (cat.Id, -(70m + i)), (cat.Id, -(80m + i)));
            await Notes(ledger, split, bankA.Id, $"paged split {i}", ["paged"], bankA.Id);
        }

        await using var db = Db.NewDbContext();
        var reporting = new ReportingRepository(db, TimeProvider.System);
        var tag = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId && t.Name == "paged").Select(t => t.Id).SingleAsync();
        foreach (var (name, filter) in new (string, Func<TransactionQuery, TransactionQuery>)[]
                 {
                     ("tag", q => new TransactionQuery { LedgerId = q.LedgerId, TagId = tag, Sort = q.Sort, Descending = q.Descending, Limit = q.Limit, Offset = q.Offset }),
                     ("text", q => new TransactionQuery { LedgerId = q.LedgerId, Text = "paged", Sort = q.Sort, Descending = q.Descending, Limit = q.Limit, Offset = q.Offset }),
                 })
            foreach (var (sort, descending) in new[] { (TransactionSort.Date, true), (TransactionSort.Date, false), (TransactionSort.Amount, true) })
            {
                var whole = await reporting.ListTransactionsAsync(filter(new TransactionQuery
                {
                    LedgerId = ledger.LedgerId, Sort = sort, Descending = descending, Limit = 500,
                }));
                // Five transfers of two lines and five splits of two lines.
                Assert.Equal(20, whole.Lines.Count);
                Assert.False(whole.HasMore);
                Assert.Equal(whole.Lines.Count, Keys(whole.Lines).Distinct(StringComparer.Ordinal).Count());
                foreach (var size in new[] { 3, 7 })
                {
                    var joined = new List<TransactionLine>();
                    for (var offset = 0; ; offset += size)
                    {
                        var page = await reporting.ListTransactionsAsync(filter(new TransactionQuery
                        {
                            LedgerId = ledger.LedgerId, Sort = sort, Descending = descending, Limit = size, Offset = offset,
                        }));
                        joined.AddRange(page.Lines);
                        Assert.Equal(offset + size < whole.Lines.Count, page.HasMore);
                        if (!page.HasMore) break;
                    }
                    Assert.True(
                        whole.Lines.Select(l => (l.HeaderId, l.AccountId, l.Amount))
                            .SequenceEqual(joined.Select(l => (l.HeaderId, l.AccountId, l.Amount))),
                        $"{name} by {sort} {(descending ? "desc" : "asc")} in pages of {size}: the pages do not join up to the whole listing");
                }
            }
    }

    // =====================================================================
    // What the rule leaves unchanged, against the readers before it
    // =====================================================================

    /// <summary>
    /// Tags whose report cell the rule changes on this ledger, and why. Every other tag
    /// counts what the reports counted before; each one listed must really differ.
    /// </summary>
    private static readonly Dictionary<string, string> ChangedCells = new(StringComparer.Ordinal)
    {
        ["t-both-b"] = "a one-split's category-side tag, replaced by the real side's own",
        ["t-owned"] = "the transaction tags of a split entered from a category, which no real register shows",
        ["t-between-a"] = "a posting between two categories counts each leg's own tags, not both legs'",
        ["t-between-b"] = "a posting between two categories counts each leg's own tags, not both legs'",
    };

    /// <summary>
    /// Tags whose tag-filter total the rule changes on this ledger: the filter keeps the
    /// postings the tag is in play for, where it used to keep every posting of a
    /// transaction that carried the tag anywhere.
    /// </summary>
    private static readonly Dictionary<string, string> ChangedFilters = new(StringComparer.Ordinal)
    {
        ["t-both-b"] = "a category-side tag no longer counts",
        ["t-split-line"] = "a line's own tag keeps that line only",
        ["t-line-only"] = "a line's own tag keeps that line only",
        ["t-sym-line"] = "a line's own tag keeps that line only",
        ["t-imported-line"] = "a line's own tag keeps that line only",
        ["t-owned"] = "the transaction tags of a split entered from a category no longer count",
        ["t-owned-line"] = "a line's own tag keeps that line only",
        ["t-between-a"] = "each leg of a posting between two categories keeps its own tags",
        ["t-between-b"] = "each leg of a posting between two categories keeps its own tags",
    };

    [Fact]
    public async Task What_the_rule_does_not_change_reads_as_the_readers_before_it_did()
    {
        var (ledger, client, factory, s) = await EveryShapeAsync();
        await using var _ = factory;
        using var __ = client;
        await using var db = Db.NewDbContext();
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledger.LedgerId && a.HoldingsAccountId != null)
            .ToDictionaryAsync(a => a.Id, a => a.HoldingsAccountId!.Value);
        var failures = new List<string>();
        var compared = new Dictionary<string, int>(StringComparer.Ordinal);

        // The register: before the rule a row carried the view's memo (the leg's, else
        // the transaction's) and the transaction's tags, a split's line its own memo
        // and tags beside them. Where the rule shows the same source, it shows the same
        // values: an event's own side, a split's own side, and an event's other rows
        // whose other leg is on the investment side.
        foreach (var account in s.Accounts)
        {
            var collapsed = account.AccountType == "investment";
            foreach (var row in RowsOf(await PageAsync(client, ledger, account.Id)))
            {
                // What the view's own columns held before the rule (dropped in 266),
                // from the stored inputs as the view built them: the leg's memo, else
                // the transaction's; the transaction's tags by name; the raw leg and
                // transaction memos.
                var raw = await (
                    from l in db.TxnLegs.AsNoTracking()
                    join h in db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                    join rt in db.ResolvedTransactions.AsNoTracking() on l.Id equals rt.Id
                    where l.Id == row.Id
                    select new { l.LegMemo, HeaderMemo = h.Memo, l.HeaderId, rt.CounterpartyAccountId })
                    .SingleAsync();
                var headerTags = await db.TxnHeaderTags.AsNoTracking().Where(ht => ht.HeaderId == raw.HeaderId)
                    .Join(db.Tags.AsNoTracking(), ht => ht.TagId, t => t.Id, (_, t) => t.Name)
                    .OrderBy(n => n).ToListAsync();
                var before = new
                {
                    Memo = raw.LegMemo ?? raw.HeaderMemo,
                    Tags = headerTags,
                    raw.LegMemo,
                    raw.HeaderMemo,
                    raw.CounterpartyAccountId,
                };
                var ownTags = await db.TxnLegTags.AsNoTracking().Where(lt => lt.LegId == row.Id)
                    .Join(db.Tags.AsNoTracking(), lt => lt.TagId, t => t.Id, (_, t) => t.Name).ToListAsync();
                var originating = await db.TxnHeaders.AsNoTracking().Where(h => h.Id == row.HeaderId)
                    .Select(h => h.OriginatingAccountId).SingleAsync();
                var where = $"{account.Name} {row.Payee} ({row.NotesKind})";
                void Same(string field, object? was, object? now)
                {
                    if (!Equals(was, now)) failures.Add($"{where} {field}: '{now}', before '{was}'");
                }

                string? kind = null;
                switch (row.NotesKind)
                {
                    case "eventSide":
                        kind = "event side";
                        Same("memo", before.Memo, row.Memo);
                        Same("tags", string.Join(",", before.Tags), string.Join(",", row.Tags));
                        break;
                    case "splitSide" when collapsed:
                        // The one row stands for the transaction: its tags, and its memo
                        // where the first line has none of its own.
                        kind = "split side, one row";
                        Same("tags", string.Join(",", before.Tags), string.Join(",", row.Tags));
                        if (before.LegMemo is null) Same("memo", before.Memo, row.Memo);
                        break;
                    case "splitSide":
                        kind = "split side, a line";
                        Same("headerMemo", before.HeaderMemo, row.HeaderMemo);
                        Same("headerTags", string.Join(",", before.Tags), row.HeaderTags is null ? null : string.Join(",", row.HeaderTags));
                        Same("memo", before.LegMemo, row.Memo);
                        Same("tags", Join(ownTags), Join(row.Tags));
                        break;
                    case "eventOpposite" when originating is { } brokerage
                                              && (before.CounterpartyAccountId == brokerage
                                                  || (holdingsOf.TryGetValue(brokerage, out var holdings)
                                                      && before.CounterpartyAccountId == holdings)):
                        kind = "event's other row";
                        Same("memo", before.Memo, row.Memo);
                        if (ownTags.Count == 0) Same("tags", string.Join(",", before.Tags), string.Join(",", row.Tags));
                        break;
                }
                if (kind is not null) compared[kind] = compared.GetValueOrDefault(kind) + 1;
            }
        }
        Assert.Equal(["event side", "event's other row", "split side, a line", "split side, one row"],
            compared.Keys.Order(StringComparer.Ordinal));

        // The reports: a tag cell counted a category posting under its transaction's
        // tags and either leg's own; the tag filter kept every posting of a transaction
        // carrying the tag anywhere.
        var (cellsBefore, filterBefore) = await PrePortReportsAsync(db, ledger.LedgerId);
        var cells = (await SummaryAsync(client, ledger, "&groupBy=Tag")).Rows
            .Where(r => r.GroupName != "(untagged)")
            .ToDictionary(r => r.GroupName, r => r.Amount, StringComparer.Ordinal);
        foreach (var tag in cellsBefore.Keys.Union(cells.Keys, StringComparer.Ordinal))
        {
            var (was, now) = (cellsBefore.GetValueOrDefault(tag), cells.GetValueOrDefault(tag));
            if (ChangedCells.ContainsKey(tag) == (was == now))
                failures.Add(ChangedCells.TryGetValue(tag, out var why)
                    ? $"cell {tag}: listed as changed ({why}) but counts {now} as before"
                    : $"cell {tag}: {now}, before {was}");
        }
        var tagIds = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.Ordinal);
        foreach (var (tag, was) in filterBefore)
        {
            var now = (await SummaryAsync(client, ledger, $"&tagId={tagIds[tag]}")).Total;
            if (ChangedFilters.ContainsKey(tag) == (was == now))
                failures.Add(ChangedFilters.TryGetValue(tag, out var why)
                    ? $"filter {tag}: listed as changed ({why}) but keeps {now} as before"
                    : $"filter {tag}: {now}, before {was}");
        }
        Assert.Contains("t-one-entered", cellsBefore.Keys);
        Assert.Contains("t-split", cellsBefore.Keys);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
    }

    /// <summary>
    /// The reports' tag cells and tag filter as they read before the memo-and-tags rule
    /// (Net measure, no window), kept as written then: the oracle for what the rule
    /// leaves unchanged.
    /// </summary>
    private static async Task<(Dictionary<string, decimal> Cells, Dictionary<string, decimal> Filtered)> PrePortReportsAsync(
        AppDbContext db, Guid ledgerId)
    {
        var kinds = new[] { "income", "expense" };
        var q =
            from r in db.ResolvedTransactions.AsNoTracking()
            join a in db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == ledgerId
                  && a.AccountType == "category"
                  && a.CategoryKind != null
                  && kinds.Contains(a.CategoryKind)
                  && !r.IsHidden
                  && r.IsMergedInto == null
            select new { r, a };

        var pairs = q
            .Join(db.TxnHeaderTags, x => x.r.HeaderId, ht => ht.HeaderId,
                  (x, ht) => new { LegId = x.r.Id, ht.TagId })
            .Union(q.Join(
                from lt in db.TxnLegTags
                join l in db.TxnLegs.AsNoTracking() on lt.LegId equals l.Id
                select new { l.HeaderId, l.PostingIndex, lt.TagId },
                x => new { x.r.HeaderId, PostingIndex = x.r.LegIndex },
                y => new { y.HeaderId, y.PostingIndex },
                (x, y) => new { LegId = x.r.Id, y.TagId }));
        var cells = await (
            from p in pairs
            join x in q on p.LegId equals x.r.Id
            join t in db.Tags.AsNoTracking() on p.TagId equals t.Id
            group x.r.Amount by t.Name into g
            select new { Name = g.Key, Sum = g.Sum() })
            .ToListAsync();

        var filtered = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var tag in await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledgerId).ToListAsync())
        {
            var sum = await q
                .Where(x =>
                    db.TxnHeaderTags.Any(t => t.HeaderId == x.r.HeaderId && t.TagId == tag.Id)
                    || db.TxnLegs.Any(l => l.HeaderId == x.r.HeaderId
                                           && db.TxnLegTags.Any(lt => lt.LegId == l.Id && lt.TagId == tag.Id)))
                .SumAsync(x => x.r.Amount);
            filtered[tag.Name] = -sum;
        }
        // Net: the stored amount negated.
        return (cells.ToDictionary(c => c.Name, c => -c.Sum, StringComparer.Ordinal), filtered);
    }

    // =====================================================================
    // The ledger of every shape
    // =====================================================================

    private sealed record EveryShape(
        AccountRow BankA, AccountRow BankB, AccountRow CatA, AccountRow CatB, AccountRow Fee, AccountRow Income,
        AccountRow BrokerA, AccountRow BrokerB, IReadOnlyList<AccountRow> Accounts);

    private async Task<(SyntheticLedger Ledger, HttpClient Client, ApiFactory Factory, EveryShape Shape)> EveryShapeAsync(bool notes = true)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var fee = await ledger.AddCategoryAsync("Category D");
        var income = await ledger.AddCategoryAsync("Category E", kind: "income");
        var brokerA = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var brokerB = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = await AuthedClientAsync(factory, ledger);

        Task N(Guid header, Guid side, string? memo, string[]? tags, Guid originating, params LineIntent<string>[] lines) =>
            notes ? Notes(ledger, header, side, memo, tags, originating, lines) : Task.CompletedTask;
        static LineIntent<string> Line(int index, string? memo, string[]? tags) => new(index,
            memo is null ? FieldEdit<string>.Leave : FieldEdit<string>.Set(memo),
            tags is null ? FieldEdit<IReadOnlyList<string>>.Leave : FieldEdit<IReadOnlyList<string>>.Set(tags));

        var day = 0;
        DateTime Next() => Day(3, 1).AddHours(++day * 5);
        decimal amount = 0;
        decimal Amount() => -(++amount);   // every line its own amount, so a listing line names one leg

        // One-splits: nothing, the entered side, the other side, one each, both.
        await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee One", (catA.Id, Amount()));
        var o1 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee One", (catA.Id, Amount()));
        await N(o1, bankA.Id, "one entered", ["t-one-entered"], bankA.Id);
        var o2 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee One", (catA.Id, Amount()));
        await N(o2, catA.Id, "one category", ["t-one-category"], bankA.Id);
        var o3 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee One", (catB.Id, Amount()));
        await N(o3, bankA.Id, "one memo", null, bankA.Id);
        await N(o3, catB.Id, null, ["t-one-tags"], bankA.Id);
        var o4 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee One", (catB.Id, Amount()));
        await N(o4, bankA.Id, "one both a", ["t-both-a"], bankA.Id);
        await N(o4, catB.Id, "one both b", ["t-both-b"], bankA.Id);
        // Transfers between two real accounts, entered from either side.
        var x1 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Move", (bankB.Id, Amount()));
        await N(x1, bankA.Id, "move entered", ["t-move"], bankA.Id);
        var x2 = await CreateBankAsync(client, ledger, bankB.Id, Next(), "Payee Move", (bankA.Id, Amount()));
        await N(x2, bankA.Id, "move other", null, bankB.Id);
        // Both sides of a transfer with values of their own.
        var x3 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Move", (bankB.Id, Amount()));
        await N(x3, bankA.Id, "move out", ["t-move-out"], bankA.Id);
        await N(x3, bankB.Id, "move in", ["t-move-in"], bankA.Id);

        // Multi-splits: nothing, the transaction only, the transaction and a line,
        // a line only; the symmetric shape; real accounts on the opposite side.
        await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Split", (catA.Id, Amount()), (catB.Id, Amount()));
        var m1 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Split", (catA.Id, Amount()), (catB.Id, Amount()));
        await N(m1, bankA.Id, "split whole", ["t-split"], bankA.Id);
        var m2 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Split", (catA.Id, Amount()), (catB.Id, Amount()));
        await N(m2, bankA.Id, "split both", ["t-split-both"], bankA.Id, Line(0, "split line", ["t-split-line", "t-split-both"]));
        var m3 = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Split", (catA.Id, Amount()), (catB.Id, Amount()));
        await N(m3, bankA.Id, null, null, bankA.Id, Line(1, "line only", ["t-line-only"]));
        var sym = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Symmetric", (catA.Id, Amount()), (catA.Id, Amount()));
        await N(sym, bankA.Id, "symmetric whole", ["t-sym"], bankA.Id, Line(1, "symmetric line", ["t-sym-line"]));
        var real = await CreateBankAsync(client, ledger, bankA.Id, Next(), "Payee Real Split",
            (bankB.Id, Amount()), (bankB.Id, Amount()), (catA.Id, Amount()));
        await N(real, bankA.Id, "real whole", ["t-real"], bankA.Id, Line(0, "real line", null));
        // A split entered from a category: both real accounts are its opposite side.
        var owned = await ImportFromCategoryAsync(ledger, catA.Id, Next(), "Payee Owned",
            (bankA.Id, -Amount()), (bankB.Id, -Amount()));
        await N(owned, catA.Id, "owned whole", ["t-owned"], catA.Id, Line(0, "owned line", ["t-owned-line"]));
        // A posting between two categories, with each side's own value.
        var between = await ImportFromCategoryAsync(ledger, catA.Id, Next(), "Payee Between", (catB.Id, -Amount()));
        await N(between, catA.Id, "between a", ["t-between-a"], catA.Id);
        await N(between, catB.Id, null, ["t-between-b"], catA.Id);

        // Imported into a brokerage with no action: the brokerage is the split side.
        var imported = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(imported, ledger.LedgerId, brokerA.Id,
                origin: "file_import", externalId: $"imp-{imported:N}");
            header.ProviderKey = "qif";
            header.Payee = "Payee Imported";
            header.PostedAt = Next();
            header.TransactedAt = header.PostedAt;
            db.TxnHeaders.Add(header);
            var a1 = Amount();
            var a2 = Amount();
            db.TxnLegs.AddRange(
                Leg(imported, ledger.LedgerId, brokerA.Id, a1, 0), Leg(imported, ledger.LedgerId, catA.Id, -a1, 0),
                Leg(imported, ledger.LedgerId, brokerA.Id, a2, 1), Leg(imported, ledger.LedgerId, catB.Id, -a2, 1));
            await db.SaveChangesAsync();
        }
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeNotesAsync(imported);
        await N(imported, brokerA.Id, "imported whole", ["t-imported"], brokerA.Id, Line(1, "imported line", ["t-imported-line"]));

        // Events: one posting (buy, sell, dividend, Xfr), several (buy with a fee,
        // a buy funded from another brokerage), and an in-kind transfer.
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Buy, SecurityId = security, Shares = 10m, Price = 20m,
            PostedAt = Next(), Payee = "Payee Buy",
        });
        await N(buy, brokerA.Id, "buy note", ["t-buy"], brokerA.Id);
        var sell = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Sell, SecurityId = security, Shares = 1m, Price = 25m,
            PostedAt = Next(), Payee = "Payee Sell",
        });
        await N(sell, brokerA.Id, "sell note", null, brokerA.Id);
        var dividend = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.DividendCash, SecurityId = security,
            CategoryAccountId = income.Id, Amount = 12m, PostedAt = Next(), Payee = "Payee Dividend",
        });
        await N(dividend, brokerA.Id, "dividend note", ["t-dividend"], brokerA.Id);
        var xfr = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = bankA.Id,
            Amount = 40m, PostedAt = Next(), Payee = "Payee Xfr",
        });
        await N(xfr, brokerA.Id, "xfr note", ["t-xfr"], brokerA.Id);
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = bankA.Id,
            Amount = 41m, PostedAt = Next(), Payee = "Payee Xfr Bare",
        });
        var buyFee = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Buy, SecurityId = security, Shares = 3m, Price = 21m,
            FeeAccountId = fee.Id, FeeAmount = 3m, PostedAt = Next(), Payee = "Payee Buy Fee",
        });
        // Two names the database's collation orders unlike an ordinal comparison, on a
        // transaction's own tags, so the order a row lists its tags in is pinned against
        // the order the view listed them in.
        await N(buyFee, brokerA.Id, "fee note", ["t-fee", "ab", "a-c"], brokerA.Id);
        var funded = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.BuyXfr, SecurityId = security, Shares = 2m, Price = 22m,
            TransferAccountId = brokerB.Id, FeeAccountId = fee.Id, FeeAmount = 2m, PostedAt = Next(), Payee = "Payee Funded",
        });
        await N(funded, brokerA.Id, "funded note", ["t-funded"], brokerA.Id);
        var inKind = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.TransferShares, SecurityId = security, Shares = 4m,
            TransferAccountId = brokerB.Id, PostedAt = Next(), Payee = "Payee In Kind",
        });
        await N(inKind, brokerA.Id, "in kind note", ["t-in-kind"], brokerA.Id);

        await using (var db = Db.NewDbContext())
        {
            var holdings = new[] { brokerA.HoldingsAccountId!.Value, brokerB.HoldingsAccountId!.Value };
            var accounts = await db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledger.LedgerId && db.TxnLegs.Any(l => l.AccountId == a.Id))
                .ToListAsync();
            Assert.Contains(accounts, a => holdings.Contains(a.Id));
            return (ledger, client, factory,
                new EveryShape(bankA, bankB, catA, catB, fee, income, brokerA, brokerB, accounts));
        }
    }

    // =====================================================================
    // Oracles
    // =====================================================================

    private sealed record StoredLeg(Guid Id, Guid HeaderId, int PostingIndex, Guid AccountId, string AccountType,
        decimal Amount, Guid? OtherId, Guid? OtherAccountId, bool OtherIsReal);

    private async Task<List<StoredLeg>> LegsAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        var legs = await (
            from l in db.TxnLegs.AsNoTracking()
            join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            where l.LedgerId == ledger.LedgerId
            select new { l.Id, l.HeaderId, l.PostingIndex, l.AccountId, a.AccountType, l.Amount })
            .ToListAsync();
        return [.. legs.Select(l =>
        {
            var other = legs.FirstOrDefault(o => o.HeaderId == l.HeaderId && o.PostingIndex == l.PostingIndex && o.Id != l.Id);
            return new StoredLeg(l.Id, l.HeaderId, l.PostingIndex, l.AccountId, l.AccountType, l.Amount,
                other?.Id, other?.AccountId, other is not null && other.AccountType != "category");
        })];
    }

    private sealed record ReportExpectation(IReadOnlyDictionary<string, decimal> ByTag, decimal Tagged, decimal Untagged);

    /// <summary>
    /// Each category posting's tags as the real account it pairs with shows them (the
    /// category leg itself when it pairs with another category), in memory. Net
    /// measure: every category, the amount negated.
    /// </summary>
    private async Task<ReportExpectation> ReportOracleAsync(SyntheticLedger ledger)
    {
        var byTag = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal tagged = 0, untagged = 0;
        foreach (var leg in (await LegsAsync(ledger)).Where(l => l.AccountType == "category"))
        {
            var seen = leg.OtherIsReal ? leg.OtherId!.Value : leg.Id;
            var tags = (await ledger.PresentAsync(seen)).InPlayTags.Distinct(StringComparer.Ordinal).ToList();
            var value = -leg.Amount;
            if (tags.Count == 0) untagged += value;
            else tagged += value;
            foreach (var tag in tags) byTag[tag] = byTag.GetValueOrDefault(tag) + value;
        }
        return new ReportExpectation(byTag, tagged, untagged);
    }

    /// <summary>The fields the rule fills on a register row, against the in-memory rule.</summary>
    private static string? Diff(ShownNotes<string> expected, RegisterRowDto row, bool collapsed)
    {
        var p = expected.Presentation;
        // An investment register shows one row per entry: an action-less split's row
        // on its split side is the transaction's, as the bank register's split parent is.
        var memo = collapsed && p.Kind == LegKind.SplitSide ? p.InPlayHeaderMemo : expected.Memo;
        var tags = collapsed && p.Kind == LegKind.SplitSide ? expected.HeaderTags ?? [] : expected.Tags;
        var problems = new List<string>();
        void Check(string field, object? want, object? got)
        {
            if (!Equals(want, got)) problems.Add($"{field} '{got}', expected '{want}'");
        }
        Check("notesKind", NotesKinds.Of(p.Kind), row.NotesKind);
        Check("isReadOnly", p.IsReadOnly, row.IsReadOnly);
        Check("memo", memo, row.Memo);
        Check("tags", Join(tags), Join(row.Tags));
        Check("memoInherited", p.MemoInherited, row.MemoInherited);
        Check("tagsInherited", p.TagsInherited, row.TagsInherited);
        Check("inheritedMemo", p.InheritedMemo, row.InheritedMemo);
        Check("inheritedTags", Join(expected.InheritedTags), Join(row.InheritedTags));
        Check("inheritFromAccountId", p.InheritFromAccountId, row.InheritFromAccountId);
        Check("headerMemo", p.InPlayHeaderMemo, row.HeaderMemo);
        Check("headerTags", expected.HeaderTags is null ? null : Join(expected.HeaderTags),
            row.HeaderTags is null ? null : Join(row.HeaderTags));
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>The ledger's tag names in the order the database sorts names in.</summary>
    private sealed class Collation(IReadOnlyList<string> names)
    {
        /// <summary><paramref name="tags"/>, each once, in the database's order.</summary>
        public List<string> Order(IEnumerable<string> tags) =>
            [.. tags.Distinct(StringComparer.Ordinal).OrderBy(t =>
            {
                var at = names.ToList().IndexOf(t);
                return at >= 0 ? at : throw new InvalidOperationException($"no tag named '{t}' in the ledger");
            })];
    }

    private static async Task<Collation> CollationOrderAsync(AppDbContext db, SyntheticLedger ledger) =>
        new(await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId)
            .OrderBy(t => t.Name).Select(t => t.Name).ToListAsync());

    private static string Join(IEnumerable<string> tags) => string.Join(",", Sorted(tags));

    private static List<string> Sorted(IEnumerable<string> tags) =>
        [.. tags.Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ThenBy(t => t, StringComparer.Ordinal)];

    // =====================================================================
    // Helpers
    // =====================================================================

    private static Task Notes(SyntheticLedger ledger, Guid header, Guid side, string? memo, string[]? tags, Guid originating,
        params LineIntent<string>[] lines) =>
        ledger.WriteNotesAsync(header, new AnnotationIntent<string>(side,
            memo is null ? FieldEdit<string>.Leave : FieldEdit<string>.Set(memo),
            tags is null ? FieldEdit<IReadOnlyList<string>>.Leave : FieldEdit<IReadOnlyList<string>>.Set(tags),
            lines.Length == 0 ? null : lines), originating);

    /// <summary>
    /// A one-split whose category side had its own tag, split from the bank into two
    /// lines through the write primitive, then the tag cleared from the transaction and
    /// from the first line on the split side: only the category leg's override is left.
    /// </summary>
    /// <remarks>
    /// The split itself copies what the bank side showed (the inherited tag) onto the
    /// transaction and the first line's bank leg, so straight after it the tag still
    /// counts for both lines' money: a reshape never changes what a report counts.
    /// Clearing those two is what leaves a split line with an override no report counts.
    /// </remarks>
    private async Task<(Guid Header, decimal Total)> SplitKeepingCategoryOverrideAsync(
        HttpClient client, SyntheticLedger ledger, Guid bank, Guid category, Guid other, string tag)
    {
        var header = await CreateBankAsync(client, ledger, bank, Day(3, 9), "Payee Kept", (category, -11m));
        await Notes(ledger, header, category, null, [tag], bank);
        await ledger.ReshapeAsync(header, bank, async () =>
        {
            await using var db = Db.NewDbContext();
            db.TxnLegs.AddRange(Leg(header, ledger.LedgerId, bank, -13m, 1), Leg(header, ledger.LedgerId, other, 13m, 1));
            await db.SaveChangesAsync();
            await ledger.RecomputePostingCountsAsync();
            await ledger.RecomputeNotesAsync(header);
            await ledger.RecomputeBalancesAsync([bank, category, other]);
        });
        var split = await ledger.LoadNotesAsync(header);
        Assert.Equal([tag], split.Header.Tags);
        Assert.Equal([tag], split.For(new LegKey(0, bank)).Tags);
        Assert.Equal([tag], split.For(new LegKey(0, category)).Tags);
        const decimal total = 24m;
        Assert.Equal(total, (await SummaryAsync(client, ledger, "&groupBy=Tag", measure: "Spending")).Rows
            .Single(r => r.GroupName == tag).Amount);

        await Notes(ledger, header, bank, null, [], bank,
            new LineIntent<string>(0, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set([])));
        var cleared = await ledger.LoadNotesAsync(header);
        Assert.Empty(cleared.Header.Tags);
        Assert.Empty(cleared.For(new LegKey(0, bank)).Tags);
        Assert.Equal([tag], cleared.For(new LegKey(0, category)).Tags);
        return (header, total);
    }

    /// <summary>
    /// A transaction entered from a category, as a Moneydance import brings one in (the
    /// API takes no category as the account a transaction is entered from), owned by
    /// <paramref name="home"/>: each posting's amount on it, the opposite on the
    /// posting's counterparty.
    /// </summary>
    private async Task<Guid> ImportFromCategoryAsync(
        SyntheticLedger ledger, Guid home, DateTime postedAt, string payee, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var id = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(id, ledger.LedgerId, home,
                origin: "file_import", externalId: $"imp-{id:N}");
            header.ProviderKey = "qif";
            header.Payee = payee;
            header.PostedAt = postedAt;
            header.TransactedAt = postedAt;
            db.TxnHeaders.Add(header);
            for (var i = 0; i < postings.Length; i++)
                db.TxnLegs.AddRange(
                    Leg(id, ledger.LedgerId, home, postings[i].Amount, i),
                    Leg(id, ledger.LedgerId, postings[i].Counterparty, -postings[i].Amount, i));
            await db.SaveChangesAsync();
        }
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeBalancesAsync([home, .. postings.Select(p => p.Counterparty).Distinct()]);
        await ledger.RecomputeNotesAsync(id);
        return id;
    }

    private static TxnLegRow Leg(Guid headerId, Guid ledgerId, Guid accountId, decimal amount, int posting) => new()
    {
        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId,
        AccountId = accountId, PostingIndex = posting, Amount = amount,
    };

    private static IEnumerable<RegisterRowDto> RowsOf(RegisterPage page) =>
        page.Entries.SelectMany(e => e.Txn is { } txn ? [txn] : e.Legs ?? []);

    private async Task<List<Guid>> LegIdsAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .OrderBy(l => l.PostingIndex).Select(l => l.Id).ToListAsync();
    }

    private static async Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceAccountId, DateTime postedAt, string payee,
        params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                SourceAccountId = sourceAccountId,
                Postings = postings.Select(p => new TransactionPosting
                {
                    CounterpartyAccountId = p.Counterparty, Amount = p.Amount,
                }).ToArray(),
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<RegisterPage> PageAsync(HttpClient client, SyntheticLedger ledger, Guid accountId, string query = "") =>
        await GetJsonAsync<RegisterPage>(client, $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=500{query}");

    private static Task<ReportResult> SummaryAsync(HttpClient client, SyntheticLedger ledger, string query, string measure = "Net") =>
        GetJsonAsync<ReportResult>(client, $"/api/ledgers/{ledger.LedgerId}/reports/summary?measure={measure}&bucket=None{query}");

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.True(resp.IsSuccessStatusCode, $"{url}: {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }
}
