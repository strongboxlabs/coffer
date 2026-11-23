using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Errors;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Endpoint group for recurring-reminder series (ADR-0047). Read surface
/// (list + upcoming + detail), manual authoring (create/edit per shape +
/// disable + skip), and posting an occurrence by hand, per shape. Copying a
/// template as it stands is the auto-post job's alone and has no route: the UI
/// always posts the occurrence as its dialog shows it. Series are managed here; the live
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
        group.MapDelete("/{reminderId:guid}/skip", UnskipAsync);
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
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate the EDITED postings exactly like a bank create (shape first). A fire is a
        // new write, so every amount it posts is checked at its currency's places, one equal
        // to the template's line included (ADR-0114 D15): nothing posts a figure that does
        // not fit.
        if (PostingValidation.ValidatePostings(request.Postings, request.SourceAccountId) is { } pr) return pr;
        if (request.Notes is { } notes && TagValidation.ValidateTags(notes.AllTagNames()) is { } tagsRejection)
            return tagsRejection;

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

        // ADR-0114 D11: an occurrence posted before the ledger's earliest day moves the
        // starts earlier, as a bank create does; read before the write that can move them.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var result = await reminders.FireBankAsync(
            ledgerId, reminderId, request.OccurrenceDate, request, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == RemindersRepository.FireOutcome.Ok)
            await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
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
            RemindersRepository.FireOutcome.NotesRefused => AnnotationProblem.Of(result.Refusal!),
            RemindersRepository.FireOutcome.PostingRefused => PostingProblem(result.PostingRefusal!),
            RemindersRepository.FireOutcome.EstimateUnavailableForFire =>
                BusinessError.Problem(BusinessError.Codes.ReminderEstimateNotEligible,
                    "This reminder estimates its amount and there is nothing to estimate "
                    + "from yet. Post it with an explicit amount, or turn the estimate off."),
            RemindersRepository.FireOutcome.TemplateHasNoLines =>
                BusinessError.Problem(BusinessError.Codes.ReminderTemplateHasNoLines,
                    "This reminder has no lines, so it names no account to post from. "
                    + "Edit the reminder to give it its lines, or delete it."),
            RemindersRepository.FireOutcome.LoanAcrossCurrencies =>
                LoanAcrossCurrenciesProblem(result.LoanCurrencies!),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>The 422 for a managed loan payment that would cross currencies
    /// (ADR-0114 D13), at setup or at fire: both currencies, and how such a payment is
    /// entered instead (ADR-0078 D2).</summary>
    internal static IResult LoanAcrossCurrenciesProblem(RemindersRepository.LoanPaymentCurrencies currencies) =>
        BusinessError.Problem(BusinessError.Codes.PaymentReminderCurrencyMismatch,
            $"This loan is in {currencies.LoanCurrency} and the payment would use an account in "
            + $"{currencies.OtherCurrency}. A scheduled loan payment stays in the loan's currency: pay it "
            + $"from an account in {currencies.LoanCurrency}, or enter it as a transfer with both amounts.");

    /// <summary>The 422 for a posting the leg-pair builder refused (ADR-0114 D13): its
    /// code, and a detail naming the posting.</summary>
    private static IResult PostingProblem(PostingLegPair.Refusal refusal) =>
        BusinessError.Problem(refusal.Code, refusal.Detail);

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
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        // ADR-0114 D11: as the bank fire, read before the write that can move the starts.
        var before = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var result = await reminders.FireInvestmentAsync(
            ledgerId, reminderId, request.OccurrenceDate, request.Transaction, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == RemindersRepository.FireOutcome.Ok)
            await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, before, cancellationToken)
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
                InvestmentFailureProblem(result.InvestmentFailure!.Value, result.InvestmentFailureDetail),
            RemindersRepository.FireOutcome.NotesRefused => AnnotationProblem.Of(result.Refusal!),
            RemindersRepository.FireOutcome.EstimateUnavailableForFire =>
                BusinessError.Problem(BusinessError.Codes.ReminderEstimateNotEligible,
                    "This reminder estimates its amount and there is nothing to estimate "
                    + "from yet. Post it with an explicit amount, or turn the estimate off."),
            RemindersRepository.FireOutcome.TemplateHasNoLines =>
                BusinessError.Problem(BusinessError.Codes.ReminderTemplateHasNoLines,
                    "This reminder has no lines, so it names no account to post from. "
                    + "Edit the reminder to give it its lines, or delete it."),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>Map an investment validation failure to its 422, as the investment
    /// create endpoint does: with <paramref name="detail"/>, a places refusal names the
    /// currency, its places and the field (ADR-0114 D15).</summary>
    private static IResult InvestmentFailureProblem(
        InvestmentTransactionsRepository.CreateFailure failure,
        InvestmentTransactionsRepository.FailureDetail? detail) =>
        InvestmentTransactionsEndpoints.MapFailureProblem(failure, detail);

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/reminders/{reminderId}</c> — series detail
    /// (recurrence + the template's legs). Drives the editor's load.
    /// </summary>
    private static async Task<IResult> GetByIdAsync(
        Guid ledgerId,
        Guid reminderId,
        // ?occurrenceDate= (as on the unskip route): the occurrence a loan series' split
        // legs are priced for, as of that payment's own date (ADR-0078, 2026-10-21);
        // absent, the next due one.
        DateOnly? occurrenceDate,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var detail = await reminders.GetDetailAsync(ledgerId, reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);
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
        SchedulesRepository schedules,
        RecurrenceExpander expander,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateRecurrence(expander, request.Rrule, request.StartDate, request.EndDate,
                request.AutoCommitDaysBefore) is { } recurrenceRejection)
            return recurrenceRejection;
        if (PostingValidation.ValidatePostings(request.Postings, request.SourceAccountId) is { } postingRejection)
            return postingRejection;

        // Estimates are single-posting, non-loan only (mig 220). A manual bank create is
        // never a loan series — those come from the loan route — so the posting count is
        // the whole check here.
        if (RejectIneligibleEstimate(
                request.EstimateSampleCount, request.Postings.Count, isLoanReminder: false)
            is { } estimateRejection)
            return estimateRejection;
        if (request.Notes is { } notes && TagValidation.ValidateTags(notes.AllTagNames()) is { } tagsRejection)
            return tagsRejection;

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
        if (result.Refusal is { } refusal) return AnnotationProblem.Of(refusal);
        // Nothing was written, so the auto-post schedule is left as it is too.
        if (result.PostingRefusal is { } posting) return PostingProblem(posting);
        await EnableAutoPostIfRequestedAsync(
                ledgerId, request.AutoCommitDaysBefore, currentUser.UserId, schedules,
                clock, cancellationToken)
            .ConfigureAwait(false);
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
        SchedulesRepository schedules,
        RecurrenceExpander expander,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateRecurrence(expander, request.Rrule, request.StartDate, request.EndDate,
                request.AutoCommitDaysBefore) is { } recurrenceRejection)
            return recurrenceRejection;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        if (request.Transaction.Tags is { } investmentTags
            && TagValidation.ValidateTags(investmentTags) is { } tagsRejection)
            return tagsRejection;

        var result = await reminders.CreateInvestmentAsync(ledgerId, request, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal) return AnnotationProblem.Of(refusal);
        if (result.Outcome == RemindersRepository.CreateOutcome.ShapeFailure)
            return InvestmentFailureProblem(result.InvestmentFailure!.Value, result.InvestmentFailureDetail);
        await EnableAutoPostIfRequestedAsync(
                ledgerId, request.AutoCommitDaysBefore, currentUser.UserId, schedules,
                clock, cancellationToken)
            .ConfigureAwait(false);
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
        SchedulesRepository schedules,
        RecurrenceExpander expander,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateBankEdit(expander, request) is { } editRejection) return editRejection;

        if (request.Postings is { } postings)
        {
            if (PostingValidation.ValidatePostings(postings.Items, postings.SourceAccountId) is { } pr) return pr;
        }
        if (request.Notes is { } notes && TagValidation.ValidateTags(notes.AllTagNames()) is { } tagsRejection)
            return tagsRejection;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        if (request.Postings is { } postings2)
        {
            if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, postings2.SourceAccountId, cancellationToken)
                    .ConfigureAwait(false))
                return BusinessError.Problem(BusinessError.Codes.TransactionAccountIsInvestment,
                    "sourceAccountId is not a bank-shape account.");
            // What the template ALREADY sits on, as the transaction PATCH passes its
            // header's: an inactive account left where it is stays editable, one
            // newly chosen is refused.
            var alreadyReferenced = await reminders
                .TemplateLegAccountIdsAsync(ledgerId, reminderId, cancellationToken)
                .ConfigureAwait(false);
            // The template's lines as stored, read before the save rebuilds them with new
            // ids: a line resent as stored passes the places rule even when it does not fit
            // its currency (ADR-0114 D15).
            var stored = await reminders
                .TemplateStoredPairsAsync(ledgerId, reminderId, postings2.SourceAccountId, cancellationToken)
                .ConfigureAwait(false);
            if (await PostingValidation.ValidatePostingAccountsAsync(
                    ledgerId, postings2.SourceAccountId, postings2.Items, accounts,
                    cancellationToken, alreadyReferenced, stored, "postings.items")
                    .ConfigureAwait(false) is { } ar)
                return ar;
        }

        var result = await reminders.EditBankAsync(ledgerId, reminderId, request, cancellationToken).ConfigureAwait(false);
        await EnableAutoPostIfSavedAsync(
                ledgerId, result, request.AutoCommitDaysBefore, currentUser.UserId, schedules,
                clock, cancellationToken)
            .ConfigureAwait(false);
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
        SchedulesRepository schedules,
        RecurrenceExpander expander,
        TimeProvider clock,
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

        if (request.Transaction?.Tags is { } investmentTags
            && TagValidation.ValidateTags(investmentTags) is { } tagsRejection)
            return tagsRejection;

        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.EditInvestmentAsync(ledgerId, reminderId, request, cancellationToken).ConfigureAwait(false);
        await EnableAutoPostIfSavedAsync(
                ledgerId, result, request.AutoCommitDaysBefore, currentUser.UserId, schedules,
                clock, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == RemindersRepository.EditOutcome.ShapeFailure)
            return InvestmentFailureProblem(result.InvestmentFailure!.Value, result.InvestmentFailureDetail);
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
    /// <c>DELETE /{reminderId}/skip?occurrenceDate=</c> — undo a skip.
    /// </summary>
    /// <remarks>
    /// The occurrence comes from the QUERY STRING rather than a body: DELETE with a body
    /// is poorly supported by intermediaries and awkward for callers, and the slot is an
    /// identifier here rather than a payload.
    /// </remarks>
    private static async Task<IResult> UnskipAsync(
        Guid ledgerId,
        Guid reminderId,
        DateOnly occurrenceDate,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await reminders.UnskipAsync(
            ledgerId, reminderId, occurrenceDate, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            RemindersRepository.UnskipOutcome.Ok =>
                Results.Ok(new UnskipReminderResponse(occurrenceDate, result.NextDueDate)),
            RemindersRepository.UnskipOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotInLedger, "Reminder not found in this ledger."),
            RemindersRepository.UnskipOutcome.NotMaterialized =>
                BusinessError.Problem(BusinessError.Codes.ReminderNotMaterialized,
                    "Reminder has no template yet — re-import it from Moneydance."),
            // Not an error worth a distinct code: the caller asked for a state the slot is
            // already in, and answering 200 keeps un-skip idempotent the way skip is.
            RemindersRepository.UnskipOutcome.NotSkipped =>
                Results.Ok(new UnskipReminderResponse(occurrenceDate, result.NextDueDate)),
            _ => Results.StatusCode(500),
        };
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

    /// <summary>
    /// The bank edit's request-shape checks, before anything is read: at least one field,
    /// a valid rule, an end on or after a start sent with it, and auto-post days of zero or
    /// more. Shared with the Demo's examples phase, which edits a reminder it created.
    /// </summary>
    internal static IResult? ValidateBankEdit(RecurrenceExpander expander, EditReminderRequest request)
    {
        ArgumentNullException.ThrowIfNull(expander);
        ArgumentNullException.ThrowIfNull(request);

        var hasField = request.Rrule is not null || request.StartDate is not null
            || request.ClearEndDate || request.EndDate is not null
            || request.ClearAutoCommit || request.AutoCommitDaysBefore is not null
            // Mig 220. Omitting these made a PATCH that only enables estimation read as
            // an EMPTY patch and get rejected — the field existed on the DTO and was
            // unreachable through the endpoint.
            || request.ClearEstimate || request.EstimateSampleCount is not null
            || request.Payee is not null || request.Notes is not null
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
        return null;
    }

    /// <summary>Shared recurrence-field shape validation for the two create
    /// paths (rrule valid + start present + end ≥ start + autocommit ≥ 0), and for the
    /// Demo's examples phase, which creates a reminder as the bank create does.</summary>
    internal static IResult? ValidateRecurrence(
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

    /// <summary>
    /// Reject an estimate the series cannot honour (migration 220). Shared with the Demo's
    /// examples phase, which creates a reminder as the bank create does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Server-side on every write path, not hidden in the UI.</b> The SPA hides the
    /// control too, but a hidden control is a courtesy and this is the invariant: the
    /// resolver refuses to produce an estimate for these shapes, so storing the flag
    /// would leave a reminder configured to estimate and permanently unable to.
    /// </para>
    /// <para>
    /// <b>Two exclusions, one predicate.</b> A SPLIT has no single amount to estimate —
    /// distributing one averaged number across several postings would need an invented
    /// rule for which leg absorbs the difference. A LOAN payment is computed from its
    /// terms and balance, which is real information; letting an average of the
    /// last three override it is a straight downgrade, and two overrides racing for one
    /// field is how one silently wins.
    /// </para>
    /// <para>
    /// Rejected rather than silently dropped, for the reason auto-post had to be fixed: a
    /// control that accepts input and does nothing with it is worse than one that refuses.
    /// </para>
    /// </remarks>
    /// <param name="postingCount">
    /// Null when the request does not touch postings — on a PATCH that means the stored
    /// template stands, so the caller supplies its posting count instead.
    /// </param>
    internal static IResult? RejectIneligibleEstimate(
        int? estimateSampleCount, int? postingCount, bool isLoanReminder)
    {
        if (estimateSampleCount is not { } n) return null;

        if (n is < 1 or > 24)
        {
            return BusinessError.Problem(
                BusinessError.Codes.ReminderEstimateRangeInvalid,
                "estimateSampleCount must be between 1 and 24.");
        }

        if (isLoanReminder)
        {
            return BusinessError.Problem(
                BusinessError.Codes.ReminderEstimateNotEligible,
                "A loan reminder's amount is computed from the loan's terms and balance, "
                + "so it cannot be estimated from past occurrences.");
        }

        if (postingCount is { } count && count > 1)
        {
            return BusinessError.Problem(
                BusinessError.Codes.ReminderEstimateNotEligible,
                "A reminder with a split has no single amount to estimate. Remove the "
                + "split, or set the amount manually.");
        }

        return null;
    }

    /// <summary>
    /// Enables the auto-post job for an edit that was saved. A refused edit writes
    /// nothing: the job is saved through the request's database context, which still
    /// tracks what the refused edit had changed before it was refused, so enabling it
    /// then would save those changes too.
    /// </summary>
    private static Task EnableAutoPostIfSavedAsync(
        Guid ledgerId, RemindersRepository.EditReminderResult result, int? autoCommitDaysBefore,
        Guid configuredByUserId, SchedulesRepository schedules, TimeProvider clock,
        CancellationToken cancellationToken) =>
        result.Outcome == RemindersRepository.EditOutcome.Ok
            ? EnableAutoPostIfRequestedAsync(
                ledgerId, autoCommitDaysBefore, configuredByUserId, schedules, clock, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// A reminder asking to be auto-posted turns the ledger's auto-post job ON, if it is
    /// not configured yet. Called only once the reminder's own write has been saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ticking "Auto-post / N days before" IS the opt-in. Requiring a second switch
    /// elsewhere before that tick did anything reproduced the very defect ADR-0097 set
    /// out to remove: a user told their rent posts itself, and nothing acting on it. The
    /// reminders page still owns WHEN the job runs; it no longer owns whether.
    /// </para>
    /// <para>
    /// Create-if-absent only — <see cref="SchedulesRepository.EnsureCreatedEnabledAsync"/>
    /// never flips an existing row, so editing a reminder cannot restart a job the
    /// scheduler auto-disabled after five failures, nor one an operator paused.
    /// </para>
    /// <para>
    /// Called on the four write paths that can set the field (bank/investment ×
    /// create/edit) rather than inside the repository, so the coupling from reminders to
    /// scheduling is visible at the call site instead of buried in a write. A Moneydance
    /// import that brings such a reminder does the same, inside its own transaction
    /// (<see cref="Import.ReminderAutoPostOnImport"/>), and so does the Demo's examples
    /// phase after the reminder it creates.
    /// </para>
    /// </remarks>
    internal static Task EnableAutoPostIfRequestedAsync(
        Guid ledgerId, int? autoCommitDaysBefore, Guid configuredByUserId,
        SchedulesRepository schedules, TimeProvider clock,
        CancellationToken cancellationToken) =>
        autoCommitDaysBefore is null
            ? Task.CompletedTask
            : schedules.EnsureCreatedEnabledAsync(
                ledgerId, JobTypes.ReminderAutoPost, ScheduleDefaults.ReminderAutoPostHour,
                configuredByUserId, clock.GetUtcNow().UtcDateTime, cancellationToken);

    private static async Task<IResult> CreatedDetailAsync(
        Guid ledgerId, Guid reminderId, RemindersRepository reminders, CancellationToken ct)
    {
        // A write's echo: a loan split, if any, is priced for the next due occurrence.
        var detail = await reminders.GetDetailAsync(ledgerId, reminderId, occurrenceDate: null, ct).ConfigureAwait(false);
        return Results.Created($"/api/ledgers/{ledgerId}/reminders/{reminderId}", detail);
    }

    private static async Task<IResult> EditOutcomeResultAsync(
        Guid ledgerId, Guid reminderId, RemindersRepository.EditReminderResult result,
        RemindersRepository reminders, CancellationToken ct) => result.Outcome switch
    {
        RemindersRepository.EditOutcome.NotesRefused => AnnotationProblem.Of(result.Refusal!),
        RemindersRepository.EditOutcome.PostingRefused => PostingProblem(result.PostingRefusal!),
        RemindersRepository.EditOutcome.Ok =>
            Results.Ok(await reminders.GetDetailAsync(ledgerId, reminderId, occurrenceDate: null, ct).ConfigureAwait(false)),
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
        RemindersRepository.EditOutcome.PostingLegNotInTemplate =>
            BusinessError.Problem(BusinessError.Codes.TransactionPostingLegNotInHeader,
                "A posting's legId is not the id of a line's leg on this reminder's source account."),
        RemindersRepository.EditOutcome.PostingLegIdDuplicated =>
            BusinessError.Problem(BusinessError.Codes.TransactionPostingLegIdDuplicated,
                "Two postings carry the same legId. Each posting must name a distinct line, or omit legId to add a new one."),
        RemindersRepository.EditOutcome.SourceImmutable =>
            BusinessError.Problem(BusinessError.Codes.ReminderSourceImmutable,
                "A reminder's source account is set when the reminder is created and cannot be "
                + "changed by an edit."),
        RemindersRepository.EditOutcome.EstimateNotEligible =>
            BusinessError.Problem(BusinessError.Codes.ReminderEstimateNotEligible,
                "Only a single-posting, non-loan reminder can estimate its amount. A "
                + "split has no single amount to estimate, and a loan payment is computed "
                + "from its terms and balance."),
        _ => Results.StatusCode(500),
    };
}
