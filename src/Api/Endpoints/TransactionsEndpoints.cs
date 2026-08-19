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
        // (slice 2c.6a) The POST /{headerId}/approve route was
        // collapsed into PATCH with `approve: true`. The bank-feed
        // accept flow always edits at least one field anyway
        // (category / payee), so one round-trip is the natural shape.
        group.MapDelete("/{headerId:guid}", DeleteAsync);
        group.MapPost("/{headerId:guid}/unhide", UnhideAsync);
        group.MapPost("/{headerId:guid}/move-account", MoveAccountAsync);
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
        // Editor renders these as "Possible matches" chips;
        // clicking pre-fills the editor and arms
        // `mergeFromHeaderId` on the next PATCH.
        group.MapGet("/{headerId:guid}/merge-candidates", MergeCandidatesAsync);
        // Bulk endpoints (ADR-0024). POST (not GET) on selection-summary
        // because the SelectionRequest body can be ~360 KB worst case
        // (10K-id exclude list) — that's outside the safe URL length.
        group.MapPost("/selection-summary", SelectionSummaryAsync);
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
    /// <c>GET /api/ledgers/{ledgerId}/transactions</c>. Query params:
    /// <list type="bullet">
    ///   <item><description><c>account_id</c> — optional. When supplied,
    ///   narrows to one account within the ledger.</description></item>
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
        // Filters (mig 164). All optional; omitted ⇒ no-op.
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
        // Sort (mig 166). Display-order only; both whitelisted below.
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

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (account_id is { } scopedAccountId)
        {
            var belongs = await accounts.BelongsToLedgerAsync(
                ledgerId, scopedAccountId, cancellationToken).ConfigureAwait(false);
            if (!belongs)
                return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger.");
        }

        var filter = BuildFilter(search, date_from, date_to, amount_min, amount_max,
            security_id, tag, category_id, status, today);
        var sortSpec = string.IsNullOrWhiteSpace(sort)
            ? RegisterSort.Default
            : new RegisterSort(sort, Descending: dir is not "asc");

        var page = await register.GetPageAsync(
            ledgerId, account_id, cursor, effectiveDirection, starting_at,
            effectiveLimit, hidden ?? false, filter, sortSpec, cancellationToken).ConfigureAwait(false);
        return Results.Ok(page);
    }

    /// <summary>Allowed server-side status filter values (null = all). The
    /// Hidden view uses the separate <c>hidden</c> flag, not this.</summary>
    private static bool IsValidStatusFilter(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status is "cleared" or "uncleared" or "reconciling" or "scheduled" or "needs_review";

    /// <summary>Allowed sort columns (mig 166). Null/blank = the default
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
        string? tag, Guid? categoryId, string? status, DateOnly? today) =>
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
            Today: today);

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/transactions/index-buckets?account_id=...</c>.
    /// Returns one bucket per month with at least one visible entry on
    /// the requested account, ordered most-recent first. Drives the
    /// SPA's date-aware scroll-track (the custom replacement for the
    /// native browser scrollbar — Google Photos pattern).
    /// </summary>
    /// <remarks>
    /// <para><c>account_id</c> is required: the scroll-track is a
    /// per-account UX and the ledger-wide aggregate would change the
    /// "header counted once" semantics non-trivially.</para>
    ///
    /// <para>Each bucket carries an entry <c>count</c> and a
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
        // Filters (mig 164) — the rail must reflect the same set the page shows.
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
            security_id, tag, category_id, status, today);

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
            security_id, tag, category_id, status: null, today);

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
    ///   - Ledger visible to the caller.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateTransactionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        TransactionsRepository transactions,
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

        if (request.Tags is { } tags)
        {
            var tagsRejection = ValidateTags(tags);
            if (tagsRejection is not null) return tagsRejection;
        }

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

        var headerId = await transactions.CreateAsync(
            ledgerId,
            request.SourceAccountId,
            request.PostedAt,
            request.Payee,
            request.Memo,
            request.CheckNumber,
            request.TransactedAt,
            request.Postings,
            request.Tags,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/ledgers/{ledgerId}/transactions/{headerId}",
            new { headerId });
    }

    /// <summary>
    /// Per-tag validation (slice 2c.6b). Empty list is legal — it
    /// means "clear all tags." Each name is trimmed; empty-after-
    /// trim is a hard 422 (no silent drops). Name length and total
    /// count are capped to keep payloads predictable.
    /// </summary>
    private const int MaxTagNameLength = 64;
    private const int MaxTagsPerHeader = 20;

    private static IResult? ValidateTags(IReadOnlyList<string> tags)
    {
        if (tags.Count > MaxTagsPerHeader)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionTagsTooMany,
                $"At most {MaxTagsPerHeader} tags may be applied to one transaction.");
        foreach (var raw in tags)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionTagEmpty,
                    "Tag names cannot be empty or whitespace-only.");
            if (trimmed.Length > MaxTagNameLength)
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionTagTooLong,
                    $"Tag names must be {MaxTagNameLength} characters or fewer.");
        }
        return null;
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/transactions/{headerId}</c>.
    /// Apply user edits to a transaction in one atomic Postgres
    /// transaction: any subset of header fields (payee, memo,
    /// posted_at, transacted_at) plus any number of leg edits
    /// (amount, leg memo). Amount edits trigger a paired override
    /// on the counterparty leg so ADR-0019's sum-to-zero invariant
    /// holds server-side — the client never gets to violate it.
    ///
    /// Validation:
    ///   - Empty patch (no header fields AND no leg edits) → 422
    ///     <c>transaction-patch-empty</c>. Forces the SPA to send
    ///     only real edits and keeps the override tables free of
    ///     no-op writes.
    ///   - Cross-ledger header probe → 422
    ///     <c>transaction-not-in-ledger</c> instead of leaking via a
    ///     silent RLS-scoped no-op.
    ///   - Leg id doesn't belong to the header in the URL → 422
    ///     <c>transaction-leg-not-in-header</c>; the whole patch is
    ///     rolled back.
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasHeaderField =
            request.Payee is not null
            || request.Memo is not null
            || request.CheckNumber is not null
            || request.PostedAt is not null
            || request.TransactedAt is not null;
        var hasPostings = request.Postings is not null;
        // Slice 2c.6a: `approve: true` alone is a valid PATCH — it
        // clears needs_review without other edits (the user accepted
        // a bank-feed row as-is). Empty-body 422 still fires when
        // approve is absent / false AND no other field is supplied.
        var hasApprove = request.Approve == true;
        // Slice 2c.6b: `tags: []` (clear) and `tags: [...]` (set)
        // are both valid standalone PATCHes.
        var hasTags = request.Tags is not null;
        // Slice 2c.6d: a merge stamp alone is also a valid PATCH
        // (rare, but supports "merge without editing anything else").
        var hasMerge = request.MergeFromHeaderId is not null;
        if (!hasHeaderField && !hasPostings && !hasApprove && !hasTags && !hasMerge)
            return BusinessError.Problem(BusinessError.Codes.TransactionPatchEmpty,
                "Supply at least one header field, a postings reshape, tags, approve=true, or mergeFromHeaderId.");

        if (request.Postings is { } postings)
        {
            if (postings.SourceAccountId == Guid.Empty)
                return BusinessError.Problem(BusinessError.Codes.TransactionAccountRequired,
                    "postings.sourceAccountId is required.");
            var rejection = PostingValidation.ValidatePostings(postings.Items, postings.SourceAccountId);
            if (rejection is not null) return rejection;
        }

        if (request.Tags is { } tags)
        {
            var tagsRejection = ValidateTags(tags);
            if (tagsRejection is not null) return tagsRejection;
        }

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (request.Postings is { } postings2)
        {
            var accountsRejection = await PostingValidation.ValidatePostingAccountsAsync(
                ledgerId, postings2.SourceAccountId, postings2.Items, accounts,
                cancellationToken).ConfigureAwait(false);
            if (accountsRejection is not null) return accountsRejection;
        }

        var outcome = await transactions.PatchAsync(
            ledgerId, headerId, request, cancellationToken).ConfigureAwait(false);
        if (outcome != TransactionsRepository.PatchResult.Ok)
        {
            return outcome switch
            {
                TransactionsRepository.PatchResult.HeaderNotInLedger =>
                    BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                        "Transaction does not belong to this ledger."),
                TransactionsRepository.PatchResult.PostingsLegNotInHeader =>
                    BusinessError.Problem(BusinessError.Codes.TransactionPostingLegNotInHeader,
                        "A posting's legId does not match any existing leg on this transaction."),
                TransactionsRepository.PatchResult.PostingsSourceAccountMismatch =>
                    BusinessError.Problem(BusinessError.Codes.TransactionSourceAccountMismatch,
                        "The supplied sourceAccountId does not match the transaction's source-side legs."),
                TransactionsRepository.PatchResult.MergeSourceInvalid =>
                    BusinessError.Problem(BusinessError.Codes.MergeSourceInvalid,
                        "The row you're merging is no longer a fresh review row, or mergeFromHeaderId isn't a settled, visible transaction in this ledger."),
                TransactionsRepository.PatchResult.HeaderNotBankShape =>
                    BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                        "Header is an investment transaction; use /api/ledgers/{ledgerId}/investment-transactions/{headerId}."),
                _ => Results.Problem("Unknown patch result.", statusCode: 500),
            };
        }

        // PATCH succeeded. When the caller supplies an account_id
        // (the register view they're currently looking at), return
        // the freshly-resolved entry so the SPA can patch it into
        // the window via `mutateEntries` — preserving scroll
        // position vs. a full window refresh. Falls back to the
        // postings request's SourceAccountId when present.
        //
        // Inverted-merge direction: when MergeFromHeaderId is set,
        // the editor row (headerId) is now the LOSER and is
        // invisible in the register. The survivor is the
        // candidate (MergeFromHeaderId). Resolve THAT row so the
        // SPA can refocus onto it after the editor row vanishes.
        var resolveAccountId = account_id ?? request.Postings?.SourceAccountId;
        var survivingHeaderId = request.MergeFromHeaderId ?? headerId;
        if (resolveAccountId is { } rid)
        {
            var entry = await register.GetEntryForHeaderAsync(
                survivingHeaderId, rid, cancellationToken).ConfigureAwait(false);
            if (entry is not null) return Results.Ok(entry);
        }
        return Results.NoContent();
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
    /// as "Possible matches" chips. Same probe-safety contract as
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
    /// <c>POST /api/ledgers/{ledgerId}/transactions/{headerId}/unhide</c>
    /// (ADR-0072 D2). Un-hide a soft-hidden transaction so it returns to the
    /// register. Idempotent — un-hiding a visible row is a no-op (204).
    /// </summary>
    private static async Task<IResult> UnhideAsync(
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

        var outcome = await transactions.UnhideAsync(ledgerId, headerId, cancellationToken)
                                         .ConfigureAwait(false);
        return outcome switch
        {
            TransactionsRepository.UnhideOutcome.Unhidden => Results.NoContent(),
            TransactionsRepository.UnhideOutcome.NotHidden => Results.NoContent(),
            TransactionsRepository.UnhideOutcome.HeaderNotFound =>
                BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                    "Transaction does not belong to this ledger."),
            _ => Results.Problem("Unknown unhide result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/selection-summary</c>
    /// (ADR-0024). Returns the count and account-scoped sum for the
    /// supplied selection — drives the bulk-action footer's
    /// "N selected · Σ $X.XX" readout. The SPA debounces this against
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
        return Results.Ok(summary ?? new SelectionSummary(0, null));
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

        if (!ValidReconStatuses.Contains(request.Status))
            return BusinessError.Problem(
                BusinessError.Codes.TransactionReconStatusInvalid,
                "status must be one of: uncleared, reconciling, cleared.");

        if (request.AccountId == Guid.Empty)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionAccountRequired,
                "accountId is required — reconciliation status is per-account.");

        var rejection = ValidateSelection(request.Selection);
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

        var (hardDeleted, softHidden) = await bulk.BulkDeleteAsync(
            ledgerId, request.Selection, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BulkDeleteResponse(hardDeleted, softHidden));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-unhide</c> (ADR-0072
    /// D2). Un-hide every (hidden) header in the selection in one transaction,
    /// recomputing balances + holdings. The selection carries
    /// <c>statusFilter="hidden"</c> so it scopes to the Hidden view.
    /// </summary>
    private static async Task<IResult> BulkUnhideAsync(
        Guid ledgerId,
        BulkUnhideRequest request,
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

        var unhidden = await bulk.BulkUnhideAsync(
            ledgerId, request.Selection, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BulkUnhideResponse(unhidden));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/transactions/{headerId}/move-account</c>
    /// (ADR-0072 D3). Move one bank-shape transaction from its source account to
    /// another real account, recomputing both balances.
    /// </summary>
    private static async Task<IResult> MoveAccountAsync(
        Guid ledgerId,
        Guid headerId,
        MoveAccountRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BulkTransactionsRepository bulk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await bulk.MoveAccountAsync(
            ledgerId, headerId, request.SourceAccountId, request.TargetAccountId,
            cancellationToken).ConfigureAwait(false);
        return MoveOutcomeToResult(outcome);
    }

    private static IResult MoveOutcomeToResult(
        BulkTransactionsRepository.MoveAccountOutcome outcome) => outcome switch
    {
        BulkTransactionsRepository.MoveAccountOutcome.Moved => Results.NoContent(),
        BulkTransactionsRepository.MoveAccountOutcome.HeaderNotFound =>
            BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                "Transaction does not belong to this ledger."),
        BulkTransactionsRepository.MoveAccountOutcome.NotBankShape =>
            BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                "Investment transactions cannot be moved with this endpoint."),
        BulkTransactionsRepository.MoveAccountOutcome.NotOnSourceAccount =>
            BusinessError.Problem(BusinessError.Codes.TransactionSourceAccountMismatch,
                "The transaction has no leg on the given source account."),
        BulkTransactionsRepository.MoveAccountOutcome.TargetInvalid =>
            BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetInvalid,
                "Target must be a real account in this ledger, not a category."),
        BulkTransactionsRepository.MoveAccountOutcome.TargetSameAsSource =>
            BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetSameAsSource,
                "Source and target accounts are the same."),
        BulkTransactionsRepository.MoveAccountOutcome.SplitToInvestment =>
            BusinessError.Problem(BusinessError.Codes.TransactionMoveSplitToInvestment,
                "A split transaction cannot be moved to an investment account."),
        BulkTransactionsRepository.MoveAccountOutcome.Collision =>
            BusinessError.Problem(BusinessError.Codes.TransactionMoveCollision,
                "The target account is already part of this transaction (would collide with an existing leg)."),
        _ => Results.Problem("Unknown move result.", statusCode: 500),
    };

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

        var (outcome, moved) = await bulk.BulkMoveAccountAsync(
            ledgerId, request.Selection, request.TargetAccountId,
            cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            BulkTransactionsRepository.BulkMoveOutcome.Moved =>
                Results.Ok(new BulkMoveAccountResponse(moved)),
            BulkTransactionsRepository.BulkMoveOutcome.TargetInvalid =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetInvalid,
                    "Target must be a real account in this ledger, not a category."),
            BulkTransactionsRepository.BulkMoveOutcome.SourceScopeRequired =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveSourceRequired,
                    "Bulk move requires an account-scoped selection (selection.accountId)."),
            BulkTransactionsRepository.BulkMoveOutcome.TargetSameAsSource =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveTargetSameAsSource,
                    "Source and target accounts are the same."),
            BulkTransactionsRepository.BulkMoveOutcome.InvestmentShape =>
                BusinessError.Problem(BusinessError.Codes.TransactionHeaderIsInvestment,
                    "One or more selected transactions are investment transactions, which cannot be moved with this endpoint."),
            BulkTransactionsRepository.BulkMoveOutcome.SplitToInvestment =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveSplitToInvestment,
                    "One or more selected split transactions cannot be moved to an investment account."),
            BulkTransactionsRepository.BulkMoveOutcome.Collision =>
                BusinessError.Problem(BusinessError.Codes.TransactionMoveCollision,
                    "One or more selected transactions already have a leg on the target account."),
            _ => Results.Problem("Unknown move result.", statusCode: 500),
        };
    }
}
