using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The register's entry keys and filter (<see cref="RegisterQuery"/>), in LINQ over the
/// stored memo-and-tags presentation, against the rule run in memory over the loaded
/// rows (<see cref="NotesOracle"/>).
/// </summary>
/// <remarks>
/// <para><b>The matrix.</b> Over a ledger of every shape, every register, filter and sort
/// returns exactly the entries the oracle keeps, with each entry's date and sequence;
/// in the order the oracle sorts them where the order is a number or a date (a text
/// sort follows the database's collation, so only its entries are compared); and the
/// same list page by page in both directions. A page anchored on a header starts where
/// that list has it. The ledger holds only states a writer produces: transactions
/// through the API, notes through the side-aware seeds (the write primitive's
/// placement).</para>
/// <para><b>The shapes.</b> A multi-split's transaction-level memo and tags never match
/// its opposite side; inherited values match; the symmetric shape's other account shows
/// one entry per line; an event's investment side (its holdings account, both brokerages
/// of an in-kind transfer) groups by header; an anchor on an opposite side starts at the
/// header's first line.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RegisterEntryKeyTests : DatabaseTest
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private readonly ITestOutputHelper _output;

    public RegisterEntryKeyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] SortColumns =
        ["date", "amount", "payee", "category", "security", "shares", "price", "action"];

    private static IEnumerable<RegisterSort> AllSorts() =>
        SortColumns.SelectMany(c => new[] { new RegisterSort(c, Descending: true), new RegisterSort(c, Descending: false) });

    // =====================================================================
    // One statement with a row-value keyset
    // =====================================================================

    [Fact]
    public async Task The_keyset_is_one_row_value_comparison_over_the_stored_presentation()
    {
        var u = await BuildMatrixLedgerAsync();
        await using var db = Db.NewDbContext();

        // EF writes the SQL it would run into the command log; capture the page's
        // statement through ToQueryString on the same building blocks.
        var filtered = RegisterQuery.Filtered(db, [u.BankA], u.Ledger.LedgerId, hidden: false,
            new RegisterFilter(Tag: "tag-h", Search: "Payee"), Today);
        var sql = filtered.Select(l => new { l.EntryKey, l.Shown.Memo }).ToQueryString();
        _output.WriteLine(sql);
        // The stored presentation composes into SQL, not into client evaluation.
        Assert.Contains("txn_leg_note_tags", sql, StringComparison.Ordinal);
        Assert.Contains("ILIKE", sql, StringComparison.Ordinal);

        // A calendar date is posted_at's UTC date, and every API connection pins the
        // session to UTC (DbSessionTimeZone), so the database's date is the same.
        var dated = RegisterQuery.Filtered(db, [u.BankA], u.Ledger.LedgerId, hidden: false,
                new RegisterFilter(DateFrom: new DateOnly(2026, 3, 1), Status: "scheduled"), Today)
            .Select(l => l.Row.Id).ToQueryString();
        _output.WriteLine(dated);
        Assert.Contains("CAST(r.posted_at AT TIME ZONE 'UTC' AS date) >= @from", dated, StringComparison.Ordinal);
        Assert.Equal("UTC", Coffer.Api.Configuration.DbSessionTimeZone.Zone);

        // A keyset page past a real cursor runs, and its statement compares row values.
        var byPayee = new RegisterSort("payee", true);
        var first = await RegisterQuery.EntryKeysAsync(db, [u.BankA], u.Ledger.LedgerId, null, null,
            RegisterRepository.DirectionBefore, 2, false, RegisterFilter.None, Today, byPayee, default);
        Assert.Equal(2, first.Count);
        var capture = new CommandCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(capture)
            .Options;
        await using (var logged = new AppDbContext(options))
        {
            var next = await RegisterQuery.EntryKeysAsync(logged, [u.BankA], u.Ledger.LedgerId, first[^1].EntryKey, first[^1].Seq,
                RegisterRepository.DirectionBefore, 2, false, RegisterFilter.None, Today, byPayee, default);
            Assert.Equal(2, next.Count);
        }
        var page = Assert.Single(capture.Commands, c => c.Contains("GROUP BY", StringComparison.Ordinal));
        _output.WriteLine(page);
        // (sort value, seq, entry key) < (cursor's sort value, cursor seq, cursor key),
        // the cursor's value read from its entry's legs when its key still names one
        Assert.Matches(new Regex(@"\)\s*<\s*\(\s*CASE\s+WHEN\s+EXISTS\s*\(", RegexOptions.Singleline), page);
    }

    [Fact]
    public async Task The_stored_notes_read_as_the_rule_computes_them_over_the_matrix_ledger()
    {
        var u = await BuildMatrixLedgerAsync();
        await using var db = Db.NewDbContext();
        await StoredNotesEquivalence.AssertEquivalentAsync(db, u.Ledger.LedgerId);
        await StoredNotesEquivalence.AssertFiltersEquivalentAsync(db, u.Ledger.LedgerId, StoredNotesEquivalence.FilterSample.All);
        Assert.Empty(await LegNotes.DifferencesAsync(db, u.Ledger.LedgerId, CancellationToken.None));
    }

    [Fact]
    public async Task A_register_call_can_print_the_plans_of_the_statements_it_runs()
    {
        // The timing gate's diagnostic: plans taken on the API's own connection.
        var u = await BuildMatrixLedgerAsync();
        var explain = new ExplainCapture { Selects = sql => sql.Contains("resolved_transactions", StringComparison.Ordinal) };
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithServices(explain.Register);
        using var client = await AuthedClientAsync(factory, u.Ledger);

        await PageAsync(client, u.Ledger, u.BankA, "&tag=tag-h");
        Assert.Empty(explain.Drain());

        explain.Enabled = true;
        var page = await PageAsync(client, u.Ledger, u.BankA, "&tag=tag-h");
        var plans = explain.Drain();
        Assert.NotEmpty(page.Entries);
        // The entry keys and the rows, each with what it read and how long it took. The
        // rows carry their shown tag names, aggregated in the same statement.
        var keys = Assert.Single(plans, p => p.Sql.Contains("GROUP BY", StringComparison.Ordinal)
                                             && !p.Sql.Contains("array_agg", StringComparison.Ordinal));
        Assert.Contains("Execution Time", keys.Plan, StringComparison.Ordinal);
        Assert.Contains("Buffers", keys.Plan, StringComparison.Ordinal);
        Assert.Single(plans, p => p.Sql.Contains("array_agg", StringComparison.Ordinal));
        Assert.True(plans.Count >= 2, $"{plans.Count} plans");

        // An unfiltered rail counts entries, so it reads each leg's stored entry grouping
        // from the leg row itself: the view's own header read, and no other table.
        var rail = await client.GetAsync($"/api/ledgers/{u.Ledger.LedgerId}/transactions/index-buckets?account_id={u.BankA}");
        Assert.True(rail.IsSuccessStatusCode, await rail.Content.ReadAsStringAsync());
        var railPlan = Assert.Single(explain.Drain()).Plan;
        Assert.Single(Regex.Matches(railPlan, @" on txn_headers\b"));
        Assert.Contains("notes_groups_by_header", railPlan, StringComparison.Ordinal);
        Assert.DoesNotContain("txn_leg_note_tags", railPlan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_cannot_run_the_statement_still_prints_the_plan_and_the_call_answers()
    {
        // A statement that times out under ANALYZE is the one whose plan matters most:
        // the capture falls back to the plan alone, and the API's own statement runs.
        var u = await BuildMatrixLedgerAsync();
        var explain = new ExplainCapture
        {
            Selects = sql => sql.Contains("resolved_transactions", StringComparison.Ordinal),
            AnalyzeOptions = "ANALYZE, BUFFERS, NOT_AN_OPTION",
            Enabled = true,
        };
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithServices(explain.Register);
        using var client = await AuthedClientAsync(factory, u.Ledger);

        var page = await PageAsync(client, u.Ledger, u.BankA, "&tag=tag-h");
        Assert.NotEmpty(page.Entries);
        var plans = explain.Drain();
        Assert.True(plans.Count >= 2, $"{plans.Count} plans");
        Assert.All(plans, p =>
        {
            // Why the first plan failed, then the plan alone: estimates, nothing run.
            Assert.Contains("not_an_option", p.Plan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cost=", p.Plan, StringComparison.Ordinal);
            Assert.DoesNotContain("Execution Time", p.Plan, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_plan_that_fails_inside_a_transaction_leaves_the_transaction_usable()
    {
        // Inside a transaction a failed statement aborts it; the capture rolls the failed
        // plan back to its savepoint, so the statement it explained and every later one
        // in the same transaction still run, and the transaction commits.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -5m, Day(3, 1), "Payee A");
        var explain = new ExplainCapture { AnalyzeOptions = "ANALYZE, BUFFERS, NOT_AN_OPTION", Enabled = true };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(explain)
            .Options;
        await using var db = new AppDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.LedgerId == ledger.LedgerId).Select(a => a.Id).ToListAsync();
        Assert.Contains(bank.Id, accounts);
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.AccountId == bank.Id).CountAsync();
        Assert.True(legs > 0);
        await transaction.CommitAsync();

        var plans = explain.Drain();
        Assert.True(plans.Count >= 2, $"{plans.Count} plans");
        Assert.All(plans, p =>
        {
            Assert.Contains("not_an_option", p.Plan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cost=", p.Plan, StringComparison.Ordinal);
        });
    }

    private sealed class CommandCapture : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // =====================================================================
    // The matrix, against the rule run over the loaded rows
    // =====================================================================

    [Fact]
    public async Task Every_register_filter_and_sort_selects_and_pages_the_entries_the_rule_shows()
    {
        var u = await BuildMatrixLedgerAsync();
        await using var db = Db.NewDbContext();
        var oracle = await NotesOracle.LoadAsync(db, u.Ledger.LedgerId);

        var compared = 0;
        var nonEmpty = 0;
        var ordered = 0;
        var walked = 0;
        var failures = new List<string>();
        foreach (var (register, scope) in u.Registers)
        foreach (var (filterName, filter, hidden) in u.Filters)
        foreach (var sort in AllSorts())
        {
            compared++;
            var cell = $"{register} / {filterName} / {sort.Column} {sort.Dir}";
            var expected = Expected(oracle, scope, hidden, filter, sort);
            var keys = await KeysAsync(db, u.Ledger.LedgerId, scope, null, null, RegisterRepository.DirectionBefore, 1000, hidden, filter, sort);

            // The same entries, each at its header's date and sequence.
            var got = keys.Select(k => $"{k.EntryKey}@{k.PostedAt:O}#{k.Seq}").Order(StringComparer.Ordinal).ToList();
            var want = expected.Select(e => $"{e.Key}@{e.PostedAt:O}#{e.Seq}").Order(StringComparer.Ordinal).ToList();
            if (!got.SequenceEqual(want))
            {
                failures.Add($"{cell}: rule [{Show(expected)}] register [{Show(keys)}]");
                continue;
            }
            if (keys.Count > 0) nonEmpty++;

            // In the rule's order where the order is a number or a date; a text sort
            // follows the database's collation, which the oracle does not reproduce.
            if (expected.Ordered)
            {
                ordered++;
                if (!keys.Select(k => k.EntryKey).SequenceEqual(expected.Select(e => e.Key)))
                {
                    failures.Add($"{cell}: in the rule's order [{Show(expected)}], the register's [{Show(keys)}]");
                    continue;
                }
            }

            // Page by page, down and back up: the cursor's sort value is read back from
            // its key, so every sort walks its own way, and the pages are the list.
            if (keys.Count > 2 && (filterName == "none" || sort.Column is "date" or "amount"))
            {
                walked++;
                await WalkAsync(db, u.Ledger.LedgerId, scope, hidden, filter, sort, keys, cell, failures);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(25)));
        // The matrix must not have lost its registers, filters or data.
        Assert.True(compared > 4_000, $"only {compared} cells compared");
        Assert.True(nonEmpty > 2_000, $"only {nonEmpty} non-empty cells compared");
        Assert.True(ordered > 2_000, $"only {ordered} cells compared in order");
        Assert.True(walked > 250, $"only {walked} cells walked page by page");
    }

    private static async Task WalkAsync(
        AppDbContext db, Guid ledgerId, Guid[] scope, bool hidden, RegisterFilter filter, RegisterSort sort,
        List<RegisterEntryKeyRow> all, string cell, List<string> failures)
    {
        const int limit = 2;
        var down = new List<RegisterEntryKeyRow>();
        RegisterEntryKeyRow? cursor = null;
        for (var pageNo = 0; pageNo < 100; pageNo++)
        {
            var page = await KeysAsync(db, ledgerId, scope, cursor?.EntryKey, cursor?.Seq, RegisterRepository.DirectionBefore, limit, hidden, filter, sort);
            if (page.Count == 0) break;
            down.AddRange(page);
            cursor = page[^1];
        }
        if (!down.Select(k => k.EntryKey).SequenceEqual(all.Select(k => k.EntryKey)))
        {
            failures.Add($"{cell}, paged down: [{Show(down)}], the whole list [{Show(all)}]");
            return;
        }

        // Back up from the last entry: the list before it, page by page, in display order.
        var up = new List<RegisterEntryKeyRow>();
        for (var pageNo = 0; pageNo < 100; pageNo++)
        {
            var page = await KeysAsync(db, ledgerId, scope, cursor!.EntryKey, cursor.Seq, RegisterRepository.DirectionAfter, limit, hidden, filter, sort);
            if (page.Count == 0) break;
            up.InsertRange(0, page);
            cursor = page[0];
        }
        if (!up.Select(k => k.EntryKey).SequenceEqual(all.SkipLast(1).Select(k => k.EntryKey)))
            failures.Add($"{cell}, paged up: [{Show(up)}], the list before its last entry [{Show(all.SkipLast(1).ToList())}]");
    }

    /// <summary>One register entry as the oracle keeps it: its key, date, sequence and sort value.</summary>
    private sealed record OracleEntry(Guid Key, DateTime PostedAt, long Seq, Guid HeaderId);

    /// <summary>The oracle's entries of one register cell, and whether their order is the oracle's to state.</summary>
    private sealed class ExpectedEntries(List<OracleEntry> entries, bool ordered) : List<OracleEntry>(entries)
    {
        public bool Ordered { get; } = ordered;
    }

    /// <summary>
    /// The entries a register cell shows, by the rule run over the loaded rows: the legs in
    /// scope on live, unmerged headers on the visibility side asked for, that the filter
    /// keeps (over the presentation the rule computes for each), grouped by the entry key
    /// the rule computes; ordered by the sort's value over an entry's kept legs, then its
    /// sequence, then its key, where that value is a number or a date.
    /// </summary>
    private static ExpectedEntries Expected(NotesOracle oracle, Guid[] scope, bool hidden, RegisterFilter f, RegisterSort sort)
    {
        var today = f.Today ?? Today;
        var groups = oracle.Rows
            .Where(r => scope.Contains(r.AccountId) && r.IsMergedInto == null && r.IsHidden == hidden && Keeps(oracle, r, f, today))
            .GroupBy(oracle.EntryKeyOf)
            .ToList();
        static OracleEntry Entry(IGrouping<Guid, NotesOracle.Row> g) =>
            new(g.Key, g.Max(r => r.PostedAt), g.Max(r => r.HeaderSeq), g.First().HeaderId);

        IComparable? Value(IGrouping<Guid, NotesOracle.Row> g) => sort.Column switch
        {
            "amount" => g.Max(r => ShownAmount(oracle, r)),
            "shares" => g.Max(r => r.Quantity) ?? 0m,
            "price" => g.Max(r => r.UnitPrice) ?? 0m,
            "payee" or "category" or "security" or "action" => null,
            _ => g.Max(r => r.PostedAt),
        };
        var ordered = sort.Column is not ("payee" or "category" or "security" or "action");
        if (!ordered) return new ExpectedEntries([.. groups.Select(Entry)], ordered: false);

        // A uuid sorts as its bytes do, which is its hex text in ordinal order.
        var keyed = groups.Select(g => (Entry: Entry(g), Value: Value(g)!)).ToList();
        var sorted = sort.Descending
            ? keyed.OrderByDescending(x => x.Value).ThenByDescending(x => x.Entry.Seq)
                .ThenByDescending(x => x.Entry.Key.ToString("N"), StringComparer.Ordinal)
            : keyed.OrderBy(x => x.Value).ThenBy(x => x.Entry.Seq)
                .ThenBy(x => x.Entry.Key.ToString("N"), StringComparer.Ordinal);
        return new ExpectedEntries([.. sorted.Select(x => x.Entry)], ordered: true);
    }

    /// <summary>The register filter over one row, reading what the rule computes for its leg.</summary>
    private static bool Keeps(NotesOracle oracle, NotesOracle.Row r, RegisterFilter f, DateOnly today)
    {
        var date = DateOnly.FromDateTime(r.PostedAt);
        var amount = Math.Abs(ShownAmount(oracle, r));
        if (f.DateFrom is { } from && date < from) return false;
        if (f.DateTo is { } to && date > to) return false;
        if (f.AmountMin is { } min && amount < min) return false;
        if (f.AmountMax is { } max && amount > max) return false;
        if (f.SecurityId is { } security && r.SecurityId != security) return false;
        if (f.CategoryId is { } category && r.CounterpartyAccountId != category) return false;

        var notes = oracle.ByLeg[r.LegId].Notes;
        var inPlay = oracle.Names(r.LegId, t => t.InPlay);
        if (f.Tag is { } tag && !inPlay.Contains(tag, StringComparer.Ordinal)) return false;
        if (f.Search is { } search)
        {
            var like = Like("%" + search + "%");
            bool Matches(string? value) => value is not null && like.IsMatch(value);
            if (!(Matches(r.Payee) || Matches(notes.Memo) || Matches(notes.HeaderMemo) || Matches(r.CheckNumber)
                  || Matches(r.CounterpartyAccountName) || inPlay.Any(Matches)))
                return false;
        }
        return f.Status switch
        {
            null => true,
            "needs_review" => r.NeedsReview,
            "scheduled" => date > today,
            "cleared" or "uncleared" or "reconciling" => r.Status == f.Status && !r.IsPending && date <= today,
            _ => false,
        };
    }

    /// <summary>
    /// The amount the row of <paramref name="r"/>'s entry shows, by the grouping the rule
    /// computes: the header's net on the account for an entry keyed by its header, the
    /// leg's own amount for one keyed by its leg.
    /// </summary>
    private static decimal ShownAmount(NotesOracle oracle, NotesOracle.Row r) =>
        oracle.ByLeg[r.LegId].Notes.GroupsByHeader ? r.HeaderAccountNetAmount ?? r.Amount : r.Amount;

    /// <summary>An ILIKE pattern as a regular expression: % any run, _ any character, a backslash escapes the next.</summary>
    private static Regex Like(string pattern)
    {
        var expression = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) expression.Append(Regex.Escape(pattern[++i].ToString()));
            else if (c == '%') expression.Append(".*");
            else if (c == '_') expression.Append('.');
            else expression.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(expression.Append('$').ToString(),
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    [Fact]
    public async Task Every_anchor_starts_where_the_list_has_its_header()
    {
        // A page anchored on a header starts at that header's entry and goes on as the
        // list does; a header the filter excludes falls through to the filtered list's
        // first page. A header with several entries in a register (an opposite side's
        // lines) is asserted on its own.
        var u = await BuildMatrixLedgerAsync();
        await using var db = Db.NewDbContext();
        var oracle = await NotesOracle.LoadAsync(db, u.Ledger.LedgerId);
        var headerOfEntry = oracle.Rows.GroupBy(oracle.EntryKeyOf).ToDictionary(g => g.Key, g => g.First().HeaderId);
        var repository = new RegisterRepository(db, TimeProvider.System);
        const int limit = 3;

        var none = u.Filters.Single(f => f.Name == "none");
        var cells = u.Filters.Select(f => (Filter: f, Sort: RegisterSort.Default))
            .Concat(AllSorts().Where(s => s != RegisterSort.Default).Select(s => (Filter: none, Sort: s)));
        var failures = new List<string>();
        var anchored = 0;
        var fellThrough = 0;
        foreach (var (register, scope) in u.Registers)
        foreach (var ((filterName, filter, hidden), sort) in cells)
        {
            var all = await KeysAsync(db, u.Ledger.LedgerId, scope, null, null, RegisterRepository.DirectionBefore, 1000, hidden, filter, sort);
            var headerOf = all.Select(k => headerOfEntry[k.EntryKey]).ToList();
            var inRegister = oracle.Rows.Where(r => scope.Contains(r.AccountId) && r.IsHidden == hidden && r.IsMergedInto == null)
                .Select(r => r.HeaderId).Distinct().Order().ToList();
            var listed = inRegister.Where(h => headerOf.Count(x => x == h) == 1).ToList();
            var excluded = inRegister.Where(h => !headerOf.Contains(h)).ToList();
            var picks = new[] { listed.FirstOrDefault(), listed.ElementAtOrDefault(listed.Count / 2), listed.LastOrDefault(), excluded.FirstOrDefault() }
                .Where(h => h != Guid.Empty).Distinct();

            foreach (var header in picks)
            {
                var cell = $"{register} / {filterName} / {sort.Column} {sort.Dir} / anchor {header.ToString()[..8]}";
                var page = await repository.GetPageAsync(u.Ledger.LedgerId, scope[0], null, RegisterRepository.DirectionBefore,
                    header, limit, hidden, filter with { IncludeSubcategories = scope.Length > 1 }, sort);
                var at = headerOf.IndexOf(header);
                var start = at < 0 ? 0 : at;
                var expected = all.Skip(start).Take(limit).ToList();
                if (at >= 0) anchored++; else fellThrough++;

                var newest = Decode(page.CursorForNewer);
                var oldest = Decode(page.CursorForOlder);
                var ok = page.Entries.Count == expected.Count
                    && (at < 0
                        ? newest is null && (all.Count > limit ? oldest?.EntryKey == expected[^1].EntryKey : oldest is null)
                        : newest?.EntryKey == expected[0].EntryKey && oldest?.EntryKey == expected[^1].EntryKey);
                if (!ok)
                    failures.Add($"{cell}: list from {start} [{Show(expected)}] page {page.Entries.Count} " +
                                 $"newest {newest?.EntryKey.ToString()[..8]} oldest {oldest?.EntryKey.ToString()[..8]}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(25)));
        Assert.True(anchored > 500, $"only {anchored} anchored pages compared");
        Assert.True(fellThrough > 100, $"only {fellThrough} filtered-out anchors compared");

        static RegisterEntryKeyRow? Decode(string? cursor)
        {
            if (cursor is null) return null;
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var c = JsonSerializer.Deserialize<RegisterRepository.RegisterCursor>(Convert.FromBase64String(base64))!;
            return new RegisterEntryKeyRow { PostedAt = c.PostedAt, Seq = c.Seq, EntryKey = c.EntryKey };
        }
    }

    [Fact]
    public async Task The_transaction_level_never_matches_a_split_on_its_opposite_side()
    {
        // The transaction's memo and tags are shown on its split side only, so a
        // register that holds a multi-split's lines from their other side never finds
        // them by those values, while the split side does.
        var u = await BuildMatrixLedgerAsync();
        await using var db = Db.NewDbContext();

        Assert.NotEmpty(u.OppositeCells);
        foreach (var (register, filterName) in u.OppositeCells)
        {
            var scope = u.Registers.Single(r => r.Name == register).Scope;
            var (_, filter, hidden) = u.Filters.Single(f => f.Name == filterName);
            var opposite = await KeysAsync(db, u.Ledger.LedgerId, scope, null, null, RegisterRepository.DirectionBefore, 1000, hidden, filter, RegisterSort.Default);
            Assert.DoesNotContain(opposite, k => u.SplitOppositeLegs.Contains(k.EntryKey));

            // The split side finds the very headers by the same filter.
            var splitSide = await KeysAsync(db, u.Ledger.LedgerId, [u.BankA], null, null, RegisterRepository.DirectionBefore, 1000, hidden, filter, RegisterSort.Default);
            Assert.Contains(splitSide, k => u.Splits.Contains(k.EntryKey));
        }
    }

    // =====================================================================
    // Shapes the rule changes, against the new rule
    // =====================================================================

    [Fact]
    public async Task The_symmetric_shape_shows_one_entry_per_line_on_its_other_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var header = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee A", (category.Id, -5m), (category.Id, -7m));
        var lines = await LegIdsAsync(header, category.Id);
        // Line 1 gets a tag of its own on the split side; the category side inherits it.
        await ledger.WriteNotesAsync(header, new AnnotationIntent<string>(bank.Id, FieldEdit<string>.Set("whole"),
                FieldEdit<IReadOnlyList<string>>.Set(["tag-whole"]),
                [new LineIntent<string>(1, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set(["tag-line"]))]),
            bank.Id);

        await using var db = Db.NewDbContext();
        var fromBank = await KeysAsync(db, ledger.LedgerId, [bank.Id], RegisterFilter.None);
        var fromCategory = await KeysAsync(db, ledger.LedgerId, [category.Id], RegisterFilter.None);

        // Both accounts are on every posting, yet only the split side groups the lines.
        Assert.Equal([header], fromBank.Select(k => k.EntryKey));
        Assert.Equal(lines.Order(), fromCategory.Select(k => k.EntryKey).Order());

        // The category side finds the line by the tag it shows, and not by the transaction's.
        Assert.Equal([lines[1]], (await KeysAsync(db, ledger.LedgerId, [category.Id], new RegisterFilter(Tag: "tag-line"))).Select(k => k.EntryKey));
        Assert.Empty(await KeysAsync(db, ledger.LedgerId, [category.Id], new RegisterFilter(Tag: "tag-whole")));
        Assert.Empty(await KeysAsync(db, ledger.LedgerId, [category.Id], new RegisterFilter(Search: "whole")));
        // The split side counts the transaction's tags for every line.
        Assert.Equal([header], (await KeysAsync(db, ledger.LedgerId, [bank.Id], new RegisterFilter(Tag: "tag-whole"))).Select(k => k.EntryKey));

        // Through the page: one group on the bank, one row per line on the category.
        var bankPage = await PageAsync(client, ledger, bank.Id);
        var categoryPage = await PageAsync(client, ledger, category.Id);
        Assert.Equal(["group"], bankPage.Entries.Select(e => e.Kind));
        Assert.Equal(["txn", "txn"], categoryPage.Entries.Select(e => e.Kind));
        Assert.Equal(lines.Order(), categoryPage.Entries.Select(e => e.Txn!.Id).Order());
    }

    [Fact]
    public async Task An_opposite_side_matches_what_it_shows_and_never_the_transaction_level()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var c1 = await ledger.AddCategoryAsync("Category A");
        var c2 = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var header = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee A", (c1.Id, -5m), (c2.Id, -7m));
        await ledger.WriteNotesAsync(header, new AnnotationIntent<string>(bank.Id, FieldEdit<string>.Set("transaction memo"),
                FieldEdit<IReadOnlyList<string>>.Set(["tag-whole"]),
                [new LineIntent<string>(0, FieldEdit<string>.Set("first line"), FieldEdit<IReadOnlyList<string>>.Set(["tag-line"]))]),
            bank.Id);
        var c1Line = Assert.Single(await LegIdsAsync(header, c1.Id));

        await using var db = Db.NewDbContext();
        // The line's own values on the split side are what its opposite side shows.
        Assert.Equal([c1Line], (await KeysAsync(db, ledger.LedgerId, [c1.Id], new RegisterFilter(Tag: "tag-line"))).Select(k => k.EntryKey));
        Assert.Equal([c1Line], (await KeysAsync(db, ledger.LedgerId, [c1.Id], new RegisterFilter(Search: "first line"))).Select(k => k.EntryKey));
        // Nothing of the transaction level, on either line's opposite side.
        foreach (var category in new[] { c1.Id, c2.Id })
        {
            Assert.Empty(await KeysAsync(db, ledger.LedgerId, [category], new RegisterFilter(Tag: "tag-whole")));
            Assert.Empty(await KeysAsync(db, ledger.LedgerId, [category], new RegisterFilter(Search: "transaction memo")));
        }
        Assert.Empty(await KeysAsync(db, ledger.LedgerId, [c2.Id], new RegisterFilter(Tag: "tag-line")));
        // The split side matches both levels: the header's memo and tags, and the line's.
        foreach (var f in new[] { new RegisterFilter(Tag: "tag-whole"), new RegisterFilter(Tag: "tag-line"),
                     new RegisterFilter(Search: "transaction memo"), new RegisterFilter(Search: "first line") })
            Assert.Equal([header], (await KeysAsync(db, ledger.LedgerId, [bank.Id], f)).Select(k => k.EntryKey));
    }

    [Fact]
    public async Task A_one_split_side_is_found_by_the_values_it_inherits()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var header = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee A", (category.Id, -5m));
        await ledger.WriteNotesAsync(header, new AnnotationIntent<string>(bank.Id, FieldEdit<string>.Set("entered memo"),
            FieldEdit<IReadOnlyList<string>>.Set(["tag-own"])), bank.Id);

        // The side it was entered from keys it by the header; the other side by its leg.
        var categoryLeg = Assert.Single(await LegIdsAsync(header, category.Id));
        await using var db = Db.NewDbContext();
        foreach (var f in new[] { new RegisterFilter(Tag: "tag-own"), new RegisterFilter(Search: "entered memo"), new RegisterFilter(Search: "tag-o") })
        {
            Assert.Equal([categoryLeg], (await KeysAsync(db, ledger.LedgerId, [category.Id], f)).Select(k => k.EntryKey));
            Assert.Equal([header], (await KeysAsync(db, ledger.LedgerId, [bank.Id], f)).Select(k => k.EntryKey));
        }
        // The category side overrides: it now shows, and is found by, its own value only.
        await ledger.WriteNotesAsync(header, new AnnotationIntent<string>(category.Id, FieldEdit<string>.Leave,
            FieldEdit<IReadOnlyList<string>>.Set(["tag-category"])), bank.Id);
        Assert.Empty(await KeysAsync(db, ledger.LedgerId, [category.Id], new RegisterFilter(Tag: "tag-own")));
        Assert.Equal([categoryLeg], (await KeysAsync(db, ledger.LedgerId, [category.Id], new RegisterFilter(Tag: "tag-category"))).Select(k => k.EntryKey));
        Assert.Empty(await KeysAsync(db, ledger.LedgerId, [bank.Id], new RegisterFilter(Tag: "tag-category")));
    }

    [Fact]
    public async Task An_events_investment_side_groups_by_header_and_its_counterparts_inherit_the_event()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var dest = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var funding = await ledger.AddInvestmentAccountAsync("Brokerage C");
        var bank = await ledger.AddBankAccountAsync("Account A");
        var fee = await ledger.AddCategoryAsync("Category A");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = LedgerActions.BuyXfr, SecurityId = security, Shares = 10m, Price = 20m,
            TransferAccountId = funding.Id, FeeAccountId = fee.Id, FeeAmount = 3m, PostedAt = Day(1, 5),
        });
        var transfer = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = LedgerActions.TransferShares, SecurityId = security, Shares = 4m,
            TransferAccountId = dest.Id, PostedAt = Day(2, 5),
        });
        var xfr = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = LedgerActions.Transfer, TransferAccountId = bank.Id,
            Amount = 40m, PostedAt = Day(3, 5),
        });
        await ledger.WriteNotesAsync(buy, new AnnotationIntent<string>(source.Id, FieldEdit<string>.Set("event memo"),
            FieldEdit<IReadOnlyList<string>>.Set(["tag-event"])), source.Id);
        await ledger.WriteNotesAsync(transfer, new AnnotationIntent<string>(source.Id, FieldEdit<string>.Set("moved memo"),
            FieldEdit<IReadOnlyList<string>>.Set(["tag-moved"])), source.Id);
        await ledger.WriteNotesAsync(xfr, new AnnotationIntent<string>(source.Id, FieldEdit<string>.Set("cash memo"),
            FieldEdit<IReadOnlyList<string>>.Set(["tag-cash"])), source.Id);

        await using var db = Db.NewDbContext();
        // Holdings: the buy's security line is on the investment side, so it is the
        // event's one entry there, keyed by the header like the brokerage's.
        var holdings = source.HoldingsAccountId!.Value;
        Assert.Contains(buy, (await KeysAsync(db, ledger.LedgerId, [holdings], RegisterFilter.None)).Select(k => k.EntryKey));
        // An in-kind transfer is one entry in both brokerages, and both find it by the event's values.
        foreach (var brokerage in new[] { source.Id, dest.Id })
        {
            Assert.Contains(transfer, (await KeysAsync(db, ledger.LedgerId, [brokerage], RegisterFilter.None)).Select(k => k.EntryKey));
            Assert.Equal([transfer], (await KeysAsync(db, ledger.LedgerId, [brokerage], new RegisterFilter(Tag: "tag-moved"))).Select(k => k.EntryKey));
        }
        // The funding brokerage and the fee category are opposite rows that inherit the event.
        foreach (var counterpart in new[] { funding.Id, fee.Id })
        {
            var rows = await KeysAsync(db, ledger.LedgerId, [counterpart], new RegisterFilter(Tag: "tag-event"));
            var row = Assert.Single(rows);
            Assert.NotEqual(buy, row.EntryKey);
            Assert.Single(await KeysAsync(db, ledger.LedgerId, [counterpart], new RegisterFilter(Search: "event memo")));
        }
        // A one-posting Xfr seen from the bank inherits the brokerage leg's value; the
        // header is the brokerage's, so the bank's row is keyed by its leg.
        var bankLeg = Assert.Single(await LegIdsAsync(xfr, bank.Id));
        Assert.Equal([bankLeg], (await KeysAsync(db, ledger.LedgerId, [bank.Id], new RegisterFilter(Tag: "tag-cash"))).Select(k => k.EntryKey));
        Assert.Equal([bankLeg], (await KeysAsync(db, ledger.LedgerId, [bank.Id], new RegisterFilter(Search: "cash memo"))).Select(k => k.EntryKey));
        Assert.Equal([xfr], (await KeysAsync(db, ledger.LedgerId, [source.Id], new RegisterFilter(Tag: "tag-cash"))).Select(k => k.EntryKey));
    }

    [Fact]
    public async Task An_anchor_on_an_opposite_side_starts_at_the_headers_first_line_and_shows_each_line_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var other = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await CreateBankAsync(client, ledger, bank.Id, Day(2, 1), "Payee A", (category.Id, -1m));
        var split = await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee B",
            (category.Id, -5m), (category.Id, -7m), (other.Id, -9m));
        await CreateBankAsync(client, ledger, bank.Id, Day(4, 1), "Payee C", (category.Id, -2m));
        var lines = await LegIdsAsync(split, category.Id);

        var page = await PageAsync(client, ledger, category.Id, $"&starting_at={split}");
        var ids = page.Entries.Select(e => e.Txn!.Id).ToList();
        // The anchored header's lines come first, each once, then the older entry.
        Assert.Equal(3, ids.Count);
        Assert.Equal(lines.Order(), ids.Take(2).Order());
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.NotNull(page.CursorForNewer);
    }

    [Fact]
    public async Task A_cursor_whose_key_no_longer_names_an_entry_pages_on_past_it()
    {
        // A held cursor can name its entry by the other id: the header for a row now
        // keyed by its leg, or a leg for a row now keyed by its header (the header
        // changed hands between two fetches, or the cursor predates the keying).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        for (var day = 1; day <= 5; day++)
            await CreateBankAsync(client, ledger, bank.Id, Day(3, day), $"Payee {day}", (category.Id, -day));

        await using var db = Db.NewDbContext();
        // The category's rows are keyed by leg, so the held key is the header; the
        // bank's are keyed by header, so the held key is the bank's leg.
        foreach (var register in new[] { category.Id, bank.Id })
        foreach (var sort in new[] { RegisterSort.Default, new RegisterSort("amount", Descending: false) })
        foreach (var direction in new[] { RegisterRepository.DirectionBefore, RegisterRepository.DirectionAfter })
        {
            var all = await RegisterQuery.EntryKeysAsync(db, [register], ledger.LedgerId, null, null,
                RegisterRepository.DirectionBefore, 100, false, RegisterFilter.None, Today, sort, default);
            Assert.Equal(5, all.Count);
            var middle = all[2];
            var leg = await db.TxnLegs.AsNoTracking().SingleOrDefaultAsync(l => l.Id == middle.EntryKey);
            var heldKey = leg is not null
                ? leg.HeaderId
                : await db.TxnLegs.AsNoTracking()
                    .Where(l => l.HeaderId == middle.EntryKey && l.AccountId == register).Select(l => l.Id).SingleAsync();
            var page = await RegisterQuery.EntryKeysAsync(db, [register], ledger.LedgerId, heldKey, middle.Seq,
                direction, 100, false, RegisterFilter.None, Today, sort, default);

            // Past the held entry, in the direction asked; the held entry itself may
            // come back once, since its own key no longer ties with the cursor's.
            var expected = direction == RegisterRepository.DirectionBefore ? all.Skip(3) : all.Take(2);
            Assert.Equal(expected.Select(k => k.EntryKey),
                page.Select(k => k.EntryKey).Where(k => k != middle.EntryKey));
        }
    }

    // =====================================================================
    // The matrix ledger
    // =====================================================================

    /// <summary>
    /// The ledger every register cell is read from: its registers and filters, the
    /// cells where a multi-split is seen from its opposite side under a filter on its
    /// transaction-level values, those splits and their opposite-side legs.
    /// </summary>
    private sealed record Matrix(
        SyntheticLedger Ledger,
        Guid BankA,
        IReadOnlyList<(string Name, Guid[] Scope)> Registers,
        IReadOnlyList<(string Name, RegisterFilter Filter, bool Hidden)> Filters,
        IReadOnlySet<(string Register, string Filter)> OppositeCells,
        IReadOnlySet<Guid> Splits,
        IReadOnlySet<Guid> SplitOppositeLegs);

    private async Task<Matrix> BuildMatrixLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var catC = await ledger.AddCategoryAsync("Category C", parentId: catA.Id);
        var fee = await ledger.AddCategoryAsync("Category D");
        var income = await ledger.AddCategoryAsync("Category E", kind: "income");
        var brokerA = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var brokerB = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> OneSplit(Guid from, Guid to, decimal amount, DateTime on, string payee, string? memos = null, string? check = null)
        {
            var header = await CreateBankAsync(client, ledger, from, on, payee, check, (to, amount));
            if (memos is not null)
            {
                // Both sides own a memo, so neither inherits.
                await ledger.SetSideMemoAsync(header, from, memos, from);
                await ledger.SetSideMemoAsync(header, to, memos + " other side", from);
            }
            return header;
        }

        // One-splits, a shared date, a check number.
        var cleared = await OneSplit(bankA.Id, catA.Id, -10m, Day(3, 1), "Payee North", "alpha one");
        await OneSplit(bankA.Id, catA.Id, -25m, Day(3, 3), "Payee South");
        var reconciling = await OneSplit(bankA.Id, catA.Id, -40m, Day(3, 5), "Payee East", "alpha two", check: "1042");
        await OneSplit(bankA.Id, catB.Id, -15m, Day(3, 5), "Payee West");
        await OneSplit(bankA.Id, catB.Id, -15m, Day(3, 5), "Payee West");
        await OneSplit(bankA.Id, catA.Id, 30m, Day(3, 8), "Payee North", "alpha three");
        await OneSplit(bankA.Id, catC.Id, -12m, Day(3, 9), "Payee Central");
        // Transfers, entered from either side.
        await OneSplit(bankA.Id, bankB.Id, -100m, Day(3, 10), "Payee Move");
        await OneSplit(bankB.Id, bankA.Id, -50m, Day(3, 12), "Payee Move", "alpha four");
        // Future-dated (Scheduled), hidden.
        await OneSplit(bankA.Id, catA.Id, -8m, Day(7, 1), "Payee Later");
        var hidden = await OneSplit(bankA.Id, catB.Id, -9m, Day(3, 14), "Payee Hidden");
        await ledger.HideTransactionAsync(hidden);
        var catAHidden = await OneSplit(bankA.Id, catA.Id, -11m, Day(3, 15), "Payee Hidden");
        await ledger.HideTransactionAsync(catAHidden);
        // On the date-range filter's last day and on today, late in the day: the
        // boundaries are inclusive, and a date is the posting's calendar date.
        await OneSplit(bankA.Id, catB.Id, -13m, Day(4, 15).AddHours(23.5), "Payee Edge", "alpha five");
        await OneSplit(bankA.Id, catA.Id, -14m, Day(6, 15).AddHours(20), "Payee Today");

        // Multi-splits: the transaction-level memo and tags on the header, no line values.
        var split1 = await CreateBankAsync(client, ledger, bankA.Id, Day(3, 20), "Payee Split", null,
            (catA.Id, -5m), (catB.Id, -7m), (catC.Id, -9m));
        var split2 = await CreateBankAsync(client, ledger, bankA.Id, Day(3, 22), "Payee Split", null,
            (catB.Id, -3m), (catB.Id, -4m), (catA.Id, -6m));
        // Two lines to another bank: a real account as the opposite side.
        var split3 = await CreateBankAsync(client, ledger, bankA.Id, Day(3, 25).AddHours(9), "Payee Split", null,
            (bankB.Id, -20m), (bankB.Id, -3m), (catA.Id, -5m));
        // Imported into a brokerage with no action (the API enters a brokerage's rows
        // as events): the split side is the brokerage.
        var split4 = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(split4, ledger.LedgerId, brokerA.Id,
                origin: "file_import", externalId: $"imp-{split4:N}");
            header.ProviderKey = "qif";
            header.Payee = "Payee Split";
            header.PostedAt = Day(4, 5).AddHours(10);
            header.TransactedAt = Day(4, 5).AddHours(10);
            db.TxnHeaders.Add(header);
            db.TxnLegs.AddRange(
                Leg(split4, ledger.LedgerId, brokerA.Id, -6m), Leg(split4, ledger.LedgerId, catA.Id, 6m),
                Leg(split4, ledger.LedgerId, brokerA.Id, -8m, posting: 1), Leg(split4, ledger.LedgerId, catB.Id, 8m, posting: 1));
            await db.SaveChangesAsync();
        }
        // The importer's write recomputes the posting counts; a direct insert has to.
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeAllNotesAsync();
        foreach (var (split, from, memo) in new[]
                 {
                     (split1, bankA.Id, "beta one"), (split2, bankA.Id, "beta two"),
                     (split3, bankA.Id, "beta three"), (split4, brokerA.Id, "beta four"),
                 })
            await ledger.WriteNotesAsync(split, new AnnotationIntent<string>(from, FieldEdit<string>.Set(memo),
                FieldEdit<IReadOnlyList<string>>.Set(["tag-h"])), from);

        // Reconciliation per account.
        await SetReconAsync(client, ledger, cleared, bankA.Id, "cleared");
        await SetReconAsync(client, ledger, reconciling, bankA.Id, "reconciling");
        await SetReconAsync(client, ledger, split1, bankA.Id, "cleared");
        await SetReconAsync(client, ledger, cleared, catA.Id, "cleared");

        // Feed rows: one waiting for review, one still pending at the bank, and one
        // merged into the row it duplicates, which leaves every register.
        var winner = await OneSplit(bankA.Id, catA.Id, -17m, Day(4, 18), "Payee Merged");
        Guid loser;
        await using (var db = Db.NewDbContext())
        {
            Guid Feed(DateTime on, string payee, decimal amount, bool needsReview, bool pending)
            {
                var headerId = Guid.NewGuid();
                var header = TxnHeaderRow.NewIngested(headerId, ledger.LedgerId, bankA.Id,
                    origin: "file_import", externalId: $"imp-{headerId:N}");
                header.ProviderKey = "qif";
                header.Payee = payee;
                header.PostedAt = on;
                header.TransactedAt = on;
                header.NeedsReview = needsReview;
                header.IsPending = pending;
                db.TxnHeaders.Add(header);
                db.TxnLegs.AddRange(Leg(headerId, ledger.LedgerId, bankA.Id, amount), Leg(headerId, ledger.LedgerId, catA.Id, -amount));
                return headerId;
            }
            Feed(Day(4, 20), "Payee Feed", -21m, needsReview: true, pending: false);
            Feed(Day(4, 12).AddHours(15), "Payee Pending", -16m, needsReview: false, pending: true);
            loser = Feed(Day(4, 19), "Payee Merged Feed", -17m, needsReview: true, pending: false);
            await db.SaveChangesAsync();
        }
        // A direct insert stores no notes; the API's writes do.
        await ledger.RecomputeAllNotesAsync();
        var merged = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        // Events: multi-posting ones carry their value on the header.
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Buy, SecurityId = security, Shares = 10m, Price = 20m,
            FeeAccountId = fee.Id, FeeAmount = 3m, PostedAt = Day(4, 1), Payee = "Payee Broker",
        });
        var buyx = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.BuyXfr, SecurityId = security, Shares = 5m, Price = 22m,
            TransferAccountId = brokerB.Id, FeeAccountId = fee.Id, FeeAmount = 2m, PostedAt = Day(4, 10), Payee = "Payee Broker",
        });
        foreach (var (evt, memo) in new[] { (buy, "gamma buy"), (buyx, "gamma funded") })
            await ledger.WriteNotesAsync(evt, new AnnotationIntent<string>(brokerA.Id, FieldEdit<string>.Set(memo),
                FieldEdit<IReadOnlyList<string>>.Set(["tag-e"])), brokerA.Id);
        // One-posting events carry nothing here: their counterpart inherits the
        // brokerage leg, a change asserted on its own.
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = bankA.Id,
            Amount = 40m, PostedAt = Day(5, 1),
        });
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerA.Id, Action = LedgerActions.DividendCash, SecurityId = security,
            CategoryAccountId = income.Id, Amount = 12m, PostedAt = Day(5, 5),
        });

        var registers = new List<(string, Guid[])>
        {
            ("bank A", [bankA.Id]), ("bank B", [bankB.Id]),
            ("category A", [catA.Id]), ("category B", [catB.Id]), ("category C", [catC.Id]),
            ("category A and below", [catA.Id, catC.Id]),
            ("fee", [fee.Id]), ("income", [income.Id]),
            ("brokerage A", [brokerA.Id]), ("brokerage B", [brokerB.Id]),
        };
        var filters = new List<(string, RegisterFilter, bool)>
        {
            ("none", RegisterFilter.None, false),
            ("hidden", RegisterFilter.None, true),
            ("dates", new RegisterFilter(DateFrom: new DateOnly(2026, 3, 3), DateTo: new DateOnly(2026, 4, 15)), false),
            ("amount min", new RegisterFilter(AmountMin: 10m), false),
            ("amount max", new RegisterFilter(AmountMax: 20m), false),
            ("amount range", new RegisterFilter(AmountMin: 5m, AmountMax: 30m), false),
            ("security", new RegisterFilter(SecurityId: security), false),
            ("category A", new RegisterFilter(CategoryId: catA.Id), false),
            ("category bank A", new RegisterFilter(CategoryId: bankA.Id), false),
            ("category brokerage A", new RegisterFilter(CategoryId: brokerA.Id), false),
            ("cleared", new RegisterFilter(Status: "cleared", Today: Today), false),
            ("uncleared", new RegisterFilter(Status: "uncleared", Today: Today), false),
            ("reconciling", new RegisterFilter(Status: "reconciling", Today: Today), false),
            ("scheduled", new RegisterFilter(Status: "scheduled", Today: Today), false),
            ("needs review", new RegisterFilter(Status: "needs_review", Today: Today), false),
            ("tag h", new RegisterFilter(Tag: "tag-h"), false),
            ("tag e", new RegisterFilter(Tag: "tag-e"), false),
            ("search alpha", new RegisterFilter(Search: "alpha"), false),
            ("search beta", new RegisterFilter(Search: "beta"), false),
            ("search gamma", new RegisterFilter(Search: "GAMMA"), false),
            ("search payee", new RegisterFilter(Search: "north"), false),
            // The text is a pattern: a wildcard, and an escaped character.
            ("search wildcard", new RegisterFilter(Search: "payee_n"), false),
            ("search escaped", new RegisterFilter(Search: @"payee\ north"), false),
            ("search escaped wildcard", new RegisterFilter(Search: @"payee\%"), false),
            ("search counterparty", new RegisterFilter(Search: "Category B"), false),
            ("search check", new RegisterFilter(Search: "1042"), false),
            ("search tag name", new RegisterFilter(Search: "tag-"), false),
            ("combined", new RegisterFilter(Search: "Payee", DateFrom: new DateOnly(2026, 3, 4), AmountMin: 9m), false),
        };

        // Where a multi-split is seen from its opposite side under a filter on its
        // transaction-level memo or tags.
        var oppositeCells = new HashSet<(string, string)>();
        foreach (var register in new[] { "category A", "category B", "category C", "category A and below", "bank B" })
        foreach (var filter in new[] { "tag h", "search beta", "search tag name" })
            oppositeCells.Add((register, filter));

        var opposite = new HashSet<Guid>();
        foreach (var (split, from) in new[] { (split1, bankA.Id), (split2, bankA.Id), (split3, bankA.Id), (split4, brokerA.Id) })
            opposite.UnionWith(await LegIdsOffAsync(split, from));

        return new Matrix(ledger, bankA.Id, registers, filters, oppositeCells,
            new HashSet<Guid> { split1, split2, split3, split4 }, opposite);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static Task<List<RegisterEntryKeyRow>> KeysAsync(
        AppDbContext db, Guid ledgerId, Guid[] scope, Guid? cursorKey, long? cursorSeq, string direction, int limit,
        bool hidden, RegisterFilter f, RegisterSort sort) =>
        RegisterQuery.EntryKeysAsync(db, scope, ledgerId, cursorKey, cursorSeq, direction, limit, hidden,
            f, f.Today ?? Today, sort, default);

    private static Task<List<RegisterEntryKeyRow>> KeysAsync(AppDbContext db, Guid ledgerId, Guid[] scope, RegisterFilter f) =>
        RegisterQuery.EntryKeysAsync(db, scope, ledgerId, null, null, RegisterRepository.DirectionBefore, 1000, false,
            f, Today, RegisterSort.Default, default);

    private static string Show(List<RegisterEntryKeyRow> keys) =>
        string.Join(", ", keys.Select(k => $"{k.Seq}:{k.EntryKey.ToString()[..8]}"));

    private static string Show(IEnumerable<OracleEntry> entries) =>
        string.Join(", ", entries.Select(e => $"{e.Seq}:{e.Key.ToString()[..8]}"));

    private async Task<List<Guid>> LegIdsAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .OrderBy(l => l.PostingIndex).Select(l => l.Id).ToListAsync();
    }

    private async Task<List<Guid>> LegIdsOffAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId != accountId)
            .Select(l => l.Id).ToListAsync();
    }

    private static TxnLegRow Leg(Guid headerId, Guid ledgerId, Guid accountId, decimal amount, int posting = 0) => new()
    {
        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId,
        AccountId = accountId, PostingIndex = posting, Amount = amount,
    };

    private static Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceAccountId, DateTime postedAt, string payee,
        params (Guid Counterparty, decimal Amount)[] postings) =>
        CreateBankAsync(client, ledger, sourceAccountId, postedAt, payee, null, postings);

    private static async Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceAccountId, DateTime postedAt, string payee,
        string? checkNumber, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                CheckNumber = checkNumber,
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

    private static async Task SetReconAsync(HttpClient client, SyntheticLedger ledger, Guid headerId, Guid accountId, string status)
    {
        var resp = await client.PutAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/recon-status",
            new SetReconStatusRequest { Status = status, AccountId = accountId });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static async Task<RegisterPage> PageAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, string query = "")
    {
        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=100{query}");
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<RegisterPage>())!;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }
}
