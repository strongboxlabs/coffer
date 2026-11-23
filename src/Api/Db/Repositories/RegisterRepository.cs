using System.Linq.Expressions;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Server-side register filter (mig 164). Every field is optional; a null
/// field is a no-op predicate. Pushed into the entry query
/// (<see cref="RegisterQuery"/>) so the
/// windowed keyset cursor walks only matching entries (client-side filtering
/// can't — it only sees the loaded window). <see cref="Status"/> folds the
/// former client-side status tabs server-side: <c>cleared</c> / <c>uncleared</c>
/// / <c>scheduled</c> / <c>needs_review</c> (null = all; the Hidden view uses
/// the separate <c>hidden</c> flag). <see cref="Today"/> is the caller's LOCAL
/// calendar date, so "scheduled" (posted after today) matches the user's date,
/// not the server's UTC one.
/// </summary>
public sealed record RegisterFilter(
    string? Search = null,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    /// <summary>
    /// The least magnitude, inclusive, of the amount the entry's row shows on its
    /// account: the header's net there for an entry keyed by its header, the leg's own
    /// amount for an entry keyed by its leg, as <see cref="Direction"/> reads it. The
    /// amount sort orders by the same amount.
    /// </summary>
    decimal? AmountMin = null,
    /// <summary>The greatest magnitude, inclusive, of that same amount.</summary>
    decimal? AmountMax = null,
    Guid? SecurityId = null,
    string? Tag = null,
    Guid? CategoryId = null,
    string? Status = null,
    DateOnly? Today = null,
    /// <summary>
    /// Include the account's DESCENDANTS as well as the account itself. Only
    /// meaningful for a category, which is the only account type with a tree.
    /// </summary>
    /// <remarks>
    /// A parent category's own register is empty — every posting sits on its
    /// children — so without this, opening a parent category shows nothing while
    /// the rest of the app reports a substantial total against it. Off by default: a plain
    /// account register must keep planning and paging exactly as it did.
    /// </remarks>
    bool IncludeSubcategories = false,
    /// <summary>
    /// Cash-flow direction, by the sign of the amount the entry's row shows on its
    /// account: the header's net there for an entry keyed by its header, the leg's own
    /// amount for an entry keyed by its leg. Inflow above zero, outflow below it, a zero
    /// amount in neither. Not the page's scroll direction
    /// (<see cref="RegisterRepository.DirectionBefore"/>). Only the ledger-wide entries
    /// (<see cref="RegisterRepository.GetLedgerPageAsync"/>) set it; the register
    /// endpoints never do.
    /// </summary>
    TransactionDirection Direction = TransactionDirection.All)
{
    public static readonly RegisterFilter None = new();

    /// <summary>True when any dimension narrows the result (Today alone is
    /// context, not a narrowing filter).</summary>
    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Search)
        || DateFrom is not null || DateTo is not null
        || AmountMin is not null || AmountMax is not null
        || SecurityId is not null || Tag is not null || CategoryId is not null
        || !string.IsNullOrWhiteSpace(Status)
        || Direction != TransactionDirection.All
        // Widening to the subtree changes which entries exist, so the scroll
        // rail's buckets must be rebuilt rather than reused from the narrow
        // view. That is a CACHE-KEY concern, not a UI one: the web deliberately
        // leaves this dimension out of its own "is a filter active" test, since
        // it is a scope (which register you are reading) rather than a filter
        // narrowing one, and it carries a persistent toggle instead of a chip.
        || IncludeSubcategories;

    /// <summary>Compact, stable key for cache-keying the scroll-track buckets
    /// per filter (the date rail must re-derive when the filter narrows the
    /// set). Excludes Today — status intent already varies the key. A direction
    /// adds one part only when set, so every key without one is unchanged.</summary>
    public string Fingerprint =>
        IsActive
            ? string.Join('|', IncludeSubcategories ? "sub" : null,
                Search, DateFrom, DateTo, AmountMin, AmountMax,
                SecurityId, Tag, CategoryId, Status)
              + (Direction == TransactionDirection.All ? string.Empty : "|" + Direction)
            : string.Empty;
}

/// <summary>
/// Register display ordering (mig 166). <see cref="Column"/> is one of the
/// whitelisted sort dimensions — <c>date</c> / <c>amount</c> / <c>payee</c> /
/// <c>category</c> on any register, plus <c>security</c> / <c>shares</c> /
/// <c>price</c> / <c>action</c> on investment registers — and the entry query
/// falls back to <c>date</c> for anything else. <see cref="Descending"/>
/// defaults true (newest / largest first), matching the pre-sort behavior.
/// Sort is display-order only: it never affects counts or select-all (both are
/// order-independent), so it lives on this windowed read path alone.
/// </summary>
public sealed record RegisterSort(string Column, bool Descending)
{
    public static readonly RegisterSort Default = new("date", Descending: true);

    /// <summary>The direction's wire spelling, <c>asc</c> or <c>desc</c>.</summary>
    public string Dir => Descending ? "desc" : "asc";
}

/// <summary>
/// Per-status entry counts for one account's register, respecting the active
/// NON-status filter (search / date / amount / category / tag / security).
/// Drives the status dropdown's count badges. Counts are per entry, as the page
/// groups rows — the same unit as the scroll-rail buckets and the "N matches"
/// chip — so the numbers reconcile with each other and with the rows.
/// `needs_review` overlaps the recon buckets (it's the bank-feed flag, a
/// separate dimension), so the buckets don't sum to All.
/// </summary>
public sealed record RegisterStatusCounts(
    int All,
    int Cleared,
    int Uncleared,
    int Reconciling,
    int Scheduled,
    int NeedsReview,
    int Hidden)
{
    public static readonly RegisterStatusCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Register-query gateway over <c>resolved_transactions</c> (migration
/// 005, extended by migration 018). Paginates by <em>entry</em> rather
/// than by row — an entry is either a single transaction or a multi-
/// split group (ADR-0019). A page of <c>limit</c> entries contains
/// every leg of every group it touches, so user-facing pagination
/// never slices a group across pages.
/// </summary>
/// <remarks>
/// <para>The two queries that drive this — entry-key paging and row
/// fetching — are LINQ over <see cref="RegisterQuery"/>, which filters and
/// keys every leg through the memo-and-tags rule
/// (<see cref="Coffer.Domain.Annotations.MemoTagRules"/>). This file adds the
/// typed input, the opaque-cursor codec, and the assembly of the flat row
/// stream into <see cref="RegisterEntryDto"/> values.</para>
///
/// <para>The cursor returned in <see cref="RegisterPage.NextCursor"/>
/// is an opaque base64url-encoded JSON string carrying
/// <c>(posted_at, created_at, entry_key)</c> of the last entry on
/// the current page. Clients hand it back unchanged on the next
/// request. <c>created_at</c> is the secondary sort key — it
/// tiebreaks entries that share a <c>posted_at</c> so newly created
/// transactions sort above older same-day rows (migration 029).</para>
/// </remarks>
public sealed class RegisterRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The account scope for a register query: the account and — when the
    /// filter asks — its descendants. Every register is one account's; the
    /// ledger-wide entries (<see cref="GetLedgerPageAsync"/>) are many registers'
    /// first entries read together, not a register of the ledger.
    /// </summary>
    /// <remarks>
    /// Migration 226 widened the register functions from one account id to a
    /// SET, so that a rollup CATEGORY can show its descendants' entries: a
    /// parent category's own register is empty, because all of its money sits
    /// on its children.
    /// </remarks>
    /// <remarks>
    /// The walk itself is <see cref="CategoryTree.DescendantsAsync"/> — shared
    /// with the budget's expanded row and the reparent guard, which needed the
    /// same answer. The round trip happens only when the toggle is on.
    /// </remarks>
    private async Task<Guid[]> ResolveScopeAsync(
        Guid accountId, Guid ledgerId, RegisterFilter filter,
        CancellationToken cancellationToken)
    {
        if (!filter.IncludeSubcategories) return [accountId];

        var scope = await CategoryTree
            .DescendantsAsync(_db, ledgerId, accountId, includeRoot: true, cancellationToken)
            .ConfigureAwait(false);
        return [.. scope];
    }

    public RegisterRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Direction parameter for keyset pagination. <c>"before"</c>
    /// returns entries strictly older than the cursor (the
    /// downward / older scroll); <c>"after"</c> returns entries
    /// strictly newer than the cursor (the upward / newer scroll).
    /// The SPA sends these strings as they are.
    /// </summary>
    public const string DirectionBefore = "before";
    public const string DirectionAfter = "after";

    /// <summary>
    /// Fetch one page of resolved-transaction entries of
    /// <paramref name="accountId"/>'s register in <paramref name="ledgerId"/>
    /// (with its descendants when the filter asks).
    ///
    /// <para>Three call shapes:</para>
    /// <list type="bullet">
    ///   <item>
    ///     <description><c>cursor=null, startingAtHeaderId=null</c> →
    ///     return the most-recent <paramref name="limit"/> entries
    ///     (canonical first-page load).</description>
    ///   </item>
    ///   <item>
    ///     <description><c>cursor=X, direction='before' | 'after'</c>
    ///     → return <paramref name="limit"/> entries strictly older
    ///     (or newer) than the cursor. Used for sliding-window
    ///     prepend / append loads as the user scrolls past either
    ///     edge.</description>
    ///   </item>
    ///   <item>
    ///     <description><c>startingAtHeaderId=Y</c> → resolve that
    ///     header to its first entry in display order, return a page
    ///     anchored at the focused entry (focused entry at index 0,
    ///     <paramref name="limit"/>-1 entries strictly past it after
    ///     it). Used by the "Show other side" navigation arrival
    ///     path.</description>
    ///   </item>
    /// </list>
    ///
    /// Rows that are merged away or user-hidden are filtered out
    /// (by <see cref="RegisterQuery"/>).
    /// </summary>
    public async Task<RegisterPage> GetPageAsync(
        Guid ledgerId,
        Guid accountId,
        string? cursor,
        string direction,
        Guid? startingAtHeaderId,
        int limit,
        bool hidden,
        RegisterFilter? filter = null,
        RegisterSort? sort = null,
        CancellationToken cancellationToken = default)
    {
        var f = filter ?? RegisterFilter.None;
        // Resolved ONCE, before anything queries, because three separate
        // places have to agree about which accounts are in view: the cursor
        // resolution, the entry-key window, and the row fetch. They did not,
        // and the disagreement was silent — see the row fetch below.
        var scope = await ResolveScopeAsync(accountId, ledgerId, f, cancellationToken)
            .ConfigureAwait(false);
        var s = sort ?? RegisterSort.Default;
        // Three branches feed the same Q1+Q2 pipeline below:
        //   * starting_at: resolve the focused header's entry cursor;
        //     pin the result to direction='before' so older entries
        //     follow the focused row. The focused entry itself is
        //     prepended to the result manually since 'before' is
        //     STRICTLY less than the cursor.
        //   * cursor + direction: keyset pagination in the chosen
        //     direction. Cursor is exclusive in both directions.
        //   * empty: most-recent page (no cursor, direction='before').
        RegisterCursor? reference;
        var includeAnchorEntry = false;
        if (startingAtHeaderId is { } anchorHeaderId)
        {
            // The header's first entry in display order: its one entry on a side
            // that groups by header, its first line on an opposite side (the other
            // lines follow it on the page).
            var anchor = await RegisterQuery.FirstEntryOfHeaderAsync(
                    _db, RegisterQuery.InScope(_db, scope, hidden), anchorHeaderId, s, cancellationToken)
                .ConfigureAwait(false);
            // Header gone / hidden / out-of-scope ⇒ empty page (unchanged).
            if (anchor is null)
                return new RegisterPage([], CursorForOlder: null, CursorForNewer: null);
            // The header exists — pin it ONLY if it matches the active filter
            // (ADR-0076), and then at its first entry that matches. If the filter
            // excludes it, drop the anchor and fall through to the most-recent
            // FILTERED page: a stale ?focus= under a filter, or a post-save edit that
            // moved the row out of the filter, shows the filtered list — not the
            // non-matching row, not an empty page.
            var pinned = f.IsActive
                ? await RegisterQuery.FirstEntryOfHeaderAsync(
                        _db, RegisterQuery.Filtered(_db, scope, ledgerId, hidden, f, TodayOf(f)),
                        anchorHeaderId, s, cancellationToken)
                    .ConfigureAwait(false)
                : anchor;
            if (pinned is not null)
            {
                reference = new RegisterCursor { PostedAt = pinned.PostedAt, Seq = pinned.Seq, EntryKey = pinned.EntryKey };
                includeAnchorEntry = true;
                direction = DirectionBefore;
            }
            else
            {
                reference = null;
            }
        }
        else
        {
            reference = DecodeCursor(cursor);
        }

        // Q1 — fetch limit+1 entry keys (limit when anchoring) to
        // detect "more available in the queried direction." For the
        // starting_at path we fetch limit-1 strictly-older entries
        // and synthesize the anchor at index 0 ourselves.
        var q1Limit = includeAnchorEntry ? limit - 1 : limit + 1;
        // Widened to the category's descendants when the caller asked; the
        // identity case for every ordinary account register.
        var entryKeys = await RegisterQuery.EntryKeysAsync(
                _db, scope, ledgerId, reference?.EntryKey, reference?.Seq, direction, q1Limit, hidden,
                f, TodayOf(f), s, cancellationToken)
            .ConfigureAwait(false);

        // The extra key is the one farthest from the cursor. The keys come back in display
        // order, so for 'before' it is the last and for 'after' the first; the page keeps
        // the keys next to its cursor either way, or a scroll toward newer entries would
        // skip the one just past the window's edge.
        var hasMoreInDirection = !includeAnchorEntry && entryKeys.Count > limit;
        if (hasMoreInDirection)
            entryKeys = direction == DirectionAfter
                ? entryKeys.GetRange(entryKeys.Count - limit, limit)
                : entryKeys.GetRange(0, limit);

        if (includeAnchorEntry && reference is not null)
        {
            // Synthesize the focused entry as the first row.
            entryKeys.Insert(0, new RegisterEntryKeyRow
            {
                PostedAt = reference.PostedAt,
                Seq = reference.Seq,
                EntryKey = reference.EntryKey,
            });
        }

        if (entryKeys.Count == 0)
            return new RegisterPage([], CursorForOlder: null, CursorForNewer: null);

        var entries = await EntriesOfAsync(scope, hidden, entryKeys, cancellationToken).ConfigureAwait(false);

        // Cursor calc:
        //   * cursorForOlder: if there's more older history available
        //     past the oldest entry in the result, encode that
        //     entry's cursor; else null.
        //   * cursorForNewer: if there's stuff newer than the newest
        //     entry in the result, encode that entry's cursor; else
        //     null.
        //
        // For 'before' direction without anchor: hasMoreInDirection
        // gives us cursorForOlder; cursorForNewer is non-null only
        // when this wasn't the most-recent page (i.e. the caller
        // passed a cursor — meaning there's stuff newer than what
        // we returned, accessible via direction='after').
        //
        // For 'after' direction: hasMoreInDirection gives us
        // cursorForNewer; cursorForOlder reflects that there's
        // history below the new top edge (always true under 'after',
        // since 'after' implies the SPA already had older entries).
        //
        // For starting_at: cursorForNewer is the focused entry's
        // cursor (always — we know stuff above the anchor exists in
        // the timeline unless the focused row is the most-recent
        // entry, which is a benign edge case the SPA handles with a
        // no-op on prepend). cursorForOlder reflects hasMoreInDirection
        // from the older-than-anchor query.
        string? cursorForOlder;
        string? cursorForNewer;
        if (includeAnchorEntry)
        {
            // The SQL fetched `q1Limit = limit - 1` strictly-older
            // entries; we don't know if there are even more older
            // without an extra round-trip. Approximation: assume
            // there are (cursorForOlder = last entry's cursor) so
            // the SPA's "load more older" path works; the SPA can
            // discover the timeline tail by an eventual empty
            // response. Cheap and correct: only the very last page
            // ever pays for an empty fetch.
            cursorForOlder = entryKeys.Count > 0
                ? EncodeCursor(entryKeys[^1].PostedAt, entryKeys[^1].Seq, entryKeys[^1].EntryKey)
                : null;
            cursorForNewer = entryKeys.Count > 0
                ? EncodeCursor(entryKeys[0].PostedAt, entryKeys[0].Seq, entryKeys[0].EntryKey)
                : null;
        }
        else if (direction == DirectionAfter)
        {
            cursorForNewer = hasMoreInDirection
                ? EncodeCursor(entryKeys[0].PostedAt, entryKeys[0].Seq, entryKeys[0].EntryKey)
                : null;
            cursorForOlder = EncodeCursor(
                entryKeys[^1].PostedAt, entryKeys[^1].Seq, entryKeys[^1].EntryKey);
        }
        else // 'before'
        {
            cursorForOlder = hasMoreInDirection
                ? EncodeCursor(entryKeys[^1].PostedAt, entryKeys[^1].Seq, entryKeys[^1].EntryKey)
                : null;
            // Only non-null when caller passed a cursor (i.e. this
            // isn't the most-recent page — there's stuff newer).
            cursorForNewer = reference is not null
                ? EncodeCursor(entryKeys[0].PostedAt, entryKeys[0].Seq, entryKeys[0].EntryKey)
                : null;
        }

        return new RegisterPage(entries, cursorForOlder, cursorForNewer);
    }

    /// <summary>
    /// Every entry <paramref name="headerId"/> has in
    /// <paramref name="accountId"/>'s register, in the order and grouping the
    /// page shows them (the default sort). The save routes hand these back so
    /// the SPA replaces that header's rows in its window in place — no full
    /// window refresh, no scroll-jolt.
    /// </summary>
    /// <remarks>
    /// A header is one entry on the side it belongs to (a split's parent, an
    /// event) and one entry per line on an opposite side, so a save made from a
    /// category register can come back as several entries.
    /// </remarks>
    /// <returns>Empty when the header has no visible leg on the account.</returns>
    public async Task<IReadOnlyList<RegisterEntryDto>> GetEntriesForHeaderAsync(
        Guid headerId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        Guid[] scope = [accountId];
        var entryKeys = await RegisterQuery.EntriesOfHeaderAsync(
                _db, RegisterQuery.InScope(_db, scope, hidden: false), headerId, RegisterSort.Default, cancellationToken)
            .ConfigureAwait(false);
        return entryKeys.Count == 0
            ? []
            : await EntriesOfAsync(scope, hidden: false, entryKeys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The row each header in <paramref name="headerIds"/> shows in
    /// <paramref name="accountId"/>'s register, keyed by header: its first entry there
    /// in the default order, read and assembled exactly as the page does it.
    /// </summary>
    /// <remarks>
    /// For a reader that presents a header as its register row without being the
    /// register (the investment merge candidates), so that every figure it shows is the
    /// row's own rather than a second reading of the legs that could disagree with it.
    /// A header with no visible leg on the account is absent, and so is one whose entry
    /// there is a split group, which has no single row.
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, RegisterRowDto>> GetRowsForHeadersAsync(
        IReadOnlyCollection<Guid> headerIds,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headerIds);
        var rows = new Dictionary<Guid, RegisterRowDto>();
        if (headerIds.Count == 0) return rows;

        Guid[] scope = [accountId];
        var entryKeys = await RegisterQuery.EntriesOfHeadersAsync(
                _db, RegisterQuery.InScope(_db, scope, hidden: false), [.. headerIds.Distinct()],
                RegisterSort.Default, cancellationToken)
            .ConfigureAwait(false);
        if (entryKeys.Count == 0) return rows;

        var entries = await EntriesOfAsync(scope, hidden: false, entryKeys, cancellationToken)
            .ConfigureAwait(false);
        foreach (var entry in entries)
        {
            if (entry.Txn is { } row) rows.TryAdd(row.HeaderId, row);
        }
        return rows;
    }

    /// <summary>
    /// The entries <paramref name="entryKeys"/> name, in that order (the page's Q2): every
    /// row of each, with the entry key the key query grouped it under and the memo and
    /// tags the rule shows for it, read from the scope the keys came from.
    /// </summary>
    private async Task<IReadOnlyList<RegisterEntryDto>> EntriesOfAsync(
        Guid[] scope, bool hidden, IReadOnlyList<RegisterEntryKeyRow> entryKeys, CancellationToken cancellationToken)
    {
        var keyed = await KeyedRowsAsync(scope, hidden, [.. entryKeys.Select(k => k.EntryKey)], cancellationToken)
            .ConfigureAwait(false);

        // The keys are authoritative for entry ORDER: the rows' static (posted_at
        // DESC) hydration order doesn't match under a non-date sort, and even under
        // date sort the keys add the entry-key tiebreaker. Reorder the rows to the
        // keys' order; OrderBy is stable, so an entry's legs stay contiguous and in
        // their leg_index order, and AssembleEntries emits entries exactly in the
        // order the keys came in.
        var order = new Dictionary<Guid, int>(entryKeys.Count);
        for (var idx = 0; idx < entryKeys.Count; idx++)
            order[entryKeys[idx].EntryKey] = idx;
        var orderedRows = keyed.OrderBy(r => order.GetValueOrDefault(r.EntryKey, int.MaxValue)).ToList();

        return await AssembleAsync(orderedRows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every row of the entries <paramref name="keys"/> name in <paramref name="scope"/>,
    /// each with the entry key the key query grouped it under and the memo and tags the
    /// rule shows for it, in the rows' date order (posted_at, seq DESC, leg_index): the
    /// statement behind every page of entries.
    /// </summary>
    private async Task<List<PageRow>> KeyedRowsAsync(
        Guid[] scope, bool hidden, Guid[] keys, CancellationToken cancellationToken)
    {
        // THE SAME SCOPE the keys used (a narrower one would drop a category subtree's
        // descendants and leave the page empty). EF.Constant inlines the keys as SQL
        // literals so Postgres sees the selectivity at plan time. A key is a header
        // id (a side that groups by header) or a leg id (each line of an opposite
        // side); matching either keeps the fetch on an index, and the row's own entry
        // key then picks exactly the entries asked for.
        // The two matches are read apart and put together: one OR over the header and
        // the leg is a filter over every leg of the scope, each alone an index lookup.
        var inScope = RegisterQuery.InScope(_db, scope, hidden);
        return await inScope.Where(l => EF.Constant(keys).Contains(l.Row.HeaderId))
            .Concat(inScope.Where(l => EF.Constant(keys).Contains(l.Row.Id)
                                       && !EF.Constant(keys).Contains(l.Row.HeaderId)))
            .Where(l => EF.Constant(keys).Contains(l.EntryKey))
            .OrderByDescending(l => l.Row.PostedAt)
            .ThenByDescending(l => l.Row.HeaderSeq)
            .ThenBy(l => l.Row.LegIndex)
            .Select(PageRowOf(_db))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The newest register entries across the ledger: one page, newest first, of the
    /// entries of every account a person reads (not a category, not a holdings sibling,
    /// active and inactive alike), each drawn exactly as its own account's register draws
    /// it, with that account and its currency.
    /// </summary>
    /// <remarks>
    /// <para>An entry is what an account's register shows as one row (ADR-0108 D14): on
    /// the side a header belongs to, the header, a split as one group; on every other
    /// side each line on its own; so a transfer between two of the accounts is an entry
    /// on each. Each is read and assembled by the register's own statements, from its own
    /// account's legs, so a row here is the row its register shows.</para>
    /// <para>The register's filter narrows the entries before the page is cut, as it does
    /// on a register: <paramref name="needsReview"/> is the register's
    /// <c>needs_review</c> status; <paramref name="direction"/> keeps the entries whose
    /// row shows an amount above or below zero on their account (<see cref="RegisterFilter.Direction"/>);
    /// <paramref name="today"/>, when given, leaves out the entries dated after
    /// it, the ones the register calls Scheduled. Unlike the register there is no
    /// cursor: this is a first page, with whether more entries match.</para>
    /// <para>The needs-review page reads its keys in one statement, which the
    /// needs-review index serves. Every other page reads the ledger's newest headers
    /// first and then the entries of those alone
    /// (<see cref="RegisterQuery.FirstEntryKeysOnAccountsByHeaderAsync"/>), since one
    /// statement over every leg in scope aggregates them all before the cut. Both give
    /// the same page. Every statement reads one snapshot, so the batches, the keys and
    /// the rows agree.</para>
    /// </remarks>
    public Task<LedgerEntriesPage> GetLedgerPageAsync(
        Guid ledgerId,
        bool needsReview,
        TransactionDirection direction,
        DateOnly? today,
        int limit,
        CancellationToken cancellationToken = default) =>
        GetLedgerPageAsync(ledgerId, needsReview, direction, today, limit, byHeader: !needsReview, cancellationToken);

    /// <summary>
    /// <see cref="GetLedgerPageAsync(Guid, bool, TransactionDirection, DateOnly?, int, CancellationToken)"/>
    /// with the caller choosing how it is read: <paramref name="byHeader"/> reads the
    /// keys from the newest headers in batches, otherwise in one statement over every
    /// leg in scope. The page is the same either way; a test holds the two to that.
    /// </summary>
    internal async Task<LedgerEntriesPage> GetLedgerPageAsync(
        Guid ledgerId,
        bool needsReview,
        TransactionDirection direction,
        DateOnly? today,
        int limit,
        bool byHeader,
        CancellationToken cancellationToken)
    {
        await using var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken).ConfigureAwait(false);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType != "category")
            .Where(AccountsRepository.NotAHoldingsSibling(_db, ledgerId))
            .Select(a => new { a.Id, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (accounts.Count == 0) return new LedgerEntriesPage([], HasMore: false);

        Guid[] scope = [.. accounts.Select(a => a.Id)];
        var filter = new RegisterFilter(
            DateTo: today,
            Status: needsReview ? "needs_review" : null,
            Today: today,
            Direction: direction);
        // One past the page, to know whether more follow.
        var keys = byHeader
            ? await RegisterQuery.FirstEntryKeysOnAccountsByHeaderAsync(
                    _db, scope, ledgerId, limit + 1, filter, TodayOf(filter), cancellationToken)
                .ConfigureAwait(false)
            : await RegisterQuery.FirstEntryKeysOnAccountsAsync(
                    _db, scope, ledgerId, limit + 1, filter, TodayOf(filter), cancellationToken)
                .ConfigureAwait(false);
        var hasMore = keys.Count > limit;
        if (hasMore) keys = keys.GetRange(0, limit);
        if (keys.Count == 0) return new LedgerEntriesPage([], HasMore: false);

        var rows = await KeyedRowsAsync(scope, hidden: false, [.. keys.Select(k => k.EntryKey)], cancellationToken)
            .ConfigureAwait(false);
        // In the keys' order, as the register's page does. A key read for one account
        // can name an entry on another account of the scope too (the same header on
        // both brokerages of an in-kind transfer); only the entries the keys name stay.
        var order = new Dictionary<(Guid AccountId, Guid EntryKey), int>(keys.Count);
        for (var idx = 0; idx < keys.Count; idx++)
            order[(keys[idx].AccountId, keys[idx].EntryKey)] = idx;
        var orderedRows = rows
            .Where(r => order.ContainsKey((r.Row.AccountId, r.EntryKey)))
            .OrderBy(r => order[(r.Row.AccountId, r.EntryKey)])
            .ToList();
        var entries = await AssembleAsync(orderedRows, cancellationToken).ConfigureAwait(false);

        var currencyOf = accounts.ToDictionary(a => a.Id, a => a.CurrencyCode);
        return new LedgerEntriesPage(
            [.. entries.Select(e =>
            {
                var accountId = (e.Txn ?? e.Legs![0]).AccountId;
                return new LedgerEntryDto(accountId, currencyOf[accountId], e);
            })],
            hasMore);
    }

    /// <summary>
    /// A view row (without the view's own memo and tag columns), the register entry it
    /// belongs to, and the notes the rule shows for it.
    /// </summary>
    private sealed class PageRow
    {
        public ResolvedTransactionView Row { get; init; } = null!;
        public Guid EntryKey { get; init; }
        public LegKind Kind { get; init; }
        public bool IsReadOnly { get; init; }
        public string? Memo { get; init; }
        public bool MemoInherited { get; init; }
        public string? InheritedMemo { get; init; }
        public Guid? InheritFromAccountId { get; init; }
        public bool TagsInherited { get; init; }
        public string? HeaderMemo { get; init; }
        public bool HeaderInPlay { get; init; }
        public string[]? Tags { get; init; }
        public string[]? InheritedTags { get; init; }
        public string[]? HeaderTags { get; init; }
    }

    /// <summary>
    /// A register leg as the page reads it: the stored presentation's memo and flags,
    /// and its three tag sets (shown, inherited, the transaction's) through
    /// <see cref="StoredNotesQuery.TagNamesOf"/>, in one statement with the rows.
    /// </summary>
    private static Expression<Func<RegisterLeg, PageRow>> PageRowOf(AppDbContext db)
    {
        var shownNames = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Shown);
        var inheritedNames = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.Inherited);
        var headerNames = StoredNotesQuery.TagNamesOf(db, StoredNotesQuery.TransactionLevel);
        var withoutNotes = StoredNotesQuery.WithoutNotes;
        Expression<Func<RegisterLeg, PageRow>> row = l => new PageRow
        {
            Row = withoutNotes.Inline(l.Row),
            EntryKey = l.EntryKey,
            Kind = l.Shown.Kind,
            IsReadOnly = l.Shown.IsReadOnly,
            Memo = l.Shown.Memo,
            MemoInherited = l.Shown.MemoInherited,
            InheritedMemo = l.Shown.InheritedMemo,
            InheritFromAccountId = l.Shown.InheritFromAccountId,
            TagsInherited = l.Shown.TagsInherited,
            HeaderMemo = l.Shown.InPlayHeaderMemo,
            HeaderInPlay = l.Shown.InPlayHeaderTagHeaderId != null,
            Tags = shownNames.Inline(l.NotesLegId),
            InheritedTags = inheritedNames.Inline(l.NotesLegId),
            HeaderTags = headerNames.Inline(l.NotesLegId),
        };
        return row.Expand();
    }

    /// <summary>The page's rows, with the lookups the projection needs, as entries: each
    /// row with the currencies of its figures, and a category's balance converted into the
    /// ledger's reporting currency (<see cref="RegisterMoney"/>).</summary>
    private async Task<IReadOnlyList<RegisterEntryDto>> AssembleAsync(
        IReadOnlyList<PageRow> rows, CancellationToken cancellationToken)
    {
        var holdingsByAccount = await GetHoldingsSiblingMapAsync(rows.Select(r => r.Row).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var money = await RegisterMoney.LoadAsync(
                _db, _clock, [.. rows.Select(r => RegisterMoney.Of(r.Row))], cancellationToken)
            .ConfigureAwait(false);
        return [.. money.Apply(AssembleEntries(rows, holdingsByAccount)).Select(e => e with
        {
            Txn = e.Txn is null ? null : OtherLegAcrossCurrencies(e.Txn),
            Legs = e.Legs is null ? null : [.. e.Legs.Select(OtherLegAcrossCurrencies)],
        })];
    }

    /// <summary>
    /// The other leg's figure as a row reads it between two real accounts, before the
    /// row's currency is known: the view's <c>counterparty_amount</c> and
    /// <c>counterparty_currency_code</c>, or null on a category row and on a row whose
    /// other side is a category or absent, which mirror (ADR-0114 D13).
    /// </summary>
    private static (decimal? Amount, string? CurrencyCode) OtherLegBetweenRealAccounts(ResolvedTransactionView r) =>
        r.AccountType != CategoryType && r.CounterpartyAccountType is not (null or CategoryType)
            ? (r.CounterpartyAmount, r.CounterpartyCurrencyCode)
            : (null, null);

    private const string CategoryType = "category";

    /// <summary>
    /// <paramref name="row"/>, its currency set, with the other leg's figure kept only on a
    /// transfer between currencies (ADR-0114 D14, "The register row"): the counterparty
    /// chip's, and on an investment event the transfer slot's (ADR-0028 Slot 6). On a
    /// transfer in one currency the other leg is this one negated, so it is dropped.
    /// </summary>
    private static RegisterRowDto OtherLegAcrossCurrencies(RegisterRowDto row)
    {
        var shown = AcrossCurrencies(row.CounterpartyCurrencyCode, row.CurrencyCode)
            ? row
            : row with { CounterpartyAmount = null, CounterpartyCurrencyCode = null };
        return shown is InvestmentRowDto investment
               && !AcrossCurrencies(investment.TransferCurrencyCode, investment.CurrencyCode)
            ? investment with { TransferAmount = null, TransferCurrencyCode = null }
            : shown;
    }

    private static bool AcrossCurrencies(string? other, string own) =>
        other is not null && !string.Equals(other, own, StringComparison.Ordinal);

    // ---------------------------------------------------------------
    // Row → entry assembly
    // ---------------------------------------------------------------

    /// <summary>
    /// Walk the rows (in entry order, then leg_index ASC) and bucket
    /// contiguous rows of one entry key on one account. Single-row buckets emit as
    /// <c>kind="txn"</c>; multi-row buckets as <c>kind="group"</c> with legs in
    /// the order returned.
    ///
    /// <para>The key is the one the page's entry query grouped the row under
    /// (<see cref="Coffer.Domain.Annotations.LegPresentation.GroupsByHeader"/>):
    /// the header on the split side and on an event's investment side, where all
    /// of the header's legs on this account form one entry (a split-parent, or
    /// the investment aggregator's collapse target); the leg on an opposite side,
    /// where each line is its own entry. No posting count decides it.</para>
    ///
    /// <para>ADR-0080: an entry on an INVESTMENT account is collapsed
    /// server-side into ONE aggregated event row via
    /// <see cref="InvestmentEventProjector"/> (both single- and
    /// multi-leg — the projector subsumes the SPA's former
    /// <c>aggregateLegs</c> + <c>normalizeSingleLeg</c>). Investment
    /// entries therefore never emit a <c>group</c>. Every row of an entry
    /// shares one account, so one holdings-sibling lookup per entry suffices.</para>
    ///
    /// <para>The account is part of the bucket: on a page across several accounts
    /// (<see cref="GetLedgerPageAsync"/>) one key can be an entry on two of them, an
    /// in-kind share transfer's header on both brokerages. A register's own page has
    /// one account per key, so there it changes nothing.</para>
    /// </summary>
    private static IReadOnlyList<RegisterEntryDto> AssembleEntries(
        IReadOnlyList<PageRow> rows,
        IReadOnlyDictionary<Guid, Guid?> holdingsByAccount)
    {
        var entries = new List<RegisterEntryDto>();
        var i = 0;
        while (i < rows.Count)
        {
            var entryKey = rows[i].EntryKey;
            var accountId = rows[i].Row.AccountId;
            var groupEnd = i;
            while (groupEnd < rows.Count && rows[groupEnd].EntryKey == entryKey
                   && rows[groupEnd].Row.AccountId == accountId)
                groupEnd++;

            var span = groupEnd - i;
            var first = rows[i].Row;
            if (first.AccountType == "investment")
            {
                // Collapse the entry's legs into one investment event (ADR-0080).
                var legs = new List<PageRow>(span);
                for (var j = i; j < groupEnd; j++) legs.Add(rows[j]);
                holdingsByAccount.TryGetValue(first.AccountId, out var holdingsSibling);
                entries.Add(RegisterEntryDto.ForTxn(ProjectInvestmentEvent(legs, holdingsSibling)));
            }
            else if (span == 1)
            {
                entries.Add(RegisterEntryDto.ForTxn(Project(rows[i])));
            }
            else
            {
                var legs = new List<RegisterRowDto>(span);
                for (var j = i; j < groupEnd; j++) legs.Add(Project(rows[j]));
                entries.Add(RegisterEntryDto.ForGroup(entryKey, legs));
            }
            i = groupEnd;
        }
        return entries;
    }

    /// <summary>
    /// Collapse one investment header's legs (on one account) into a single
    /// aggregated event row (ADR-0080). The canonical leg (lowest
    /// <c>leg_index</c> — rows arrive leg_index ASC within an entry) supplies
    /// the header-constant fields; <see cref="InvestmentEventProjector"/>
    /// supplies the summed amount, running balance, security identity, and the
    /// category / transfer / fee slots.
    /// </summary>
    /// <remarks>
    /// The one row stands for the whole entry, so it carries what the entry's
    /// transaction row shows: an event's side shows its header on every leg already,
    /// and an action-less split on its split side shows the transaction's memo and
    /// tags (the fields the rule fills where the header is in play), as the bank
    /// register's split parent does.
    /// </remarks>
    private static InvestmentRowDto ProjectInvestmentEvent(
        IReadOnlyList<PageRow> legs,
        Guid? holdingsSibling)
    {
        var canonical = legs[0];
        var projection = InvestmentEventProjector.ProjectEvent(
            legs.Select(l => InvestmentEventLegMapping.ToEventLeg(l.Row)).ToList(), holdingsSibling);
        var notes = NotesOf(canonical);
        if (canonical.Kind == LegKind.SplitSide)
            notes = notes with { Memo = notes.HeaderMemo, Tags = notes.HeaderTags ?? [] };

        return WithNotes(ProjectInvestment(canonical.Row), notes) with
        {
            Amount = projection.Amount,
            BalanceAfter = projection.BalanceAfter,
            // The synthesized row reads as the header itself, not a child.
            LegIndex = 0,
            CounterpartyId = projection.CounterpartyId,
            CounterpartyAccountId = projection.CounterpartyAccountId,
            CounterpartyAccountName = projection.CounterpartyAccountName,
            CounterpartyAccountType = projection.CounterpartyAccountType,
            SecurityId = projection.SecurityId,
            SecurityTicker = projection.SecurityTicker,
            SecurityName = projection.SecurityName,
            Quantity = projection.Quantity,
            UnitPrice = projection.UnitPrice,
            CategoryAccountId = projection.CategoryAccountId,
            CategoryAccountName = projection.CategoryAccountName,
            CategoryAccountType = projection.CategoryAccountType,
            TransferAccountId = projection.TransferAccountId,
            TransferAccountName = projection.TransferAccountName,
            TransferAccountType = projection.TransferAccountType,
            // The slot legs' other sides between real accounts (ADR-0114 D14);
            // OtherLegAcrossCurrencies keeps them only across currencies.
            CounterpartyAmount = projection.CounterpartyAccountType is (null or CategoryType)
                ? null : projection.CounterpartyAmount,
            CounterpartyCurrencyCode = projection.CounterpartyAccountType is (null or CategoryType)
                ? null : projection.CounterpartyCurrencyCode,
            TransferAmount = projection.TransferAccountType is (null or CategoryType)
                ? null : projection.TransferAmount,
            TransferCurrencyCode = projection.TransferAccountType is (null or CategoryType)
                ? null : projection.TransferCurrencyCode,
            FeeAmount = projection.FeeAmount,
            SettledAmount = projection.SettledAmount,
            FeeCategoryId = projection.FeeCategoryId,
            FeeCategoryName = projection.FeeCategoryName,
        };
    }

    /// <summary>
    /// Fetch <c>{ accountId → holdings_account_id }</c> for the investment
    /// accounts touched by <paramref name="rows"/> — the Holdings-sibling ids the
    /// projector strips as structural noise (ADR-0028). One bounded round-trip
    /// (usually a single account for the account-scoped register); empty when the
    /// page has no investment rows, so bank-only reads pay nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, Guid?>> GetHoldingsSiblingMapAsync(
        IReadOnlyList<ResolvedTransactionView> rows,
        CancellationToken cancellationToken)
    {
        var investmentAccountIds = rows
            .Where(r => r.AccountType == "investment")
            .Select(r => r.AccountId)
            .Distinct()
            .ToArray();
        if (investmentAccountIds.Length == 0)
            return EmptyHoldingsMap;

        return await _db.Accounts
            .AsNoTracking()
            .Where(a => EF.Constant(investmentAccountIds).Contains(a.Id))
            .Select(a => new { a.Id, a.HoldingsAccountId })
            .ToDictionaryAsync(a => a.Id, a => a.HoldingsAccountId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<Guid, Guid?> EmptyHoldingsMap =
        new Dictionary<Guid, Guid?>();

    /// <summary>
    /// Project a view row to the discriminated-union DTO (ADR-0030 §2),
    /// branching on the owning account's domain (mig 119
    /// <c>account_type</c>): <c>'investment'</c> → <see cref="InvestmentRowDto"/>,
    /// every other type → <see cref="BankRowDto"/>. Used by the
    /// account-scoped register read paths, whose response is therefore
    /// homogeneous (one account = one kind).
    /// </summary>
    private static RegisterRowDto Project(PageRow row) =>
        row.Row.AccountType == "investment"
            ? WithNotes(ProjectInvestment(row.Row), NotesOf(row))
            : WithNotes(ProjectBank(row.Row), NotesOf(row));

    /// <summary>The memo-and-tags fields of a register row, as the rule presents them.</summary>
    private sealed record RowNotes(
        LegKind Kind,
        bool IsReadOnly,
        string? Memo,
        IReadOnlyList<string> Tags,
        bool MemoInherited,
        bool TagsInherited,
        string? InheritedMemo,
        IReadOnlyList<string> InheritedTags,
        Guid? InheritFromAccountId,
        string? HeaderMemo,
        IReadOnlyList<string>? HeaderTags);

    private static RowNotes NotesOf(PageRow r) => new(
        r.Kind,
        r.IsReadOnly,
        r.Memo,
        Listed(r.Tags),
        r.MemoInherited,
        r.TagsInherited,
        r.InheritedMemo,
        Listed(r.InheritedTags),
        r.InheritFromAccountId,
        r.HeaderMemo,
        r.HeaderInPlay ? Listed(r.HeaderTags) : null);

    /// <summary>
    /// Tag names as the statement listed them, in the database's order for names
    /// (<see cref="StoredNotesQuery.TagNamesOf"/>); an absent set is empty.
    /// </summary>
    private static IReadOnlyList<string> Listed(string[]? names) =>
        names is null or { Length: 0 } ? Array.Empty<string>() : names;

    private static T WithNotes<T>(T row, RowNotes notes) where T : RegisterRowDto => row with
    {
        Memo = notes.Memo,
        Tags = notes.Tags,
        MemoInherited = notes.MemoInherited,
        TagsInherited = notes.TagsInherited,
        InheritedMemo = notes.InheritedMemo,
        InheritedTags = notes.InheritedTags,
        InheritFromAccountId = notes.InheritFromAccountId,
        HeaderMemo = notes.HeaderMemo,
        HeaderTags = notes.HeaderTags,
        NotesKind = NotesKinds.Of(notes.Kind),
        IsReadOnly = notes.IsReadOnly,
    };

    /// <summary>
    /// The bank's figure from the row's originals (ADR-0114 D15), as text in the invariant
    /// culture so every digit the sidecar keeps reaches the client, a trailing zero
    /// included; null where the row has none. The same on every leg of a header, so a
    /// register row, an investment event and an entries-page row all carry it.
    /// </summary>
    private static string? OriginalAmountOf(ResolvedTransactionView r) => AsSent(r.OriginalAmount);

    /// <summary>A bank's figure as invariant text, every digit kept; null for none.</summary>
    private static string? AsSent(decimal? figure) =>
        figure?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A bank row's non-notes fields; <see cref="WithNotes"/> adds the rest.</summary>
    private static BankRowDto ProjectBank(ResolvedTransactionView r) => new()
    {
        Id = r.Id,
        AccountId = r.AccountId,
        Payee = r.Payee,
        Amount = r.Amount,
        PostedAt = r.PostedAt,
        TransactedAt = r.TransactedAt,
        Status = r.Status,
        IsHidden = r.IsHidden,
        BalanceAfter = r.BalanceAfter,
        Origin = r.Origin,
        IsPending = r.IsPending,
        ExternalId = r.ExternalId,
        CheckNumber = r.CheckNumber,
        CounterpartyId = r.CounterpartyId,
        TxnGroupId = r.TxnGroupId,
        LegIndex = r.LegIndex,
        CounterpartyAccountId = r.CounterpartyAccountId,
        CounterpartyAccountName = r.CounterpartyAccountName,
        CounterpartyAccountType = r.CounterpartyAccountType,
        CounterpartyAmount = OtherLegBetweenRealAccounts(r).Amount,
        CounterpartyCurrencyCode = OtherLegBetweenRealAccounts(r).CurrencyCode,
        Tags = Array.Empty<string>(),
        HeaderId = r.HeaderId,
        ClearedAt = r.ClearedAt,
        ClearedByUserId = r.ClearedByUserId,
        CreatedAt = r.CreatedAt,
        OnlineMatchFitid = r.OnlineMatchFitid,
        OnlineMatchFiId = r.OnlineMatchFiId,
        NeedsReview = r.NeedsReview,
        OriginalAmount = OriginalAmountOf(r),
        RoundedAtImport = r.AmountRoundedAtImport,
        ReviewClearedAmount = AsSent(r.ReviewClearedAmount),
        ReviewClearedByMatch = r.ReviewClearedByMatch,
        ProviderRawPayload = r.ProviderRawPayload,
        HeaderAccountNetAmount = r.HeaderAccountNetAmount,
        ProviderKey = r.ProviderKey,
        IsMergeWinner = r.IsMergeWinner,
        ImportSource = r.ImportSource,
        DerivedAction = r.DerivedAction,
        AccountPostingsOnHeader = r.AccountPostingsOnHeader,
        HeaderTotalPostings = r.HeaderTotalPostings,
    };

    /// <summary>An investment row's non-notes fields; <see cref="WithNotes"/> adds the rest.</summary>
    private static InvestmentRowDto ProjectInvestment(ResolvedTransactionView r) => new()
    {
        Id = r.Id,
        AccountId = r.AccountId,
        Payee = r.Payee,
        Amount = r.Amount,
        PostedAt = r.PostedAt,
        TransactedAt = r.TransactedAt,
        Status = r.Status,
        IsHidden = r.IsHidden,
        BalanceAfter = r.BalanceAfter,
        Origin = r.Origin,
        IsPending = r.IsPending,
        ExternalId = r.ExternalId,
        CheckNumber = r.CheckNumber,
        CounterpartyId = r.CounterpartyId,
        TxnGroupId = r.TxnGroupId,
        LegIndex = r.LegIndex,
        CounterpartyAccountId = r.CounterpartyAccountId,
        CounterpartyAccountName = r.CounterpartyAccountName,
        CounterpartyAccountType = r.CounterpartyAccountType,
        CounterpartyAmount = OtherLegBetweenRealAccounts(r).Amount,
        CounterpartyCurrencyCode = OtherLegBetweenRealAccounts(r).CurrencyCode,
        Tags = Array.Empty<string>(),
        HeaderId = r.HeaderId,
        ClearedAt = r.ClearedAt,
        ClearedByUserId = r.ClearedByUserId,
        CreatedAt = r.CreatedAt,
        OnlineMatchFitid = r.OnlineMatchFitid,
        OnlineMatchFiId = r.OnlineMatchFiId,
        NeedsReview = r.NeedsReview,
        OriginalAmount = OriginalAmountOf(r),
        RoundedAtImport = r.AmountRoundedAtImport,
        ReviewClearedAmount = AsSent(r.ReviewClearedAmount),
        ReviewClearedByMatch = r.ReviewClearedByMatch,
        ProviderRawPayload = r.ProviderRawPayload,
        HeaderAccountNetAmount = r.HeaderAccountNetAmount,
        ProviderKey = r.ProviderKey,
        IsMergeWinner = r.IsMergeWinner,
        ImportSource = r.ImportSource,
        DerivedAction = r.DerivedAction,
        AccountPostingsOnHeader = r.AccountPostingsOnHeader,
        HeaderTotalPostings = r.HeaderTotalPostings,
        // Investment-only fields.
        InvestmentAction = r.InvestmentAction,
        SecurityId = r.SecurityId,
        SecurityTicker = r.SecurityTicker,
        SecurityName = r.SecurityName,
        Quantity = r.Quantity,
        UnitPrice = r.UnitPrice,
        PostingRole = r.PostingRole,
        IngestActionHint = r.IngestActionHint,
        IngestSecurityId = r.IngestSecurityId,
        IngestShares = r.IngestShares,
        IngestUnitPrice = r.IngestUnitPrice,
        IngestFee = r.IngestFee,
        IngestAmount = r.IngestAmount,
        IngestSecurityTickerHint = r.IngestSecurityTickerHint,
    };

    // ---------------------------------------------------------------
    // Cursor encoding
    // ---------------------------------------------------------------

    internal sealed class RegisterCursor
    {
        public DateTime PostedAt { get; init; }
        // ADR-0034 v2: canonical sort tiebreaker. Replaces CreatedAt
        // (which was non-deterministic on batch-imported same-day
        // headers — they all shared a single now() value).
        public long Seq { get; init; }
        public Guid EntryKey { get; init; }
    }

    private static string EncodeCursor(DateTime postedAt, long seq, Guid entryKey)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new RegisterCursor
            {
                PostedAt = postedAt,
                Seq = seq,
                EntryKey = entryKey,
            });
        return Base64UrlEncode(json);
    }

    private static RegisterCursor? DecodeCursor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var bytes = Base64UrlDecode(raw);
            return JsonSerializer.Deserialize<RegisterCursor>(bytes);
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// One bucket per month-with-activity for the account's register,
    /// ordered most-recent first. Drives the SPA's date-aware
    /// scroll-track: each bucket renders at a uniform pixel height,
    /// so years with sparse activity cluster visually (Google Photos
    /// pattern). The <c>SampleHeaderId</c> is the bucket's seek
    /// anchor — clicking the bucket triggers
    /// <c>register.refresh(SampleHeaderId)</c>.
    /// </summary>
    /// <remarks>
    /// <para>Reads from <c>resolved_transactions</c> with the same
    /// visibility predicate the register uses
    /// (<c>!IsHidden &amp;&amp; IsMergedInto == null</c>); a hidden
    /// entry doesn't appear in the register, so it shouldn't shape
    /// the scroll-track either. Distinct-by-header collapses
    /// multi-leg same-account events (rare — e.g. BuyXfr fan-out)
    /// to one count per header, matching the entry-grain the register
    /// itself paginates by (ADR-0019).</para>
    ///
    /// <para>The per-bucket sample header is chosen as the most-recent
    /// in canonical <c>(PostedAt DESC, HeaderSeq DESC)</c> order so
    /// clicking the bucket lands the user at the latest activity in
    /// that month, with older entries scrollable below — matches the
    /// register's existing "starting_at" anchor semantics.</para>
    ///
    /// <para>Cost: one round-trip over <c>resolved_transactions</c>
    /// filtered to the account, distinct on
    /// <c>(HeaderId, PostedAt, HeaderSeq)</c>. The grouping happens
    /// in memory after the SQL pull because EF Core's GroupBy
    /// projection with both a Count and an Ordered-First selector
    /// doesn't translate cleanly; the distinct set is bounded by
    /// the account's lifetime entry count (large for a long-kept
    /// account), and the SPA fetches once per register session +
    /// caches via TanStack Query. Acceptable for v1; revisit if
    /// portfolio-scale accounts make the round-trip painful.</para>
    /// </remarks>
    /// <summary>
    /// Verify-and-heal pass over every balance row in a ledger.
    /// Snapshots the current <c>txn_header_account_balances</c>
    /// values, runs <c>fn_recompute_balances_for_account</c> for
    /// every account that has at least one leg or one stored row, then
    /// diffs. Each row that changed indicates the stored balance was
    /// stale at snapshot time, each row the recompute wrote that the
    /// snapshot did not hold was missing, and each snapshot row the
    /// recompute removed was one the walk does not produce (migration
    /// 277: a recompute clears every stored row in its window); the
    /// recompute side-effect has already corrected all three.
    /// </summary>
    /// <remarks>
    /// <para>This is the verify-and-heal pattern: detection is the
    /// snapshot-vs-current diff; healing is the recompute itself
    /// (idempotent — running it on already-correct rows is a
    /// no-op). One round-trip per account; cost scales linearly
    /// with the ledger's account count.</para>
    ///
    /// <para><b>Not isolated.</b> The snapshot, each account's
    /// recompute and the re-read are separate statements in no
    /// transaction, and its caller (the <c>balances</c> projection's
    /// repair, <c>LedgerConsistencyRepository.RewriteAsync</c>) opens none. A
    /// save that commits while a repair runs can therefore appear in
    /// that repair's report as drift, a changed value or a missing
    /// row; a repair run again afterwards reports nothing for it,
    /// because the save recomputed its own rows.</para>
    /// </remarks>
    public async Task<BalanceHealthReport> VerifyAndHealBalancesAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        // Snapshot stored balance rows for this ledger.
        var snapshot = await _db.TxnHeaderAccountBalances
            .AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Select(b => new { b.AccountId, b.HeaderId, b.BalanceAfter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshotByKey = snapshot.ToDictionary(b => (b.AccountId, b.HeaderId), b => b.BalanceAfter);

        // Distinct accounts with at least one leg in this ledger, and those with a stored
        // row and no leg, whose every row is one the walk does not produce: the
        // recompute clears them.
        var accountIds = (await _db.TxnLegs
                .AsNoTracking()
                .Where(l => l.LedgerId == ledgerId)
                .Select(l => l.AccountId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Union(snapshot.Select(b => b.AccountId))
            .ToList();

        // Recompute each account from the earliest possible anchor
        // so every balance row is rebuilt. Iterates the EF-bound TVF
        // wrapper; .FirstAsync forces materialization (the side
        // effect is what we care about).
        var earliest = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var accountId in accountIds)
        {
            _ = await _db.RecomputeBalancesForAccount(accountId, earliest)
                .Select(r => r.AccountId)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // Re-read post-recompute state and join with snapshot.
        var current = await _db.TxnHeaderAccountBalances
            .AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Join(_db.TxnHeaders.AsNoTracking(),
                  b => b.HeaderId, h => h.Id,
                  (b, h) => new { b.AccountId, b.HeaderId, b.BalanceAfter, h.PostedAt })
            .Join(_db.Accounts.AsNoTracking(),
                  bh => bh.AccountId, a => a.Id,
                  (bh, a) => new { bh.AccountId, AccountName = a.Name, bh.HeaderId, bh.PostedAt, bh.BalanceAfter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every row the recompute stores is one it found wrong or one it found missing:
        // a row it wrote that the snapshot did not hold was healed as much as one whose
        // value it changed.
        var drifted = new List<BalanceHealthDriftDto>();
        foreach (var row in current)
        {
            var wasStored = snapshotByKey.TryGetValue((row.AccountId, row.HeaderId), out var before);
            if (wasStored && before == row.BalanceAfter) continue;
            drifted.Add(Drift(
                row.AccountId, row.AccountName, row.HeaderId, row.PostedAt,
                wasStored ? before : null, row.BalanceAfter));
        }

        // A snapshot row the recompute removed was one the walk does not produce. One whose
        // header has gone since went with it, and is not reported.
        var kept = current.Select(r => (r.AccountId, r.HeaderId)).ToHashSet();
        var removed = await NotDerivedAsync(
                snapshot.Where(b => !kept.Contains((b.AccountId, b.HeaderId)))
                    .Select(b => (b.AccountId, b.HeaderId, b.BalanceAfter))
                    .ToList(),
                cancellationToken)
            .ConfigureAwait(false);
        drifted.AddRange(removed);

        return new BalanceHealthReport(
            Healthy: drifted.Count == 0,
            AccountsChecked: accountIds.Count,
            RowsChecked: current.Count + removed.Count,
            DriftedCount: drifted.Count,
            Drifted: drifted);
    }

    /// <summary>
    /// Check every running balance the pure walk produces against its stored row —
    /// WITHOUT writing anything. A stored row that disagrees, a walked row with no
    /// stored row, and a stored row the walk does not produce are all drift.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="VerifyAndHealBalancesAsync"/>, which heals
    /// as a side effect of checking. With the running-sum rules implemented only
    /// inside the recompute's DELETE + INSERT, the only way to learn whether a
    /// balance is right would be to overwrite it, so asking a diagnostic question
    /// would silently rewrite every row it checked.
    /// <para>
    /// Migration 206 extracts <c>account_balance_walk</c>, so the question can be
    /// asked directly. Same rules as the writer — one implementation, so
    /// this cannot drift from what a repair would produce — seeded from the
    /// account's opening balance rather than from stored state, since stored
    /// state is precisely what is under test.
    /// </para>
    /// <para>
    /// The walk also says which rows belong: a stored row it does not produce, on a header
    /// it skips (a template, a hidden or merged-away header, one with no line on the
    /// account) or on an account with no line at all, is one no write path leaves and
    /// every reader that follows the walk ignores. It is reported as
    /// <see cref="BalanceHealthDriftDto.NotDerived"/>, and the balances repair removes it.
    /// The set is the stored rows less the walked ones, so no second list of the headers
    /// the walk skips is kept here.
    /// </para>
    /// </remarks>
    public async Task<BalanceHealthReport> CheckBalancesAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        // The stored rows and every account's walk read one state: a header saved
        // between them would otherwise be walked and not yet stored, and read as missing.
        // A caller's transaction below REPEATABLE READ would give each read its own, so
        // the check refuses to run in one rather than join it.
        await using var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken, requireRepeatableRead: true)
            .ConfigureAwait(false);

        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && _db.TxnLegs.Any(l => l.AccountId == a.Id && l.LedgerId == ledgerId))
            .Select(a => new { a.Id, a.Name, a.OpeningBalance })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var stored = (await _db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Select(b => new { b.AccountId, b.HeaderId, b.BalanceAfter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(b => (b.AccountId, b.HeaderId), b => b.BalanceAfter);

        // The dawn of time: the walk takes the whole account so the comparison
        // never inherits a stored figure it is meant to be verifying.
        var earliest = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var drifted = new List<BalanceHealthDriftDto>();
        var rowsChecked = 0;
        var walkedKeys = new HashSet<(Guid AccountId, Guid HeaderId)>();
        foreach (var account in accounts)
        {
            var walked = await _db
                .AccountBalanceWalk(account.Id, earliest, account.OpeningBalance)
                .Select(w => new { w.HeaderId, w.PostedAt, w.BalanceAfter })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in walked)
            {
                rowsChecked++;
                walkedKeys.Add((account.Id, row.HeaderId));
                // The recompute stores every row the walk yields, so a walked row with
                // no stored row is drift as much as a stored row that disagrees: a
                // recompute that should have written it did not run.
                var isStored = stored.TryGetValue((account.Id, row.HeaderId), out var before);
                if (isStored && before == row.BalanceAfter) continue;
                drifted.Add(Drift(
                    account.Id, account.Name, row.HeaderId, row.PostedAt,
                    isStored ? before : null, row.BalanceAfter));
            }
        }

        // The stored rows the walk did not produce, read in the same state.
        var notDerived = await NotDerivedAsync(
                stored.Where(s => !walkedKeys.Contains(s.Key))
                    .Select(s => (s.Key.AccountId, s.Key.HeaderId, s.Value))
                    .ToList(),
                cancellationToken)
            .ConfigureAwait(false);
        drifted.AddRange(notDerived);

        return new BalanceHealthReport(
            Healthy: drifted.Count == 0,
            AccountsChecked: accounts.Count,
            RowsChecked: rowsChecked + notDerived.Count,
            DriftedCount: drifted.Count,
            Drifted: drifted);
    }

    /// <summary>
    /// The drift rows for stored running balances the walk does not produce, with each
    /// row's account name and its header's date, in date order. A row whose header is
    /// gone is left out: its row went with it.
    /// </summary>
    private async Task<List<BalanceHealthDriftDto>> NotDerivedAsync(
        IReadOnlyList<(Guid AccountId, Guid HeaderId, decimal Stored)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        Guid[] headerIds = [.. rows.Select(r => r.HeaderId).Distinct()];
        Guid[] accountIds = [.. rows.Select(r => r.AccountId).Distinct()];
        var postedAt = await _db.TxnHeaders.AsNoTracking()
            .Where(h => headerIds.Contains(h.Id))
            .Select(h => new { h.Id, h.PostedAt })
            .ToDictionaryAsync(h => h.Id, h => h.PostedAt, cancellationToken)
            .ConfigureAwait(false);
        var names = await _db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Where(r => postedAt.ContainsKey(r.HeaderId))
            .Select(r => new BalanceHealthDriftDto(
                AccountId: r.AccountId,
                AccountName: names.GetValueOrDefault(r.AccountId, "(account)"),
                HeaderId: r.HeaderId,
                PostedAt: postedAt[r.HeaderId],
                StoredBefore: r.Stored,
                RecomputedAfter: 0m,
                Diff: -r.Stored,
                Reason: ConsistencyReasons.NotDerived))
            .OrderBy(d => d.PostedAt)
            .ThenBy(d => d.AccountId)
            .ThenBy(d => d.HeaderId)
            .ToList();
    }

    /// <summary>
    /// One drifted row, for the check and the repair alike: <paramref name="stored"/> is
    /// the stored balance, or null when no row is stored, which reads as 0.
    /// </summary>
    private static BalanceHealthDriftDto Drift(
        Guid accountId, string accountName, Guid headerId, DateTime postedAt,
        decimal? stored, decimal walked) =>
        new(
            AccountId: accountId,
            AccountName: accountName,
            HeaderId: headerId,
            PostedAt: postedAt,
            StoredBefore: stored ?? 0m,
            RecomputedAfter: walked,
            Diff: walked - (stored ?? 0m),
            Reason: stored is null ? ConsistencyReasons.NotStored : ConsistencyReasons.ValueDiffers);

    /// <summary>
    /// Return every leg of the given header across ALL accounts, in
    /// posting-index order. Used by the investment editor's
    /// re-open path so <c>legsToDraft</c> can find the off-account
    /// legs (income category, transfer destination, fee category) —
    /// the register endpoint scopes legs to a single account and
    /// can't supply those.
    /// </summary>
    public async Task<IReadOnlyList<InvestmentRowDto>> GetAllLegsForHeaderAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        // The editor loads a transaction from this read, so a merged-away header has no
        // legs here, as an id that does not exist has none (MergedAway).
        var live = _db.TxnHeaders.NotMergedAway();
        var rows = await StoredNotesQuery.RegisterLegs(_db)
            .AsNoTracking()
            .Where(l => l.Row.HeaderId == headerId
                     && live.Any(h => h.Id == headerId && h.LedgerId == ledgerId))
            .OrderBy(l => l.Row.LegIndex)
            .ThenBy(l => l.Row.Id)
            .Select(PageRowOf(_db))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // ADR-0030 §2: the editor-reload path returns the FULL leg shape
        // on every leg regardless of account domain — its only caller is
        // the investment editor, whose legsToDraft reads posting_role /
        // security_id / quantity off the off-account (category / transfer
        // / fee) legs too. Account-type discrimination would drop those
        // fields on the bank-domain off-account legs and break the
        // editor, so this path projects all legs as InvestmentRowDto.
        // Each leg's notes are what its own account's register shows for it,
        // as stored, the same fields a register row carries; its currencies and
        // balance are what that register shows too.
        var money = await RegisterMoney.LoadAsync(
                _db, _clock, [.. rows.Select(r => RegisterMoney.Of(r.Row))], cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(r => (InvestmentRowDto)OtherLegAcrossCurrencies(money.Apply(WithNotes(ProjectInvestment(r.Row), NotesOf(r)))))
            .ToList();
    }

    /// <summary>
    /// Bulk lookup of <c>(balance_after, net_amount)</c> for the given
    /// header ids on a specific account. Backs the SPA's after-save
    /// refresh path — after a balance-affecting mutation, the SPA
    /// fetches fresh values for every header currently in its
    /// register window and patches them in place (no data swap, no
    /// virtuoso re-render). Cheap: PK lookup on
    /// <c>(header_id, account_id)</c>; one round-trip for the whole
    /// window.
    /// </summary>
    /// <remarks>
    /// Returns rows only for header ids that have a matching balance
    /// row on the requested account. Missing rows (e.g. the header
    /// was deleted, or doesn't touch this account) are silently
    /// absent — the SPA leaves those entries unchanged. Empty input
    /// returns an empty list without querying.
    /// <para>The balance is the one the register row shows: a category's converted into
    /// the ledger's reporting currency (<see cref="RegisterMoney"/>), so a refresh after a
    /// save never puts back a figure summed across currencies.</para>
    /// </remarks>
    public async Task<IReadOnlyList<HeaderBalanceDto>> GetBalancesForHeadersAsync(
        Guid ledgerId,
        Guid accountId,
        IReadOnlyList<Guid> headerIds,
        CancellationToken cancellationToken = default)
    {
        if (headerIds.Count == 0) return Array.Empty<HeaderBalanceDto>();
        var ids = headerIds.ToArray();
        var stored = await (
                from b in _db.TxnHeaderAccountBalances.AsNoTracking()
                join h in _db.TxnHeaders.AsNoTracking() on b.HeaderId equals h.Id
                join a in _db.Accounts.AsNoTracking() on b.AccountId equals a.Id
                where b.LedgerId == ledgerId
                      && b.AccountId == accountId
                      && EF.Constant(ids).Contains(b.HeaderId)
                select new { b.HeaderId, b.BalanceAfter, b.NetAmount, h.PostedAt, h.Seq, a.AccountType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stored.Count == 0 || stored[0].AccountType != "category")
            return [.. stored.Select(b => new HeaderBalanceDto(b.HeaderId, b.BalanceAfter, b.NetAmount))];

        // A category's balance combines accounts: read as its register reads it.
        var money = await RegisterMoney.LoadAsync(_db, _clock,
                [.. stored.Select(b => new RegisterMoney.Row(accountId, true, null, null, b.HeaderId, b.PostedAt, b.Seq))],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. stored.Select(b =>
        {
            var shown = money.BalanceOf(accountId, b.HeaderId, b.BalanceAfter);
            return new HeaderBalanceDto(b.HeaderId, shown.Amount, b.NetAmount,
                shown.Unavailable.Count > 0 ? shown.Unavailable : null);
        })];
    }

    public async Task<IReadOnlyList<IndexBucketDto>> GetIndexBucketsAsync(
        Guid ledgerId,
        Guid accountId,
        bool hidden = false,
        RegisterFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var f = filter ?? RegisterFilter.None;

        // One filter definition (ADR-0076): the rail reads the SAME
        // RegisterQuery.Filtered the page composes over, so the date buckets
        // reflect exactly the filtered set the register shows.
        var bucketScope = await ResolveScopeAsync(accountId, ledgerId, f, cancellationToken)
            .ConfigureAwait(false);
        // One row per entry, as the page groups them: a header is one entry on the
        // side it belongs to and one per line on an opposite side. The entry's legs
        // share the header's date and sequence.
        var entryTuples = await RegisterQuery.Filtered(_db, bucketScope, ledgerId, hidden, f, TodayOf(f))
            .Select(l => new { l.EntryKey, l.Row.HeaderId, l.Row.PostedAt, l.Row.HeaderSeq })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entryTuples
            .GroupBy(x => new { x.PostedAt.Year, x.PostedAt.Month })
            .Select(g =>
            {
                var sample = g
                    .OrderByDescending(x => x.PostedAt)
                    .ThenByDescending(x => x.HeaderSeq)
                    .First();
                return new IndexBucketDto(
                    YearMonth: $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                    Count: g.Count(),
                    SampleHeaderId: sample.HeaderId);
            })
            .OrderByDescending(b => b.YearMonth, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Per-status entry counts for one account (the status dropdown badges),
    /// respecting the active NON-status filter. Reuses the ONE filter
    /// (<see cref="RegisterQuery.Filtered"/>) for the non-status
    /// dimensions, then buckets by status in-memory mirroring the SPA's
    /// resolveRowStatus precedence (scheduled &gt; pending &gt; recon status).
    /// Per entry — the same unit as the page, the buckets and the "N matches" chip.
    /// </summary>
    public async Task<RegisterStatusCounts> GetStatusCountsAsync(
        Guid ledgerId,
        Guid accountId,
        RegisterFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        // Status is what we bucket, so strip it; the rest narrow every count.
        var f = (filter ?? RegisterFilter.None) with { Status = null };

        // Both visibility sides via the shared filter (ADR-0076). It keeps one
        // visibility side per call, so the Hidden bucket is a second call; the
        // visible entries feed the recon / scheduled / needs-review buckets. One
        // row per entry as the page groups them (an entry appears iff any of its
        // legs matched the per-leg filter); an entry whose legs differ in a status
        // field is counted once, by its first line, the one an investment event's
        // row is built from.
        var countScope = await ResolveScopeAsync(accountId, ledgerId, f, cancellationToken)
            .ConfigureAwait(false);
        var rows = await RegisterQuery.Filtered(_db, countScope, ledgerId, hidden: false, f, TodayOf(f))
            .Select(l => new
            {
                l.EntryKey,
                l.Row.LegIndex,
                l.Row.Id,
                l.Row.PostedAt,
                l.Row.Status,
                l.Row.IsPending,
                l.Row.NeedsReview,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var entries = rows.OrderBy(r => r.LegIndex).ThenBy(r => r.Id).DistinctBy(r => r.EntryKey).ToList();
        var hiddenCount = await RegisterQuery.Filtered(_db, countScope, ledgerId, hidden: true, f, TodayOf(f))
            .Select(l => l.EntryKey)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        // The one boundary the balances share (ScheduledBoundary).
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(f.Today, _clock);

        int all = 0, cleared = 0, uncleared = 0, reconciling = 0,
            scheduled = 0, needsReview = 0;
        foreach (var e in entries)
        {
            all++;
            // needs_review is a separate dimension (the bank-feed flag): it
            // overlaps the recon buckets rather than partitioning with them.
            if (e.NeedsReview) needsReview++;
            // Precedence: scheduled (future-dated) wins, then pending drops out
            // of the recon buckets, then the persisted recon status.
            if (ScheduledBoundary.IsScheduled(e.PostedAt, scheduledFrom)) { scheduled++; continue; }
            if (e.IsPending) continue;
            switch (e.Status)
            {
                case "cleared": cleared++; break;
                case "uncleared": uncleared++; break;
                case "reconciling": reconciling++; break;
            }
        }
        return new RegisterStatusCounts(all, cleared, uncleared, reconciling,
            scheduled, needsReview, hiddenCount);
    }

    /// <summary>
    /// The caller's date for the SQL filter, resolved by the one fallback
    /// (<see cref="ScheduledBoundary.Today"/>) rather than left to
    /// <c>COALESCE(p_today, CURRENT_DATE)</c>, so the rows, the badges and every
    /// current balance read the same date when the caller sends none.
    /// </summary>
    private DateOnly TodayOf(RegisterFilter f) => ScheduledBoundary.Today(f.Today, _clock);

}
