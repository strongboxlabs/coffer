using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Endpoint group for recurring-reminder series (ADR-0047). Read surface
/// (list + upcoming + detail), manual authoring (create/edit per shape +
/// disable + skip), and fire. Series are managed here; the live
/// register/balances never see their templates (live_txn_headers view +
/// recompute exclusion, mig 124 / ADR-0048). Create/edit fork by transaction
/// shape (bank vs investment), mirroring the live /transactions vs
/// /investment-transactions split; everything else is shape-agnostic.
/// </summary>
public static class RemindersEndpoints
{
    public static IEndpointRouteBuilder MapRemindersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/ledgers/{ledgerId:guid}/reminders")
            .RequireAuthorization()
            .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapGet("/upcoming", UpcomingAsync);
        group.MapGet("/{reminderId:guid}", GetByIdAsync);
        group.MapPost("/", CreateBankAsync);
        group.MapPost("/investment", CreateInvestmentAsync);
        group.MapPatch("/{reminderId:guid}", EditBankAsync);
        group.MapPatch("/{reminderId:guid}/investment", EditInvestmentAsync);
        group.MapPatch("/{reminderId:guid}/active", SetActiveAsync);
        group.MapPost("/{reminderId:guid}/skip", SkipAsync);
        group.MapPost("/{reminderId:guid}/fire", FireAsync);
        group.MapPost("/{reminderId:guid}/fire/bank", FireBankAsync);
        group.MapPost("/{reminderId:guid}/fire/investment", FireInvestmentAsync);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/reminders/upcoming?from&amp;to</c> — the
    /// agenda/calendar data. The window is clamped to ~2 years so RRULE
    /// expansion stays bounded.
    /// </summary>
    private static async Task<IResult> UpcomingAsync(
        Guid ledgerId,
        DateOnly from,
        DateOnly to,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var cappedTo = to > from.AddDays(732) ? from.AddDays(732) : to;
        var list = await reminders.GetUpcomingAsync(ledgerId, from, cappedTo, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(list);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/reminders</c> — the reminders management
    /// list (one row per series).
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var list = await reminders.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(list);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders/{reminderId}/fire</c> —
    /// materialize the occurrence as a committed transaction (idempotent).
    /// </summary>
    private static async Task<IResult> FireAsync(
        Guid ledgerId,
        Guid reminderId,
        Contracts.FireReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Clone the template verbatim (no edits). Adjust-at-post goes through
        // /fire/bank or /fire/investment.
        var result = await reminders.FireAsync(
            ledgerId, reminderId, request.OccurrenceDate, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            RemindersRepository.FireOutcome.Ok =>
                Results.Ok(new Contracts.FireReminderResponse(
                    result.HeaderId!.Value, result.SkippedEarlierCount, result.SkippedEarlierFrom)),
            RemindersRepository.FireOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger,
                    "Reminder not found in this ledger."),
            RemindersRepository.FireOutcome.NotMaterialized =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                    "Reminder has no template yet — re-import it from Moneydance."),
            RemindersRepository.FireOutcome.OccurrenceSkipped =>
                BusinessError.Problem(BusinessError.Codes.ReminderOccurrenceSkipped,
                    "That occurrence was skipped; un-skip it before firing."),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders/{reminderId}/fire/bank</c> —
    /// adjust-at-post for a BANK series: commit the edited transaction (incl.
    /// splits) as the occurrence, reusing the live bank create (ADR-0049).
    /// </summary>
    private static async Task<IResult> FireBankAsync(
        Guid ledgerId,
        Guid reminderId,
        Contracts.FireBankReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate the EDITED postings exactly like a bank create (shape first).
        if (PostingValidation.ValidatePostings(request.Postings, request.SourceAccountId) is { } pr) return pr;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, request.SourceAccountId, cancellationToken)
                .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.TransactionAccountIsInvestment,
                "sourceAccountId is not a bank-shape account.");
        if (await PostingValidation.ValidatePostingAccountsAsync(
                ledgerId, request.SourceAccountId, request.Postings, accounts, cancellationToken)
                .ConfigureAwait(false) is { } ar)
            return ar;

        var result = await reminders.FireBankAsync(
            ledgerId, reminderId, request.OccurrenceDate, request, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            RemindersRepository.FireOutcome.Ok =>
                Results.Ok(new Contracts.FireReminderResponse(
                    result.HeaderId!.Value, result.SkippedEarlierCount, result.SkippedEarlierFrom)),
            RemindersRepository.FireOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger."),
            RemindersRepository.FireOutcome.NotMaterialized =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                    "Reminder has no template yet — re-import it from Moneydance."),
            RemindersRepository.FireOutcome.OccurrenceSkipped =>
                BusinessError.Problem(BusinessError.Codes.ReminderOccurrenceSkipped,
                    "That occurrence was skipped; un-skip it before firing."),
            RemindersRepository.FireOutcome.ShapeMismatch =>
                BusinessError.Problem(BusinessError.Codes.ReminderShapeMismatch,
                    "This is an investment reminder; use the investment fire route."),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders/{reminderId}/fire/investment</c>
    /// — adjust-at-post for an INVESTMENT series: commit the edited investment
    /// transaction as the occurrence (real holdings/lots), reusing the live
    /// investment create path (ADR-0049).
    /// </summary>
    private static async Task<IResult> FireInvestmentAsync(
        Guid ledgerId,
        Guid reminderId,
        Contracts.FireInvestmentReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.FireInvestmentAsync(
            ledgerId, reminderId, request.OccurrenceDate, request.Transaction, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            RemindersRepository.FireOutcome.Ok =>
                Results.Ok(new Contracts.FireReminderResponse(
                    result.HeaderId!.Value, result.SkippedEarlierCount, result.SkippedEarlierFrom)),
            RemindersRepository.FireOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger."),
            RemindersRepository.FireOutcome.NotMaterialized =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                    "Reminder has no template yet — re-import it from Moneydance."),
            RemindersRepository.FireOutcome.OccurrenceSkipped =>
                BusinessError.Problem(BusinessError.Codes.ReminderOccurrenceSkipped,
                    "That occurrence was skipped; un-skip it before firing."),
            RemindersRepository.FireOutcome.ShapeMismatch =>
                BusinessError.Problem(BusinessError.Codes.ReminderShapeMismatch,
                    "This is a bank reminder; use the bank fire route."),
            RemindersRepository.FireOutcome.ShapeFailure =>
                InvestmentFailureProblem(result.InvestmentFailure!.Value),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>Map an investment validation failure to its 422 (shared with the
    /// investment create endpoint's vocabulary).</summary>
    private static IResult InvestmentFailureProblem(InvestmentTransactionsRepository.CreateFailure failure)
    {
        var (code, message) = InvestmentTransactionsEndpoints.MapFailure(failure);
        return BusinessError.Problem(code, message);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/reminders/{reminderId}</c> — series detail
    /// (recurrence + the template's legs). Drives the editor's load.
    /// </summary>
    private static async Task<IResult> GetByIdAsync(
        Guid ledgerId,
        Guid reminderId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var detail = await reminders.GetDetailAsync(ledgerId, reminderId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger,
                "Reminder not found in this ledger.")
            : Results.Ok(detail);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders</c> — create a BANK-shape
    /// reminder series.
    /// </summary>
    private static async Task<IResult> CreateBankAsync(
        Guid ledgerId,
        CreateReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RemindersRepository reminders,
        RecurrenceExpander expander,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateRecurrence(expander, request.Rrule, request.StartDate, request.EndDate,
                request.AutoCommitDaysBefore) is { } recurrenceRejection)
            return recurrenceRejection;
        if (PostingValidation.ValidatePostings(request.Postings, request.SourceAccountId) is { } postingRejection)
            return postingRejection;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        // Bank endpoint serves bank-shape source accounts only (ADR-0029).
        if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, request.SourceAccountId, cancellationToken)
                .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.TransactionAccountIsInvestment,
                "sourceAccountId is not a bank-shape account; use /reminders/investment for investment reminders.");

        if (await PostingValidation.ValidatePostingAccountsAsync(
                ledgerId, request.SourceAccountId, request.Postings, accounts, cancellationToken)
                .ConfigureAwait(false) is { } accountsRejection)
            return accountsRejection;

        var result = await reminders.CreateBankAsync(ledgerId, request, cancellationToken).ConfigureAwait(false);
        return await CreatedDetailAsync(ledgerId, result.ReminderId!.Value, reminders, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders/investment</c> — create an
    /// INVESTMENT-shape reminder series (shape validated by the shared
    /// investment build core).
    /// </summary>
    private static async Task<IResult> CreateInvestmentAsync(
        Guid ledgerId,
        CreateInvestmentReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        RecurrenceExpander expander,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateRecurrence(expander, request.Rrule, request.StartDate, request.EndDate,
                request.AutoCommitDaysBefore) is { } recurrenceRejection)
            return recurrenceRejection;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.CreateInvestmentAsync(ledgerId, request, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == RemindersRepository.CreateOutcome.ShapeFailure)
        {
            var (code, message) = InvestmentTransactionsEndpoints.MapFailure(result.InvestmentFailure!.Value);
            return BusinessError.Problem(code, message);
        }
        return await CreatedDetailAsync(ledgerId, result.ReminderId!.Value, reminders, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}</c> — edit a
    /// BANK-shape series.
    /// </summary>
    private static async Task<IResult> EditBankAsync(
        Guid ledgerId,
        Guid reminderId,
        EditReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RemindersRepository reminders,
        RecurrenceExpander expander,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasField = request.Rrule is not null || request.StartDate is not null
            || request.ClearEndDate || request.EndDate is not null
            || request.ClearAutoCommit || request.AutoCommitDaysBefore is not null
            || request.Payee is not null || request.Memo is not null
            || request.CheckNumber is not null || request.Postings is not null;
        if (!hasField)
            return BusinessError.Problem(BusinessError.Codes.ReminderPatchEmpty,
                "Supply at least one recurrence field, a header field, or postings.");

        if (request.Rrule is { } rrule && !expander.IsValidRrule(rrule))
            return BusinessError.Problem(BusinessError.Codes.ReminderRruleInvalid, "rrule is not a valid RFC-5545 rule.");
        if (request.EndDate is { } end && request.StartDate is { } start && end < start)
            return BusinessError.Problem(BusinessError.Codes.ReminderEndBeforeStart, "endDate must be on or after startDate.");
        if (request.AutoCommitDaysBefore is { } ac && ac < 0)
            return BusinessError.Problem(BusinessError.Codes.ReminderAutoCommitNegative, "autoCommitDaysBefore must be >= 0.");

        if (request.Postings is { } postings)
        {
            if (PostingValidation.ValidatePostings(postings.Items, postings.SourceAccountId) is { } pr) return pr;
        }

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        if (request.Postings is { } postings2)
        {
            if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, postings2.SourceAccountId, cancellationToken)
                    .ConfigureAwait(false))
                return BusinessError.Problem(BusinessError.Codes.TransactionAccountIsInvestment,
                    "sourceAccountId is not a bank-shape account.");
            if (await PostingValidation.ValidatePostingAccountsAsync(
                    ledgerId, postings2.SourceAccountId, postings2.Items, accounts, cancellationToken)
                    .ConfigureAwait(false) is { } ar)
                return ar;
        }

        var result = await reminders.EditBankAsync(ledgerId, reminderId, request, cancellationToken).ConfigureAwait(false);
        return await EditOutcomeResultAsync(ledgerId, reminderId, result, reminders, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}/investment</c> —
    /// edit an INVESTMENT-shape series (shape validated by the build core).
    /// </summary>
    private static async Task<IResult> EditInvestmentAsync(
        Guid ledgerId,
        Guid reminderId,
        EditInvestmentReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        RecurrenceExpander expander,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasField = request.Rrule is not null || request.StartDate is not null
            || request.ClearEndDate || request.EndDate is not null
            || request.ClearAutoCommit || request.AutoCommitDaysBefore is not null
            || request.Transaction is not null;
        if (!hasField)
            return BusinessError.Problem(BusinessError.Codes.ReminderPatchEmpty,
                "Supply at least one recurrence field or a transaction shape.");

        if (request.Rrule is { } rrule && !expander.IsValidRrule(rrule))
            return BusinessError.Problem(BusinessError.Codes.ReminderRruleInvalid, "rrule is not a valid RFC-5545 rule.");
        if (request.EndDate is { } end && request.StartDate is { } start && end < start)
            return BusinessError.Problem(BusinessError.Codes.ReminderEndBeforeStart, "endDate must be on or after startDate.");
        if (request.AutoCommitDaysBefore is { } ac && ac < 0)
            return BusinessError.Problem(BusinessError.Codes.ReminderAutoCommitNegative, "autoCommitDaysBefore must be >= 0.");

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.EditInvestmentAsync(ledgerId, reminderId, request, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == RemindersRepository.EditOutcome.ShapeFailure)
        {
            var (code, message) = InvestmentTransactionsEndpoints.MapFailure(result.InvestmentFailure!.Value);
            return BusinessError.Problem(code, message);
        }
        return await EditOutcomeResultAsync(ledgerId, reminderId, result, reminders, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}/active</c> —
    /// disable/enable a series (soft, never deletes).
    /// </summary>
    private static async Task<IResult> SetActiveAsync(
        Guid ledgerId,
        Guid reminderId,
        SetReminderActiveRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var outcome = await reminders.SetActiveAsync(ledgerId, reminderId, request.Active, cancellationToken)
            .ConfigureAwait(false);
        return outcome == RemindersRepository.ActiveOutcome.Ok
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger.");
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/reminders/{reminderId}/skip</c> —
    /// suppress one occurrence (ADR-0047 D6).
    /// </summary>
    private static async Task<IResult> SkipAsync(
        Guid ledgerId,
        Guid reminderId,
        SkipReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.SkipAsync(
            ledgerId, reminderId, request.OccurrenceDate, currentUser.UserId, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            RemindersRepository.SkipOutcome.Ok =>
                Results.Ok(new SkipReminderResponse(
                    request.OccurrenceDate, result.NextDueDate,
                    result.SkippedEarlierCount, result.SkippedEarlierFrom)),
            RemindersRepository.SkipOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger."),
            RemindersRepository.SkipOutcome.NotMaterialized =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                    "Reminder has no template yet — re-import it from Moneydance."),
            RemindersRepository.SkipOutcome.AlreadyFired =>
                BusinessError.Problem(BusinessError.Codes.ReminderOccurrenceAlreadyFired,
                    "That occurrence was already fired into a committed transaction."),
            _ => Results.StatusCode(500),
        };
    }

    // ----- shared handler helpers -----

    private static async Task<IResult?> NotVisibleAsync(
        LedgersRepository ledgers, ICurrentUserAccessor currentUser, Guid ledgerId, CancellationToken ct)
    {
        var visible = await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, ct).ConfigureAwait(false);
        return visible is null
            ? BusinessError.Problem(BusinessError.Codes.LedgerNotVisible, "Ledger not found or not visible to this user.")
            : null;
    }

    /// <summary>Shared recurrence-field shape validation for the two create
    /// paths (rrule valid + start present + end ≥ start + autocommit ≥ 0).</summary>
    private static IResult? ValidateRecurrence(
        RecurrenceExpander expander, string rrule, DateOnly startDate, DateOnly? endDate, int? autoCommitDaysBefore)
    {
        if (!expander.IsValidRrule(rrule))
            return BusinessError.Problem(BusinessError.Codes.ReminderRruleInvalid, "rrule is not a valid RFC-5545 rule.");
        if (startDate == default)
            return BusinessError.Problem(BusinessError.Codes.ReminderStartDateRequired, "startDate is required.");
        if (endDate is { } end && end < startDate)
            return BusinessError.Problem(BusinessError.Codes.ReminderEndBeforeStart, "endDate must be on or after startDate.");
        if (autoCommitDaysBefore is { } ac && ac < 0)
            return BusinessError.Problem(BusinessError.Codes.ReminderAutoCommitNegative, "autoCommitDaysBefore must be >= 0.");
        return null;
    }

    private static async Task<IResult> CreatedDetailAsync(
        Guid ledgerId, Guid reminderId, RemindersRepository reminders, CancellationToken ct)
    {
        var detail = await reminders.GetDetailAsync(ledgerId, reminderId, ct).ConfigureAwait(false);
        return Results.Created($"/api/ledgers/{ledgerId}/reminders/{reminderId}", detail);
    }

    private static async Task<IResult> EditOutcomeResultAsync(
        Guid ledgerId, Guid reminderId, RemindersRepository.EditReminderResult result,
        RemindersRepository reminders, CancellationToken ct) => result.Outcome switch
    {
        RemindersRepository.EditOutcome.Ok =>
            Results.Ok(await reminders.GetDetailAsync(ledgerId, reminderId, ct).ConfigureAwait(false)),
        RemindersRepository.EditOutcome.NotFound =>
            BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger."),
        RemindersRepository.EditOutcome.NotMaterialized =>
            BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                "Reminder has no template yet — re-import it from Moneydance."),
        RemindersRepository.EditOutcome.ShapeMismatch =>
            BusinessError.Problem(BusinessError.Codes.ReminderShapeMismatch,
                "This series is a different transaction shape; use the matching edit route."),
        RemindersRepository.EditOutcome.EndBeforeStart =>
            BusinessError.Problem(BusinessError.Codes.ReminderEndBeforeStart,
                "The resulting endDate would be before startDate."),
        _ => Results.StatusCode(500),
    };
}
