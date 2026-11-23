using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Register-query endpoint per the PR 3.7 deliverable in
/// <c>docs/README.md</c>. One route today (per-ledger transactions list)
/// with keyset-paginated reads off the <c>resolved_transactions</c>
/// view; mutating operations (create / edit / delete transaction) land
/// in a later PR alongside the UI surfaces that need them.
/// </summary>
public static class TransactionsEndpoints
{
    /// <summary>Default page size when the client omits <c>limit</c>.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// Absolute ceiling on page size. A large register can be tens of
    /// thousands of rows; this protects the API from a hostile caller
    /// requesting the whole table in one shot.
    /// </summary>
    public const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapTransactionsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/transactions")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        // The newest register entries across the ledger, each as its own account's
        // register draws it: the home screen's To review and Recent transactions.
        group.MapGet("/entries", LedgerEntriesAsync);
        // Date-aware scroll-track (companion to ADR-0024). One bucket
        // per month-with-activity for the account's register —
        // drives the SPA's custom scroll-track replacement.
        group.MapGet("/index-buckets", IndexBucketsAsync);
        // Per-status entry counts for the account's register (the status
        // dropdown badges), respecting the active non-status filter.
        group.MapGet("/status-counts", StatusCountsAsync);
        // Bulk per-(header, account) balance lookup. The SPA's
        // after-save refresh path uses this to patch
        // balance / net-amount values on its currently-loaded
        // register window in place, avoiding a full re-fetch +
        // virtuoso data-swap (which jerks the scroll position).
        group.MapPost("/balances", HeaderBalancesAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{headerId:guid}", PatchAsync);
        group.MapPut("/{headerId:guid}/recon-status", SetReconStatusAsync);
        // COMMANDS, not field edits. A PATCH whose entire body was one flag
        // was using PATCH as a verb: `{approve: true}` and `{mergeFromHeaderId}`
        // do not describe a new state of the row, they name an action. Both now
        // have a route that says what they are, and the empty-body guard below
        // stops reading as "this PATCH is nearly empty, so allow these three
        // exceptions".
        //
        // `approve` STAYS on PATCH as well, deliberately. The editor's accept is
        // an edit AND an approval, and splitting it would make an edit that
        // succeeded followed by an approval that failed leave the row changed
        // but still queued. One round-trip keeps it atomic — which is the
        // reasoning that collapsed the original POST /approve route in slice
        // 2c.6a, and it still holds for the flow that edits.
        group.MapPost("/{headerId:guid}/approve", ApproveAsync);
        group.MapPost("/{headerId:guid}/merge", MergeAsync);
        group.MapDelete("/{headerId:guid}", DeleteAsync);
        // Slice 2c.6c: per-row similar-payees recall (Tier 1 —
        // exact-match on raw bank payee from prior approved bank
        // rows). Returns ≤5 (payee, counterparty) suggestions the
        // editor renders as one-click chips.
        // Full leg set for a single header — across ALL accounts, not
        // just the requesting account's register. The investment
        // editor needs off-account legs (category, transfer dest, fee
        // category) to populate its category / transfer / fee
        // dropdowns when re-opening an existing transaction. The
        // register endpoint scopes legs to the account being viewed
        // and can't supply those.
        group.MapGet("/{headerId:guid}/legs", HeaderLegsAsync);
        group.MapGet("/{headerId:guid}/similar-payees", SimilarPayeesAsync);
        // Slice 2c.6d: merge candidates — settled rows (accepted,
        // un-merged, un-won, unhidden) whose aggregated source-
        // account amount matches the target's, within ±7 days.
        // Editor renders these as "Merge candidates" chips;
        // picking one folds the edited row into that candidate
        // through POST /{headerId}/merge on save. Nothing is copied
        // into the editor.
        group.MapGet("/{headerId:guid}/merge-candidates", MergeCandidatesAsync);
        // Bulk endpoints (ADR-0024). POST (not GET) on selection-summary
        // because the SelectionRequest body can be ~360 KB worst case
        // (10K-id exclude list) — that's outside the safe URL length.
        // It is a read over rows any grant can already GET, so it is gated
        // at read level like the other compute POSTs: a viewer's selection
        // bar shows the count and Σ. The bulk writes below stay write-gated.
        group.MapPost("/selection-summary", SelectionSummaryAsync).AsLedgerRead();
        group.MapPost("/bulk-recon-status", BulkReconStatusAsync);
        group.MapPost("/bulk-delete", BulkDeleteAsync);
        group.MapPost("/bulk-unhide", BulkUnhideAsync);
        group.MapPost("/bulk-move-account", BulkMoveAccountAsync);

        return routes;
    }

    private static readonly HashSet<string> ValidReconStatuses = new(StringComparer.Ordinal)
    {
        "uncleared",
        "reconciling",
        "cleared",
    };

    private static readonly HashSet<string> ValidSelectionKinds = new(StringComparer.Ordinal)
    {
        "explicit",
        "all",
    };

    private static readonly HashSet<string> ValidStatusFilters = new(StringComparer.Ordinal)
    {
        "all",
        "cleared",
        "uncleared",
        "reconciling",
        "scheduled",
        "needs_review",
        "hidden",
    };

    /// <summary>
    /// Shared validation for the <see cref="SelectionRequest"/> body
    /// across the three bulk endpoints. Returns a 422 IResult on
    /// rejection or null when the selection is well-formed.
    /// </summary>
    private static IResult? ValidateSelection(SelectionRequest selection)
    {
        if (!ValidSelectionKinds.Contains(selection.Kind))
            return BusinessError.Problem(
                BusinessError.Codes.SelectionKindInvalid,
                "selection.kind must be one of: explicit, all.");

        // A selection is made on one register, and every register is one
        // account's: there is no ledger-wide selection (the API serves only
        // the UI). Answered as the register page answers a missing account_id.
        if (selection.AccountId is null)
            return BusinessError.Problem(
                BusinessError.Codes.AccountNotInLedger,
                "selection.accountId is required: a selection is made on one register.");

        if (selection.Kind == "explicit")
        {
            if (selection.HeaderIds.Count == 0)
                return BusinessError.Problem(
                    BusinessError.Codes.SelectionEmpty,
                    "selection.headerIds must contain at least one id.");
            if (selection.HeaderIds.Count > SelectionLimits.MaxIds)
                return BusinessError.Problem(
                    BusinessError.Codes.SelectionExcludeTooLarge,
                    $"selection.headerIds may contain at most {SelectionLimits.MaxIds} ids.");
            return null;
        }

        // "all" kind
        if (!ValidStatusFilters.Contains(selection.StatusFilter))
            return BusinessError.Problem(
                BusinessError.Codes.SelectionStatusFilterInvalid,
                "selection.statusFilter must be one of: all, cleared, uncleared, reconciling, scheduled, needs_review, hidden.");
        if (selection.ExcludeIds.Count > SelectionLimits.MaxIds)
            return BusinessError.Problem(
                BusinessError.Codes.SelectionExcludeTooLarge,
                $"selection.excludeIds may contain at most {SelectionLimits.MaxIds} ids.");

        return null;
    }

    /// <summary>
    /// Refuses, whole and before anything is written, an explicit selection that names
    /// a transaction the register shows read-only: a bulk action changes the
    /// transaction, and a read-only row's is changed from the register it belongs to.
    /// Called by the bulk endpoints that change transactions (delete, unhide, move);
    /// null when the selection may proceed.
    /// </summary>
    private static async Task<IResult?> RefuseReadOnlyRowsAsync(
        BulkTransactionsRepository bulk, Guid ledgerId, SelectionRequest selection,
        CancellationToken cancellationToken) =>
        await bulk.HasReadOnlyRowAsync(ledgerId, selection, cancellationToken).ConfigureAwait(false)
            ? BusinessError.Problem(
                BusinessError.Codes.SelectionIncludesReadOnly,
                "The selection includes a row this register shows read-only, so nothing was changed. "
                + "Act on it from the account it was entered from, or leave it out of the selection.")
            : null;

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions</c>. Query params:
    /// <list type="bullet">
    ///   <item><description><c>account_id</c> — required: the register is one
    ///   account's (with <c>include_subcategories</c>, a category's and its
    ///   descendants'). Without it, 422 <c>account-not-in-ledger</c>, as the
    ///   scroll-track buckets and the status counts answer.</description></item>
    ///   <item><description><c>cursor</c> — opaque continuation token
    ///   from a prior page's <c>cursorForOlder</c> /
    ///   <c>cursorForNewer</c>. Omitted on the first page.</description></item>
    ///   <item><description><c>direction</c> — <c>"before"</c> (default;
    ///   entries older than cursor) or <c>"after"</c> (entries newer
    ///   than cursor). Powers sliding-window pagination on the
    ///   client.</description></item>
    ///   <item><description><c>starting_at</c> — optional header id;
    ///   when set, ignores <c>cursor</c> and returns a page anchored
    ///   at that header (focused entry at index 0, the rest of the
    ///   page strictly older). Used by the "Show other side"
    ///   navigation arrival path.</description></item>
    ///   <item><description><c>limit</c> — page size. Defaults to
    ///   <see cref="DefaultLimit"/>; clamped at <see cref="MaxLimit"/>.</description></item>
    /// </list>
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        Guid? account_id,
        string? cursor,
        string? direction,
        Guid? starting_at,
        int? limit,
        bool? hidden,
        // Filters (RegisterQuery.Filtered). All optional; omitted ⇒ no-op.
        string? search,
        DateOnly? date_from,
        DateOnly? date_to,
        decimal? amount_min,
        decimal? amount_max,
        Guid? security_id,
        string? tag,
        Guid? category_id,
        string? status,
        DateOnly? today,
        // Mig 226. A parent CATEGORY holds no postings of its own — they are all
        // on its children — so its register is empty without this. Ignored for
        // real accounts, which have no tree.
        bool? include_subcategories,
        // Sort (RegisterQuery). Display-order only; both whitelisted below.
        string? sort,
        string? dir,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit ?? DefaultLimit;
        if (effectiveLimit < 1 || effectiveLimit > MaxLimit)
            return BusinessError.Problem(
                BusinessError.Codes.RegisterLimitInvalid,
                $"limit must be between 1 and {MaxLimit}.");

        var effectiveDirection = direction ?? RegisterRepository.DirectionBefore;
        if (effectiveDirection != RegisterRepository.DirectionBefore
            && effectiveDirection != RegisterRepository.DirectionAfter)
            return BusinessError.Problem(
                BusinessError.Codes.RegisterDirectionInvalid,
                "direction must be 'before' or 'after'.");

        if (!IsValidSortColumn(sort) || !IsValidSortDir(dir))
            return BusinessError.Problem(
                BusinessError.Codes.RegisterSortInvalid,
                "sort must be one of date / amount / payee / category / security / shares / price / action; dir must be 'asc' or 'desc'.");

        if (!IsValidStatusFilter(status))
            return BusinessError.Problem(
                BusinessError.Codes.RegisterStatusFilterInvalid,
                "status must be one of cleared / uncleared / reconciling / scheduled / needs_review.");

        if (account_id is not { } scopedAccountId)
            return BusinessError.Problem(
                BusinessError.Codes.AccountNotInLedger,
                "account_id query parameter is required for the register page.");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, scopedAccountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var filter = BuildFilter(search, date_from, date_to, amount_min, amount_max,
            security_id, tag, category_id, status, today, include_subcategories ?? false);
        var sortSpec = string.IsNullOrWhiteSpace(sort)
            ? RegisterSort.Default
            : new RegisterSort(sort, Descending: dir is not "asc");

        var page = await register.GetPageAsync(
            ledgerId, scopedAccountId, cursor, effectiveDirection, starting_at,
            effectiveLimit, hidden ?? false, filter, sortSpec, cancellationToken).ConfigureAwait(false);
        return Results.Ok(page);
    }

    /// <summary>Page size of <c>GET /transactions/entries</c> when the client omits <c>limit</c>, and its ceiling.</summary>
    public const int EntriesMaxLimit = 50;

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/entries</c>: the newest register
    /// entries across the ledger, for the home screen's To review and Recent
    /// transactions lists (<see cref="RegisterRepository.GetLedgerPageAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>The scope is every account of the ledger that is not a category and not a
    /// holdings sibling, active and inactive: the accounts <c>GET /accounts?includeInactive=true</c>
    /// lists, less its categories. Each entry is drawn exactly as its own account's
    /// register draws it (ADR-0108 D14), with that account's id and currency; a transfer
    /// between two of the accounts is an entry on each.</para>
    /// <para>This is not the ledger-wide branch of <c>GET /transactions</c> that ADR-0108
    /// D14 removed: that route still requires <c>account_id</c> and pages one register by
    /// cursor. This one answers a single first page and pages no further.</para>
    /// <para>Query params:</para>
    /// <list type="bullet">
    ///   <item><description><c>needs_review</c> — <c>true</c>: only the entries awaiting
    ///   review, the register's <c>status=needs_review</c>. Omitted or <c>false</c>: every
    ///   entry.</description></item>
    ///   <item><description><c>direction</c> — <c>all</c> (default), <c>inflow</c> or
    ///   <c>outflow</c>: the CASH-FLOW direction, by the sign of the amount the entry's row
    ///   shows on its account (zero is neither): a split on its own account by its net, each
    ///   line on another account by its own amount. Not the register's <c>direction</c> on
    ///   <c>GET /transactions</c>, which is <c>before</c>/<c>after</c> and pages by cursor.
    ///   Anything else: 422 <c>transaction-entries-direction-invalid</c>.</description></item>
    ///   <item><description><c>today</c> — the caller's local date. When sent, entries
    ///   dated after it (the register's Scheduled) are left out.</description></item>
    ///   <item><description><c>limit</c> — page size, 1 to <see cref="EntriesMaxLimit"/>;
    ///   defaults to <see cref="EntriesMaxLimit"/>. Outside that: 422
    ///   <c>register-limit-invalid</c>, as the register page answers.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> LedgerEntriesAsync(
        Guid ledgerId,
        bool? needs_review,
        string? direction,
        DateOnly? today,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit ?? EntriesMaxLimit;
        if (effectiveLimit < 1 || effectiveLimit > EntriesMaxLimit)
            return BusinessError.Problem(
                BusinessError.Codes.RegisterLimitInvalid,
                $"limit must be between 1 and {EntriesMaxLimit}.");

        TransactionDirection? flow = direction switch
        {
            null or "all" => TransactionDirection.All,
            "inflow" => TransactionDirection.Inflow,
            "outflow" => TransactionDirection.Outflow,
            _ => null,
        };
        if (flow is not { } effectiveDirection)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionEntriesDirectionInvalid,
                "direction must be 'all', 'inflow' or 'outflow'.");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var page = await register.GetLedgerPageAsync(
            ledgerId, needs_review ?? false, effectiveDirection, today, effectiveLimit, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(page);
    }

    /// <summary>Allowed server-side status filter values (null = all). The
    /// Hidden view uses the separate <c>hidden</c> flag, not this.</summary>
    private static bool IsValidStatusFilter(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status is "cleared" or "uncleared" or "reconciling" or "scheduled" or "needs_review";

    /// <summary>Allowed sort columns (<c>RegisterQuery</c>). Null/blank = the default
    /// (date, desc). The investment-only columns (security / shares / price /
    /// action) are accepted on any account — on a non-investment register they
    /// collapse to a harmless, deterministic no-op order (the values coalesce
    /// to '' / 0), and the SPA decides which columns to OFFER per register kind.
    /// This gate only rejects genuinely unknown columns.</summary>
    private static readonly string[] ValidSortColumns =
        { "date", "amount", "payee", "category", "security", "shares", "price", "action" };

    private static bool IsValidSortColumn(string? column) =>
        string.IsNullOrWhiteSpace(column) || ValidSortColumns.Contains(column);

    private static bool IsValidSortDir(string? dir) =>
        string.IsNullOrWhiteSpace(dir) || dir is "asc" or "desc";

    /// <summary>Build a <see cref="RegisterFilter"/> from the query params,
    /// trimming/nulling blank text.</summary>
    private static RegisterFilter BuildFilter(
        string? search, DateOnly? dateFrom, DateOnly? dateTo,
        decimal? amountMin, decimal? amountMax, Guid? securityId,
        string? tag, Guid? categoryId, string? status, DateOnly? today,
        bool includeSubcategories = false) =>
        new(
            Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            DateFrom: dateFrom,
            DateTo: dateTo,
            AmountMin: amountMin,
            AmountMax: amountMax,
            SecurityId: securityId,
            Tag: string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            CategoryId: categoryId,
            Status: string.IsNullOrWhiteSpace(status) ? null : status,
            Today: today,
            IncludeSubcategories: includeSubcategories);

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/index-buckets?account_id=...</c>.
    /// Returns one bucket per month with at least one visible entry on
    /// the requested account, ordered most-recent first. Drives the
    /// SPA's date-aware scroll-track (the custom replacement for the
    /// native browser scrollbar — Google Photos pattern).
    /// </summary>
    /// <remarks>
    /// <para><c>account_id</c> is required, as for the register page itself:
    /// every register is one account's.</para>
    ///
    /// <para>Each bucket carries an entry <c>count</c> (entries as the page
    /// groups them: a header is one entry on the side it belongs to and one
    /// per line on an opposite side) and a
    /// <c>sampleHeaderId</c> — the most-recent header in that month
    /// by canonical <c>(posted_at, seq)</c>. The SPA uses the sample
    /// id as the seek anchor when the user clicks / drags to that
    /// bucket: <c>register.refresh(sampleHeaderId)</c> opens a
    /// window with that entry visible.</para>
    /// </remarks>
    private static async Task<IResult> IndexBucketsAsync(
        Guid ledgerId,
        Guid? account_id,
        bool? hidden,
        // Filters (RegisterQuery.Filtered) — the rail must reflect the same set the page shows.
        string? search,
        DateOnly? date_from,
        DateOnly? date_to,
        decimal? amount_min,
        decimal? amount_max,
        Guid? security_id,
        string? tag,
        Guid? category_id,
        string? status,
        DateOnly? today,
        // Mig 226. A parent CATEGORY holds no postings of its own — they are all
        // on its children — so its register is empty without this. Ignored for
        // real accounts, which have no tree.
        bool? include_subcategories,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        if (account_id is not { } scopedAccountId)
            return BusinessError.Problem(
                BusinessError.Codes.AccountNotInLedger,
                "account_id query parameter is required for the scroll-track buckets.");

        if (!IsValidStatusFilter(status))
            return BusinessError.Problem(
                BusinessError.Codes.RegisterStatusFilterInvalid,
                "status must be one of cleared / uncleared / reconciling / scheduled / needs_review.");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, scopedAccountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var filter = BuildFilter(search, date_from, date_to, amount_min, amount_max,
            security_id, tag, category_id, status, today, include_subcategories ?? false);

        var buckets = await register.GetIndexBucketsAsync(
            ledgerId, scopedAccountId, hidden ?? false, filter, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(buckets);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/status-counts?account_id=...</c>.
    /// Per-status entry counts for the account's register, respecting the active
    /// NON-status filter (search / date / amount / category / tag / security).
    /// Drives the status dropdown's count badges — no <c>status</c> param, it
    /// returns the count for EVERY view in one call.
    /// </summary>
    private static async Task<IResult> StatusCountsAsync(
        Guid ledgerId,
        Guid? account_id,
        string? search,
        DateOnly? date_from,
        DateOnly? date_to,
        decimal? amount_min,
        decimal? amount_max,
        Guid? security_id,
        string? tag,
        Guid? category_id,
        DateOnly? today,
        // Must match the list's scope or the badges would count a different set
        // of entries than the rows beneath them.
        bool? include_subcategories,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        if (account_id is not { } scopedAccountId)
            return BusinessError.Problem(
                BusinessError.Codes.AccountNotInLedger,
                "account_id query parameter is required for status counts.");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, scopedAccountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        // status: null — GetStatusCountsAsync buckets across every status itself.
        var filter = BuildFilter(search, date_from, date_to, amount_min, amount_max,
            security_id, tag, category_id, status: null, today,
            include_subcategories ?? false);

        var counts = await register.GetStatusCountsAsync(
            ledgerId, scopedAccountId, filter, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(counts);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/legs</c>.
    /// Returns every leg of the header across ALL accounts (not just
    /// the requesting register's account scope). The investment
    /// editor calls this on re-open so <c>legsToDraft</c> can read
    /// the off-account legs (income category, transfer destination,
    /// fee category) that the per-account register response omits.
    /// </summary>
    private static async Task<IResult> HeaderLegsAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var legs = await register.GetAllLegsForHeaderAsync(
            ledgerId, headerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(legs);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/balances?account_id=...</c>
    /// with body <c>{ headerIds: [...] }</c>. Returns
    /// <c>{ headerId, balanceAfter, netAmount }</c> for each requested
    /// header that has a row on the requested account. POST (not GET)
    /// because the header-id list can be sizeable (whole register
    /// window) and we don't want to push it through a URL.
    /// </summary>
    /// <remarks>
    /// Backs the SPA's after-save in-place balance refresh — the
    /// alternative (full window re-fetch + virtuoso data-swap) caused
    /// a perceptible scroll jump on every save. This endpoint lets
    /// the SPA leave its rendered rows in place and patch only the
    /// balance + net-amount columns via <c>register.mutateEntries</c>.
    /// </remarks>
    private static async Task<IResult> HeaderBalancesAsync(
        Guid ledgerId,
        Guid? account_id,
        HeaderBalancesRequest body,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        if (account_id is not { } scopedAccountId)
            return BusinessError.Problem(
                BusinessError.Codes.AccountNotInLedger,
                "account_id query parameter is required for the balances lookup.");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, scopedAccountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var balances = await register.GetBalancesForHeadersAsync(
            ledgerId, scopedAccountId, body.HeaderIds, cancellationToken).ConfigureAwait(false);
        return Results.Ok(balances);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions</c>. Create a
    /// new manual transaction with one or more postings (ADR-0025).
    /// <c>postings.Count == 1</c> creates a single-row;
    /// <c>&gt; 1</c> creates a multi-split. Same endpoint, same
    /// shape — the schema treats them identically and so does this
    /// surface.
    ///
    /// Validation:
    ///   - <c>postedAt</c> required.
    ///   - <c>sourceAccountId</c> required, in this ledger.
    ///   - <c>postings.Count &gt;= 1</c>.
    ///   - Per posting: counterparty present + in ledger;
    ///     counterparty != source; amount != 0.
    ///   - Per posting between real accounts in different currencies (ADR-0114 D13):
    ///     <c>counterpartyAmount</c> present, nonzero and opposite in sign to
    ///     <c>amount</c> (<c>transaction-posting-counterparty-amount-required</c>,
    ///     <c>transaction-posting-two-amounts-nonzero</c>,
    ///     <c>transaction-posting-two-amounts-sign</c>). On any other posting it is dropped.
    ///   - Ledger visible to the caller.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateTransactionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        TransactionsRepository transactions,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PostedAt == default)
            return BusinessError.Problem(BusinessError.Codes.TransactionPostedAtRequired,
                "postedAt is required.");
        if (request.SourceAccountId == Guid.Empty)
            return BusinessError.Problem(BusinessError.Codes.TransactionAccountRequired,
                "sourceAccountId is required.");
        var postingRejection = PostingValidation.ValidatePostings(
            request.Postings, request.SourceAccountId);
        if (postingRejection is not null) return postingRejection;

        if (request.Notes is { } notes
            && TagValidation.ValidateTags(notes.AllTagNames()) is { } tagsRejection)
            return tagsRejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // ADR-0029 positive identity gate: this endpoint serves
        // bank-shape source accounts only. Anything outside the
        // bank set (investment, category, etc.) is refused with a
        // typed 422 so the SPA can route the user to the right
        // editor. Each topic owns its own positive identity check;
        // we don't peek into other topics' domains.
        if (!await accounts.IsBankShapeInLedgerAsync(
                ledgerId, request.SourceAccountId, cancellationToken)
                .ConfigureAwait(false))
        {
            return BusinessError.Problem(
                BusinessError.Codes.TransactionAccountIsInvestment,
                "sourceAccountId is not a bank-shape account; investment accounts use /api/ledgers/{ledgerId}/investment-transactions.");
        }

        var accountsRejection = await PostingValidation.ValidatePostingAccountsAsync(
            ledgerId, request.SourceAccountId, request.Postings, accounts,
            cancellationToken).ConfigureAwait(false);
        if (accountsRejection is not null) return accountsRejection;

        // ADR-0114 D11, ADR-0114 D14: a transaction dated before the ledger's earliest day moves
        // every net-worth pair's start to its day, and one on a brokerage before its first
        // funding moves the benchmarks' start, so the backfill covers that day for later
        // entries. The starts are read before the write that can move them.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var created = await transactions.CreateAsync(
            ledgerId,
            request.SourceAccountId,
            request.PostedAt,
            request.Payee,
            request.CheckNumber,
            request.TransactedAt,
            request.Postings,
            request.Notes?.Intent(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (created.Refusal is { } refusal) return AnnotationProblem.Of(refusal);
        if (created.PostingRefusal is { } posting) return PostingProblem(posting);
        await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
            .ConfigureAwait(false);

        var headerId = created.HeaderId;
        return Results.Created(
            $"/api/ledgers/{ledgerId}/transactions/{headerId}",
            new { headerId });
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/transactions/{headerId}</c>.
    /// Apply user edits to a transaction in one atomic Postgres
    /// transaction: any subset of header fields (payee, check number,
    /// posted_at, transacted_at), notes, and a postings reshape
    /// (ADR-0025). The reshape writes each posting's counterparty leg
    /// itself, from the posting's amount: negated, so ADR-0019's
    /// sum-to-zero holds server-side and the client never gets to
    /// violate it, except on a posting between real accounts in
    /// different currencies, whose counterparty leg is its
    /// <c>counterpartyAmount</c> in that account's currency
    /// (ADR-0114 D13). There, a missing second amount is refused, and
    /// a zero or same-signed pair is refused unless it is resent equal
    /// to its two stored legs, from either side.
    ///
    /// Validation:
    ///   - Empty patch (no header field, postings, notes or approve) → 422
    ///     <c>transaction-patch-empty</c>, so the SPA sends only real edits.
    ///   - Cross-ledger header probe → 422
    ///     <c>transaction-not-in-ledger</c> instead of leaking via a
    ///     silent RLS-scoped no-op.
    ///   - A posting's legId that is no source-side leg of the header → 422
    ///     <c>transaction-posting-leg-not-in-header</c>; nothing is written.
    ///   - A posting across currencies the reshape refuses → 422 with its
    ///     code (above); nothing is written.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        PatchTransactionRequest request,
        Guid? account_id,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RegisterRepository register,
        TransactionsRepository transactions,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidatePatchBody(request) is { } bodyRejection) return bodyRejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (request.Postings is { } postings2)
        {
            // What the posting ALREADY sits on. An inactive account that is
            // already here is left alone (historical preservation); one newly
            // chosen is refused. Without the distinction, resending an
            // unchanged counterparty looked exactly like re-targeting onto a
            // deactivated account, so editing a memo on an old row failed and
            // deactivating an account froze its entire history.
            var alreadyReferenced = await transactions
                .LegAccountIdsAsync(ledgerId, headerId, cancellationToken)
                .ConfigureAwait(false);
            // What each kept posting stores, so an amount resent as stored passes the
            // places rule even when it does not fit its currency (ADR-0114 D15).
            var stored = await transactions
                .StoredPairsAsync(ledgerId, headerId, postings2.SourceAccountId, cancellationToken)
                .ConfigureAwait(false);
            var accountsRejection = await PostingValidation.ValidatePostingAccountsAsync(
                ledgerId, postings2.SourceAccountId, postings2.Items, accounts,
                cancellationToken, alreadyReferenced, stored, "postings.items").ConfigureAwait(false);
            if (accountsRejection is not null) return accountsRejection;
        }

        // ADR-0114 D11: a new posting date before the ledger's earliest day moves every
        // net-worth pair's start to it, and a date or a reshape that puts the row on a
        // brokerage before its first funding moves the benchmarks' start; read before the
        // write that can move them, as is a merge, whose survivor takes the folded row's
        // date. A notes or approve edit moves neither.
        var datedEdit = (request.HasPostedAt && request.PostedAt is not null) || request.Postings is not null
            || request.MergeFromHeaderId is not null;
        NeededStarts? before = datedEdit
            ? await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false)
            : null;
        var outcome = await transactions.PatchAsync(
            ledgerId, headerId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Refusal is { } refusal) return AnnotationProblem.Of(refusal);
        if (outcome.Result != TransactionsRepository.PatchResult.Ok)
        {
            return MapPatchFailure(outcome);
        }
        if (before is { } read)
            await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, read, cancellationToken)
                .ConfigureAwait(false);

        // PATCH succeeded. When the caller supplies an account_id
        // (the register view they're currently looking at), return
        // every freshly-resolved entry the header has there
        // (SavedEntriesResponse) so the SPA can replace its rows in
        // the window — preserving scroll position vs. a full window
        // refresh. Falls back to the postings request's
        // SourceAccountId when present.
        //
        // A PATCH never merges (see the empty-body guard in ValidatePatchBody): a merge
        // goes through POST /{headerId}/merge, which answers the survivor's
        // entries itself. The edited row is the one to resolve.
        var resolveAccountId = account_id ?? request.Postings?.SourceAccountId;
        return await SavedEntriesAsync(register, headerId, resolveAccountId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The PATCH body's own checks, before any read: something to do, a well-formed
    /// postings reshape, and valid tags. The repository leaves these to its caller, so
    /// every writer that patches through it makes them first.
    /// </summary>
    /// <returns>The refusal, or null when the body passes.</returns>
    internal static IResult? ValidatePatchBody(PatchTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Presence, not nullness (migration 230): `{"payee": null}` is a
        // request to CLEAR the payee, which is a header edit and must not read
        // as an empty body.
        var hasHeaderField =
            request.HasPayee
            || request.HasCheckNumber
            || request.HasPostedAt
            || request.HasTransactedAt;
        var hasPostings = request.Postings is not null;
        // Slice 2c.6a: `approve: true` alone is a valid PATCH — it
        // clears needs_review without other edits (the user accepted
        // a bank-feed row as-is). Empty-body 422 still fires when
        // approve is absent / false AND no other field is supplied.
        var hasApprove = request.Approve == true;
        // A notes body alone is a valid PATCH (a memo or tag edit).
        var hasNotes = request.Notes is not null;
        // No hasMerge: a merge cannot arrive on this route any more. It is a
        // command with its own POST, and MergeFromHeaderId is [JsonIgnore] so a
        // body carrying it is simply a body that set nothing.
        if (!hasHeaderField && !hasPostings && !hasApprove && !hasNotes)
            return BusinessError.Problem(BusinessError.Codes.TransactionPatchEmpty,
                "Supply at least one header field, a postings reshape, notes, or approve=true.");

        if (request.Postings is { } postings)
        {
            if (postings.SourceAccountId == Guid.Empty)
                return BusinessError.Problem(BusinessError.Codes.TransactionAccountRequired,
                    "postings.sourceAccountId is required.");
            var rejection = PostingValidation.ValidatePostings(postings.Items, postings.SourceAccountId);
            if (rejection is not null) return rejection;
        }

        if (request.Notes is { } notes
            && TagValidation.ValidateTags(notes.AllTagNames()) is { } tagsRejection)
            return tagsRejection;

        return null;
    }

    /// <summary>
    /// A save's answer: 200 with every entry <paramref name="headerId"/> has in
    /// <paramref name="accountId"/>'s register (<see cref="SavedEntriesResponse"/>),
    /// or 204 when no register is named or the header has no entry there.
    /// </summary>
    internal static async Task<IResult> SavedEntriesAsync(
        RegisterRepository register, Guid headerId, Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is not { } rid) return Results.NoContent();
        var entries = await register.GetEntriesForHeaderAsync(headerId, rid, cancellationToken).ConfigureAwait(false);
        return entries.Count > 0 ? Results.Ok(new SavedEntriesResponse(entries)) : Results.NoContent();
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees</c>
    /// — slice 2c.6c Tier 1. Returns up to 5
    /// <see cref="SimilarPayeeDto"/> suggestions for the editor's
    /// "Similar payees" chip row: the <c>(payee, counterparty)</c>
    /// pairs the user previously chose on prior approved rows from
    /// the same provider as this row, whose raw bank payee exactly
    /// matches this row's. The counterparty may be a category or —
    /// when the prior rows were settled as transfers — a real
    /// account. Empty list when there's no anchor (manual row,
    /// missing payee, no prior matches).
    /// </summary>
    private const int SimilarPayeesLimit = 5;
    private const int MergeCandidatesLimit = 5;

    private static async Task<IResult> SimilarPayeesAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TransactionsRepository transactions,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Cross-ledger probe: an unknown headerId or one in another
        // ledger should not leak distinguishable responses. The
        // repo method already filters on (id, ledger_id) for the
        // anchor read; an unauthorized header returns empty. That's
        // intentionally indistinguishable from "no suggestions."
        var suggestions = await transactions.GetSimilarPayeesAsync(
            ledgerId, headerId, SimilarPayeesLimit, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(suggestions);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/merge-candidates</c>
    /// — slice 2c.6d. Returns up to 5
    /// <see cref="MergeCandidateDto"/> entries the editor renders
    /// as "Merge candidates" chips. Same probe-safety contract as
    /// similar-payees: cross-ledger or unknown headers come back
    /// empty rather than 404, so the SPA treats absence and miss
    /// identically.
    /// </summary>
    private static async Task<IResult> MergeCandidatesAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TransactionsRepository transactions,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var candidates = await transactions.GetMergeCandidatesAsync(
            ledgerId, headerId, MergeCandidatesLimit, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(candidates);
    }

    /// <summary>
    /// <c>PUT /api/ledgers/{ledgerId}/transactions/{headerId}/recon-status</c>.
    /// Set the reconciliation state on one header. Cycling lives
    /// in the SPA (uncleared → reconciling → cleared → uncleared);
    /// the API only enforces validity and audit-column consistency.
    ///
    /// Validation:
    ///   - <c>status</c> must be one of <c>uncleared</c>,
    ///     <c>reconciling</c>, <c>cleared</c>; anything else returns
    ///     <c>transaction-recon-status-invalid</c>.
    ///   - Header must belong to the ledger; cross-ledger probes
    ///     return <c>transaction-not-in-ledger</c>.
    /// </summary>
    private static async Task<IResult> SetReconStatusAsync(
        Guid ledgerId,
        Guid headerId,
        SetReconStatusRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TransactionsRepository transactions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidReconStatuses.Contains(request.Status))
            return BusinessError.Problem(
                BusinessError.Codes.TransactionReconStatusInvalid,
                "status must be one of: uncleared, reconciling, cleared.");

        if (request.AccountId == Guid.Empty)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionAccountRequired,
                "accountId is required — reconciliation status is per-account.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await transactions.SetReconStatusAsync(
            ledgerId, headerId, request.AccountId, request.Status,
            currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            TransactionsRepository.SetReconStatusResult.Ok => Results.NoContent(),
            TransactionsRepository.SetReconStatusResult.HeaderNotFound =>
                BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                    "Transaction does not belong to this ledger."),
            _ => Results.Problem("Unknown recon-status result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/transactions/{headerId}</c>.
    /// Remove a transaction from the user-visible register. Policy:
    ///
    /// <list type="bullet">
    ///   <item>Header with no <c>external_id</c> (manual entries) →
    ///     hard-delete the header. Legs + override rows cascade.</item>
    ///   <item>Header with <c>external_id</c> (any feed / import) →
    ///     soft-hide via <c>is_hidden=true</c> so the next re-source
    ///     doesn't resurrect it.</item>
    /// </list>
    ///
    /// Response carries the chosen outcome so the SPA can render the
    /// right toast ("Permanently deleted." vs "Hidden from register.").
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TransactionsRepository transactions,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await transactions.DeleteAsync(ledgerId, headerId, cancellationToken)
                                        .ConfigureAwait(false);

        return outcome switch
        {
            TransactionsRepository.DeleteOutcome.HardDeleted =>
                Results.Ok(new DeleteTransactionResponse("hard-deleted")),
            TransactionsRepository.DeleteOutcome.SoftHidden =>
                Results.Ok(new DeleteTransactionResponse("soft-hidden")),
            TransactionsRepository.DeleteOutcome.HeaderNotFound =>
                BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                    "Transaction does not belong to this ledger."),
            TransactionsRepository.DeleteOutcome.HeaderNotBankShape =>
                BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                    "Header is an investment transaction; use /api/ledgers/{ledgerId}/investment-transactions/{headerId}."),
            _ => Results.Problem("Unknown delete result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/{headerId}/approve</c> —
    /// accept a bank-feed row as-is, changing no field.
    /// </summary>
    /// <remarks>
    /// Idempotent: approving an already-accepted row is a success, not a 422.
    /// The caller that wants to edit AND accept in one atomic write still sends
    /// <c>approve: true</c> on the PATCH; this route is for the accept that
    /// changes nothing, which had been expressing itself as a PATCH with an
    /// otherwise-empty body.
    /// </remarks>
    /// <summary>
    /// One place the PATCH outcomes become HTTP, shared by PATCH and by the two
    /// command routes that delegate to the same repository method.
    /// </summary>
    /// <remarks>
    /// Extracted when approve and merge got their own routes. Three copies of a
    /// switch over the same enum is three places for a new member to be
    /// forgotten, and the symptom of forgetting one is a 500 on a case the
    /// repository handles deliberately.
    /// </remarks>
    private static IResult MapPatchFailure(TransactionsRepository.PatchOutcome outcome) =>
        outcome.Result switch
        {
                TransactionsRepository.PatchResult.PostingRefused => PostingProblem(outcome.PostingRefusal!),
                TransactionsRepository.PatchResult.HeaderNotInLedger =>
                    BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                        "Transaction does not belong to this ledger."),
                TransactionsRepository.PatchResult.PostingsLegNotInHeader =>
                    BusinessError.Problem(BusinessError.Codes.TransactionPostingLegNotInHeader,
                        "A posting's legId does not match any existing leg on this transaction."),
                TransactionsRepository.PatchResult.PostingsDuplicateLegId =>
                    BusinessError.Problem(BusinessError.Codes.TransactionPostingLegIdDuplicated,
                        "Two postings carry the same legId. Each posting must reference a distinct existing leg, or omit legId to create a new one."),
                TransactionsRepository.PatchResult.PostingsSourceAccountMismatch =>
                    BusinessError.Problem(BusinessError.Codes.TransactionSourceAccountMismatch,
                        "The supplied sourceAccountId does not match the transaction's source-side legs."),
                TransactionsRepository.PatchResult.MergeSourceInvalid =>
                    BusinessError.Problem(BusinessError.Codes.MergeSourceInvalid,
                        "The row you're merging is no longer a fresh review row, or mergeFromHeaderId isn't a settled, visible transaction in this ledger."),
                TransactionsRepository.PatchResult.HeaderNotBankShape =>
                    BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                        "Header is an investment transaction; use /api/ledgers/{ledgerId}/investment-transactions/{headerId}."),
                TransactionsRepository.PatchResult.HeaderDateNull =>
                    BusinessError.Problem(BusinessError.Codes.TransactionDateNull,
                        "postedAt and transactedAt cannot be null. Omit the field to leave the date unchanged."),
                _ => Results.Problem("Unknown patch result.", statusCode: 500),
            };

    /// <summary>The 422 for a posting the leg-pair builder refused (ADR-0114 D13): its
    /// code, and a detail naming the posting.</summary>
    private static IResult PostingProblem(PostingLegPair.Refusal refusal) =>
        BusinessError.Problem(refusal.Code, refusal.Detail);

    private static async Task<IResult> ApproveAsync(
        Guid ledgerId,
        Guid headerId,
        Guid? account_id,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        TransactionsRepository transactions,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await transactions.PatchAsync(
            ledgerId, headerId, new PatchTransactionRequest { Approve = true },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Result != TransactionsRepository.PatchResult.Ok)
            return MapPatchFailure(outcome);

        // Same courtesy the PATCH route does: hand back the resolved entries when the
        // caller says which register it is looking at, so the SPA can replace them in
        // place instead of refetching a window.
        return await SavedEntriesAsync(register, headerId, account_id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/{headerId}/merge</c> — fold
    /// this row into the one named in the body.
    /// </summary>
    /// <remarks>
    /// <para>Direction is INVERTED and worth restating at every entry point: the
    /// URL's <paramref name="headerId"/> is the LOSER, and
    /// <c>fromHeaderId</c> is the surviving WINNER. The user picked the
    /// canonical row in the candidates panel, so that is the row that keeps its
    /// identity, postings and any losers it already absorbed.</para>
    ///
    /// <para>No <c>approve</c> field: clearing <c>needs_review</c> on the loser
    /// is the repository's job, because a folded-away row is resolved by
    /// definition and an invariant that depends on a caller remembering to pair
    /// two fields is not an invariant.</para>
    /// </remarks>
    private static async Task<IResult> MergeAsync(
        Guid ledgerId,
        Guid headerId,
        MergeTransactionRequest request,
        Guid? account_id,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        TransactionsRepository transactions,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.FromHeaderId == Guid.Empty)
            return BusinessError.Problem(BusinessError.Codes.TransactionAccountRequired,
                "fromHeaderId is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // ADR-0114 D11: the survivor takes the folded row's date. The folded row was live,
        // so the ledger's earliest day holds, but the survivor's own lines move to that
        // date: a survivor that funds a brokerage moves the benchmarks' start earlier.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var outcome = await transactions.PatchAsync(
            ledgerId, headerId,
            new PatchTransactionRequest { MergeFromHeaderId = request.FromHeaderId },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Result != TransactionsRepository.PatchResult.Ok)
            return MapPatchFailure(outcome);
        await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
            .ConfigureAwait(false);

        // The SURVIVOR is what the caller needs back — the edited row is now a
        // tombstone and has left the register.
        return await SavedEntriesAsync(register, request.FromHeaderId, account_id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/selection-summary</c>
    /// (ADR-0024). Returns the counts and account-scoped sum for the
    /// supplied selection — drives the bulk-action footer's
    /// "N selected · M read-only · Σ $X.XX" readout. The SPA debounces this against
    /// rapid checkbox interactions so the footer doesn't lag the
    /// pointer.
    /// </summary>
    private static async Task<IResult> SelectionSummaryAsync(
        Guid ledgerId,
        SelectionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rejection = ValidateSelection(request);
        if (rejection is not null) return rejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var summary = await bulk.GetSelectionSummaryAsync(
            ledgerId, request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(summary);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-recon-status</c>
    /// (ADR-0024). Set <c>status</c> on every header in the selection
    /// inside one atomic UPDATE. Per ADR-0024 "Partial failure":
    /// all-or-nothing — the underlying Postgres statement either
    /// applies to every matched row or rolls back as one.
    /// </summary>
    private static async Task<IResult> BulkReconStatusAsync(
        Guid ledgerId,
        BulkReconStatusRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rejection = ValidateBulkReconStatus(request);
        if (rejection is not null) return rejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var updated = await bulk.BulkSetReconStatusAsync(
            ledgerId, request.Selection, request.AccountId, request.Status,
            currentUser.UserId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BulkReconStatusResponse(updated));
    }

    /// <summary>
    /// The bulk status body's own checks, before any read: a known status, the account
    /// whose legs it sets, and a well-formed selection. The repository leaves these to its
    /// caller, so every writer that sets a status through it makes them first.
    /// </summary>
    /// <returns>The refusal, or null when the body passes.</returns>
    internal static IResult? ValidateBulkReconStatus(BulkReconStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidReconStatuses.Contains(request.Status))
            return BusinessError.Problem(
                BusinessError.Codes.TransactionReconStatusInvalid,
                "status must be one of: uncleared, reconciling, cleared.");

        if (request.AccountId == Guid.Empty)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionAccountRequired,
                "accountId is required — reconciliation status is per-account.");

        return ValidateSelection(request.Selection);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-delete</c>
    /// (ADR-0024). Applies the per-row hard-delete vs soft-hide policy
    /// across every header in the selection inside one transaction.
    /// Caller surfaces the typed-confirmation dialog (count &gt; 100)
    /// before invoking — the API enforces no rate / count limit, the
    /// user is the owner of their data.
    /// </summary>
    private static async Task<IResult> BulkDeleteAsync(
        Guid ledgerId,
        BulkDeleteRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rejection = ValidateSelection(request.Selection);
        if (rejection is not null) return rejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var readOnly = await RefuseReadOnlyRowsAsync(bulk, ledgerId, request.Selection, cancellationToken)
            .ConfigureAwait(false);
        if (readOnly is not null) return readOnly;

        // Named token: BulkDeleteAsync takes a hardDeleteSourcedRows flag (the undo-import
        // path) before it, so a positional token would land in that flag. The bulk-delete endpoint
        // keeps the DEFAULT — hide a feed-sourced row so a re-sync upserts into it rather
        // than resurrecting it. Only undo wants the other behaviour.
        var (hardDeleted, softHidden) = await bulk.BulkDeleteAsync(
            ledgerId, request.Selection, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(new BulkDeleteResponse(hardDeleted, softHidden));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-unhide</c> (ADR-0072
    /// D2). Un-hide every hidden header in the selection in one transaction,
    /// recomputing balances + holdings. The selection is an explicit list of ids (a
    /// restore of one row is a selection of one) or a select-all over its view, usually
    /// the Hidden view. Only the selected rows that are hidden are restored and counted;
    /// a visible, unknown or merged-away id is left as it is.
    /// </summary>
    private static async Task<IResult> BulkUnhideAsync(
        Guid ledgerId,
        BulkUnhideRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rejection = ValidateSelection(request.Selection);
        if (rejection is not null) return rejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var readOnly = await RefuseReadOnlyRowsAsync(bulk, ledgerId, request.Selection, cancellationToken)
            .ConfigureAwait(false);
        if (readOnly is not null) return readOnly;

        // ADR-0114 D11: a hidden row counts toward no start, so unhiding one dated before
        // them moves them earlier; read before the write that can move the starts.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var unhidden = await bulk.BulkUnhideAsync(
            ledgerId, request.Selection, cancellationToken).ConfigureAwait(false);
        if (unhidden > 0)
            await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
                .ConfigureAwait(false);
        return Results.Ok(new BulkUnhideResponse(unhidden));
    }

    // The bulk move's wording for the two rules a move of any size asks (ADR-0072 D3). A
    // move of one row is a bulk move of a selection of one.
    private const string DomainCrossedMessage =
        "A category and an account are not interchangeable: move a category's "
        + "transactions to another category, and an account's to another account.";

    private const string InvestmentShapeMessage =
        "An investment transaction's account leg cannot be moved; its holdings would "
        + "stay behind. Its category leg can be recategorised, and a cash-only transfer "
        + "can move from the brokerage to a bank account.";

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-move-account</c>
    /// (ADR-0072 D3). Move the whole (account-scoped) selection to another real
    /// account, all-or-nothing. Recomputes source + target balances.
    /// </summary>
    private static async Task<IResult> BulkMoveAccountAsync(
        Guid ledgerId,
        BulkMoveAccountRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rejection = ValidateSelection(request.Selection);
        if (rejection is not null) return rejection;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var readOnly = await RefuseReadOnlyRowsAsync(bulk, ledgerId, request.Selection, cancellationToken)
            .ConfigureAwait(false);
        if (readOnly is not null) return readOnly;

        // ADR-0114 D11: a move onto a brokerage of a row dated before its first funding moves
        // the benchmarks' start earlier; read before the write that can move it.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var result = await bulk.BulkMoveAccountAsync(
            ledgerId, request.Selection, request.TargetAccountId,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == BulkTransactionsRepository.BulkMoveOutcome.Moved)
            await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
                .ConfigureAwait(false);
        return result.Outcome switch
        {
            BulkTransactionsRepository.BulkMoveOutcome.Moved =>
                Results.Ok(new BulkMoveAccountResponse(result.Moved, result.BecameTwoAmount)),
            BulkTransactionsRepository.BulkMoveOutcome.RelabelRefused =>
                RelabelCheck.Refusal(result.Relabel!)!,
            BulkTransactionsRepository.BulkMoveOutcome.TargetInvalid =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetInvalid,
                    "Target must be an account or category in this ledger."),
            BulkTransactionsRepository.BulkMoveOutcome.TargetInactive =>
                BusinessError.Problem(BusinessError.Codes.AccountInactive,
                    "Target account is inactive; reactivate it before moving transactions to it."),
            BulkTransactionsRepository.BulkMoveOutcome.AccountDomainCrossed =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveDomainCrossed,
                    DomainCrossedMessage),
            BulkTransactionsRepository.BulkMoveOutcome.SourceScopeRequired =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveSourceRequired,
                    "Bulk move requires an account-scoped selection (selection.accountId)."),
            BulkTransactionsRepository.BulkMoveOutcome.TargetSameAsSource =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetSameAsSource,
                    "Source and target accounts are the same."),
            BulkTransactionsRepository.BulkMoveOutcome.InvestmentShape =>
                BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                    InvestmentShapeMessage),
            BulkTransactionsRepository.BulkMoveOutcome.SplitToInvestment =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveSplitToInvestment,
                    "One or more selected split transactions cannot be moved to an investment account."),
            BulkTransactionsRepository.BulkMoveOutcome.SplitFromInvestment =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveSplitFromInvestment,
                    "One or more selected split transactions cannot be moved from an investment "
                    + "account to a bank account."),
            BulkTransactionsRepository.BulkMoveOutcome.Collision =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveCollision,
                    "One or more selected transactions already have a leg on the target account."),
            _ => Results.Problem("Unknown move result.", statusCode: 500),
        };
    }
}
