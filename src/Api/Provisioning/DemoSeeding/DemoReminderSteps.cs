using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;
using Coffer.Api.Provisioning.Demo;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The examples step that adds the reminders an export cannot carry (ADR-0115 D3): a bill
/// whose amount is estimated from its own history and auto-posted, the mortgage's managed
/// payment, and one skipped occurrence of an imported reminder.
/// </summary>
/// <remarks>
/// Each write goes through the repository the app's own endpoint writes through, after the
/// checks that endpoint's handler makes and its repository leaves to it, in the handler's
/// order. Any refusal, and any outcome but Ok, is the step's failure. Every date comes from
/// the Demo's last day and the reminders' own rules.
/// </remarks>
internal static class DemoReminderSteps
{
    /// <summary>The generator works in cents of the Demo's one currency.</summary>
    private const decimal CentsPerUnit = 100m;

    /// <summary>How many days before each occurrence the estimated bill posts itself.</summary>
    internal const int ElectricAutoCommitDaysBefore = 3;

    /// <summary>How many past occurrences the estimated bill averages: the bills the
    /// generator left out of the export for this step to post.</summary>
    internal const int ElectricEstimateSamples = BillsPattern.ElectricOmittedCount;

    /// <summary>How far past the Demo's last day the skip looks for the imported
    /// reminder's next occurrence.</summary>
    private const int SkipWindowDays = 366;

    /// <summary>
    /// Step 17. (a) The Electric bill as a monthly reminder from checking, its bills left
    /// out of the export posted through it in date order, and only then its estimate and
    /// its auto-post turned on, so the estimate averages linked history. (b) The mortgage's
    /// managed payment from checking, starting at its first due date after the Demo's last
    /// day. (c) The imported Internet reminder's first occurrence after the Demo's last day,
    /// skipped.
    /// </summary>
    internal static async Task RemindersAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var reminders = new RemindersRepository(
            db, new RecurrenceExpander(), new InvestmentTransactionsRepository(db, run.Clock),
            new TransactionsRepository(db, run.Clock), run.Clock);
        var accounts = new AccountsRepository(db, new LegDerivedRecomputeService(db), run.Clock);
        var expander = new RecurrenceExpander();

        // The skip's target first, before anything is written: a step reports added or
        // failed as a whole, so a target refused after (a) and (b) had written would list
        // the reminders missing while two of them exist.
        var skipTarget = await InternetSkipTargetAsync(run, db, expander, cancellationToken).ConfigureAwait(false);
        await ElectricAsync(run, db, reminders, accounts, expander, cancellationToken).ConfigureAwait(false);
        await MortgageAsync(run, db, reminders, accounts, cancellationToken).ConfigureAwait(false);
        await SkipInternetAsync(run, reminders, skipTarget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The rule slot of each of <paramref name="bills"/>, a monthly bill on
    /// <paramref name="dayOfMonth"/> moved to a business day: the slot of the bill's own
    /// month, which must be on or before the bill. Oldest first.
    /// </summary>
    /// <exception cref="DemoSeedStepException">A bill is not on or after its month's slot,
    /// or two bills share a slot.</exception>
    internal static IReadOnlyList<(DateOnly Slot, DemoDatedAmount Bill)> ElectricSlots(
        IReadOnlyList<DemoDatedAmount> bills, int dayOfMonth, RecurrenceExpander expander)
    {
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(expander);
        var ordered = bills.OrderBy(b => b.Date).ToList();
        if (ordered.Count == 0) return [];

        var rule = MonthlyRule(dayOfMonth);
        var first = ordered[0].Date;
        var start = new DateOnly(first.Year, first.Month, dayOfMonth);
        var slots = expander.Expand(rule, start, start, ordered[^1].Date);
        var paired = new List<(DateOnly, DemoDatedAmount)>();
        foreach (var bill in ordered)
        {
            var slot = slots.LastOrDefault(s => s <= bill.Date);
            if (slot == default || slot.Year != bill.Date.Year || slot.Month != bill.Date.Month)
                throw new DemoSeedStepException(
                    $"the Electric bill of {bill.Date:yyyy-MM-dd} has no slot of its reminder's rule in its month");
            if (paired.Count > 0 && paired[^1].Item1 == slot)
                throw new DemoSeedStepException($"two Electric bills fall on the reminder's slot of {slot:yyyy-MM-dd}");
            paired.Add((slot, bill));
        }
        return paired;
    }

    /// <summary>The first due date after <paramref name="asOf"/> of a monthly loan whose
    /// first payment was due on <paramref name="firstPayment"/>.</summary>
    internal static DateOnly FirstMonthlyDueAfter(DateOnly firstPayment, DateOnly asOf)
    {
        for (var months = 0; ; months++)
        {
            var due = firstPayment.AddMonths(months);
            if (due > asOf) return due;
        }
    }

    /// <summary>The rule a monthly bill on <paramref name="dayOfMonth"/> recurs by.</summary>
    internal static string MonthlyRule(int dayOfMonth) => $"FREQ=MONTHLY;BYMONTHDAY={dayOfMonth}";

    private static async Task ElectricAsync(
        DemoSeedRun run, AppDbContext db, RemindersRepository reminders, AccountsRepository accounts,
        RecurrenceExpander expander, CancellationToken cancellationToken)
    {
        var ledgerId = run.LedgerId;
        var owner = run.Context.OwnerUserId;
        var manifest = run.Context.Manifest;
        if (manifest.ElectricDayOfMonth is not { } day)
            throw new DemoSeedStepException("the manifest gives the Electric bill no day of the month");
        if (manifest.ElectricOmissions.Count != ElectricEstimateSamples)
            throw new DemoSeedStepException(
                $"the manifest leaves {manifest.ElectricOmissions.Count} Electric bills to post, "
                + $"not the {ElectricEstimateSamples} its estimate averages");

        var slots = ElectricSlots(manifest.ElectricOmissions, day, expander);
        var checking = run.Ids.Accounts[DemoAccountRoles.JointChecking];
        var electric = run.Ids.Categories["electric"];

        // (a) Created with no estimate and no auto-post, so nothing estimates before its
        // history exists, and nothing posts it while that history is being posted.
        var create = new CreateReminderRequest
        {
            Rrule = MonthlyRule(day),
            StartDate = slots[0].Slot,
            Payee = BillsPattern.ElectricPayee,
            SourceAccountId = checking,
            Postings = [Outflow(electric, slots[0].Bill.Amount)],
        };
        Refused("the Electric reminder", RemindersEndpoints.ValidateRecurrence(
            expander, create.Rrule, create.StartDate, create.EndDate, create.AutoCommitDaysBefore));
        Refused("the Electric reminder", PostingValidation.ValidatePostings(create.Postings, create.SourceAccountId));
        Refused("the Electric reminder", RemindersEndpoints.RejectIneligibleEstimate(
            create.EstimateSampleCount, create.Postings.Count, isLoanReminder: false));
        if (create.Notes is { } createNotes)
            Refused("the Electric reminder", TagValidation.ValidateTags(createNotes.AllTagNames()));
        await BankSourceAsync(accounts, ledgerId, checking, "the Electric reminder", cancellationToken).ConfigureAwait(false);
        Refused("the Electric reminder", await PostingValidation.ValidatePostingAccountsAsync(
                ledgerId, create.SourceAccountId, create.Postings, accounts, cancellationToken)
            .ConfigureAwait(false));

        var created = await reminders.CreateBankAsync(ledgerId, create, cancellationToken).ConfigureAwait(false);
        if (created.Outcome != RemindersRepository.CreateOutcome.Ok || created.ReminderId is not { } reminderId)
            throw new DemoSeedStepException($"the Electric reminder was refused: {created.Outcome}");

        // Each left-out bill posted as its slot's occurrence, oldest first, at its own
        // amount and on its own business day, so no earlier slot is left for a catch-up.
        foreach (var (slot, bill) in slots)
        {
            var fire = new FireBankReminderRequest
            {
                OccurrenceDate = slot,
                SourceAccountId = checking,
                Postings = [Outflow(electric, bill.Amount)],
                Payee = BillsPattern.ElectricPayee,
                PostedDate = bill.Date,
            };
            var what = $"the Electric bill of {bill.Date:yyyy-MM-dd}";
            Refused(what, PostingValidation.ValidatePostings(fire.Postings, fire.SourceAccountId));
            if (fire.Notes is { } fireNotes) Refused(what, TagValidation.ValidateTags(fireNotes.AllTagNames()));
            await BankSourceAsync(accounts, ledgerId, checking, what, cancellationToken).ConfigureAwait(false);
            Refused(what, await PostingValidation.ValidatePostingAccountsAsync(
                    ledgerId, fire.SourceAccountId, fire.Postings, accounts, cancellationToken)
                .ConfigureAwait(false));

            var fired = await reminders.FireBankAsync(ledgerId, reminderId, slot, fire, owner, cancellationToken)
                .ConfigureAwait(false);
            if (fired.Outcome != RemindersRepository.FireOutcome.Ok)
                throw new DemoSeedStepException($"{what} was refused: {fired.Outcome}");
            if (fired.SkippedEarlierCount != 0)
                throw new DemoSeedStepException($"{what} skipped {fired.SkippedEarlierCount} earlier occurrences");
        }

        // Only now the estimate, which averages the bills just posted, and auto-post, which
        // then posts the coming occurrences at that estimate.
        var edit = new EditReminderRequest
        {
            EstimateSampleCount = ElectricEstimateSamples,
            AutoCommitDaysBefore = ElectricAutoCommitDaysBefore,
        };
        Refused("the Electric reminder's estimate", RemindersEndpoints.ValidateBankEdit(expander, edit));
        var edited = await reminders.EditBankAsync(ledgerId, reminderId, edit, cancellationToken).ConfigureAwait(false);
        if (edited.Outcome != RemindersRepository.EditOutcome.Ok)
            throw new DemoSeedStepException($"the Electric reminder's estimate was refused: {edited.Outcome}");
        // As the edit handler does after a saved edit: an edit that asks for auto-post
        // turns the ledger's auto-post job on.
        await RemindersEndpoints.EnableAutoPostIfRequestedAsync(
                ledgerId, edit.AutoCommitDaysBefore, owner, new SchedulesRepository(db), run.Clock, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task MortgageAsync(
        DemoSeedRun run, AppDbContext db, RemindersRepository reminders, AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        var ledgerId = run.LedgerId;
        var mortgage = run.Ids.Accounts[DemoAccountRoles.Mortgage];
        var checking = run.Ids.Accounts[DemoAccountRoles.JointChecking];

        var terms = await db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && t.AccountId == mortgage)
            .Select(t => new { t.FirstPaymentDate, t.PaymentsPerYear })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DemoSeedStepException("the mortgage has no loan terms");
        if (terms.FirstPaymentDate is not { } firstPayment)
            throw new DemoSeedStepException("the mortgage's loan terms have no first payment date");
        if (terms.PaymentsPerYear != 12)
            throw new DemoSeedStepException(
                $"the mortgage is paid {terms.PaymentsPerYear} times a year, not monthly");
        var start = FirstMonthlyDueAfter(firstPayment, run.AsOf);

        // The repository leaves the paying account's check to its caller, as the
        // payment-reminder endpoint makes it.
        if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, checking, cancellationToken).ConfigureAwait(false))
            throw new DemoSeedStepException("the mortgage's paying account is not a bank-type account in the Demo");
        var outcome = await reminders
            .CreateManagedLoanReminderAsync(ledgerId, mortgage, checking, start, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Result != RemindersRepository.ManagedLoanReminderResult.Ok)
            throw new DemoSeedStepException($"the mortgage's managed payment was refused: {outcome.Result}");
    }

    /// <summary>The imported Internet reminder's occurrence the skip example skips
    /// (<see cref="SkipTarget"/>), read before the step writes anything.</summary>
    private static async Task<DateOnly> InternetSkipTargetAsync(
        DemoSeedRun run, AppDbContext db, RecurrenceExpander expander, CancellationToken cancellationToken)
    {
        var ledgerId = run.LedgerId;
        var reminderId = run.Ids.InternetReminder;
        var series = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId)
            .Select(r => new { r.Rrule, r.StartDate, r.EndDate, r.NextDueDate })
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        var windowEnd = run.AsOf.AddDays(SkipWindowDays);
        if (series.EndDate is { } end && end < windowEnd) windowEnd = end;
        return SkipTarget(
            expander.Expand(series.Rrule, series.StartDate, run.AsOf.AddDays(1), windowEnd), series.NextDueDate);
    }

    private static async Task SkipInternetAsync(
        DemoSeedRun run, RemindersRepository reminders, DateOnly target, CancellationToken cancellationToken)
    {
        var skipped = await reminders
            .SkipAsync(run.LedgerId, run.Ids.InternetReminder, target, run.Context.OwnerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (skipped.Outcome != RemindersRepository.SkipOutcome.Ok)
            throw new DemoSeedStepException(
                $"the Internet reminder's occurrence of {target:yyyy-MM-dd} could not be skipped: {skipped.Outcome}");
    }

    /// <summary>
    /// The occurrence the skip example skips: the imported reminder's first after the
    /// Demo's last day (<paramref name="after"/>, oldest first), whose bill the history
    /// does not hold. A skip also skips every earlier occurrence not yet acted on, so the
    /// target must be the reminder's next due one (<paramref name="nextDue"/>).
    /// </summary>
    /// <remarks>
    /// The generated Internet bill posts on its own day of the month, weekends and
    /// holidays included, and the export acknowledges the reminder through that bill, so
    /// by construction no occurrence on or before the last day is still due and this
    /// check always passes. It stays as a backstop: were an occurrence still due, skipping
    /// past it would skip that bill too, so the whole step is refused before it writes
    /// anything. Only the whole step can be reported missing (a step's outcome is added
    /// or failed, and the marker records the steps added), so losing just the skip could
    /// not be shown as such.
    /// </remarks>
    /// <exception cref="DemoSeedStepException">There is no occurrence after the last day,
    /// or the next due one is not it.</exception>
    internal static DateOnly SkipTarget(IReadOnlyList<DateOnly> after, DateOnly? nextDue)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (after.Count == 0)
            throw new DemoSeedStepException("the imported Internet reminder has no occurrence after the Demo's last day");
        if (nextDue is not { } due)
            throw new DemoSeedStepException("the imported Internet reminder has no occurrence due");
        if (due != after[0])
            throw new DemoSeedStepException(
                $"the imported Internet reminder is next due on {due:yyyy-MM-dd}, not on {after[0]:yyyy-MM-dd}, "
                + "its first occurrence after the Demo's last day, and a skip there would also skip the one due");
        return after[0];
    }

    /// <summary>The handler's bank-shape check of a reminder's source account.</summary>
    private static async Task BankSourceAsync(
        AccountsRepository accounts, Guid ledgerId, Guid accountId, string what, CancellationToken cancellationToken)
    {
        if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false))
            throw new DemoSeedStepException($"{what} was refused: its source is not a bank-type account");
    }

    /// <summary>A payment of <paramref name="cents"/> from the source to
    /// <paramref name="category"/>.</summary>
    private static TransactionPosting Outflow(Guid category, long cents) =>
        new() { CounterpartyAccountId = category, Amount = -(cents / CentsPerUnit) };

    /// <summary>A shared validator's refusal, as the step's failure.</summary>
    private static void Refused(string what, IResult? refusal)
    {
        if (refusal is not null)
            throw new DemoSeedStepException($"{what} was refused: {BusinessError.Describe(refusal)}");
    }
}
