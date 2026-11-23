using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Presentation;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// What the readers read of the stored memo-and-tags presentation
/// (<see cref="StoredNotesQuery"/>) against the oracle: the rule run in memory
/// (<see cref="StoredNotes"/>, through <see cref="LegNotes.ComputeAsync"/>) over the
/// ledger's loaded rows, for every leg and every perspective a reader takes: the
/// register, the report (grouped by category and by the account a posting pairs with)
/// and the listing (by account, by counterparty, ledger-wide).
/// </summary>
/// <remarks>
/// <para>The oracle picks each perspective's leg in C#, from the view row's plain
/// columns, so a perspective the SQL picks differently shows as a named leg and field;
/// the tag sets are compared by name in each role.</para>
/// <para>The oracle is the code that wrote the values, so it also checks how that code
/// builds the rule's facts: the posting's other leg, its account and the posting count
/// that <see cref="StoredNotes.Facts"/> (what <see cref="StoredNotes.Compute"/> runs the
/// rule over) builds from the stored legs must be the view's own <c>counterparty_id</c>,
/// <c>counterparty_account_id</c> and <c>header_total_postings</c>.</para>
/// </remarks>
internal static class StoredNotesEquivalence
{
    /// <summary>One line per (perspective, leg) whose stored reading differs from the oracle's; empty when they agree.</summary>
    public static async Task<IReadOnlyList<string>> DifferencesAsync(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var oracle = await NotesOracle.LoadAsync(db, ledgerId);
        var differences = new List<string>(oracle.FactDifferences());

        Compare("register", oracle.Rows.Select(r => oracle.Read(r, r.LegId, register: true)).ToList(),
            await ReadAsync(db, ledgerId, StoredNotesQuery.RegisterLegs(db).Select(StoredRegister(db))), differences);

        foreach (var grouped in new[] { false, true })
        {
            Compare($"report(groupedOnCounterparty={grouped})",
                oracle.Rows.Select(r => oracle.Read(r, NotesOracle.ReportLeg(r, grouped), register: false)).ToList(),
                await ReadAsync(db, ledgerId, StoredNotesQuery.ReportLegs(db, grouped).Select(StoredReport(db))),
                differences);
        }

        foreach (var scope in Enum.GetValues<ListingScope>())
        {
            Compare($"listing({scope})",
                oracle.Rows.Select(r => oracle.Read(r, oracle.ListingLeg(r, scope), register: false)).ToList(),
                await ReadAsync(db, ledgerId, StoredNotesQuery.ListingLegs(db, scope).Select(StoredReport(db))),
                differences);
        }
        return differences;
    }

    /// <summary>The ledger's legs whose stored presentation columns are set.</summary>
    public static Task<int> PresentedLegsAsync(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM txn_legs WHERE ledger_id = {ledgerId} AND notes_kind IS NOT NULL")
            .SingleAsync();
    }

    /// <summary>What the oracle's register shows for every leg of the ledger.</summary>
    public static async Task<List<Reading>> OracleRegisterAsync(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var oracle = await NotesOracle.LoadAsync(db, ledgerId);
        return [.. oracle.Rows.Select(r => oracle.Read(r, r.LegId, register: true))];
    }

    /// <summary>Fails the test, naming every leg and perspective that differs.</summary>
    public static async Task AssertEquivalentAsync(AppDbContext db, Guid ledgerId)
    {
        var differences = await DifferencesAsync(db, ledgerId);
        Assert.True(differences.Count == 0,
            $"{differences.Count} stored readings differ from the rule run over the loaded rows:\n  " + string.Join("\n  ", differences.Take(40)));
    }

    /// <summary>
    /// How many accounts and categories a scoped listing is checked for, and how many tags
    /// in each: all of them on a fixture, a sample on the stress seed. The ledger-wide
    /// listing and the reports are checked for every tag.
    /// </summary>
    internal sealed record FilterSample(int Scopes, int ScopedTags)
    {
        public static readonly FilterSample All = new(int.MaxValue, int.MaxValue);
    }

    /// <summary>
    /// Fails the test unless what the readers' filters select, read through the
    /// repositories, is what the oracle's presentation selects: the report summary
    /// filtered by each tag (grouped by category and by the account a posting pairs
    /// with), the tag cells with "(untagged)", and the listing by tag and by text in
    /// every scope.
    /// </summary>
    /// <remarks>
    /// <see cref="AssertEquivalentAsync"/> proves each leg's stored presentation; this
    /// proves the predicates that select on it (the tag filter's own choice of leg, the
    /// untagged complement, the text search's narrowing) agree with that presentation,
    /// at each call site, so a filter edited apart from its reader fails here.
    /// </remarks>
    public static async Task AssertFiltersEquivalentAsync(AppDbContext db, Guid ledgerId, FilterSample sample)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sample);
        var oracle = await NotesOracle.LoadAsync(db, ledgerId);
        var differences = new List<string>();
        var repo = new Coffer.Api.Db.Repositories.ReportingRepository(db, TimeProvider.System);
        var tags = oracle.Tags.OrderBy(t => t.Value, StringComparer.Ordinal).Select(t => new { Id = t.Key, Name = t.Value }).ToList();
        var tagId = tags.ToDictionary(t => t.Name, t => t.Id, StringComparer.Ordinal);
        Assert.True(tags.Count > 0, "the ledger has no tags, so no tag filter is compared");
        var checks = 0;

        // ----- Reports ---------------------------------------------------------------
        foreach (var grouped in new[] { false, true })
        {
            var postings = oracle.Lines(r => NotesOracle.ReportLeg(r, grouped))
                .Where(x => x.AccountType == "category" && x.CategoryKind is "income" or "expense"
                            && !x.IsHidden && x.IsMergedInto == null)
                .ToList();
            var groupBy = grouped ? Contracts.ReportGroupBy.Account : Contracts.ReportGroupBy.Category;
            foreach (var tag in tags)
            {
                var expected = postings
                    .Where(x => x.InPlay.Contains(tag.Name, StringComparer.Ordinal))
                    .Where(x => !grouped || x.CounterpartyAccountId != null)
                    .GroupBy(x => grouped ? x.CounterpartyAccountId : x.AccountId)
                    .ToDictionary(g => Key(g.Key), g => -g.Sum(x => x.Amount), StringComparer.Ordinal);
                var actual = await repo.SummarizeAsync(new Contracts.ReportSpec
                {
                    LedgerId = ledgerId, TagId = tag.Id, Measure = Contracts.ReportMeasure.Net, GroupBy = groupBy,
                });
                CompareCells($"report by {groupBy} with tag {tag.Name}", expected, actual, differences);
                checks++;
            }

            if (grouped) continue;
            // The tag cells: each posting under every tag in play for it, once, or under
            // "(untagged)" when none is.
            var cells = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var x in postings)
            {
                var names = x.InPlay.Distinct(StringComparer.Ordinal).ToList();
                IEnumerable<string> keys = names.Count == 0 ? [Key(null)] : names.Select(n => Key(tagId[n]));
                foreach (var key in keys)
                    cells[key] = cells.GetValueOrDefault(key) - x.Amount;
            }
            CompareCells("report by tag", cells, await repo.SummarizeAsync(new Contracts.ReportSpec
            {
                LedgerId = ledgerId, Measure = Contracts.ReportMeasure.Net, GroupBy = Contracts.ReportGroupBy.Tag,
            }), differences);
            checks++;
        }

        // ----- Listings --------------------------------------------------------------
        foreach (var scope in Enum.GetValues<ListingScope>())
        {
            var lines = oracle.Lines(r => oracle.ListingLeg(r, scope))
                .Where(x => !x.IsHidden && x.IsMergedInto == null)
                .ToList();
            // Each scope as the listing reaches it: one account, one category, or the ledger.
            var scopes = scope switch
            {
                ListingScope.Account => lines.GroupBy(x => x.AccountId)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(sample.Scopes)
                    .Select(g => (Name: $"account {g.Key.ToString()[..8]}", Account: (Guid?)g.Key, Category: (Guid?)null,
                        Lines: g.ToList())).ToList(),
                ListingScope.Counterparty => lines.Where(x => x.AccountType != "category" && x.CounterpartyAccountType == "category")
                    .GroupBy(x => x.CounterpartyAccountId!.Value)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(sample.Scopes)
                    .Select(g => (Name: $"category {g.Key.ToString()[..8]}", Account: (Guid?)null, Category: (Guid?)g.Key,
                        Lines: g.ToList())).ToList(),
                _ => [(Name: "ledger", Account: null, Category: null, Lines: lines.Where(x => x.AccountType != "category").ToList())],
            };
            var scopedTags = scope == ListingScope.Ledger ? tags : tags.Take(sample.ScopedTags).ToList();
            foreach (var s in scopes)
            {
                foreach (var tag in scopedTags)
                {
                    var expected = s.Lines.Where(x => x.InPlay.Contains(tag.Name, StringComparer.Ordinal));
                    var actual = await ListAllAsync(repo, new Contracts.TransactionQuery
                    {
                        LedgerId = ledgerId, AccountId = s.Account, CategoryId = s.Category, TagId = tag.Id,
                    });
                    CompareLines($"listing({scope}) {s.Name} with tag {tag.Name}", expected, actual, differences);
                    checks++;
                }
                foreach (var text in Texts(lines))
                {
                    var expected = s.Lines.Where(x => Has(x.Payee, text) || Has(x.Memo, text) || Has(x.HeaderMemo, text));
                    var actual = await ListAllAsync(repo, new Contracts.TransactionQuery
                    {
                        LedgerId = ledgerId, AccountId = s.Account, CategoryId = s.Category, Text = text,
                    });
                    CompareLines($"listing({scope}) {s.Name} with text '{text}'", expected, actual, differences);
                    checks++;
                }
            }
        }

        Assert.True(checks > tags.Count, $"only {checks} filter results compared");
        Assert.True(differences.Count == 0,
            $"{differences.Count} of {checks} filter results differ from the rule run over the loaded rows:\n  " + string.Join("\n  ", differences.Take(40)));
    }

    /// <summary>A report group's key: its id, or the untagged row's.</summary>
    private static string Key(Guid? groupId) => groupId?.ToString() ?? "(none)";

    private static void CompareCells(
        string what, Dictionary<string, decimal> expected, Contracts.ReportResult actual, List<string> differences)
    {
        var got = actual.Rows.GroupBy(r => Key(r.GroupId))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.Ordinal);
        foreach (var key in expected.Keys.Union(got.Keys).Order(StringComparer.Ordinal))
        {
            var (e, a) = (expected.GetValueOrDefault(key), got.GetValueOrDefault(key));
            if (!expected.ContainsKey(key) || !got.ContainsKey(key) || e != a)
                differences.Add($"{what}: group {key} oracle {(expected.ContainsKey(key) ? e : "absent")}, stored {(got.ContainsKey(key) ? a : "absent")}");
        }
    }

    private static void CompareLines(
        string what, IEnumerable<NotesOracle.Line> expected, List<Contracts.TransactionLine> actual, List<string> differences)
    {
        var e = expected.Select(x => $"{x.HeaderId}|{x.AccountId}|{x.CounterpartyAccountId}|{x.Amount}|{x.PostedAt.Ticks}")
            .GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var a = actual.Select(x => $"{x.HeaderId}|{x.AccountId}|{x.CounterpartyAccountId}|{x.Amount}|{x.PostedAt.Ticks}")
            .GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var wrong = e.Keys.Union(a.Keys).Where(k => e.GetValueOrDefault(k) != a.GetValueOrDefault(k)).ToList();
        if (wrong.Count > 0)
            differences.Add($"{what}: {e.Values.Sum()} oracle lines, {a.Values.Sum()} stored, {wrong.Count} differ, e.g. {wrong[0]} oracle {e.GetValueOrDefault(wrong[0])} stored {a.GetValueOrDefault(wrong[0])}");
    }

    private static async Task<List<Contracts.TransactionLine>> ListAllAsync(
        Coffer.Api.Db.Repositories.ReportingRepository repo, Contracts.TransactionQuery query)
    {
        var all = new List<Contracts.TransactionLine>();
        for (var offset = 0; ; offset += 500)
        {
            var page = await repo.ListTransactionsAsync(new Contracts.TransactionQuery
            {
                LedgerId = query.LedgerId, AccountId = query.AccountId, CategoryId = query.CategoryId,
                TagId = query.TagId, Text = query.Text, Limit = 500, Offset = offset,
            });
            all.AddRange(page.Lines);
            if (!page.HasMore) return all;
        }
    }

    /// <summary>ILIKE's match, for text with no wildcard in it.</summary>
    private static bool Has(string? value, string text) =>
        value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Texts to search for, drawn from what the lines show: the most common memo, header
    /// memo and payee, and a shorter piece of a memo in another case, so a match inside a
    /// value and a match ignoring case are both exercised. None holds a LIKE wildcard.
    /// </summary>
    private static List<string> Texts(List<NotesOracle.Line> lines)
    {
        static IEnumerable<string> Common(IEnumerable<string?> values) => values
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.IndexOfAny(['%', '_', '\\']) < 0)
            .GroupBy(v => v!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key.Trim()).Take(2);
        var texts = Common(lines.Select(x => x.Memo))
            .Concat(Common(lines.Select(x => x.HeaderMemo)))
            .Concat(Common(lines.Select(x => x.Payee)).Take(1))
            .ToList();
        if (texts.FirstOrDefault(t => t.Length >= 6) is { } longer)
            texts.Add(longer[1..^1].ToUpperInvariant());
        return texts.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>What a reader reads of one leg's presentation.</summary>
    internal sealed class Reading
    {
        public Guid LegId { get; init; }
        public Guid HeaderId { get; init; }
        public Guid NotesLegId { get; init; }
        public Guid EntryKey { get; init; }
        public LegKind Kind { get; init; }
        public bool IsReadOnly { get; init; }
        public bool GroupsByHeader { get; init; }
        public string? Memo { get; init; }
        public bool MemoInherited { get; init; }
        public string? InheritedMemo { get; init; }
        public Guid? InheritFromAccountId { get; init; }
        public bool TagsInherited { get; init; }
        public string? HeaderMemo { get; init; }
        public bool HeaderInPlay { get; init; }
        public string[]? Shown { get; init; }
        public string[]? InPlay { get; init; }
        public string[]? Inherited { get; init; }
        public string[]? Transaction { get; init; }

        /// <summary>Every field, tag sets in ordinal order (the database lists them in its own), an empty set and none alike.</summary>
        public override string ToString() => string.Join(" | ",
            $"notesLeg={Short(NotesLegId)}", $"entry={Short(EntryKey)}", $"kind={Kind}", $"ro={IsReadOnly}",
            $"groups={GroupsByHeader}", $"memo={Memo ?? "∅"}", $"memoInh={MemoInherited}",
            $"inhMemo={InheritedMemo ?? "∅"}", $"from={(InheritFromAccountId is { } f ? Short(f) : "∅")}",
            $"tagsInh={TagsInherited}", $"hdrMemo={HeaderMemo ?? "∅"}", $"hdrInPlay={HeaderInPlay}",
            $"shown=[{Join(Shown)}]", $"inPlay=[{Join(InPlay)}]", $"inherited=[{Join(Inherited)}]",
            $"transaction=[{Join(Transaction)}]");

        private static string Short(Guid id) => id.ToString()[..8];

        private static string Join(string[]? names) =>
            names is null ? "" : string.Join(",", names.Order(StringComparer.Ordinal));
    }

    private static Task<List<Reading>> ReadAsync(AppDbContext db, Guid ledgerId, IQueryable<Reading> readings) =>
        readings
            .Where(r => db.TxnHeaders.Any(h => h.Id == r.HeaderId && h.LedgerId == ledgerId))
            .ToListAsync();

    private static void Compare(string perspective, List<Reading> oracle, List<Reading> stored, List<string> differences)
    {
        if (oracle.Count == 0) differences.Add($"{perspective}: the oracle read no legs, so nothing was compared");
        var storedByLeg = stored.ToLookup(r => r.LegId);
        foreach (var expected in oracle)
        {
            var actual = storedByLeg[expected.LegId].ToList();
            if (actual.Count != 1)
            {
                differences.Add($"{perspective} leg {expected.LegId}: {actual.Count} stored readings");
                continue;
            }
            var (e, a) = (expected.ToString(), actual[0].ToString());
            if (!string.Equals(e, a, StringComparison.Ordinal))
                differences.Add($"{perspective} leg {expected.LegId}:\n      oracle {e}\n      stored {a}");
        }
        if (stored.Count != oracle.Count)
            differences.Add($"{perspective}: {oracle.Count} oracle readings, {stored.Count} stored");
    }

    private static Expression<Func<RegisterLeg, Reading>> StoredRegister(AppDbContext db)
    {
        var shown = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Shown);
        var inPlay = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.InPlay);
        var inherited = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Inherited);
        var transaction = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.TransactionLevel);
        Expression<Func<RegisterLeg, Reading>> read = l => new Reading
        {
            LegId = l.Row.Id,
            HeaderId = l.Row.HeaderId,
            NotesLegId = l.NotesLegId,
            EntryKey = l.EntryKey,
            Kind = l.Shown.Kind,
            IsReadOnly = l.Shown.IsReadOnly,
            GroupsByHeader = l.Shown.GroupsByHeader,
            Memo = l.Shown.Memo,
            MemoInherited = l.Shown.MemoInherited,
            InheritedMemo = l.Shown.InheritedMemo,
            InheritFromAccountId = l.Shown.InheritFromAccountId,
            TagsInherited = l.Shown.TagsInherited,
            HeaderMemo = l.Shown.InPlayHeaderMemo,
            HeaderInPlay = l.Shown.InPlayHeaderTagHeaderId != null,
            Shown = shown.Inline(l.NotesLegId),
            InPlay = inPlay.Inline(l.NotesLegId),
            Inherited = inherited.Inline(l.NotesLegId),
            Transaction = transaction.Inline(l.NotesLegId),
        };
        return read.Expand();
    }

    private static Expression<Func<ReportLeg, Reading>> StoredReport(AppDbContext db)
    {
        var shown = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Shown);
        var inPlay = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.InPlay);
        var inherited = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Inherited);
        var transaction = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.TransactionLevel);
        Expression<Func<ReportLeg, Reading>> read = l => new Reading
        {
            LegId = l.Row.Id,
            HeaderId = l.Row.HeaderId,
            NotesLegId = l.NotesLegId,
            Kind = l.Shown.Kind,
            IsReadOnly = l.Shown.IsReadOnly,
            GroupsByHeader = l.Shown.GroupsByHeader,
            Memo = l.Shown.Memo,
            MemoInherited = l.Shown.MemoInherited,
            InheritedMemo = l.Shown.InheritedMemo,
            InheritFromAccountId = l.Shown.InheritFromAccountId,
            TagsInherited = l.Shown.TagsInherited,
            HeaderMemo = l.Shown.InPlayHeaderMemo,
            HeaderInPlay = l.Shown.InPlayHeaderTagHeaderId != null,
            Shown = shown.Inline(l.NotesLegId),
            InPlay = inPlay.Inline(l.NotesLegId),
            Inherited = inherited.Inline(l.NotesLegId),
            Transaction = transaction.Inline(l.NotesLegId),
        };
        return read.Expand();
    }
}

/// <summary>
/// The memo-and-tags rule run in memory over a ledger's loaded rows: the view rows'
/// plain columns, and for every leg what <see cref="StoredNotes"/> computes from the
/// stored inputs (<see cref="LegNotes.ComputeAsync"/>). The oracle a stored reading is
/// compared with; nothing here reads the stored presentation.
/// </summary>
internal sealed class NotesOracle
{
    private NotesOracle(
        IReadOnlyList<Row> rows, IReadOnlyDictionary<Guid, LegNotes.Computed> byLeg,
        IReadOnlyDictionary<Guid, string> tags, IReadOnlyDictionary<Guid, LegFacts> facts)
    {
        Rows = rows;
        ByLeg = byLeg;
        Tags = tags;
        _facts = facts;
    }

    /// <summary>The facts the rule's own construction (<see cref="StoredNotes.Facts"/>) builds for each leg.</summary>
    private readonly IReadOnlyDictionary<Guid, LegFacts> _facts;

    /// <summary>The ledger's view rows, with what a filter or a perspective reads of them.</summary>
    public IReadOnlyList<Row> Rows { get; }

    /// <summary>What the rule computes for each leg of the ledger.</summary>
    public IReadOnlyDictionary<Guid, LegNotes.Computed> ByLeg { get; }

    /// <summary>The ledger's tag names by id.</summary>
    public IReadOnlyDictionary<Guid, string> Tags { get; }

    /// <summary>One view row's plain columns: no stored presentation.</summary>
    internal sealed record Row(
        Guid LegId, Guid HeaderId, Guid AccountId, string AccountType, string? CategoryKind, decimal Amount,
        decimal? HeaderAccountNetAmount, DateTime PostedAt, long HeaderSeq, bool IsHidden, Guid? IsMergedInto,
        Guid CounterpartyId, Guid? CounterpartyAccountId, string? CounterpartyAccountType, string? CounterpartyAccountName,
        string? Payee, string? CheckNumber, string Status, bool IsPending, bool NeedsReview, Guid? SecurityId,
        string? SecurityTicker, decimal? Quantity, decimal? UnitPrice, string? DerivedAction, int HeaderTotalPostings);

    /// <summary>One view row presented from a perspective's leg, with what the filters read.</summary>
    internal sealed record Line(
        Guid LegId, Guid HeaderId, Guid AccountId, string AccountType, string? CategoryKind, decimal Amount,
        DateTime PostedAt, bool IsHidden, Guid? IsMergedInto, Guid? CounterpartyAccountId,
        string? CounterpartyAccountType, string? Payee, string? Memo, string? HeaderMemo, IReadOnlyList<string> InPlay);

    public static async Task<NotesOracle> LoadAsync(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (
                from rt in db.ResolvedTransactions.AsNoTracking()
                join a in db.Accounts.AsNoTracking() on rt.AccountId equals a.Id
                where a.LedgerId == ledgerId
                select new Row(
                    rt.Id, rt.HeaderId, rt.AccountId, a.AccountType, a.CategoryKind, rt.Amount,
                    rt.HeaderAccountNetAmount, rt.PostedAt, rt.HeaderSeq, rt.IsHidden, rt.IsMergedInto,
                    rt.CounterpartyId, rt.CounterpartyAccountId, rt.CounterpartyAccountType, rt.CounterpartyAccountName,
                    rt.Payee, rt.CheckNumber, rt.Status, rt.IsPending, rt.NeedsReview, rt.SecurityId,
                    rt.SecurityTicker, rt.Quantity, rt.UnitPrice, rt.DerivedAction, rt.HeaderTotalPostings))
            .ToListAsync();
        var headerIds = await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId)
            .OrderBy(h => h.Id).Select(h => h.Id).ToListAsync();
        var byLeg = new Dictionary<Guid, LegNotes.Computed>();
        foreach (var chunk in headerIds.Chunk(LegNotes.BatchSize))
            foreach (var c in await LegNotes.ComputeAsync(db, chunk, CancellationToken.None))
                byLeg[c.Notes.LegId] = c;
        var tags = await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledgerId)
            .ToDictionaryAsync(t => t.Id, t => t.Name);
        // The facts as the rule builds them from the header's legs. Only the shape is
        // loaded (no notes): what is checked is the posting's other leg and the posting
        // count, against the view's own reading of the same rows.
        var legs = (await db.TxnLegs.AsNoTracking().Where(l => l.LedgerId == ledgerId)
                .Select(l => new { l.Id, l.HeaderId, l.AccountId, l.PostingIndex }).ToListAsync())
            .ToLookup(l => l.HeaderId);
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId != null)
            .ToDictionaryAsync(a => a.Id, a => a.HoldingsAccountId!.Value);
        var facts = new Dictionary<Guid, LegFacts>();
        foreach (var h in await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId)
                     .Select(h => new { h.Id, h.Action, h.OriginatingAccountId }).ToListAsync())
        {
            var input = new NotesHeaderInput(h.Id, h.Action, h.OriginatingAccountId, null, [],
                [.. legs[h.Id].Select(l => new NotesLegInput(l.Id, l.AccountId, l.PostingIndex, null, []))]);
            foreach (var f in StoredNotes.Facts(input, holdingsOf)) facts[f.LegId] = f;
        }
        return new NotesOracle(rows, byLeg, tags, facts);
    }

    /// <summary>A report reads the posting's real account's leg; with another category, its own, or the other one when grouped on it.</summary>
    public static Guid ReportLeg(Row r, bool groupedOnCounterparty)
    {
        ArgumentNullException.ThrowIfNull(r);
        var fromOther = groupedOnCounterparty
            ? r.CounterpartyAccountId is not null
            : r.CounterpartyAccountId is not null && r.CounterpartyAccountType != "category";
        return fromOther ? r.CounterpartyId : r.LegId;
    }

    /// <summary>
    /// A listing line reads the leg in scope; with nothing in scope a line between two real
    /// accounts reads the outflow leg, and for a zero amount the leg on the side the header
    /// belongs to.
    /// </summary>
    public Guid ListingLeg(Row r, ListingScope scope)
    {
        ArgumentNullException.ThrowIfNull(r);
        return scope switch
        {
            ListingScope.Account => r.LegId,
            ListingScope.Counterparty => r.CounterpartyAccountId is not null ? r.CounterpartyId : r.LegId,
            _ => r.CounterpartyAccountId is not null && r.CounterpartyAccountType != "category"
                 && (r.Amount > 0m || (r.Amount == 0m && ByLeg.TryGetValue(r.CounterpartyId, out var other) && other.Notes.GroupsByHeader))
                ? r.CounterpartyId
                : r.LegId,
        };
    }

    /// <summary>The register entry the row's leg is filed under.</summary>
    public Guid EntryKeyOf(Row r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return ByLeg[r.LegId].Notes.GroupsByHeader ? r.HeaderId : r.LegId;
    }

    /// <summary>The names of the tags <paramref name="leg"/>'s presentation holds in a role, in ordinal order.</summary>
    public IReadOnlyList<string> Names(Guid leg, Func<Coffer.Api.Db.Entities.TxnLegNoteTagRow, bool> role) =>
        ByLeg.TryGetValue(leg, out var c)
            ? [.. c.Tags.Where(role).Select(t => Tags[t.TagId]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>What a reader reads of <paramref name="r"/> presented from <paramref name="notesLegId"/>.</summary>
    public StoredNotesEquivalence.Reading Read(Row r, Guid notesLegId, bool register)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!ByLeg.TryGetValue(notesLegId, out var c))
            throw new InvalidOperationException($"The rule computed nothing for leg {notesLegId}, the perspective of row {r.LegId}.");
        var n = c.Notes;
        return new StoredNotesEquivalence.Reading
        {
            LegId = r.LegId,
            HeaderId = r.HeaderId,
            NotesLegId = notesLegId,
            EntryKey = register ? EntryKeyOf(r) : Guid.Empty,
            Kind = n.Kind,
            IsReadOnly = n.IsReadOnly,
            GroupsByHeader = n.GroupsByHeader,
            Memo = n.Memo,
            MemoInherited = n.MemoInherited,
            InheritedMemo = n.InheritedMemo,
            InheritFromAccountId = n.InheritFromAccountId,
            TagsInherited = n.TagsInherited,
            HeaderMemo = n.HeaderMemo,
            HeaderInPlay = n.HeaderInPlay,
            Shown = [.. Names(notesLegId, t => t.Shown)],
            InPlay = [.. Names(notesLegId, t => t.InPlay)],
            Inherited = [.. Names(notesLegId, t => t.Inherited)],
            Transaction = [.. Names(notesLegId, t => t.TransactionLevel)],
        };
    }

    /// <summary>Every view row presented from the leg <paramref name="perspective"/> picks, with what the filters read.</summary>
    public IEnumerable<Line> Lines(Func<Row, Guid> perspective)
    {
        ArgumentNullException.ThrowIfNull(perspective);
        foreach (var r in Rows)
        {
            var leg = perspective(r);
            var n = ByLeg[leg].Notes;
            yield return new Line(r.LegId, r.HeaderId, r.AccountId, r.AccountType, r.CategoryKind, r.Amount, r.PostedAt,
                r.IsHidden, r.IsMergedInto, r.CounterpartyAccountId, r.CounterpartyAccountType, r.Payee, n.Memo, n.HeaderMemo,
                Names(leg, t => t.InPlay));
        }
    }

    /// <summary>
    /// Where the facts the rule is computed from disagree with the view's own: each leg's
    /// other leg (the same posting's, the lowest id when there were ever more than two)
    /// against the view row's <c>counterparty_id</c>, and the header's distinct posting
    /// count against its <c>header_total_postings</c>; and every view row's leg must have
    /// a computed presentation, one row each.
    /// </summary>
    public IEnumerable<string> FactDifferences()
    {
        foreach (var dup in Rows.GroupBy(r => r.LegId).Where(g => g.Count() > 1))
            yield return $"facts leg {dup.Key}: {dup.Count()} view rows";
        foreach (var r in Rows)
        {
            if (!ByLeg.ContainsKey(r.LegId)) yield return $"facts leg {r.LegId}: the rule computed nothing";
            if (!_facts.TryGetValue(r.LegId, out var f)) { yield return $"facts leg {r.LegId}: the rule built no facts for it"; continue; }
            if ((f.OtherLegId ?? Guid.Empty) != r.CounterpartyId)
                yield return $"facts leg {r.LegId}: the rule's other leg {f.OtherLegId} is not the view's counterparty {r.CounterpartyId}";
            if (f.OtherAccountId != r.CounterpartyAccountId)
                yield return $"facts leg {r.LegId}: the rule's other account {f.OtherAccountId} is not the view's {r.CounterpartyAccountId}";
            if (f.HeaderTotalPostings != r.HeaderTotalPostings)
                yield return $"facts leg {r.LegId}: the rule counts {f.HeaderTotalPostings} postings, the view {r.HeaderTotalPostings}";
        }
    }
}
