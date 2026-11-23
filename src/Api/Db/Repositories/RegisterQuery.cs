using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The register's filter and its keyset page of entries, in LINQ over
/// <see cref="StoredNotesQuery.RegisterLegs"/>: every memo and tag a filter reads is the
/// one <see cref="MemoTagRules"/> presented when it was stored, and every entry is keyed by
/// <see cref="LegPresentation.GroupsByHeader"/>.
/// </summary>
/// <remarks>
/// <para><see cref="Filtered"/> is the one definition of "a register leg the active
/// filter keeps" (ADR-0076): the page, the anchor check, the scroll rail, the status
/// counts and select-all all read it, and so do the review count on <c>GET /accounts</c>
/// (through <see cref="FilteredOnEveryAccount"/>, every account's needs-review entries in
/// one statement) and the ledger-wide entries (<see cref="FirstEntryKeysOnAccountsAsync"/>),
/// so none of them can disagree about a row.</para>
/// <para>Two readings of a filter follow the rule rather than the stored placement: a
/// tag filter matches the leg's in-play tags (a split-side line counts the header's
/// tags with its own; every other leg counts what it shows, inherited tags included),
/// and a text search matches the memo the leg shows, and on the split side and an
/// event's investment side also the transaction's memo.</para>
/// </remarks>
internal static class RegisterQuery
{
    /// <summary>
    /// The legs in <paramref name="scope"/> (one account, a category and its
    /// descendants, or every account the ledger-wide entries read) on live, unmerged
    /// headers that <paramref name="filter"/> keeps;
    /// <paramref name="ledgerId"/> is the ledger whose tags a tag filter names.
    /// <paramref name="hidden"/> picks one visibility side; null keeps both, for a
    /// caller that scopes visibility itself. <paramref name="today"/> is the caller's
    /// date for the status views.
    /// </summary>
    internal static IQueryable<RegisterLeg> Filtered(
        AppDbContext db, Guid[] scope, Guid ledgerId, bool? hidden, RegisterFilter filter, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(filter);
        return Narrowed(db, InScope(db, scope, hidden), ledgerId, filter, today);
    }

    /// <summary>
    /// <see cref="Filtered"/> over the legs of every account rather than a scope, for a
    /// reader that ties the legs to each account itself: a per-account count correlated
    /// with the account it counts for, which reads every account's register in one
    /// statement.
    /// </summary>
    internal static IQueryable<RegisterLeg> FilteredOnEveryAccount(
        AppDbContext db, Guid ledgerId, bool? hidden, RegisterFilter filter, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(filter);
        return Narrowed(db, Live(db, hidden), ledgerId, filter, today);
    }

    /// <summary>The filter's narrowing of <paramref name="q"/>, the one body <see cref="Filtered"/> and <see cref="FilteredOnEveryAccount"/> share.</summary>
    private static IQueryable<RegisterLeg> Narrowed(
        AppDbContext db, IQueryable<RegisterLeg> q, Guid ledgerId, RegisterFilter filter, DateOnly today)
    {
        if (filter.DateFrom is { } from) q = q.Where(l => DateOnly.FromDateTime(l.Row.PostedAt) >= from);
        if (filter.DateTo is { } to) q = q.Where(l => DateOnly.FromDateTime(l.Row.PostedAt) <= to);
        var shown = ShownAmount(db);
        // The magnitude of the amount the entry's row shows (ADR-0108 D2). A row that
        // shows no figure is in no range.
        if (filter.AmountMin is { } min)
        {
            Expression<Func<RegisterLeg, bool>> atLeast = l => Math.Abs(shown.Inline(l)!.Value) >= min;
            q = q.Where(atLeast.Expand());
        }
        if (filter.AmountMax is { } max)
        {
            Expression<Func<RegisterLeg, bool>> atMost = l => Math.Abs(shown.Inline(l)!.Value) <= max;
            q = q.Where(atMost.Expand());
        }
        if (filter.SecurityId is { } securityId) q = q.Where(l => l.Row.SecurityId == securityId);
        if (filter.CategoryId is { } categoryId) q = q.Where(l => l.Row.CounterpartyAccountId == categoryId);

        if (filter.Tag is { } tag)
            q = q.Where(StoredNotesQuery.HasInPlayTag(db, t => t.LedgerId == ledgerId && t.Name == tag));

        if (filter.Search is { } search)
        {
            // The search text is a pattern, as it always was: % and _ match any
            // run and any character, and a backslash (PostgreSQL's default escape,
            // named here because EF otherwise writes ESCAPE '') escapes the next.
            var pattern = "%" + search + "%";
            const string escape = "\\";
            var tagMatches = StoredNotesQuery.HasInPlayTag(db,
                t => t.LedgerId == ledgerId && EF.Functions.ILike(t.Name, pattern, escape));
            Expression<Func<RegisterLeg, bool>> matches = l =>
                EF.Functions.ILike(l.Row.Payee!, pattern, escape)
                || EF.Functions.ILike(l.Shown.Memo!, pattern, escape)
                || EF.Functions.ILike(l.Shown.InPlayHeaderMemo!, pattern, escape)
                || EF.Functions.ILike(l.Row.CheckNumber!, pattern, escape)
                || EF.Functions.ILike(l.Row.CounterpartyAccountName!, pattern, escape)
                || tagMatches.Inline(l);
            q = q.Where(matches.Expand());
        }

        if (filter.Status is { } status)
            q = status switch
            {
                "needs_review" => q.Where(l => l.Row.NeedsReview),
                "scheduled" => q.Where(l => DateOnly.FromDateTime(l.Row.PostedAt) > today),
                "cleared" or "uncleared" or "reconciling" => q.Where(l =>
                    l.Row.Status == status && !l.Row.IsPending && DateOnly.FromDateTime(l.Row.PostedAt) <= today),
                // Endpoint validation refuses any other value; one that got past it
                // matches nothing rather than everything.
                _ => q.Where(_ => false),
            };

        // The sign of the amount the entry's row shows, since a filter matches only what
        // the row shows (ADR-0108 D2).
        Expression<Func<RegisterLeg, bool>>? flows = filter.Direction switch
        {
            TransactionDirection.Inflow => l => shown.Inline(l) > 0m,
            TransactionDirection.Outflow => l => shown.Inline(l) < 0m,
            _ => null,
        };
        if (flows is not null) q = q.Where(flows.Expand());

        return q;
    }

    /// <summary>
    /// The amount an entry's row shows on its account, read off any of its legs: for an
    /// entry keyed by its header, the header's net on the account, which a split group is
    /// drawn with; for an entry keyed by its leg, that one leg's own amount. The two
    /// differ when a transaction has several lines on an account it does not belong to:
    /// each line is a row there, drawn with its own amount, and their signs can differ.
    /// The direction, the amount range and the amount sort all read it, so each matches
    /// and orders by what the row shows (ADR-0108 D2).
    /// <para>Null for an entry keyed by its header on a category whose lines are paid in
    /// more than one currency: its row shows the mixed marker, not the stored net, which
    /// adds those lines as they are, and no figure adds currencies (ADR-0114 D7). So no
    /// direction or amount range matches it, and the amount sort reads it as absent.</para>
    /// </summary>
    private static Expression<Func<RegisterLeg, decimal?>> ShownAmount(AppDbContext db)
    {
        var mixed = PaidInSeveralCurrencies(db);
        Expression<Func<RegisterLeg, decimal?>> shown = l =>
            !l.Shown.GroupsByHeader ? l.Row.Amount
            : l.Row.AccountType == "category" && mixed.Inline(l) ? null
            : l.Row.HeaderAccountNetAmount ?? l.Row.Amount;
        return shown.Expand();
    }

    /// <summary>
    /// Whether the lines of <c>l</c>'s header on its account are in more than one
    /// currency, each in the currency a category leg's money is in: the real account at
    /// the same posting index, else the account's own code. The rule
    /// <see cref="RegisterMoney"/> names a row's currency by and the category totals sum
    /// by. Read for a category only: a real account's lines are all in its currency.
    /// </summary>
    private static Expression<Func<RegisterLeg, bool>> PaidInSeveralCurrencies(AppDbContext db) => l =>
        db.TxnLegs
            .Where(o => o.HeaderId == l.Row.HeaderId && o.AccountId == l.Row.AccountId)
            .Select(o => db.TxnLegs
                             .Where(p => p.HeaderId == o.HeaderId && p.PostingIndex == o.PostingIndex && p.Id != o.Id)
                             .Join(db.Accounts, p => p.AccountId, pa => pa.Id, (p, pa) => pa)
                             .Where(pa => pa.AccountType != "category")
                             .Select(pa => pa.CurrencyCode)
                             .FirstOrDefault()
                         ?? db.Accounts.Where(a => a.Id == o.AccountId).Select(a => a.CurrencyCode).FirstOrDefault())
            .Distinct()
            .Count() > 1;

    /// <summary>
    /// One keyset page of entry keys, in display order: <paramref name="limit"/>
    /// entries strictly past the cursor entry (<paramref name="cursorEntryKey"/>,
    /// <paramref name="cursorSeq"/>), older or smaller first for
    /// <see cref="RegisterRepository.DirectionBefore"/> under a descending sort, the
    /// reverse for <see cref="RegisterRepository.DirectionAfter"/>. No cursor: the first
    /// page.
    /// </summary>
    /// <remarks>
    /// An entry sorts by its value in <paramref name="sort"/>'s column (the largest over
    /// its legs; text and numbers absent on every leg sort as empty and zero), then by
    /// its header's sequence, then by its key, so the order is total and the keyset a
    /// plain row comparison. The cursor entry's sort value is read back from its key,
    /// so a cursor carries no sort column. An unknown column sorts by date.
    /// </remarks>
    internal static Task<List<RegisterEntryKeyRow>> EntryKeysAsync(
        AppDbContext db,
        Guid[] scope,
        Guid ledgerId,
        Guid? cursorEntryKey,
        long? cursorSeq,
        string direction,
        int limit,
        bool hidden,
        RegisterFilter filter,
        DateOnly today,
        RegisterSort sort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var page = new PageRequest(
            Filtered(db, scope, ledgerId, hidden, filter, today),
            InScope(db, scope, hidden),
            cursorEntryKey is { } key && cursorSeq is { } seq ? (key, seq) : null,
            // Fetched away from the cursor, then put back in display order.
            FetchDescending: direction == RegisterRepository.DirectionAfter ? !sort.Descending : sort.Descending,
            Reverse: direction == RegisterRepository.DirectionAfter,
            limit);
        return RunSortedAsync(db, page, sort, cancellationToken);
    }

    /// <summary>
    /// The newest <paramref name="limit"/> entries across the accounts in
    /// <paramref name="scope"/> that <paramref name="filter"/> keeps, on the visible side:
    /// each one account's entry, keyed as that account's register keys it, in the
    /// register's date order (<see cref="RegisterSort.Default"/>), then by account.
    /// </summary>
    /// <remarks>
    /// An entry belongs to one account, and across several accounts one key can name an
    /// entry on more than one of them: an in-kind share transfer is keyed by its header
    /// on both brokerages, each of them the event's investment side. So the entries are
    /// grouped by account and key, where <see cref="EntryKeysAsync"/> groups one
    /// register's legs by key alone. There is no cursor: this is the first page only.
    /// <para>This reads every leg of the scope that the filter keeps before it cuts the
    /// page; <see cref="FirstEntryKeysOnAccountsByHeaderAsync"/> reads the same page
    /// from the newest headers only.</para>
    /// </remarks>
    internal static Task<List<AccountEntryKeyRow>> FirstEntryKeysOnAccountsAsync(
        AppDbContext db,
        Guid[] scope,
        Guid ledgerId,
        int limit,
        RegisterFilter filter,
        DateOnly today,
        CancellationToken cancellationToken) =>
        FirstEntryKeysOnAccountsAsync(Filtered(db, scope, ledgerId, hidden: false, filter, today), limit, cancellationToken);

    /// <summary>
    /// <see cref="FirstEntryKeysOnAccountsAsync(AppDbContext, Guid[], Guid, int, RegisterFilter, DateOnly, CancellationToken)"/>,
    /// read in two steps: the ledger's headers newest first (<see cref="CandidateHeaders"/>),
    /// then the entries of those headers alone, through the same filter and key query.
    /// The same entries in the same order; what differs is what is read.
    /// </summary>
    /// <remarks>
    /// <para>The entries are ordered by their header's date and sequence first, and the
    /// candidates come in that order, so every entry of a later header sorts after every
    /// entry of an earlier one: once the headers read so far hold
    /// <paramref name="limit"/> entries, no header past them can hold one that belongs on
    /// the page.</para>
    /// <para>A header can hold no entry here (one on categories alone, or one whose every
    /// entry the filter leaves out) or several, so the headers are read in batches, each
    /// twice the size of the one before and starting past it, until the page is full or
    /// the headers run out.</para>
    /// </remarks>
    internal static async Task<List<AccountEntryKeyRow>> FirstEntryKeysOnAccountsByHeaderAsync(
        AppDbContext db,
        Guid[] scope,
        Guid ledgerId,
        int limit,
        RegisterFilter filter,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var keys = new List<AccountEntryKeyRow>(limit);
        var batch = limit;
        (DateTime PostedAt, long Seq)? past = null;
        while (keys.Count < limit)
        {
            var headers = CandidateHeaders(db, ledgerId, filter.DateTo);
            if (past is var (postedAt, seq))
                headers = headers.Where(h => EF.Functions.LessThan(
                    new ValueTuple<DateTime, long>(h.PostedAt, h.Seq),
                    new ValueTuple<DateTime, long>(postedAt, seq)));
            var candidates = await headers
                .OrderByDescending(h => h.PostedAt)
                .ThenByDescending(h => h.Seq)
                .Take(batch)
                .Select(h => new { h.Id, h.PostedAt, h.Seq })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0) break;

            Guid[] ids = [.. candidates.Select(h => h.Id)];
            keys.AddRange(await FirstEntryKeysOnAccountsAsync(
                    Filtered(db, scope, ledgerId, hidden: false, filter, today).Where(l => ids.Contains(l.Row.HeaderId)),
                    limit - keys.Count, cancellationToken)
                .ConfigureAwait(false));
            if (candidates.Count < batch) break;
            past = (candidates[^1].PostedAt, candidates[^1].Seq);
            batch *= 2;
        }
        return keys;
    }

    /// <summary>
    /// The headers of <paramref name="ledgerId"/> that can hold an entry on a register's
    /// visible side: not a template (the view reads none), not hidden, not merged away,
    /// and, with <paramref name="through"/>, not dated after it, the cut
    /// <see cref="RegisterFilter.DateTo"/> makes. Its first conditions are the predicate
    /// of <c>idx_txn_headers_ledger_posted_seq</c>, which serves them newest first.
    /// The balance forecast reads its committed rows through it too, so the two agree
    /// about which rows a register shows.
    /// </summary>
    internal static IQueryable<TxnHeaderRow> CandidateHeaders(AppDbContext db, Guid ledgerId, DateOnly? through)
    {
        var q = db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && !h.IsHidden && h.IsMergedInto == null && !h.IsRecurringTemplate);
        if (through is { } to)
        {
            var scheduledFrom = ScheduledBoundary.ScheduledFrom(to);
            q = q.Where(h => h.PostedAt < scheduledFrom);
        }
        return q;
    }

    private static Task<List<AccountEntryKeyRow>> FirstEntryKeysOnAccountsAsync(
        IQueryable<RegisterLeg> filtered, int limit, CancellationToken cancellationToken) =>
        filtered
            .GroupBy(l => new { l.Row.AccountId, l.EntryKey })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.EntryKey,
                PostedAt = g.Max(l => l.Row.PostedAt),
                Seq = g.Max(l => l.Row.HeaderSeq),
            })
            .OrderByDescending(e => e.PostedAt)
            .ThenByDescending(e => e.Seq)
            .ThenByDescending(e => e.EntryKey)
            .ThenByDescending(e => e.AccountId)
            .Take(limit)
            .Select(e => new AccountEntryKeyRow
            {
                AccountId = e.AccountId,
                PostedAt = e.PostedAt,
                Seq = e.Seq,
                EntryKey = e.EntryKey,
            })
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The first entry of <paramref name="headerId"/> among <paramref name="legs"/> in
    /// <paramref name="sort"/>'s display order, or null when none of its legs is there:
    /// where a page anchored on that header starts. A side that groups by header has
    /// one entry for it; an opposite side has one per line, and the page starts at the
    /// first, with the others following it.
    /// </summary>
    internal static async Task<RegisterEntryKeyRow?> FirstEntryOfHeaderAsync(
        AppDbContext db, IQueryable<RegisterLeg> legs, Guid headerId, RegisterSort sort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var page = new PageRequest(
            legs.Where(l => l.Row.HeaderId == headerId), legs, Cursor: null,
            FetchDescending: sort.Descending, Reverse: false, Limit: 1);
        return (await RunSortedAsync(db, page, sort, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>
    /// Every entry of <paramref name="headerId"/> among <paramref name="legs"/>, in
    /// <paramref name="sort"/>'s display order: what a page shows of that header.
    /// </summary>
    internal static Task<List<RegisterEntryKeyRow>> EntriesOfHeaderAsync(
        AppDbContext db, IQueryable<RegisterLeg> legs, Guid headerId, RegisterSort sort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var page = new PageRequest(
            legs.Where(l => l.Row.HeaderId == headerId), legs, Cursor: null,
            FetchDescending: sort.Descending, Reverse: false, Limit: int.MaxValue);
        return RunSortedAsync(db, page, sort, cancellationToken);
    }

    /// <summary>
    /// Every entry of the headers in <paramref name="headerIds"/> among
    /// <paramref name="legs"/>, in <paramref name="sort"/>'s display order: what a page
    /// shows of each of them.
    /// </summary>
    internal static Task<List<RegisterEntryKeyRow>> EntriesOfHeadersAsync(
        AppDbContext db, IQueryable<RegisterLeg> legs, Guid[] headerIds, RegisterSort sort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headerIds);
        ArgumentNullException.ThrowIfNull(sort);
        var page = new PageRequest(
            legs.Where(l => EF.Constant(headerIds).Contains(l.Row.HeaderId)), legs, Cursor: null,
            FetchDescending: sort.Descending, Reverse: false, Limit: int.MaxValue);
        return RunSortedAsync(db, page, sort, cancellationToken);
    }

    private static Task<List<RegisterEntryKeyRow>> RunSortedAsync(
        AppDbContext db, PageRequest page, RegisterSort sort, CancellationToken cancellationToken)
    {
        var shown = ShownAmount(db);
        return sort.Column switch
        {
            // An entry keyed by its leg has one leg, and every leg of one keyed by its
            // header carries the same net, so the largest is the amount the row shows;
            // a row that shows none sorts as zero.
            "amount" => page.RunAsync(
                g => g.Max(l => shown.Inline(l)) ?? 0m,
                q => q.Max(l => shown.Inline(l)) ?? 0m, cancellationToken),
            "payee" => page.RunAsync(
                g => g.Max(l => l.Row.Payee) ?? "",
                q => q.Max(l => l.Row.Payee) ?? "", cancellationToken),
            "category" => page.RunAsync(
                g => g.Max(l => l.Row.CounterpartyAccountName) ?? "",
                q => q.Max(l => l.Row.CounterpartyAccountName) ?? "", cancellationToken),
            "security" => page.RunAsync(
                g => g.Max(l => l.Row.SecurityTicker) ?? "",
                q => q.Max(l => l.Row.SecurityTicker) ?? "", cancellationToken),
            "shares" => page.RunAsync(
                g => g.Max(l => l.Row.Quantity) ?? 0m,
                q => q.Max(l => l.Row.Quantity) ?? 0m, cancellationToken),
            "price" => page.RunAsync(
                g => g.Max(l => l.Row.UnitPrice) ?? 0m,
                q => q.Max(l => l.Row.UnitPrice) ?? 0m, cancellationToken),
            // By the word the row's chip shows (ADR-0028), not the stored catalog value:
            // the two orders differ (Reinvest is dividend_reinvest), and the register
            // loads in pages, so only the server can order the whole list.
            "action" => page.RunAsync(
                g => g.Max(l => InvestmentActionWords.WordOf.Inline(l.Row.DerivedAction)) ?? "",
                q => q.Max(l => InvestmentActionWords.WordOf.Inline(l.Row.DerivedAction)) ?? "", cancellationToken),
            _ => page.RunAsync(
                g => g.Max(l => l.Row.PostedAt),
                q => q.Max(l => l.Row.PostedAt), cancellationToken),
        };
    }

    /// <summary>
    /// The scope and visibility every register read starts from, with no filter: the
    /// named accounts (a register is one account's, or a category's with its
    /// descendants), which need no ledger.
    /// </summary>
    internal static IQueryable<RegisterLeg> InScope(AppDbContext db, Guid[] scope, bool? hidden)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Live(db, hidden).Where(l => scope.Contains(l.Row.AccountId));
    }

    private static IQueryable<RegisterLeg> Live(AppDbContext db, bool? hidden)
    {
        var q = StoredNotesQuery.RegisterLegs(db).Where(l => l.Row.IsMergedInto == null);
        return hidden is { } h ? q.Where(l => l.Row.IsHidden == h) : q;
    }

    /// <summary>An entry with the values it sorts by.</summary>
    private sealed class SortedEntry<TSort>
    {
        public Guid EntryKey { get; init; }
        public DateTime PostedAt { get; init; }
        public long Seq { get; init; }
        public TSort SortValue { get; init; } = default!;
    }

    private sealed record PageRequest(
        IQueryable<RegisterLeg> Filtered,
        IQueryable<RegisterLeg> Unfiltered,
        (Guid Key, long Seq)? Cursor,
        bool FetchDescending,
        bool Reverse,
        int Limit)
    {
        /// <param name="entrySort">An entry's sort value over its legs.</param>
        /// <param name="cursorSort">The same value over the cursor entry's legs, as a scalar subquery.</param>
        public async Task<List<RegisterEntryKeyRow>> RunAsync<TSort>(
            Expression<Func<IGrouping<Guid, RegisterLeg>, TSort>> entrySort,
            Expression<Func<IQueryable<RegisterLeg>, TSort>> cursorSort,
            CancellationToken cancellationToken)
        {
            Expression<Func<IGrouping<Guid, RegisterLeg>, SortedEntry<TSort>>> entry = g => new SortedEntry<TSort>
            {
                EntryKey = g.Key,
                PostedAt = g.Max(l => l.Row.PostedAt),
                Seq = g.Max(l => l.Row.HeaderSeq),
                SortValue = entrySort.Inline(g),
            };
            var entries = Filtered.GroupBy(l => l.EntryKey).Select(entry.Expand());

            if (Cursor is var (key, seq))
            {
                // The cursor entry's own legs: the same scope and visibility, no
                // filter (the entry it names was on the previous page). Matching the
                // header or leg id first keeps the lookup on an index; the entry key
                // then picks exactly that entry's legs. A key can stop naming an
                // entry while the cursor is held, when the header changes hands or
                // the cursor predates how entries are keyed; its header's or leg's
                // legs then give the position, so the scroll goes on past it rather
                // than ending.
                // The two matches are read apart and put together: one OR over the header
                // and the leg is a filter over every leg of the scope, each alone an index lookup.
                var named = Unfiltered.Where(l => l.Row.HeaderId == key)
                    .Concat(Unfiltered.Where(l => l.Row.Id == key && l.Row.HeaderId != key));
                var exact = named.Where(l => l.EntryKey == key);
                Expression<Func<SortedEntry<TSort>, bool>> past = FetchDescending
                    ? e => EF.Functions.LessThan(
                        new ValueTuple<TSort, long, Guid>(e.SortValue, e.Seq, e.EntryKey),
                        new ValueTuple<TSort, long, Guid>(
                            exact.Any() ? cursorSort.Inline(exact) : cursorSort.Inline(named), seq, key))
                    : e => EF.Functions.GreaterThan(
                        new ValueTuple<TSort, long, Guid>(e.SortValue, e.Seq, e.EntryKey),
                        new ValueTuple<TSort, long, Guid>(
                            exact.Any() ? cursorSort.Inline(exact) : cursorSort.Inline(named), seq, key));
                entries = entries.Where(past.Expand());
            }

            var ordered = FetchDescending
                ? entries.OrderByDescending(e => e.SortValue).ThenByDescending(e => e.Seq).ThenByDescending(e => e.EntryKey)
                : entries.OrderBy(e => e.SortValue).ThenBy(e => e.Seq).ThenBy(e => e.EntryKey);

            var rows = await ordered
                .Take(Limit)
                .Select(e => new RegisterEntryKeyRow { PostedAt = e.PostedAt, Seq = e.Seq, EntryKey = e.EntryKey })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Reverse) rows.Reverse();
            return rows;
        }
    }
}
