using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Server-side register filter (mig 164). Every field is optional; a null
/// field is a no-op predicate. Pushed into <c>register_entry_keys</c> so the
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
    decimal? AmountMin = null,
    decimal? AmountMax = null,
    Guid? SecurityId = null,
    string? Tag = null,
    Guid? CategoryId = null,
    string? Status = null,
    DateOnly? Today = null)
{
    public static readonly RegisterFilter None = new();

    /// <summary>True when any dimension narrows the result (Today alone is
    /// context, not a narrowing filter).</summary>
    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Search)
        || DateFrom is not null || DateTo is not null
        || AmountMin is not null || AmountMax is not null
        || SecurityId is not null || Tag is not null || CategoryId is not null
        || !string.IsNullOrWhiteSpace(Status);

    /// <summary>Compact, stable key for cache-keying the scroll-track buckets
    /// per filter (the date rail must re-derive when the filter narrows the
    /// set). Excludes Today — status intent already varies the key.</summary>
    public string Fingerprint =>
        IsActive
            ? string.Join('|', Search, DateFrom, DateTo, AmountMin, AmountMax,
                SecurityId, Tag, CategoryId, Status)
            : string.Empty;
}

/// <summary>
/// Register display ordering (mig 166). <see cref="Column"/> is one of the
/// whitelisted sort dimensions — <c>date</c> / <c>amount</c> / <c>payee</c> /
/// <c>category</c> on any register, plus <c>security</c> / <c>shares</c> /
/// <c>price</c> / <c>action</c> on investment registers — and the SQL function
/// falls back to <c>date</c> for anything else. <see cref="Descending"/>
/// defaults true (newest / largest first), matching the pre-sort behavior.
/// Sort is display-order only: it never affects counts or select-all (both are
/// order-independent), so it lives on this windowed read path alone.
/// </summary>
public sealed record RegisterSort(string Column, bool Descending)
{
    public static readonly RegisterSort Default = new("date", Descending: true);

    /// <summary>Wire value for the SQL function's <c>p_sort_dir</c>.</summary>
    public string Dir => Descending ? "desc" : "asc";
}

/// <summary>
/// Per-status entry counts for one account's register, respecting the active
/// NON-status filter (search / date / amount / category / tag / security).
/// Drives the status dropdown's count badges. Counts are per-header — the same
/// unit as the scroll-rail buckets and the "N matches" chip — so the numbers
/// reconcile with each other. `needs_review` overlaps the recon buckets (it's
/// the bank-feed flag, a separate dimension), so the buckets don't sum to All.
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
/// fetching — live in Postgres functions
/// (<c>register_entry_keys</c> / <c>register_entry_rows</c>, migration
/// 019). This file is just the C# adapter: typed input, opaque-cursor
/// codec, and assembly of the flat row stream into
/// <see cref="RegisterEntryDto"/> values.</para>
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

    public RegisterRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Direction parameter for keyset pagination. <c>"before"</c>
    /// returns entries strictly older than the cursor (the
    /// downward / older scroll); <c>"after"</c> returns entries
    /// strictly newer than the cursor (the upward / newer scroll).
    /// The Postgres function uses these strings directly — keep
    /// them in lockstep with migration 031.
    /// </summary>
    public const string DirectionBefore = "before";
    public const string DirectionAfter = "after";

    /// <summary>
    /// Fetch one page of resolved-transaction entries for
    /// <paramref name="ledgerId"/>, optionally narrowed to a single
    /// <paramref name="accountId"/> within that ledger.
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
    ///     header to its entry-key tuple, return a page anchored at
    ///     the focused entry (focused entry at index 0,
    ///     <paramref name="limit"/>-1 entries strictly older after
    ///     it). Used by the "Show other side" navigation arrival
    ///     path.</description>
    ///   </item>
    /// </list>
    ///
    /// Rows that are merged away or user-hidden are filtered out
    /// (by the Postgres function, not here).
    /// </summary>
    public async Task<RegisterPage> GetPageAsync(
        Guid ledgerId,
        Guid? accountId,
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
        if (startingAtHeaderId.HasValue)
        {
            reference = await ResolveCursorForHeaderAsync(
                ledgerId, accountId, startingAtHeaderId.Value, hidden, cancellationToken)
                .ConfigureAwait(false);
            // Header gone / hidden / out-of-scope ⇒ empty page (unchanged).
            if (reference is null)
                return new RegisterPage([], CursorForOlder: null, CursorForNewer: null);
            // The header exists — pin it ONLY if it matches the active filter
            // (ADR-0076). If the filter excludes it, drop the anchor and fall
            // through to the most-recent FILTERED page: a stale ?focus= under a
            // filter, or a post-save edit that moved the row out of the filter,
            // shows the filtered list — not the non-matching row, not an empty
            // page.
            if (await AnchorMatchesFilterAsync(
                    ledgerId, accountId, startingAtHeaderId.Value, hidden, f, cancellationToken)
                    .ConfigureAwait(false))
            {
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
        var entryKeys = await _db.RegisterEntryKeys(
                accountId,
                ledgerId,
                reference?.EntryKey,
                reference?.Seq,
                direction,
                q1Limit,
                hidden,
                f.Search, f.DateFrom, f.DateTo, f.AmountMin, f.AmountMax,
                f.SecurityId, f.Tag, f.CategoryId, f.Status, f.Today,
                s.Column, s.Dir)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMoreInDirection = !includeAnchorEntry && entryKeys.Count > limit;
        if (hasMoreInDirection) entryKeys = entryKeys.GetRange(0, limit);

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

        // Q2 — fetch every row belonging to those entries via LINQ
        // over the resolved_transactions view. EF.Constant inlines
        // the entry-keys array as SQL literals so Postgres sees the
        // selectivity at plan time.
        var keysArray = entryKeys.Select(k => k.EntryKey).ToArray();

        IQueryable<ResolvedTransactionView> query = _db.ResolvedTransactions
            .AsNoTracking()
            .Where(rt => rt.IsHidden == hidden && rt.IsMergedInto == null)
            // ADR-0036: entry_key is asymmetric — header_id on the
            // originating side, leg id on the target side. Match
            // either, then the account filter narrows down. UUID
            // collision between header_id and an unrelated leg id
            // would be astronomical; an OR on the same array is
            // simpler than re-computing the CASE in LINQ.
            .Where(rt => EF.Constant(keysArray).Contains(rt.HeaderId)
                      || EF.Constant(keysArray).Contains(rt.Id));

        if (accountId.HasValue)
        {
            var aid = accountId.Value;
            query = query.Where(rt => rt.AccountId == aid);
        }
        else
        {
            query = query.Where(rt =>
                _db.Accounts.Any(a => a.Id == rt.AccountId && a.LedgerId == ledgerId));
        }

        // Q2 ordering mirrors Q1's entry ordering (ADR-0034 v2: the
        // canonical (posted_at, seq) pair). AssembleEntries buckets
        // contiguous same-entry rows so leg ordering within an entry
        // is by LegIndex ASC. The SQL function's outer SELECT always
        // returns (posted_at DESC, seq DESC) regardless of direction,
        // so Q2's static DESC sort lines up.
        var rows = await query
            .OrderByDescending(rt => rt.PostedAt)
            .ThenByDescending(rt => rt.HeaderSeq)
            .ThenBy(rt => rt.LegIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Q1 (register_entry_keys) is authoritative for entry ORDER. Q2's
        // static (posted_at DESC) hydration order doesn't match under a
        // non-date sort, and even under date sort Q1 adds the entry_key
        // tiebreaker (mig 166). Reorder the hydrated rows to Q1's key order;
        // OrderBy is stable, so an entry's legs stay contiguous and in their
        // (posted_at, seq, leg_index) order — AssembleEntries then emits
        // entries in exactly the order Q1 returned.
        //
        // A row maps to the Q1 entry whose key is its header_id (a normal entry)
        // OR its leg id (an ADR-0036 target split) — the same OR-match Q2 used to
        // fetch it. EntryKeyOf can't do this lookup: for a normal entry Q1 returns
        // header_id, while EntryKeyOf returns txn_group_id ?? id, which need not
        // be equal — so keying on it misses every row and the sort is lost.
        var q1Order = new Dictionary<Guid, int>(entryKeys.Count);
        for (var idx = 0; idx < entryKeys.Count; idx++)
            q1Order[entryKeys[idx].EntryKey] = idx;
        int Q1PositionOf(ResolvedTransactionView rt) =>
            q1Order.TryGetValue(rt.HeaderId, out var p) ? p
            : q1Order.TryGetValue(rt.Id, out var p2) ? p2
            : int.MaxValue;
        var orderedRows = rows.OrderBy(Q1PositionOf).ToList();

        var holdingsByAccount = await GetHoldingsSiblingMapAsync(orderedRows, cancellationToken)
            .ConfigureAwait(false);
        var entries = AssembleEntries(orderedRows, holdingsByAccount);

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
    /// Resolve a header id to its register-entry cursor tuple
    /// (<see cref="RegisterCursor.PostedAt"/>, <see cref="RegisterCursor.Seq"/>,
    /// <see cref="RegisterCursor.EntryKey"/>). Returns <c>null</c> when the
    /// header is hidden, merged away, or doesn't belong to the ledger/account
    /// scope — i.e. it isn't a visible entry, and the caller returns an empty
    /// page. Filter-matching is a SEPARATE concern
    /// (<see cref="AnchorMatchesFilterAsync"/>): an existing anchor the active
    /// filter excludes still resolves here but is not pinned. Used by the
    /// starting_at path to anchor a page on the focused header.
    /// </summary>
    private async Task<RegisterCursor?> ResolveCursorForHeaderAsync(
        Guid ledgerId,
        Guid? accountId,
        Guid headerId,
        bool hidden,
        CancellationToken cancellationToken)
    {
        IQueryable<ResolvedTransactionView> query = _db.ResolvedTransactions
            .AsNoTracking()
            .Where(rt => rt.HeaderId == headerId
                && rt.IsHidden == hidden
                && rt.IsMergedInto == null);

        if (accountId.HasValue)
        {
            var aid = accountId.Value;
            query = query.Where(rt => rt.AccountId == aid);
        }
        else
        {
            query = query.Where(rt =>
                _db.Accounts.Any(a => a.Id == rt.AccountId && a.LedgerId == ledgerId));
        }

        // Group by header_id (ADR-0034 v2: entry_key is always h.id) and pull
        // MAX(posted_at) / MAX(header_seq) — the same shape the SQL function
        // emits. One entry per header on the account; ledger-wide collapses
        // cleanly because every leg of one header shares posted_at/seq.
        var match = await query
            .GroupBy(rt => rt.HeaderId)
            .Select(g => new
            {
                EntryKey = g.Key,
                PostedAt = g.Max(rt => rt.PostedAt),
                Seq = g.Max(rt => rt.HeaderSeq),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null) return null;
        return new RegisterCursor
        {
            PostedAt = match.PostedAt,
            Seq = match.Seq,
            EntryKey = match.EntryKey,
        };
    }

    /// <summary>
    /// True when <paramref name="headerId"/> matches the active
    /// <paramref name="filter"/> — checked THROUGH the shared
    /// <c>register_filtered_entries</c> primitive (ADR-0076), so a
    /// focused/anchored row the filter excludes is not pinned and there's no
    /// re-derived LINQ filter to drift. An inactive filter matches everything
    /// (no query). The header is assumed to exist + be in scope already
    /// (<see cref="ResolveCursorForHeaderAsync"/> ran first).
    /// </summary>
    private async Task<bool> AnchorMatchesFilterAsync(
        Guid ledgerId,
        Guid? accountId,
        Guid headerId,
        bool hidden,
        RegisterFilter filter,
        CancellationToken cancellationToken)
    {
        if (!filter.IsActive) return true;
        return await _db.RegisterFilteredEntries(
                accountId, ledgerId, hidden,
                filter.Search, filter.DateFrom, filter.DateTo, filter.AmountMin, filter.AmountMax,
                filter.SecurityId, filter.Tag, filter.CategoryId, filter.Status, filter.Today)
            .AsNoTracking()
            .Where(rt => rt.HeaderId == headerId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch the resolved <see cref="RegisterEntryDto"/> for a single
    /// header, scoped to one account. Used by the PATCH endpoint to
    /// hand the freshly-saved row back to the SPA so it can patch the
    /// register in place via <c>mutateEntries</c> — no full window
    /// refresh, no scroll-jolt.
    /// </summary>
    /// <returns><c>null</c> when the header has no source-side leg on
    /// the supplied account, or when every row is filtered out by the
    /// view's <c>is_hidden</c> / <c>is_merged_into</c> guards.</returns>
    public async Task<RegisterEntryDto?> GetEntryForHeaderAsync(
        Guid headerId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        // Mirror the same row-level filters + ordering the windowed
        // fetch uses (RegisterEndpoint → resolved_transactions). The
        // leg_index ASC tiebreaker keeps split legs in posting-index
        // order so the SPA's split-parent rendering preserves the
        // user's reorder on the next render.
        var rows = await _db.ResolvedTransactions
            .AsNoTracking()
            .Where(rt => rt.HeaderId == headerId && rt.AccountId == accountId)
            .Where(rt => !rt.IsHidden && rt.IsMergedInto == null)
            .OrderBy(rt => rt.LegIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0) return null;

        var holdingsByAccount = await GetHoldingsSiblingMapAsync(rows, cancellationToken)
            .ConfigureAwait(false);
        var entries = AssembleEntries(rows, holdingsByAccount);
        return entries.Count > 0 ? entries[0] : null;
    }

    // ---------------------------------------------------------------
    // Row → entry assembly
    // ---------------------------------------------------------------

    /// <summary>
    /// Walk the rows (sorted by entry_key, then leg_index ASC per
    /// the function) and bucket them by <see cref="EntryKeyOf"/>.
    /// Single-row buckets emit as <c>kind="txn"</c>; multi-row
    /// buckets as <c>kind="group"</c> with legs in the order returned.
    ///
    /// <para>ADR-0036: the entry-key derivation is asymmetric.
    /// ORIGINATING-side rows (this account is touched by every
    /// posting of the header) bucket under <c>header_id</c> — all
    /// legs of the header on this account collapse into one entry,
    /// rendering as a split-parent or as the investment aggregator's
    /// collapse target. TARGET-side rows (this account is touched by
    /// some but not all postings) bucket under <c>leg_id</c> — each
    /// posting becomes its own entry; the SPA's split-counter
    /// affordance keeps them read-only via the existing
    /// TxnGroupId != null detection on the row.</para>
    ///
    /// <para>ADR-0080: an entry on an INVESTMENT account is collapsed
    /// server-side into ONE aggregated event row via
    /// <see cref="InvestmentEventProjector"/> (both single- and
    /// multi-leg — the projector subsumes the SPA's former
    /// <c>aggregateLegs</c> + <c>normalizeSingleLeg</c>). Investment
    /// entries therefore never emit a <c>group</c>; the SPA's only
    /// remaining pass is the target-split regroup (a cross-page render
    /// affordance). Every row of an entry shares one account (originating
    /// entries are single-account by construction; target entries are
    /// single-leg), so one holdings-sibling lookup per entry suffices.</para>
    /// </summary>
    private static IReadOnlyList<RegisterEntryDto> AssembleEntries(
        IReadOnlyList<ResolvedTransactionView> rows,
        IReadOnlyDictionary<Guid, Guid?> holdingsByAccount)
    {
        var entries = new List<RegisterEntryDto>();
        var i = 0;
        while (i < rows.Count)
        {
            var entryKey = EntryKeyOf(rows[i]);
            var groupEnd = i;
            while (groupEnd < rows.Count && EntryKeyOf(rows[groupEnd]) == entryKey)
                groupEnd++;

            var span = groupEnd - i;
            if (rows[i].AccountType == "investment")
            {
                // Collapse the entry's legs into one investment event (ADR-0080).
                var legs = new List<ResolvedTransactionView>(span);
                for (var j = i; j < groupEnd; j++) legs.Add(rows[j]);
                holdingsByAccount.TryGetValue(rows[i].AccountId, out var holdingsSibling);
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
    private static InvestmentRowDto ProjectInvestmentEvent(
        IReadOnlyList<ResolvedTransactionView> legs,
        Guid? holdingsSibling)
    {
        var canonical = legs[0];
        var projection = InvestmentEventProjector.ProjectEvent(
            legs.Select(InvestmentEventLegMapping.ToEventLeg).ToList(), holdingsSibling);

        return ProjectInvestment(canonical) with
        {
            Amount = projection.Amount,
            BalanceAfter = projection.BalanceAfter,
            HasOverrides = projection.HasOverrides,
            // The synthesized row reads as the header itself, not a child.
            LegIndex = 0,
            // Investments carry no tags (ADR-0028), whatever the legs held.
            Tags = Array.Empty<string>(),
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
            FeeAmount = projection.FeeAmount,
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
    /// ADR-0036 asymmetric entry-key derivation. See class-level note
    /// on <see cref="AssembleEntries"/> for the full rationale.
    /// Mirrors the CASE expression in <c>register_entry_keys</c>
    /// (mig 108) so SPA pagination and entry assembly agree.
    /// </summary>
    private static Guid EntryKeyOf(ResolvedTransactionView row) =>
        row.AccountPostingsOnHeader < row.HeaderTotalPostings
            ? row.Id
            : (row.TxnGroupId ?? row.Id);

    /// <summary>
    /// Project a view row to the discriminated-union DTO (ADR-0030 §2),
    /// branching on the owning account's domain (mig 119
    /// <c>account_type</c>): <c>'investment'</c> → <see cref="InvestmentRowDto"/>,
    /// every other type → <see cref="BankRowDto"/>. Used by the
    /// account-scoped register read paths, whose response is therefore
    /// homogeneous (one account = one kind).
    /// </summary>
    private static RegisterRowDto Project(ResolvedTransactionView r) =>
        r.AccountType == "investment" ? ProjectInvestment(r) : ProjectBank(r);

    private static BankRowDto ProjectBank(ResolvedTransactionView r) => new()
    {
        Id = r.Id,
        AccountId = r.AccountId,
        Payee = r.Payee,
        Memo = r.Memo,
        Amount = r.Amount,
        PostedAt = r.PostedAt,
        TransactedAt = r.TransactedAt,
        Status = r.Status,
        IsHidden = r.IsHidden,
        HasOverrides = r.HasOverrides,
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
        Tags = r.Tags,
        HeaderId = r.HeaderId,
        ClearedAt = r.ClearedAt,
        ClearedByUserId = r.ClearedByUserId,
        CreatedAt = r.CreatedAt,
        LegMemo = r.LegMemo,
        HeaderMemo = r.HeaderMemo,
        OnlineMatchFitid = r.OnlineMatchFitid,
        OnlineMatchFiId = r.OnlineMatchFiId,
        NeedsReview = r.NeedsReview,
        ProviderRawPayload = r.ProviderRawPayload,
        HeaderAccountNetAmount = r.HeaderAccountNetAmount,
        ProviderKey = r.ProviderKey,
        IsMergeWinner = r.IsMergeWinner,
        ImportSource = r.ImportSource,
        DerivedAction = r.DerivedAction,
        AccountPostingsOnHeader = r.AccountPostingsOnHeader,
        HeaderTotalPostings = r.HeaderTotalPostings,
    };

    private static InvestmentRowDto ProjectInvestment(ResolvedTransactionView r) => new()
    {
        Id = r.Id,
        AccountId = r.AccountId,
        Payee = r.Payee,
        Memo = r.Memo,
        Amount = r.Amount,
        PostedAt = r.PostedAt,
        TransactedAt = r.TransactedAt,
        Status = r.Status,
        IsHidden = r.IsHidden,
        HasOverrides = r.HasOverrides,
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
        Tags = r.Tags,
        HeaderId = r.HeaderId,
        ClearedAt = r.ClearedAt,
        ClearedByUserId = r.ClearedByUserId,
        CreatedAt = r.CreatedAt,
        LegMemo = r.LegMemo,
        HeaderMemo = r.HeaderMemo,
        OnlineMatchFitid = r.OnlineMatchFitid,
        OnlineMatchFiId = r.OnlineMatchFiId,
        NeedsReview = r.NeedsReview,
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
    /// the account's lifetime entry count (40K-ish on a large
    /// real-world ledger), and the SPA fetches once per register session +
    /// caches via TanStack Query. Acceptable for v1; revisit if
    /// portfolio-scale accounts make the round-trip painful.</para>
    /// </remarks>
    /// <summary>
    /// Verify-and-heal pass over every balance row in a ledger.
    /// Snapshots the current <c>txn_header_account_balances</c>
    /// values, runs <c>fn_recompute_balances_for_account</c> for
    /// every account that has at least one leg, then diffs. Each
    /// row that changed indicates the stored balance was stale at
    /// snapshot time; the recompute side-effect has already
    /// corrected it.
    /// </summary>
    /// <remarks>
    /// <para>This is the verify-and-heal pattern: detection is the
    /// snapshot-vs-current diff; healing is the recompute itself
    /// (idempotent — running it on already-correct rows is a
    /// no-op). One round-trip per account; cost scales linearly
    /// with the ledger's account count.</para>
    ///
    /// <para>Run inside a single SERIALIZABLE transaction so the
    /// snapshot and the recompute don't interleave with concurrent
    /// API writes (which would make the diff include the user's
    /// in-flight changes as false-positive drift).</para>
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

        // Distinct accounts with at least one leg in this ledger.
        var accountIds = await _db.TxnLegs
            .AsNoTracking()
            .Where(l => l.LedgerId == ledgerId)
            .Select(l => l.AccountId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Recompute each account from the earliest possible anchor
        // so every balance row is rebuilt. Iterates the EF-bound TVF
        // wrapper; .FirstAsync forces materialization (the side
        // effect is what we care about).
        var earliest = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var accountId in accountIds)
        {
            _ = await _db.RecomputeBalancesForAccount(accountId, earliest)
                .Select(r => r.AccountId)
                .FirstAsync(cancellationToken)
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

        var drifted = new List<BalanceHealthDriftDto>();
        foreach (var row in current)
        {
            if (snapshotByKey.TryGetValue((row.AccountId, row.HeaderId), out var before)
                && before != row.BalanceAfter)
            {
                drifted.Add(new BalanceHealthDriftDto(
                    AccountId: row.AccountId,
                    AccountName: row.AccountName,
                    HeaderId: row.HeaderId,
                    PostedAt: row.PostedAt,
                    StoredBefore: before,
                    RecomputedAfter: row.BalanceAfter,
                    Diff: row.BalanceAfter - before));
            }
        }

        return new BalanceHealthReport(
            Healthy: drifted.Count == 0,
            AccountsChecked: accountIds.Count,
            RowsChecked: current.Count,
            DriftedCount: drifted.Count,
            Drifted: drifted);
    }

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
        var rows = await _db.ResolvedTransactions
            .AsNoTracking()
            .Where(rt => rt.HeaderId == headerId
                     && _db.TxnHeaders.Any(h => h.Id == headerId && h.LedgerId == ledgerId))
            .OrderBy(rt => rt.LegIndex)
            .ThenBy(rt => rt.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // ADR-0030 §2: the editor-reload path returns the FULL leg shape
        // on every leg regardless of account domain — its only caller is
        // the investment editor, whose legsToDraft reads posting_role /
        // security_id / quantity off the off-account (category / transfer
        // / fee) legs too. Account-type discrimination would drop those
        // fields on the bank-domain off-account legs and break the
        // editor, so this path projects all legs as InvestmentRowDto.
        return rows.Select(ProjectInvestment).ToList();
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
    /// </remarks>
    public async Task<IReadOnlyList<HeaderBalanceDto>> GetBalancesForHeadersAsync(
        Guid ledgerId,
        Guid accountId,
        IReadOnlyList<Guid> headerIds,
        CancellationToken cancellationToken = default)
    {
        if (headerIds.Count == 0) return Array.Empty<HeaderBalanceDto>();
        var ids = headerIds.ToArray();
        return await _db.TxnHeaderAccountBalances
            .AsNoTracking()
            .Where(b => b.LedgerId == ledgerId
                     && b.AccountId == accountId
                     && EF.Constant(ids).Contains(b.HeaderId))
            .Select(b => new HeaderBalanceDto(b.HeaderId, b.BalanceAfter, b.NetAmount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexBucketDto>> GetIndexBucketsAsync(
        Guid ledgerId,
        Guid accountId,
        bool hidden = false,
        RegisterFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var f = filter ?? RegisterFilter.None;

        // One filter definition (mig 167 / ADR-0076): the rail reads the SAME
        // register_filtered_entries primitive the page composes over, so the
        // date buckets reflect exactly the filtered set the register shows.
        var headerTuples = await _db.RegisterFilteredEntries(
                accountId, ledgerId, hidden,
                f.Search, f.DateFrom, f.DateTo, f.AmountMin, f.AmountMax,
                f.SecurityId, f.Tag, f.CategoryId, f.Status, f.Today)
            .AsNoTracking()
            .Select(rt => new { rt.HeaderId, rt.PostedAt, rt.HeaderSeq })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return headerTuples
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
    /// respecting the active NON-status filter. Reuses the ONE filter helper
    /// (<see cref="ApplyRegisterFilterPredicates"/>) for the non-status
    /// dimensions, then buckets by status in-memory mirroring the SPA's
    /// resolveRowStatus precedence (scheduled &gt; pending &gt; recon status).
    /// Per-header — the same unit as the buckets / "N matches" chip.
    /// </summary>
    public async Task<RegisterStatusCounts> GetStatusCountsAsync(
        Guid ledgerId,
        Guid accountId,
        RegisterFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        // Status is what we bucket, so strip it; the rest narrow every count.
        var f = (filter ?? RegisterFilter.None) with { Status = null };

        // Both visibility sides via the shared filter primitive (mig 167 /
        // ADR-0076). It filters is_hidden = p_hidden, so the Hidden bucket is a
        // second call; the visible entries feed the recon / scheduled /
        // needs-review buckets. One row per entry (per header — the projected
        // fields are header-constant, so DISTINCT collapses each header's legs;
        // a header appears iff any of its legs matched the per-leg filter).
        var entries = await _db.RegisterFilteredEntries(
                accountId, ledgerId, hidden: false,
                f.Search, f.DateFrom, f.DateTo, f.AmountMin, f.AmountMax,
                f.SecurityId, f.Tag, f.CategoryId, f.Status, f.Today)
            .AsNoTracking()
            .Select(rt => new
            {
                rt.HeaderId,
                rt.PostedAt,
                rt.Status,
                rt.IsPending,
                rt.NeedsReview,
            })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hiddenCount = await _db.RegisterFilteredEntries(
                accountId, ledgerId, hidden: true,
                f.Search, f.DateFrom, f.DateTo, f.AmountMin, f.AmountMax,
                f.SecurityId, f.Tag, f.CategoryId, f.Status, f.Today)
            .AsNoTracking()
            .Select(rt => rt.HeaderId)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var today = (f.Today ?? DateOnly.FromDateTime(DateTime.UtcNow))
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var tomorrow = today.AddDays(1);

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
            if (e.PostedAt >= tomorrow) { scheduled++; continue; }
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

}
