using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Spending against what is typical for the category, and the targets a person
/// typed instead.
/// </summary>
/// <remarks>
/// The first REST surface over <see cref="ReportingRepository"/>, which until
/// now was reachable only from the MCP tools — the app computed spend by
/// category and would show it to an AI client but not to the person whose money
/// it was.
/// </remarks>
public static class BudgetEndpoints
{
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/budget")
                          .RequireAuthorization()
                          .RequireLedgerAccess();
        group.MapGet("/progress", GetProgressAsync);
        group.MapGet("/transactions", GetTransactionsAsync);
        group.MapPut("/targets", SetTargetAsync);
        group.MapDelete("/targets/{categoryId:guid}", DeleteTargetAsync);
        group.MapPost("/targets/fill", FillTargetsAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/budget/progress?month=yyyy-MM&amp;window=3</c>
    /// — one month's spend per category beside the mean of the trailing
    /// <c>window</c> complete months. <c>today</c> is the caller's local date: it
    /// picks the default month and splits off the rows the register shows as
    /// Scheduled.
    /// <para>422 throughout — <c>BusinessError.Problem</c> has no 400 path:
    /// <c>ledger-not-visible</c>, an unparseable month, or a window outside 1..24.</para>
    /// </summary>
    private static async Task<IResult> GetProgressAsync(
        Guid ledgerId,
        string? month,
        int? window,
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BudgetProgressRepository budget,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Parsed here rather than accepted as a DateTime so the wire format is a
        // MONTH and cannot smuggle in a day or a time that would silently shift
        // the window. Round-trip through UTC midnight on the first of the month.
        DateTime monthStartUtc;
        if (string.IsNullOrWhiteSpace(month))
        {
            var local = ScheduledBoundary.Today(today, clock);
            monthStartUtc = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        else if (DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            monthStartUtc = new DateTime(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        else
        {
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "month must be formatted yyyy-MM.");
        }

        var windowMonths = window ?? BudgetProgressRepository.DefaultWindowMonths;
        if (windowMonths is < 1 or > 24)
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "window must be between 1 and 24 months.");

        var dto = await budget.GetAsync(ledgerId, monthStartUtc, windowMonths, today, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(dto);
    }

    /// <summary>
    /// <c>PUT /api/ledgers/{ledgerId}/budget/targets</c> — set one category's
    /// target for one month, inserting or replacing.
    /// <para>422 on everything refused: <c>ledger-not-visible</c>, an unparseable
    /// month, a negative amount, an id that is not a category, or an ADR-0099 D1a
    /// conflict — whose detail NAMES the category already holding a target,
    /// because it can be several levels away and off screen.</para>
    /// </summary>
    private static async Task<IResult> SetTargetAsync(
        Guid ledgerId,
        SetBudgetTargetRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BudgetTargetsRepository targets,
        CancellationToken cancellationToken)
    {
        if (await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false) is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!TryParseMonth(request.Month, out var month))
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "month must be formatted yyyy-MM.");

        // Refused here as well as by the column CHECK. The CHECK is the backstop;
        // a 23514 surfacing as a 500 is not an answer anyone can act on.
        if (!IsAllowedTargetAmount(request.Amount))
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "A target cannot be negative. Zero is allowed, and means spending nothing here.");

        var outcome = await targets
            .SetAsync(ledgerId, request.CategoryId, month, request.Amount, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Result switch
        {
            BudgetTargetsRepository.SetResult.Ok => Results.NoContent(),
            BudgetTargetsRepository.SetResult.AmountPlacesExceedCurrency =>
                MoneyPlaces.Refusal(outcome.CurrencyCode!, "amount"),
            BudgetTargetsRepository.SetResult.CategoryNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "That category is not in this ledger."),
            BudgetTargetsRepository.SetResult.NotACategory =>
                BusinessError.Problem(BusinessError.Codes.AccountNotACategory,
                    "A budget target can only be set on a category."),
            BudgetTargetsRepository.SetResult.AncestorHasTarget =>
                BusinessError.Problem(BusinessError.Codes.BudgetTargetAncestorConflict,
                    $"'{outcome.ConflictingCategoryName}' already has a target this month, and it covers "
                    + "everything beneath it. Clear that one first, or set this amount there instead."),
            BudgetTargetsRepository.SetResult.DescendantHasTarget =>
                BusinessError.Problem(BusinessError.Codes.BudgetTargetDescendantConflict,
                    $"'{outcome.ConflictingCategoryName}' already has a target this month. A target here "
                    + "would cover it a second time — clear the one below first."),
            _ => Results.Problem("Unhandled budget target result."),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/budget/targets/{categoryId}?month=yyyy-MM</c>
    /// — return the category to its derived normal.
    /// <para>204 whether or not a row was there. ADR-0099 D1 makes PRESENCE the
    /// state, so "no target" is a legitimate destination and a caller clearing an
    /// already-clear cell has got what it asked for; 404 would invite the UI to
    /// raise an error for a no-op.</para>
    /// </summary>
    private static async Task<IResult> DeleteTargetAsync(
        Guid ledgerId,
        Guid categoryId,
        string? month,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BudgetTargetsRepository targets,
        CancellationToken cancellationToken)
    {
        if (await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false) is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!TryParseMonth(month, out var parsed))
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "month must be formatted yyyy-MM.");

        await targets.DeleteAsync(ledgerId, categoryId, parsed, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/budget/targets/fill</c> — many targets for
    /// one month, in one transaction. Backs "copy last month" and "fill from the
    /// N-month average".
    /// <para>200 with a count and the entries that were SKIPPED, rather than
    /// failing the batch on the first conflict: a fill is a convenience over rows
    /// the user can already see, and refusing all thirty because one is illegal is
    /// worse than applying twenty-nine and naming the one that was not.</para>
    /// <para>A typed amount with more places than the reporting currency allows is not a
    /// conflict to skip: the whole fill is refused with <c>amount-places-exceed-currency</c>,
    /// naming the first such entry, and nothing is written (ADR-0114 D15). A copy is not
    /// typed, so its figures are not checked.</para>
    /// </summary>
    private static async Task<IResult> FillTargetsAsync(
        Guid ledgerId,
        FillBudgetTargetsRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BudgetTargetsRepository targets,
        CancellationToken cancellationToken)
    {
        if (await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false) is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!TryParseMonth(request.Month, out var month))
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "month must be formatted yyyy-MM.");

        var entries = request.Entries ?? [];
        IReadOnlyList<BudgetTargetsRepository.BulkEntryOutcome> outcomes;
        if (request.CopyFromMonth is not null)
        {
            // Copy last month (ADR-0114 D12): the server copies the stored targets, each in
            // the currency it was typed in, so the client sends the month and no amounts.
            if (entries.Count > 0)
                return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                    "A copy names the month to copy from and carries no entries.");
            if (!TryParseMonth(request.CopyFromMonth, out var source))
                return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                    "copyFromMonth must be formatted yyyy-MM.");
            outcomes = await targets.CopyMonthAsync(ledgerId, source, month, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // A ledger has hundreds of categories, not thousands. The cap is here so a
            // malformed client cannot turn one click into an unbounded write.
            if (entries.Count > MaxFillEntries)
                return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                    $"A fill carries at most {MaxFillEntries} entries.");
            if (entries.Any(e => !IsAllowedTargetAmount(e.Amount)))
                return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                    "A target cannot be negative.");

            outcomes = await targets.SetManyAsync(
                ledgerId, month,
                entries.Select(e => (e.CategoryId, e.Amount)).ToList(),
                cancellationToken).ConfigureAwait(false);

            // A typed amount that does not fit the reporting currency refuses the whole fill,
            // with nothing written (ADR-0114 D15). The refusal names the first such entry.
            if (outcomes.FirstOrDefault(o => o.Result == BudgetTargetsRepository.SetResult.AmountPlacesExceedCurrency)
                is { CurrencyCode: { } unfitCurrency } unfit)
            {
                var index = entries.Select((e, i) => (e.CategoryId, i)).First(x => x.CategoryId == unfit.CategoryId).i;
                return MoneyPlaces.Refusal(unfitCurrency, $"entries[{index}].amount");
            }
        }

        var skipped = outcomes
            .Where(o => o.Result != BudgetTargetsRepository.SetResult.Ok)
            .Select(o => new BudgetTargetEntryResult(
                o.CategoryId, StatusOf(o.Result), o.ConflictingCategoryName))
            .ToList();

        return Results.Ok(new FillBudgetTargetsResponse(
            Applied: outcomes.Count - skipped.Count,
            Skipped: skipped));
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/budget/transactions?month=yyyy-MM&amp;categoryId=…</c>
    /// — the month's transactions for one category AND everything beneath it.
    /// <para>The subtree, not the direct postings: the budget table rolls up, so
    /// a row's figure already contains its descendants and a direct-only list
    /// would not add up to the number the reader just clicked. Expanded
    /// server-side so a click costs one request rather than one per
    /// descendant.</para>
    /// </summary>
    private static async Task<IResult> GetTransactionsAsync(
        Guid ledgerId,
        string? month,
        Guid categoryId,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BudgetProgressRepository budget,
        ReportingRepository reporting,
        CancellationToken cancellationToken)
    {
        if (await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false) is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!TryParseMonth(month, out var first))
            return BusinessError.Problem(BusinessError.Codes.BudgetRangeInvalid,
                "month must be formatted yyyy-MM.");

        var fromUtc = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var subtree = await budget
            .CategorySubtreeAsync(ledgerId, categoryId, cancellationToken)
            .ConfigureAwait(false);

        var page = await reporting.ListTransactionsAsync(new TransactionQuery
        {
            LedgerId = ledgerId,
            CategoryIds = subtree,
            FromUtc = fromUtc,
            ToUtc = fromUtc.AddMonths(1),
            // Register order, newest first — the shape a reader already knows.
            // Note this changes what the LIMIT keeps: the most recent rather than
            // the largest. That is the right trade for a list whose job is "what
            // is in this category", and the caller says which it is showing.
            Sort = TransactionSort.Date,
            Descending = true,
            Limit = Math.Clamp(limit ?? 25, 1, 200),
        }, cancellationToken).ConfigureAwait(false);

        return Results.Ok(page);
    }

    /// <summary>Upper bound on one fill; see the call site for why.</summary>
    private const int MaxFillEntries = 500;

    private static string StatusOf(BudgetTargetsRepository.SetResult r) => r switch
    {
        BudgetTargetsRepository.SetResult.Ok => "ok",
        BudgetTargetsRepository.SetResult.AncestorHasTarget => "ancestor-has-target",
        BudgetTargetsRepository.SetResult.DescendantHasTarget => "descendant-has-target",
        BudgetTargetsRepository.SetResult.NotACategory => "not-a-category",
        BudgetTargetsRepository.SetResult.CategoryNotInLedger => "category-not-in-ledger",
        _ => "unknown",
    };

    /// <summary>
    /// Whether a typed target may be written: zero or more. Zero means spending nothing
    /// in the category; a negative target means nothing, and the column CHECK would
    /// refuse it as a 500. The rule the repository leaves to its callers, so every
    /// writer of a typed target (these endpoints and the Demo's examples, ADR-0115 D3)
    /// applies the same one.
    /// </summary>
    internal static bool IsAllowedTargetAmount(decimal amount) => amount >= 0m;

    /// <summary>
    /// <c>yyyy-MM</c> to the first of that month. Shared by all three write paths,
    /// so the wire format cannot drift between them — a month, never a date.
    /// </summary>
    private static bool TryParseMonth(string? month, out DateOnly first)
    {
        first = default;
        if (string.IsNullOrWhiteSpace(month)) return false;
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        first = new DateOnly(parsed.Year, parsed.Month, 1);
        return true;
    }
}
