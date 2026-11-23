using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;
using Coffer.Api.Loans;
using Coffer.Domain.Annotations;
using Coffer.Domain.Money;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read/write gateway for recurring-reminder series (ADR-0047). A series is a
/// <c>recurring_transactions</c> row (recurrence metadata) pointing at a
/// template <c>txn_header</c> + legs that carry the transaction shape.
/// </summary>
/// <remarks>
/// Only fully-materialized series (those with a <c>template_header_id</c>) are
/// listed / fireable; a reshaped-but-not-yet-re-imported legacy row is dormant
/// until the importer re-materializes it (ADR-0048 D6).
/// </remarks>
public sealed class RemindersRepository
{
    private readonly AppDbContext _db;
    private readonly RecurrenceExpander _expander;
    private readonly InvestmentTransactionsRepository _investmentTxns;
    private readonly TransactionsRepository _transactions;
    private readonly TimeProvider _clock;

    /// <param name="clock">The server clock the estimate's derived rate reads its UTC today
    /// from (<see cref="ExchangeRateLookup"/>). Required: a caller that left it out would
    /// read the wall clock instead of the injected one.</param>
    public RemindersRepository(
        AppDbContext db,
        RecurrenceExpander expander,
        InvestmentTransactionsRepository investmentTxns,
        TransactionsRepository transactions,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _expander = expander;
        _clock = clock;
        // Reused for INVESTMENT reminder templates: BuildTemplateLegsAsync runs
        // the exact same validation + leg construction as a live investment
        // create (ADR-0047). It shares this scoped AppDbContext, but only reads
        // for validation + returns un-tracked leg rows — this repository adds +
        // saves them alongside the template header.
        _investmentTxns = investmentTxns;
        // Reused for adjust-at-post BANK fire (ADR-0049): the live bank create,
        // stamped to the occurrence + joining this repository's transaction (the
        // same scoped AppDbContext, so its ambient-tx check sees ours).
        _transactions = transactions;
    }

    public enum FireOutcome
    {
        Ok, NotFound, NotMaterialized, OccurrenceSkipped, ShapeMismatch, ShapeFailure,

        /// <summary>
        /// The series estimates its amount and the estimate could not be produced — no
        /// history yet, an average of zero, or a direction that contradicts the template
        /// (mig 220); or, on a transfer between currencies, no derived rate for the
        /// occurrence's due date (ADR-0114 D13).
        /// </summary>
        /// <remarks>
        /// Refusing rather than falling back to the template amount, and the difference
        /// matters most where nobody is watching. A user who asked for the amount to be
        /// COMPUTED has not asked for some other number to be posted when it cannot be;
        /// on the verbatim-clone path that substitution would be silent, and auto-post
        /// runs unattended. Adjust-at-post (/fire/bank) still works — there the caller
        /// supplies the amount explicitly, which is a person answering the question
        /// rather than the system guessing at it.
        /// </remarks>
        EstimateUnavailableForFire,

        /// <summary>
        /// The series' template has no lines, so it names no account to post from, and
        /// the verbatim-clone fire is refused before anything is written.
        /// </summary>
        /// <remarks>
        /// No create or edit path builds such a template, but an imported reminder with
        /// no field values can hold one and nothing in the schema rules it out. A result
        /// rather than an exception, so the auto-post job can skip it and say so instead
        /// of failing every run.
        /// </remarks>
        TemplateHasNoLines,

        /// <summary>
        /// A line of the series' template sits on an inactive account, and the
        /// verbatim-clone fire is refused before anything is written.
        /// </summary>
        /// <remarks>
        /// A new transaction never lands on a retired account; every by-hand post refuses
        /// one with <c>account-inactive</c>. The auto-post job sets such a series aside
        /// before firing and names it; this refusal holds the rule for an account retired
        /// between the job's scan and its fire.
        /// </remarks>
        AccountInactive,

        /// <summary>The write primitive refused the occurrence's notes;
        /// <see cref="FireResult.Refusal"/> says why. Nothing is written.</summary>
        NotesRefused,

        /// <summary>The leg-pair builder refused a posting of the adjust-at-post fire
        /// between real accounts in different currencies (ADR-0114 D13);
        /// <see cref="FireResult.PostingRefusal"/> says which. Nothing is written.</summary>
        PostingRefused,

        /// <summary>
        /// A figure the verbatim-clone fire would write carries more places than its
        /// currency allows (ADR-0114 D15); <see cref="FireResult.UnfitCurrencyCode"/> names
        /// the currency. Nothing is written.
        /// </summary>
        /// <remarks>
        /// A stored figure that does not fit is kept where it is, but a fire is a new write,
        /// so nothing posts it, by hand or by auto-post. Every figure the clone writes is
        /// judged: a template line's own, and the loan split's or the estimate's in its
        /// place, since a loan's escrow and a fixed payment are stored figures too. The cure
        /// is an edit of the reminder or the loan terms; the consistency check lists both.
        /// The auto-post scan sets such a series aside by the same predicate
        /// (<see cref="Coffer.Api.Reminders.AutoPostExclusion.TemplateAmountDoesNotFit"/>),
        /// so this refusal is its backstop.
        /// </remarks>
        TemplateAmountDoesNotFit,

        /// <summary>
        /// A managed loan payment would cross currencies (ADR-0114 D13): a real account it
        /// posts to, the paying account or a real interest or escrow account, is in another
        /// currency than the loan's, and the split is computed in the loan's.
        /// <see cref="FireResult.LoanCurrencies"/> names both. Nothing is written.
        /// </summary>
        /// <remarks>
        /// Setup refuses such a series, so one reaches a fire only when an account's currency
        /// changed after it was set up. It can never post as it stands, by hand or by
        /// auto-post; such a payment is entered as an ordinary transfer with both amounts
        /// (ADR-0078 D2). The auto-post scan sets it aside by the same predicate
        /// (<see cref="Coffer.Api.Reminders.AutoPostExclusion.LoanAcrossCurrencies"/>), so
        /// this refusal is its backstop there.
        /// </remarks>
        LoanAcrossCurrencies,
    }

    public sealed record FireResult(
        FireOutcome Outcome, Guid? HeaderId,
        // Catch-up (ADR-0047 §9.2): earlier un-acted occurrences this fire also
        // marked skipped, and the earliest of them (for the SPA's confirm/notice).
        int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null,
        AnnotationViolation? Refusal = null,
        PostingLegPair.Refusal? PostingRefusal = null,
        string? UnfitCurrencyCode = null,
        LoanPaymentCurrencies? LoanCurrencies = null);

    /// <summary>The two currencies of a managed loan payment that crosses them: the
    /// loan's, and that of the first real account on the payment in another one.</summary>
    public sealed record LoanPaymentCurrencies(string LoanCurrency, string OtherCurrency);

    /// <summary>The accounts a managed loan payment's split prices: the loan, and its
    /// terms' interest and escrow accounts.</summary>
    internal sealed record LoanPaymentAccounts(Guid LoanAccountId, Guid? InterestAccountId, Guid? EscrowAccountId);

    /// <summary>
    /// Whether a managed loan payment crosses currencies (ADR-0114 D13): the one predicate
    /// the setup, both fires, the auto-post scan and Cash ahead judge by.
    /// </summary>
    /// <param name="payingAccountId">The account the payment is made from.</param>
    /// <param name="loan">The accounts the split prices.</param>
    /// <param name="sides">Each account's type and currency.</param>
    /// <returns>The loan's currency and the first other one, or null when the paying
    /// account and every real account the split prices are in the loan's currency.</returns>
    /// <remarks>
    /// The split's principal, interest and escrow are computed in the loan's currency and
    /// each priced line carries one amount, so the paying account and a real interest or
    /// escrow account must be kept in it. A category is not judged: its line mirrors the
    /// paying account, as every category line does. A line the split does not price, one
    /// added to the occurrence by hand, is an ordinary posting and keeps both its amounts.
    /// </remarks>
    internal static LoanPaymentCurrencies? LoanPaymentAcrossCurrencies(
        Guid payingAccountId, LoanPaymentAccounts loan,
        IReadOnlyDictionary<Guid, PostingLegPair.AccountSide> sides)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(sides);
        if (!sides.TryGetValue(loan.LoanAccountId, out var loanSide)) return null;
        foreach (var id in new[] { payingAccountId, loan.InterestAccountId, loan.EscrowAccountId })
            if (id is { } judged
                && sides.TryGetValue(judged, out var side)
                && side.IsReal
                && !string.Equals(side.CurrencyCode, loanSide.CurrencyCode, StringComparison.Ordinal))
                return new LoanPaymentCurrencies(loanSide.CurrencyCode, side.CurrencyCode);
        return null;
    }

    /// <summary><see cref="LoanPaymentAcrossCurrencies"/> over accounts read on this
    /// write's own context, inside its transaction.</summary>
    private async Task<LoanPaymentCurrencies?> LoanPaymentAcrossCurrenciesAsync(
        Guid ledgerId, Guid payingAccountId, LoanPaymentAccounts loan, CancellationToken cancellationToken)
    {
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId,
                new[] { payingAccountId, loan.LoanAccountId, loan.InterestAccountId, loan.EscrowAccountId }
                    .OfType<Guid>(),
                cancellationToken)
            .ConfigureAwait(false);
        return LoanPaymentAcrossCurrencies(payingAccountId, loan, sides);
    }

    /// <summary>
    /// The accounts the split prices for one managed loan series: its loan, the one it
    /// names or else the first template line's loan with terms (as the clone fire's split
    /// finds it), with that loan's terms. Null when it has no loan with terms, so no split
    /// is priced.
    /// </summary>
    private static LoanPaymentAccounts? LoanAccountsOf(
        Guid? namedLoanAccountId, Guid? sourceAccountId, IEnumerable<Guid> templateAccountIds,
        IReadOnlyDictionary<Guid, LoanTermsRow> termsByLoan)
    {
        var loanId = namedLoanAccountId
                     ?? templateAccountIds.Cast<Guid?>()
                         .FirstOrDefault(a => a != sourceAccountId && termsByLoan.ContainsKey(a!.Value));
        return loanId is { } id && termsByLoan.TryGetValue(id, out var terms)
            ? new LoanPaymentAccounts(id, terms.InterestAccountId, terms.EscrowAccountId)
            : null;
    }

    /// <summary>
    /// Whether one managed loan fire crosses currencies, read inside the fire's
    /// transaction: <see cref="LoanAccountsOf"/>, then <see cref="LoanPaymentAcrossCurrencies"/>.
    /// </summary>
    private async Task<LoanPaymentCurrencies?> LoanFireAcrossCurrenciesAsync(
        Guid ledgerId, Guid? namedLoanAccountId, Guid payingAccountId, IEnumerable<Guid> templateAccountIds,
        CancellationToken cancellationToken)
    {
        var termsByLoan = await _db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .ToDictionaryAsync(t => t.AccountId, cancellationToken)
            .ConfigureAwait(false);
        return LoanAccountsOf(namedLoanAccountId, payingAccountId, templateAccountIds, termsByLoan) is { } loan
            ? await LoanPaymentAcrossCurrenciesAsync(ledgerId, payingAccountId, loan, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    /// <summary>Result of <see cref="FireInvestmentAsync"/>. On
    /// <see cref="FireOutcome.ShapeFailure"/>, <see cref="InvestmentFailure"/>
    /// carries the investment validation code to map to a 422, and
    /// <see cref="InvestmentFailureDetail"/> what that refusal names beyond its code
    /// (the currency, places and field of a places refusal, ADR-0114 D15).</summary>
    public sealed record FireInvestmentResult(
        FireOutcome Outcome, Guid? HeaderId,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure = null,
        int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null,
        AnnotationViolation? Refusal = null,
        InvestmentTransactionsRepository.FailureDetail? InvestmentFailureDetail = null);

    // Reminder mutation outcomes (ADR-0047 slice — manual authoring).
    public enum CreateOutcome
    {
        Ok, ShapeFailure, NotesRefused,

        /// <summary>The leg-pair builder refused a template posting between real
        /// accounts in different currencies (ADR-0114 D13);
        /// <see cref="CreateReminderResult.PostingRefusal"/> says which. Nothing is
        /// written.</summary>
        PostingRefused,
    }
    public enum EditOutcome
    {
        Ok, NotFound, NotMaterialized, ShapeMismatch, ShapeFailure, EndBeforeStart,

        /// <summary>The leg-pair builder refused a template posting between real
        /// accounts in different currencies (ADR-0114 D13);
        /// <see cref="EditReminderResult.PostingRefusal"/> says which. Nothing is
        /// written.</summary>
        PostingRefused,

        /// <summary>The write primitive refused the template's notes;
        /// <see cref="EditReminderResult.Refusal"/> says why. Nothing is written.</summary>
        NotesRefused,

        /// <summary>A posting's <c>LegId</c> is not a stored leg of the template on the
        /// series source. Nothing is written.</summary>
        PostingLegNotInTemplate,

        /// <summary>Two postings name the same stored line by <c>LegId</c>. Nothing is
        /// written.</summary>
        PostingLegIdDuplicated,

        /// <summary>The edit names a source account (a bank template's source, an
        /// investment template's brokerage) other than the series' stored one. A
        /// reminder's source is set when it is created and an edit cannot change it;
        /// nothing is written.</summary>
        SourceImmutable,

        /// <summary>
        /// An estimate was asked for on a series that cannot honour one — a split, or a
        /// loan reminder (mig 220).
        /// </summary>
        /// <remarks>
        /// Decided HERE rather than in the endpoint because the effective shape is the
        /// STORED series when a PATCH does not touch postings, and only this layer has
        /// it loaded. Enforcing in the endpoint would mean re-reading the series there,
        /// or checking a shape the request does not actually describe.
        /// </remarks>
        EstimateNotEligible,
    }
    public enum ActiveOutcome { Ok, NotFound }
    public enum SkipOutcome { Ok, NotFound, NotMaterialized, AlreadyFired }

    /// <summary>Create result. On <see cref="CreateOutcome.ShapeFailure"/>,
    /// <see cref="InvestmentFailure"/> carries the investment validation code to
    /// map (bank create validates in the endpoint, so it never sets it), and
    /// <see cref="InvestmentFailureDetail"/> what that refusal names beyond its code.</summary>
    public sealed record CreateReminderResult(
        CreateOutcome Outcome,
        Guid? ReminderId,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure,
        AnnotationViolation? Refusal = null,
        PostingLegPair.Refusal? PostingRefusal = null,
        InvestmentTransactionsRepository.FailureDetail? InvestmentFailureDetail = null);

    /// <summary>Edit result. On <see cref="EditOutcome.ShapeFailure"/>,
    /// <see cref="InvestmentFailure"/> and <see cref="InvestmentFailureDetail"/> are as
    /// on <see cref="CreateReminderResult"/>.</summary>
    public sealed record EditReminderResult(
        EditOutcome Outcome,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure,
        AnnotationViolation? Refusal = null,
        PostingLegPair.Refusal? PostingRefusal = null,
        InvestmentTransactionsRepository.FailureDetail? InvestmentFailureDetail = null);

    public sealed record SkipResult(
        SkipOutcome Outcome, DateOnly? NextDueDate,
        // Catch-up (ADR-0047 §9.2): earlier un-acted occurrences this skip also
        // marked skipped, and the earliest of them.
        int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null);

    public async Task<IReadOnlyList<ReminderSummary>> ListAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        // Join + order on the entities BEFORE projecting — ordering over the
        // projected record (a constructor call) doesn't translate to SQL.
        var rows = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.TemplateHeaderId != null)
            .Join(
                _db.TxnHeaders.AsNoTracking(),
                r => r.TemplateHeaderId,
                h => h.Id,
                (r, h) => new { r, h })
            .OrderBy(x => x.r.IsActive ? 0 : 1)
            .ThenBy(x => x.h.Payee)
            .ThenBy(x => x.r.Id)
            .Select(x => new
            {
                Summary = new ReminderSummary(
                    x.r.Id,
                    x.h.Payee,
                    null,
                    // Source-side net: sum the template legs on the series' source
                    // account (mig 125 pointer). Correlated subquery; 0 when the
                    // series has no source (custom / pre-125 row) or no legs.
                    _db.TxnLegs
                        .Where(l => l.HeaderId == x.h.Id && l.AccountId == x.r.SourceAccountId)
                        .Sum(l => (decimal?)l.Amount) ?? 0m,
                    x.r.Rrule,
                    x.r.StartDate,
                    x.r.EndDate,
                    x.r.NextDueDate,
                    x.r.AutoCommitDaysBefore,
                    x.r.IsActive,
                    x.r.IsLoanReminder,
                    x.r.Origin),
                Template = new SeenFrom(x.h.Id, x.h.OriginatingAccountId),
                // The source account's currency, which the amount is in; read whether
                // or not the account is active, as the agenda reads it.
                Currency = _db.Accounts
                    .Where(a => a.Id == x.r.SourceAccountId)
                    .Select(a => a.CurrencyCode)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var memos = await MemosAsync(rows.Select(r => r.Template), cancellationToken).ConfigureAwait(false);
        var list = rows
            .Select(r => r.Summary with { Memo = memos.GetValueOrDefault(r.Template.HeaderId), CurrencyCode = r.Currency })
            .ToList();

        // Loan reminders show the computed full payment (principal + interest +
        // escrow), matching the agenda + occurrence dialog (ADR-0050). The
        // template's source-leg net is escrow-only, so override it here too.
        var loanAmounts = await ComputeLoanAmountBySeriesAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // Mig 220. The manage list has to agree with the agenda: a list showing the
        // stale template amount beside a calendar showing the estimate is the same
        // figure disagreeing with itself in two places.
        var estimates = await ResolveEstimatesAsync(ledgerId, null, cancellationToken)
            .ConfigureAwait(false);

        if (loanAmounts.Count == 0 && estimates.Count == 0) return list;

        // A transfer between currencies estimates its other side per occurrence (ADR-0114
        // D13); the list shows the series' next due one.
        var rates = await LoadEstimateRatesAsync(
                ledgerId, estimates, list.Select(s => (s.Id, s.NextDueDate)), cancellationToken)
            .ConfigureAwait(false);

        return list
            .Select(s =>
            {
                // Loan first: the full payment, computed from the loan's terms alone
                // (payment + escrow; see ComputeLoanAmountBySeriesAsync), which outranks
                // an average. The resolver refuses estimates on loan series
                // anyway, so this ordering is belt and braces.
                if (loanAmounts.TryGetValue(s.Id, out var loanAmount))
                    return s with { Amount = loanAmount };

                var e = estimates.GetValueOrDefault(s.Id);
                if (e is null) return s;

                var estimate = rates.Estimate(e, s.NextDueDate);
                return s with
                {
                    Amount = estimate.Amount ?? s.Amount,
                    Estimate = estimate,
                };
            })
            .ToList();
    }

    /// <summary>
    /// The upcoming agenda within <c>[from, to]</c> (ADR-0047): every active
    /// series' RRULE occurrences in the window — already fired (a committed
    /// header linked to the series, <c>kind="scheduled"</c>), skipped
    /// (<c>kind="skipped"</c>, a read-only trail per ADR-0049 D11), or not yet
    /// acted (<c>kind="reminder"</c>), ordered by date. RRULE expansion is pure
    /// C# (no clock); the fired + skipped sets are batched queries.
    /// </summary>
    public async Task<IReadOnlyList<UpcomingOccurrence>> GetUpcomingAsync(
        Guid ledgerId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from) return Array.Empty<UpcomingOccurrence>();

        var series = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.IsActive && r.TemplateHeaderId != null)
            .Join(
                _db.TxnHeaders.AsNoTracking(),
                r => r.TemplateHeaderId, h => h.Id,
                (r, h) => new
                {
                    r.Id, r.Rrule, r.StartDate, r.EndDate, r.NextDueDate, r.LastAcknowledgedDate,
                    h.Payee, Template = new SeenFrom(h.Id, h.OriginatingAccountId),
                    // Template source-side net (legs on the series' source account).
                    Amount = _db.TxnLegs
                        .Where(l => l.HeaderId == h.Id && l.AccountId == r.SourceAccountId)
                        .Sum(l => (decimal?)l.Amount) ?? 0m,
                    Source = _db.Accounts
                        .Where(a => a.Id == r.SourceAccountId)
                        .Select(a => new SourceAccount(a.Id, a.Name, a.CurrencyCode))
                        .FirstOrDefault(),
                })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Occurrences already materialized into committed headers in the window.
        var firedRows = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId
                        && !h.IsRecurringTemplate
                        && h.RecurringTransactionId != null
                        && h.OccurrenceDate != null
                        && h.OccurrenceDate >= from
                        && h.OccurrenceDate <= to)
            // Join the series for its source-account pointer (drives the amount).
            .Join(_db.RecurringTransactions.AsNoTracking(),
                  h => h.RecurringTransactionId, r => r.Id,
                  (h, r) => new { h, r.SourceAccountId, r.NextDueDate })
            .Select(x => new
            {
                x.h.Id, ReminderId = x.h.RecurringTransactionId!.Value, Date = x.h.OccurrenceDate!.Value,
                x.h.Payee, Seen = new SeenFrom(x.h.Id, x.h.OriginatingAccountId), x.NextDueDate,
                // The fired occurrence's committed net on the series' source account.
                Amount = _db.TxnLegs
                    .Where(l => l.HeaderId == x.h.Id && l.AccountId == x.SourceAccountId)
                    .Sum(l => (decimal?)l.Amount) ?? 0m,
                Source = _db.Accounts
                    .Where(a => a.Id == x.SourceAccountId)
                    .Select(a => new SourceAccount(a.Id, a.Name, a.CurrencyCode))
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var firedKeys = firedRows.Select(f => (f.ReminderId, f.Date)).ToHashSet();

        // Skipped (series, date) slots in the window (ADR-0047 D6) — surfaced as
        // read-only "skipped" chips (ADR-0049 D11), not hidden, so a catch-up
        // cascade leaves a visible trail on the calendar. A skip and a committed
        // occurrence are mutually exclusive per slot (SkipAsync / FireAsync
        // enforce it), so only the un-fired branch consults this set.
        var skippedKeys = (await _db.RecurringOccurrenceExceptions.AsNoTracking()
                .Where(e => e.LedgerId == ledgerId
                            && e.OccurrenceDate >= from
                            && e.OccurrenceDate <= to)
                .Select(e => new { e.RecurringTransactionId, e.OccurrenceDate })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(e => (e.RecurringTransactionId, e.OccurrenceDate))
            .ToHashSet();

        // Loan reminders (ADR-0050): the template's source-side legs are mostly
        // 0 (MD computes principal/interest live), so the agenda amount would be
        // escrow-only. Override UN-FIRED occurrences with the computed full
        // payment (principal + interest + escrow). Fired occurrences keep their
        // real committed amount.
        var loanAmountBySeries = await ComputeLoanAmountBySeriesAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // Estimated amounts (mig 220). Resolved once for the whole window rather than
        // per slot: the agenda renders a month at a time, and a per-series query in that
        // loop is an N+1 on every page load.
        var estimates = await ResolveEstimatesAsync(ledgerId, null, cancellationToken)
            .ConfigureAwait(false);

        // Each memo as its register shows it: a fired occurrence's own, an un-fired
        // slot's the template's.
        var memos = await MemosAsync(
                firedRows.Select(f => f.Seen).Concat(series.Select(s => s.Template)), cancellationToken)
            .ConfigureAwait(false);

        // Whether each series with auto-post days posts by itself, by the job's own
        // facts and predicate (AutoPostExclusion).
        var autoPost = (await AutoPostFactsAsync(ledgerId, estimates, cancellationToken).ConfigureAwait(false))
            .ToDictionary(
                f => f.Id,
                f => new UpcomingAutoPost(
                    Coffer.Api.Reminders.AutoPostExclusion.ReasonFor(
                        f.IsInvestmentShape, f.HasNoLines, f.UsesInactiveAccount, f.TemplateAmountDoesNotFit,
                        f.LoanAcrossCurrencies)));

        var result = new List<UpcomingOccurrence>(firedRows.Count + series.Count);

        // Materialized occurrences -> "scheduled".
        foreach (var f in firedRows)
            result.Add(new UpcomingOccurrence(
                f.Date, "scheduled", f.ReminderId, f.Id, f.Payee, memos.GetValueOrDefault(f.Id), f.Amount, f.NextDueDate,
                SourceAccountId: f.Source?.Id, SourceAccountName: f.Source?.Name, CurrencyCode: f.Source?.CurrencyCode,
                AutoPost: autoPost.GetValueOrDefault(f.ReminderId)));

        // Un-fired series slots (expansion clipped at the series end date when
        // earlier than the window end): a skipped slot -> "skipped" (read-only),
        // everything else -> "reminder". Fired slots are emitted above.
        var slots = new List<(int Series, DateOnly Date)>();
        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            var windowEnd = s.EndDate is { } end && end < to ? end : to;
            foreach (var date in _expander.Expand(s.Rrule, s.StartDate, from, windowEnd))
            {
                if (firedKeys.Contains((s.Id, date))) continue;
                // ADR-0051 ack floor: occurrences on/before the acknowledged date
                // were handled before Coffer tracked the series (e.g. Moneydance's
                // acknowledged date on import). They must not surface as
                // reminder/backlog — nor as "skipped" chips a pre-fix catch-up
                // cascade may have written below the floor. ComputeNextDueAsync
                // applies this same floor to the cursor; this is the agenda's
                // (second) reader of that invariant, so it can't drift below the
                // floor regardless of the caller's window start.
                if (s.LastAcknowledgedDate is { } ack && date <= ack) continue;
                slots.Add((i, date));
            }
        }

        // The other side of an estimate between currencies, per slot at its due date's
        // derived rate (ADR-0114 D13): every slot's rate in one load.
        var rates = await LoadEstimateRatesAsync(
                ledgerId, estimates, slots.Select(x => (series[x.Series].Id, (DateOnly?)x.Date)), cancellationToken)
            .ConfigureAwait(false);

        foreach (var (index, date) in slots)
        {
            var s = series[index];
            // Precedence is not arbitrary: a loan amount is COMPUTED from the loan's
            // terms (payment + escrow), so it outranks an average of recent history.
            // The resolver refuses estimates on loan series anyway, so this ordering is
            // belt and braces rather than the only guard.
            var estimate = estimates.GetValueOrDefault(s.Id) is { } resolved ? rates.Estimate(resolved, date) : null;
            var amount = loanAmountBySeries.TryGetValue(s.Id, out var loanAmount) ? loanAmount
                : estimate?.Amount ?? s.Amount;
            var kind = skippedKeys.Contains((s.Id, date)) ? "skipped" : "reminder";
            result.Add(new UpcomingOccurrence(
                date, kind, s.Id, null, s.Payee, memos.GetValueOrDefault(s.Template.HeaderId), amount, s.NextDueDate,
                estimate,
                SourceAccountId: s.Source?.Id, SourceAccountName: s.Source?.Name, CurrencyCode: s.Source?.CurrencyCode,
                AutoPost: autoPost.GetValueOrDefault(s.Id)));
        }

        return result.OrderBy(x => x.Date).ThenBy(x => x.Payee).ThenBy(x => x.ReminderId).ThenBy(x => x.HeaderId).ToList();
    }

    /// <summary>
    /// The occurrences on this ledger that the auto-post job may post, plus a count of
    /// those that fell out the back of its catch-up window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Built on <see cref="GetUpcomingAsync"/> on purpose.</b> That method already
    /// applies every filter this needs — the series must be active and materialized, the
    /// expansion is clipped at the series end date, fired slots are excluded, and the
    /// ADR-0051 acknowledged-date floor is honoured. A bespoke query would be a second
    /// copy of that list, and the copy would rot: the failure mode is not a crash, it is
    /// a timer posting an occurrence the agenda does not think is due.
    /// </para>
    /// <para>
    /// <b>Kind must be "reminder".</b> A skipped slot is EMITTED with
    /// <c>Kind == "skipped"</c> rather than omitted, because the calendar shows it as a
    /// read-only trail (ADR-0049 D11). Filtering on anything looser — "not scheduled",
    /// say — would auto-post occurrences the user explicitly suppressed.
    /// </para>
    /// <para>
    /// <b>The window is asymmetric.</b> Forward it reaches the largest acdays any series
    /// on the ledger asks for, because that is how far ahead "within N days of due" can
    /// see. Backward it reaches <paramref name="catchUpDays"/> plus one more window: the
    /// extra span is scanned but never posted, purely so the bound has a number to report
    /// rather than being silent.
    /// </para>
    /// </remarks>
    /// <param name="today">
    /// The ledger's local date for this run — <c>JobRunClock.LocalToday()</c>, never
    /// <c>DateTime.UtcNow</c>, which is a different date for any evening slot west of UTC.
    /// </param>
    /// <param name="catchUpDays">How far back the job is willing to post.</param>
    /// <param name="maxAutoCommitDaysBefore">
    /// Ceiling applied to each series' acdays. Rows above it are CLAMPED rather than
    /// skipped: the cap arrived after the column did, so an existing row can hold a
    /// larger value, and refusing to post it would silently disable a reminder the user
    /// still sees configured.
    /// </param>
    public async Task<Coffer.Api.Reminders.AutoPostScanResult> ScanForAutoPostAsync(
        Guid ledgerId,
        DateOnly today,
        int catchUpDays,
        int maxAutoCommitDaysBefore,
        CancellationToken cancellationToken = default)
    {
        // acdays per series, plus the facts the policy needs.
        var configured = await AutoPostFactsAsync(ledgerId, null, cancellationToken).ConfigureAwait(false);

        if (configured.Count == 0)
            return new Coffer.Api.Reminders.AutoPostScanResult(Array.Empty<Coffer.Api.Reminders.AutoPostCandidate>(), 0);

        var byId = configured.ToDictionary(
            c => c.Id,
            c => (Acdays: Math.Clamp(c.Acdays, 0, maxAutoCommitDaysBefore),
                  c.IsLoanReminder,
                  c.IsInvestmentShape,
                  c.HasNoLines,
                  c.UsesInactiveAccount,
                  c.Payee,
                  c.TemplateAmountDoesNotFit,
                  c.LoanAcrossCurrencies));

        var forwardReach = byId.Values.Max(v => v.Acdays);
        var postFloor = today.AddDays(-catchUpDays);
        var reportFloor = postFloor.AddDays(-catchUpDays);

        var upcoming = await GetUpcomingAsync(
                ledgerId, reportFloor, today.AddDays(forwardReach), cancellationToken)
            .ConfigureAwait(false);

        var due = new List<Coffer.Api.Reminders.AutoPostCandidate>();
        var tooOld = 0;

        foreach (var o in upcoming)
        {
            // Un-acted only. "scheduled" is already committed; "skipped" was suppressed
            // by a person and must never be posted by a timer.
            if (!string.Equals(o.Kind, "reminder", StringComparison.Ordinal)) continue;
            if (!byId.TryGetValue(o.ReminderId, out var cfg)) continue;

            // Not yet inside its own window: due later than acdays allows.
            if (o.Date > today.AddDays(cfg.Acdays)) continue;

            if (o.Date < postFloor)
            {
                tooOld++;
                continue;
            }

            due.Add(new Coffer.Api.Reminders.AutoPostCandidate(
                o.ReminderId, o.Date, cfg.Acdays, cfg.IsInvestmentShape, cfg.IsLoanReminder,
                cfg.HasNoLines, cfg.UsesInactiveAccount, cfg.Payee, cfg.TemplateAmountDoesNotFit,
                cfg.LoanAcrossCurrencies));
        }

        // Oldest first. A backlog posted newest-first would advance the series cursor past
        // slots still un-acted, and ComputeNextDueAsync would then have to walk back.
        return new Coffer.Api.Reminders.AutoPostScanResult(
            due.OrderBy(d => d.OccurrenceDate).ThenBy(d => d.ReminderId).ToList(), tooOld);
    }

    /// <summary>The source account an occurrence carries: id, name and currency.</summary>
    private sealed record SourceAccount(Guid Id, string Name, string CurrencyCode);

    /// <summary>One series with auto-post days, and the facts the auto-post policy reads.</summary>
    private sealed record AutoPostFacts(
        Guid Id, int Acdays, bool IsLoanReminder, bool IsInvestmentShape, bool HasNoLines,
        bool UsesInactiveAccount, string? Payee, bool TemplateAmountDoesNotFit, bool LoanAcrossCurrencies);

    /// <summary>
    /// Every active, materialized series with auto-post days, with the facts the
    /// auto-post policy reads. The one read of those facts: the job's scan and the
    /// agenda's per-occurrence <see cref="UpcomingAutoPost"/> both take them from here,
    /// so the agenda never says an occurrence will post that the job then leaves.
    /// </summary>
    /// <param name="estimates">The ledger's resolved estimates when the caller already
    /// holds them (<see cref="ResolveEstimatesAsync"/> for every series), else null and they
    /// are resolved here if a series needs them.</param>
    private async Task<List<AutoPostFacts>> AutoPostFactsAsync(
        Guid ledgerId, IReadOnlyDictionary<Guid, EstimateResolution>? estimates,
        CancellationToken cancellationToken)
    {
        var rows = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId
                        && r.IsActive
                        && r.TemplateHeaderId != null
                        && r.AutoCommitDaysBefore != null)
            .Join(_db.TxnHeaders.AsNoTracking(),
                  r => r.TemplateHeaderId, h => h.Id,
                  (r, h) => new
                  {
                      r.Id,
                      Acdays = r.AutoCommitDaysBefore!.Value,
                      r.IsLoanReminder,
                      // Investment shape is the template header carrying an action
                      // (ADR-0022); a bank template has none.
                      IsInvestmentShape = h.Action != null,
                      // The same test FireAsync refuses on: a template with no lines has
                      // no owner.
                      HasNoLines = h.OriginatingAccountId == null,
                      // The same test FireAsync refuses on: a line on a retired account.
                      UsesInactiveAccount = _db.TxnLegs.Any(l => l.HeaderId == h.Id
                          && _db.Accounts.Any(a => a.Id == l.AccountId && !a.IsActive)),
                      h.Payee,
                      TemplateHeaderId = h.Id,
                      r.SourceAccountId,
                      Estimating = r.EstimateSampleCount != null,
                      r.LoanAccountId,
                  })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var unfit = await SeriesWhoseFireFiguresDoNotFitAsync(
                ledgerId,
                rows.Select(r => new FireFigureSeries(
                    r.Id, r.TemplateHeaderId, r.SourceAccountId, r.IsLoanReminder, r.Estimating)).ToList(),
                estimates, cancellationToken)
            .ConfigureAwait(false);
        var loanAcross = await LoanSeriesAcrossCurrenciesAsync(
                ledgerId,
                rows.Where(r => r.IsLoanReminder)
                    .Select(r => new LoanSeries(r.Id, r.TemplateHeaderId, r.SourceAccountId, r.LoanAccountId))
                    .ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => new AutoPostFacts(
                r.Id, r.Acdays, r.IsLoanReminder, r.IsInvestmentShape, r.HasNoLines,
                r.UsesInactiveAccount, r.Payee, unfit.Contains(r.Id), loanAcross.Contains(r.Id)))
            .ToList();
    }

    /// <summary>What the loan currency check reads of one managed loan series.</summary>
    private sealed record LoanSeries(Guid Id, Guid TemplateHeaderId, Guid? SourceAccountId, Guid? LoanAccountId);

    /// <summary>
    /// Every managed loan series in the ledger whose payment would cross currencies, so
    /// both fires refuse it and it can never post (ADR-0114 D13). Cash ahead lists these
    /// as cross-currency.
    /// </summary>
    internal async Task<IReadOnlySet<Guid>> LoanSeriesAcrossCurrenciesAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var series = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.IsLoanReminder && r.TemplateHeaderId != null)
            .Select(r => new LoanSeries(r.Id, r.TemplateHeaderId!.Value, r.SourceAccountId, r.LoanAccountId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoanSeriesAcrossCurrenciesAsync(ledgerId, series, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The managed loan series whose clone fire is refused because the payment would cross
    /// currencies (<see cref="FireOutcome.LoanAcrossCurrencies"/>), by the fire's own
    /// predicate (<see cref="LoanPaymentAcrossCurrencies"/>) over the accounts its split
    /// prices (<see cref="LoanAccountsOf"/>) and the series' paying account (ADR-0114 D13).
    /// </summary>
    /// <remarks>Three reads at most, whatever the series count: the loan terms, the template
    /// lines of a series that names no loan, and the accounts.</remarks>
    private async Task<IReadOnlySet<Guid>> LoanSeriesAcrossCurrenciesAsync(
        Guid ledgerId, IReadOnlyList<LoanSeries> loanSeries, CancellationToken cancellationToken)
    {
        var across = new HashSet<Guid>();
        if (loanSeries.Count == 0) return across;
        var termsByLoan = await _db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .ToDictionaryAsync(t => t.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (termsByLoan.Count == 0) return across;

        var unnamed = loanSeries.Where(s => s.LoanAccountId is null).Select(s => s.TemplateHeaderId).ToList();
        var accountsByTemplate = unnamed.Count == 0
            ? new Dictionary<Guid, List<Guid>>()
            : (await _db.TxnLegs.AsNoTracking()
                    .Where(l => unnamed.Contains(l.HeaderId))
                    .Select(l => new { l.HeaderId, l.AccountId, l.PostingIndex })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(l => l.HeaderId)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.PostingIndex).Select(l => l.AccountId).ToList());

        var judged = new List<(Guid SeriesId, Guid Paying, LoanPaymentAccounts Loan)>();
        foreach (var s in loanSeries)
            if (s.SourceAccountId is { } paying
                && LoanAccountsOf(
                    s.LoanAccountId, paying, accountsByTemplate.GetValueOrDefault(s.TemplateHeaderId) ?? [],
                    termsByLoan) is { } loan)
                judged.Add((s.Id, paying, loan));
        if (judged.Count == 0) return across;

        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId,
                judged.SelectMany(j => new[]
                    { j.Paying, j.Loan.LoanAccountId, j.Loan.InterestAccountId, j.Loan.EscrowAccountId }).OfType<Guid>(),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var j in judged)
            if (LoanPaymentAcrossCurrencies(j.Paying, j.Loan, sides) is not null)
                across.Add(j.SeriesId);
        return across;
    }

    /// <summary>What the clone fire reads of one series to decide the figures it writes.</summary>
    private sealed record FireFigureSeries(
        Guid Id, Guid TemplateHeaderId, Guid? SourceAccountId, bool IsLoanReminder, bool Estimating);

    /// <summary>
    /// The series whose clone fire would write a figure finer than its currency
    /// (<see cref="FireOutcome.TemplateAmountDoesNotFit"/>), by the fire's own predicate
    /// (<see cref="FirstFigureThatDoesNotFit"/>) over the fire's own figures: each template
    /// line's, or the loan split's or the estimate's in its place, taking them in the
    /// order <see cref="FireAsync"/> does (ADR-0114 D15).
    /// </summary>
    /// <remarks>
    /// <para>The loan split is priced without a balance: its payment whole, no interest,
    /// and its stored escrow. That fits exactly when the fire's split does, because the
    /// fire's interest is rounded at the loan's places and its principal is the payment
    /// less that interest. So the answer does not depend on the occurrence's date, and the
    /// fact stays a series property, as every auto-post reason is.</para>
    /// <para>An estimating series whose estimate cannot be produced is not counted: the
    /// fire refuses it for that before it judges any figure.</para>
    /// <para>Three reads at most beside the estimates, whatever the series count: the
    /// template lines, their accounts, and the loan terms when a loan series is among
    /// them.</para>
    /// </remarks>
    private async Task<IReadOnlySet<Guid>> SeriesWhoseFireFiguresDoNotFitAsync(
        Guid ledgerId, IReadOnlyList<FireFigureSeries> series,
        IReadOnlyDictionary<Guid, EstimateResolution>? estimates, CancellationToken cancellationToken)
    {
        var templateIds = series.Select(s => s.TemplateHeaderId).ToList();
        var legs = await _db.TxnLegs.AsNoTracking()
            .Where(l => templateIds.Contains(l.HeaderId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legs.Count == 0) return new HashSet<Guid>();
        var legsByTemplate = legs.GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TxnLegRow>)g.ToList());
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId, legs.Select(l => l.AccountId), cancellationToken)
            .ConfigureAwait(false);

        var loanSplits = EmptyLoanSplits;
        if (series.Any(s => s.IsLoanReminder))
            loanSplits = (await LoanTermsWithCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false))
                .ToDictionary(
                    x => x.Terms.AccountId,
                    x => new LoanSplit(
                        x.Terms.AccountId, x.Terms.InterestAccountId, x.Terms.EscrowAccountId,
                        Principal: LoanPayment(x.Terms, x.CurrencyCode), Interest: 0m, Escrow: x.Terms.EscrowAmount));
        if (estimates is null && series.Any(s => s.Estimating))
            estimates = await ResolveEstimatesAsync(ledgerId, null, cancellationToken).ConfigureAwait(false);

        var unfit = new HashSet<Guid>();
        foreach (var s in series)
        {
            if (!legsByTemplate.TryGetValue(s.TemplateHeaderId, out var templateLegs)) continue;
            var overrides = s.IsLoanReminder && s.SourceAccountId is { } loanSource
                ? LoanLegOverrides(loanSource, templateLegs, loanSplits)
                : null;
            if (overrides is null && s.Estimating)
            {
                // The other side of an estimate between currencies is converted per
                // occurrence and rounded once at its account's places (DerivedRate.Convert),
                // so it fits whatever the rate: that rounding of the negated source estimate
                // stands in for it here, and the fact stays a series property.
                if (estimates?.GetValueOrDefault(s.Id) is not { Amount: { } estimatedNet } resolved
                    || s.SourceAccountId is not { } estimateSource
                    || EstimateLegOverrides(
                            estimateSource, templateLegs, estimatedNet,
                            resolved.Crossing is { } crossing
                                ? CurrencyMinorUnits.RoundAwayFromZero(-estimatedNet, crossing.OtherCurrency)
                                : null)
                        is not { } estimated)
                    continue;
                overrides = estimated;
            }
            if (FirstFigureThatDoesNotFit(templateLegs, sides, overrides) is not null) unfit.Add(s.Id);
        }
        return unfit;
    }

    /// <summary>
    /// Materialize one occurrence (ADR-0047 D5) as the auto-post job does: clone a
    /// BANK series' template header + legs into a LIVE committed header dated at
    /// <paramref name="occurrenceDate"/>, stamped with the series id +
    /// occurrence date, and advance the series cursor. Idempotent — firing the
    /// same (series, date) twice returns the already-materialized header rather
    /// than creating a duplicate. The committed header is a normal transaction
    /// (NOT a template) so it flows through balances/register; the recompute
    /// interceptors fire on SaveChanges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The auto-post job is its only caller.</b> A person posts an occurrence through
    /// <see cref="FireBankAsync"/> or <see cref="FireInvestmentAsync"/>, with the values
    /// the occurrence dialog shows; nothing in the UI copies a template as it stands.
    /// </para>
    /// <para>
    /// <b>Earlier un-acted occurrences are left open.</b> A person's post marks them
    /// skipped, and the dialog shows how many before they confirm. A timer has no
    /// dialog: marking them skipped would suppress occurrences nobody chose to skip, and
    /// the catch-up bound that stops the job posting them would then stop anyone seeing
    /// them.
    /// </para>
    /// <para>
    /// <b>It refuses what it cannot post as it stands</b>, before writing anything: an
    /// investment template (an investment occurrence means holdings and lots, which only
    /// the investment create builds), a template with a line on an inactive account, a
    /// template with no lines, and an estimate that cannot be produced.
    /// </para>
    /// </remarks>
    public async Task<FireResult> FireAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate,
        CancellationToken cancellationToken = default)
    {
        // The transaction opens BEFORE the checks. Its first statement is the ledger write
        // lock (migration 275), then the slot lock: every fire takes them in that order,
        // and the ledger lock refuses a session already holding the slot lock, so an
        // auto-post and a person's post of one occurrence never each hold the lock the
        // other waits for. With the idempotency read and the skip read outside any
        // transaction and the insert opening its own further down, the checks would not
        // even be repeatable-read against the write they guard.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new FireResult(FireOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new FireResult(FireOutcome.NotMaterialized, null);

        // Idempotency: a committed occurrence for this (series, date) already?
        var existing = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && h.OccurrenceDate == occurrenceDate
                        && !h.IsRecurringTemplate)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is { } existingId) return new FireResult(FireOutcome.Ok, existingId);

        // Skip/fire are mutually exclusive per slot (ADR-0047 D6): refuse to
        // materialize an occurrence the user suppressed.
        var isSkipped = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .AnyAsync(e => e.RecurringTransactionId == reminderId
                           && e.OccurrenceDate == occurrenceDate, cancellationToken)
            .ConfigureAwait(false);
        if (isSkipped) return new FireResult(FireOutcome.OccurrenceSkipped, null);

        var template = await _db.TxnHeaders.AsNoTracking()
            .FirstAsync(h => h.Id == templateHeaderId, cancellationToken).ConfigureAwait(false);
        // An investment template carries an action. Copying its legs would commit the
        // event without the holdings and lots the investment create builds.
        if (template.Action is not null) return new FireResult(FireOutcome.ShapeMismatch, null);
        var templateLegs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == templateHeaderId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // A new transaction never lands on a retired account, as every by-hand post
        // refuses. Read inside the slot's transaction, so an account retired after the
        // job's scan is still caught.
        var legAccountIds = templateLegs.Select(l => l.AccountId).Distinct().ToList();
        if (await _db.Accounts.AsNoTracking()
                .AnyAsync(a => legAccountIds.Contains(a.Id) && !a.IsActive, cancellationToken)
                .ConfigureAwait(false))
            return new FireResult(FireOutcome.AccountInactive, null);

        // A managed loan payment stays in the loan's currency (ADR-0114 D13). A series set
        // up before the guard, or whose paying account or real escrow account changed
        // currency since, is refused here, before its split is priced.
        if (series.IsLoanReminder
            && series.SourceAccountId is { } loanPaying
            && await LoanFireAcrossCurrenciesAsync(
                    ledgerId, series.LoanAccountId, loanPaying,
                    templateLegs.OrderBy(l => l.PostingIndex).Select(l => l.AccountId),
                    cancellationToken)
                .ConfigureAwait(false) is { } crossed)
            return new FireResult(FireOutcome.LoanAcrossCurrencies, null, LoanCurrencies: crossed);

        // Loan reminders (ADR-0050): the template legs are near-zero (MD computes
        // principal/interest live), so substitute the computed split before
        // cloning — a verbatim-clone fire must commit the same real cash the
        // agenda/detail show, not the placeholder template amounts. The split is
        // priced as of this payment's own posted_at (ADR-0078, 2026-10-21).
        var postedAt = PaymentPostedAt(occurrenceDate);
        var loanOverrides = series.IsLoanReminder
            ? await ComputeLoanLegOverridesAsync(ledgerId, series.SourceAccountId, templateLegs, postedAt, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // Estimated amounts (mig 220). Mutually exclusive with a loan series by the
        // eligibility gate, so this only runs when the loan override did not, and the two
        // can never both apply to one leg.
        //
        // A configured-but-unresolvable estimate REFUSES the fire rather than committing
        // the template amount: the user asked for a computed figure, and quietly posting a
        // different one on an unattended run is answering a question they did not ask.
        IReadOnlyDictionary<Guid, decimal>? legOverrides = loanOverrides;
        if (loanOverrides is null && series.EstimateSampleCount is not null)
        {
            legOverrides = await ComputeEstimateLegOverridesAsync(
                    ledgerId, series, templateLegs, occurrenceDate, cancellationToken)
                .ConfigureAwait(false);
            if (legOverrides is null)
                return new FireResult(FireOutcome.EstimateUnavailableForFire, null);
        }

        // Every figure the clone writes is a new write, the template's own or an override in
        // its place, so it must fit its currency's places (ADR-0114 D15), as a by-hand post
        // of the same line must.
        if (await TemplateFigureThatDoesNotFitAsync(ledgerId, templateLegs, legOverrides, cancellationToken)
                .ConfigureAwait(false) is { } unfitCurrency)
            return new FireResult(FireOutcome.TemplateAmountDoesNotFit, null, UnfitCurrencyCode: unfitCurrency);

        // Clone the template into a committed occurrence + advance the cursor
        // atomically (two SaveChanges: persist the occurrence, then recompute
        // the skip-aware cursor, which reads committed headers). Adjust-at-post
        // (edited values) goes through FireBankAsync / FireInvestmentAsync.
        // The transaction was opened above, with the slot lock, so the checks and
        // this write are one unit.
        var newHeaderId = Guid.NewGuid();
        // The clone is owned by the template's owner (mig 263). Every create path builds
        // a template with lines, so it has an owner; a template without lines has none.
        // Moneydance can hold reminders with no field values; the importer skips those
        // with no splits, but nothing in the schema rules a lineless template out.
        // Cloning one would commit a transaction that moves no money and belongs to no
        // account, so the fire is refused. Nothing has been written yet, and the
        // transaction rolls back on dispose.
        if (template.OriginatingAccountId is not { } owner)
            return new FireResult(FireOutcome.TemplateHasNoLines, null);
        var occurrence = TxnHeaderRow.NewOccurrence(newHeaderId, ledgerId, owner);
        occurrence.Payee = template.Payee;
        occurrence.PostedAt = postedAt;
        // NOT NULL since mig 189: no distinct tax date is stored as the posted date.
        occurrence.TransactedAt = postedAt;
        occurrence.CheckNumber = template.CheckNumber;
        occurrence.IsPending = false;
        occurrence.IsHidden = false;
        occurrence.IsMergedInto = null;
        occurrence.RecurringTransactionId = reminderId;
        occurrence.OccurrenceDate = occurrenceDate;
        _db.TxnHeaders.Add(occurrence);
        var occurrenceLegs = new List<TxnLegRow>(templateLegs.Count);
        foreach (var leg in templateLegs)
        {
            var clone = new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = newHeaderId,
                LedgerId = ledgerId,
                AccountId = leg.AccountId,
                PostingIndex = leg.PostingIndex,
                Amount = legOverrides is not null && legOverrides.TryGetValue(leg.Id, out var overrideAmount)
                    ? overrideAmount
                    : leg.Amount,
                SecurityId = leg.SecurityId,
                Quantity = leg.Quantity,
                UnitPrice = leg.UnitPrice,
                PostingRole = leg.PostingRole,
            };
            _db.TxnLegs.Add(clone);
            occurrenceLegs.Add(clone);
        }

        // The template's notes, cloned where they sit: the header's memo and tags and
        // every leg's own, by posting and account, with the template's owner. A clone,
        // so never capped; the template already holds only what its shape can.
        var templateNotes = await AnnotationWriter.LoadAsync(_db, template, cancellationToken).ConfigureAwait(false);
        var occurrenceShape = AnnotationWriter.Shape(owner, template.Action,
            occurrenceLegs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id, l.PostingRole)),
            templateNotes.Facts);
        var clonedNotes = AnnotationPlanner.Seed(occurrenceShape, templateNotes.Notes);
        await AnnotationWriter.ApplyAsync(_db, occurrence, occurrenceLegs, clonedNotes, cancellationToken)
            .ConfigureAwait(false);

        // No catch-up cascade: earlier un-acted occurrences stay open (see the remarks).
        try
        {
            // Persist the fired occurrence FIRST so the cursor recompute counts it as
            // consumed (ComputeNextDueAsync reads committed headers + exceptions via
            // AsNoTracking).
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Advance the cursor to the earliest occurrence that is neither fired nor
            // skipped (ADR-0047). An earlier un-acted occurrence keeps it there.
            series.NextDueDate = await ComputeNextDueAsync(
                reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateOccurrence(ex))
        {
            // Someone else committed this slot between our idempotency read and this
            // write. The locks above make that unreachable for two callers that both
            // take them — this is the backstop for one that does not, or for a second
            // process, and it is what turns the index from a 500 into the SAME answer
            // the sequential path gives: the slot is fired, here is its header.
            // Disposing the lock's transaction rolls it back; the read below runs
            // outside it.
            await write.DisposeAsync().ConfigureAwait(false);
            var winner = await WinningOccurrenceHeaderIdAsync(
                reminderId, occurrenceDate, cancellationToken).ConfigureAwait(false);
            return new FireResult(FireOutcome.Ok, winner);
        }

        return new FireResult(FireOutcome.Ok, newHeaderId);
    }

    /// <summary>
    /// The currency of the first figure the clone would write that carries more places than
    /// that currency allows, or null when every one fits (ADR-0114 D15).
    /// </summary>
    private async Task<string?> TemplateFigureThatDoesNotFitAsync(
        Guid ledgerId, IReadOnlyList<TxnLegRow> templateLegs,
        IReadOnlyDictionary<Guid, decimal>? overrides, CancellationToken cancellationToken)
    {
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId, templateLegs.Select(l => l.AccountId), cancellationToken)
            .ConfigureAwait(false);
        return FirstFigureThatDoesNotFit(templateLegs, sides, overrides);
    }

    /// <summary>
    /// The currency of the first figure a clone of <paramref name="templateLegs"/> would
    /// write that carries more places than that currency allows, or null when every one
    /// fits (ADR-0114 D15). The figure is the override where the fire has one, else the
    /// template's own.
    /// </summary>
    /// <remarks>
    /// <para>A leg is read at its account's currency, and a category's leg at the real
    /// side's, the other leg of its posting, since it mirrors that side (ADR-0114 D13); a
    /// posting between two categories takes the category's own code.</para>
    /// <para>An override is judged too, because not every one is computed: a loan's
    /// escrow is its stored amount, and with a fixed payment the principal is that stored
    /// payment less the interest. A computed interest or payment is already rounded at the
    /// loan's places, so it passes.</para>
    /// <para>The one predicate for the fire and for the auto-post scan's
    /// <see cref="Coffer.Api.Reminders.AutoPostExclusion.TemplateAmountDoesNotFit"/>, so the
    /// agenda never says an occurrence posts by itself that the fire then refuses.</para>
    /// </remarks>
    private static string? FirstFigureThatDoesNotFit(
        IReadOnlyList<TxnLegRow> templateLegs,
        IReadOnlyDictionary<Guid, PostingLegPair.AccountSide> sides,
        IReadOnlyDictionary<Guid, decimal>? overrides)
    {
        foreach (var leg in templateLegs.OrderBy(l => l.PostingIndex).ThenBy(l => l.Id))
        {
            if (!sides.TryGetValue(leg.AccountId, out var side)) continue;
            var currency = side.CurrencyCode;
            if (!side.IsReal
                && templateLegs.FirstOrDefault(o => o.PostingIndex == leg.PostingIndex && o.Id != leg.Id) is { } other
                && sides.TryGetValue(other.AccountId, out var otherSide)
                && otherSide.IsReal)
                currency = otherSide.CurrencyCode;
            var amount = overrides is not null && overrides.TryGetValue(leg.Id, out var overridden)
                ? overridden
                : leg.Amount;
            if (!CurrencyMinorUnits.Fits(amount, currency)) return currency;
        }
        return null;
    }

    /// <summary>
    /// Adjust-at-post for a BANK series (ADR-0049): commit the EDITED bank
    /// transaction as this occurrence, REUSING the live bank create
    /// (<see cref="TransactionsRepository.CreateAsync"/>) — stamped to the series
    /// + slot, joining this repository's transaction so the create + catch-up
    /// cascade + cursor advance are ONE atomic unit. Idempotent; refuses a
    /// skipped slot; rejects an investment series
    /// (<see cref="FireOutcome.ShapeMismatch"/>). Posting/account validation is
    /// the endpoint's job (same as a bank create).
    /// </summary>
    public async Task<FireResult> FireBankAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate,
        FireBankReminderRequest request, Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        // Transaction first, with the ledger write lock (migration 275) as its first
        // statement and the slot lock after it, then the checks — the order FireAsync
        // takes them in (see there, and migration 218). The reads below decide whether to
        // write; outside a transaction they were not repeatable-read against that write,
        // and without the slot lock this path could interleave with a skip on the same
        // slot. The create below joins this transaction, where the ledger lock is held.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new FireResult(FireOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new FireResult(FireOutcome.NotMaterialized, null);

        // The bank fire route serves bank series only (investment edits carry
        // holdings/lots — they post through /fire/investment).
        var templateAction = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == templateHeaderId).Select(h => h.Action)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        if (templateAction is not null) return new FireResult(FireOutcome.ShapeMismatch, null);

        // Idempotency: a committed occurrence for this (series, date) already?
        var existing = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && h.OccurrenceDate == occurrenceDate
                        && !h.IsRecurringTemplate)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { } existingId) return new FireResult(FireOutcome.Ok, existingId);

        // Skip/fire are mutually exclusive per slot (ADR-0047 D6).
        var isSkipped = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .AnyAsync(e => e.RecurringTransactionId == reminderId
                           && e.OccurrenceDate == occurrenceDate, cancellationToken)
            .ConfigureAwait(false);
        if (isSkipped) return new FireResult(FireOutcome.OccurrenceSkipped, null);

        // A managed loan payment stays in the loan's currency (ADR-0114 D13): the paying
        // account the occurrence dialog names, and a real interest or escrow account the
        // split prices, are judged before the split is priced or the create runs. A line
        // the split does not price is an ordinary posting and keeps both its amounts.
        if (series.IsLoanReminder
            && series.LoanAccountId is { } firedLoanId
            && await LoanFireAcrossCurrenciesAsync(
                    ledgerId, firedLoanId, request.SourceAccountId, [], cancellationToken)
                .ConfigureAwait(false) is { } crossed)
            return new FireResult(FireOutcome.LoanAcrossCurrencies, null, LoanCurrencies: crossed);

        // Managed loan reminder: the split is authoritative. Recompute it from the
        // loan terms + the loan's balance as of this payment's own posted_at
        // (ADR-0078, 2026-10-21) and override the client's posting amounts (matched
        // by counterparty account), so a stale or hand-edited client can't post a
        // wrong principal/interest/escrow division. Read BEFORE CreateAsync so "owed"
        // is the balance going into this payment.
        var postedAt = PaymentPostedAt(request.PostedDate ?? occurrenceDate);
        var postings = request.Postings;
        if (series.IsLoanReminder && series.LoanAccountId is { } managedLoanId)
        {
            var splits = await ComputeLoanSplitsAsync(ledgerId, postedAt, cancellationToken).ConfigureAwait(false);
            // Amounts only, one for one and in order: each posting keeps its place, so
            // the notes' per-line entries still name the same lines. A line the split
            // does not price keeps both its amounts as the client sent them, the second
            // one included (ADR-0114 D13); a priced line is in the loan's currency and
            // carries one amount, so a second one sent on it is not kept.
            if (splits.TryGetValue(managedLoanId, out var split))
                postings = request.Postings.Select(p =>
                {
                    decimal? priced = p.CounterpartyAccountId == split.LoanAccountId ? -split.Principal
                        : p.CounterpartyAccountId == split.InterestAccountId ? -split.Interest
                        : p.CounterpartyAccountId == split.EscrowAccountId ? -split.Escrow
                        : null;
                    return new TransactionPosting
                    {
                        CounterpartyAccountId = p.CounterpartyAccountId,
                        Amount = priced ?? p.Amount,
                        CounterpartyAmount = priced is null ? p.CounterpartyAmount : null,
                    };
                }).ToList();
        }

        // Reuse the live bank create (source/counterpart legs for every posting,
        // incl. splits; notes through the write primitive; balances via the
        // interceptor), stamped to this occurrence; it JOINS this transaction (does
        // not commit). A refused write returns before anything is written, and the
        // transaction rolls back on dispose.
        var created = await _transactions.CreateAsync(
            ledgerId, request.SourceAccountId, postedAt, request.Payee,
            request.CheckNumber, null, postings, request.Notes?.Intent(),
            recurringTransactionId: reminderId, occurrenceDate: occurrenceDate,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (created.Refusal is { } refusal)
            return new FireResult(FireOutcome.NotesRefused, null, Refusal: refusal);
        if (created.PostingRefusal is { } refusedPosting)
            return new FireResult(FireOutcome.PostingRefused, null, PostingRefusal: refusedPosting);
        var newHeaderId = created.HeaderId;

        // Catch-up (ADR-0047 §9.2): mark earlier un-acted slots skipped.
        var cascaded = await CascadeSkipEarlierAsync(
            ledgerId, reminderId, series.Rrule, series.StartDate, occurrenceDate,
            createdByUserId, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        series.NextDueDate = await ComputeNextDueAsync(
            reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FireResult(FireOutcome.Ok, newHeaderId,
            cascaded.Count, cascaded.Count > 0 ? cascaded[0] : null);
    }

    /// <summary>
    /// Adjust-at-post for an INVESTMENT series (ADR-0049): commit the EDITED
    /// investment transaction as this occurrence, REUSING the live investment
    /// create path (<see cref="InvestmentTransactionsRepository.CreateAsync"/>)
    /// so holdings + lots + balances are built exactly as a normal investment
    /// transaction — stamped to the series + slot. Idempotent; refuses a skipped
    /// slot; runs the same catch-up cascade + cursor advance as the bank fire,
    /// all in ONE transaction (CreateAsync joins it via its ambient-tx check).
    /// Rejects a bank series (<see cref="FireOutcome.ShapeMismatch"/>).
    /// </summary>
    public async Task<FireInvestmentResult> FireInvestmentAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate,
        CreateInvestmentTransactionRequest request, Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        // Transaction first, with the ledger write lock (migration 275) as its first
        // statement and the slot lock after it, then the checks — the order FireAsync
        // takes them in (see there, and migration 218). The reads below decide whether to
        // write; outside a transaction they were not repeatable-read against that write,
        // and without the slot lock this path could interleave with a skip on the same
        // slot. The create below joins this transaction, where the ledger lock is held.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new FireInvestmentResult(FireOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new FireInvestmentResult(FireOutcome.NotMaterialized, null);

        // The investment fire route serves investment series only (a bank series
        // posts through /fire/bank).
        var templateAction = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == templateHeaderId).Select(h => h.Action)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        if (templateAction is null) return new FireInvestmentResult(FireOutcome.ShapeMismatch, null);

        // Idempotency: a committed occurrence for this (series, date) already?
        var existing = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && h.OccurrenceDate == occurrenceDate
                        && !h.IsRecurringTemplate)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { } existingId) return new FireInvestmentResult(FireOutcome.Ok, existingId);

        // Skip/fire are mutually exclusive per slot (ADR-0047 D6).
        var isSkipped = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .AnyAsync(e => e.RecurringTransactionId == reminderId
                           && e.OccurrenceDate == occurrenceDate, cancellationToken)
            .ConfigureAwait(false);
        if (isSkipped) return new FireInvestmentResult(FireOutcome.OccurrenceSkipped, null);

        // Reuse the live investment create (validates the action × field matrix,
        // builds legs + holdings + lots), stamped to this occurrence; it JOINS
        // this transaction (does not commit). On a shape-validation failure it
        // returns before any write, so the transaction rolls back clean.
        var created = await _investmentTxns.CreateAsync(
            ledgerId, request, recurringTransactionId: reminderId, occurrenceDate: occurrenceDate,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (created.Failure is { } failure)
            return new FireInvestmentResult(FireOutcome.ShapeFailure, null, failure,
                InvestmentFailureDetail: created.Detail);
        if (created.Refusal is { } refusal)
            return new FireInvestmentResult(FireOutcome.NotesRefused, null, Refusal: refusal);

        // Catch-up (ADR-0047 §9.2): mark earlier un-acted slots skipped.
        var cascaded = await CascadeSkipEarlierAsync(
            ledgerId, reminderId, series.Rrule, series.StartDate, occurrenceDate,
            createdByUserId, cancellationToken).ConfigureAwait(false);

        // CreateAsync already flushed header + legs + holdings + lots (no commit);
        // persist the cascade, then recompute the skip-aware cursor.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        series.NextDueDate = await ComputeNextDueAsync(
            reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FireInvestmentResult(FireOutcome.Ok, created.HeaderId,
            null, cascaded.Count, cascaded.Count > 0 ? cascaded[0] : null);
    }

    // ----------------------------------------------------------------------
    // Mutation surface (ADR-0047 slice — manual authoring)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Per-series detail: recurrence metadata + the template's full leg list
    /// (with account names + investment metadata). <c>Kind</c> is derived from
    /// the template header's <c>action</c> (null → bank, non-null →
    /// investment). Null when the series isn't in this ledger or has no
    /// template (dormant).
    /// </summary>
    /// <param name="occurrenceDate">The occurrence a loan series' split legs are
    /// priced for, as of that payment's own date (ADR-0078, 2026-10-21); null for the
    /// series' next due occurrence.</param>
    public async Task<ReminderDetail?> GetDetailAsync(
        Guid ledgerId, Guid reminderId, DateOnly? occurrenceDate, CancellationToken cancellationToken = default)
    {
        var row = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId && r.TemplateHeaderId != null)
            .Join(_db.TxnHeaders.AsNoTracking(), r => r.TemplateHeaderId, h => h.Id, (r, h) => new { r, h })
            .Select(x => new
            {
                x.r.Id, x.r.Rrule, x.r.StartDate, x.r.EndDate, x.r.NextDueDate,
                x.r.AutoCommitDaysBefore, x.r.IsActive, x.r.IsLoanReminder, x.r.Origin, x.r.SourceAccountId,
                x.h.Payee, x.h.CheckNumber, x.h.Action, TemplateHeaderId = x.h.Id, x.h.OriginatingAccountId,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        // The template's stored presentation, by leg: what the editor's notes come from.
        var stored = await _db.StoredLegPresentations.AsNoTracking()
            .Where(n => n.HeaderId == row.TemplateHeaderId)
            .ToDictionaryAsync(n => n.LegId, cancellationToken).ConfigureAwait(false);
        var legs = await (
            from l in _db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == row.TemplateHeaderId)
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            join s in _db.Securities.AsNoTracking() on l.SecurityId equals s.Id into secs
            from s in secs.DefaultIfEmpty()
            orderby l.PostingIndex, l.Amount
            select new ReminderLegDto(
                l.Id, l.AccountId, a.Name, l.PostingIndex, l.Amount, null, Array.Empty<string>(),
                l.SecurityId, s != null ? s.Ticker : null, l.Quantity, l.UnitPrice, l.PostingRole))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Each side's own memo and tags, the header's and every leg's, from the template's
        // stored presentation. The editor seeds its fields from them, so a save restates
        // the stored values rather than replacing ones it never showed, and an inherited
        // value is never handed to it as a leg's own. Filled before the loan pricing,
        // which copies the legs it reprices.
        var tagRoles = (await (
                from nt in _db.TxnLegNoteTags.AsNoTracking()
                where nt.HeaderId == row.TemplateHeaderId
                join t in _db.Tags.AsNoTracking() on nt.TagId equals t.Id
                select new { nt.LegId, t.Name, nt.Shown, nt.TransactionLevel })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.LegId);
        legs =
        [
            .. legs.Select(l => l with
            {
                LegMemo = StoredNotesQuery.OwnMemo(stored[l.LegId]),
                LegTags = StoredNotesQuery.OwnsShownTags(stored[l.LegId])
                    ? TagNamesInOrder(tagRoles[l.LegId].Where(t => t.Shown).Select(t => t.Name))
                    : [],
            }),
        ];
        // The transaction's own memo and tags where the header is in play on its
        // originating account; elsewhere the notes sit on the legs. A template with no
        // legs has no side to present it: the editor opens with the transaction's own
        // notes, so a save does not clear them.
        string? headerMemo;
        IReadOnlyList<string> headerTags;
        if (stored.Count == 0)
        {
            var legless = (await LeglessNotesAsync([row.TemplateHeaderId], cancellationToken).ConfigureAwait(false))
                .GetValueOrDefault(row.TemplateHeaderId);
            headerMemo = legless?.Memo;
            headerTags = legless is null ? [] : TagNamesInOrder(legless.Tags);
        }
        else
        {
            var owning = stored.Values
                .Where(n => n.AccountId == row.OriginatingAccountId && n.HeaderInPlay == true)
                .OrderBy(n => n.PostingIndex).ThenBy(n => n.LegId)
                .FirstOrDefault();
            headerMemo = owning?.HeaderMemo;
            headerTags = owning is null
                ? []
                : TagNamesInOrder(tagRoles[owning.LegId].Where(t => t.TransactionLevel).Select(t => t.Name));
        }

        // Loan reminders (ADR-0050): replace the mostly-zero template legs with
        // the computed principal/interest/escrow split so the occurrence dialog
        // prefills real, editable values (and the calendar/agenda agree). Priced as
        // of the payment the occurrence would post, the date the fire stamps it with.
        // Without a requested occurrence it is the next due one; a series with none
        // left is priced at its end date, or its start when it has no end.
        if (row.IsLoanReminder)
        {
            var pricedFor = occurrenceDate ?? row.NextDueDate ?? row.EndDate ?? row.StartDate;
            legs = await ApplyLoanSplitToLegsAsync(
                    ledgerId, row.SourceAccountId, legs, PaymentPostedAt(pricedFor), cancellationToken)
                .ConfigureAwait(false);
        }

        // Mig 220. Resolved for this ONE series (the function takes a series scope), and
        // deliberately not folded into the leg amounts — see ReminderDetail.Estimate.
        // A transfer between currencies estimates its other side for the occurrence asked
        // for, at its due date's derived rate (ADR-0114 D13), else for the one the loan
        // pricing above takes.
        var estimates = await ResolveEstimatesAsync(ledgerId, reminderId, cancellationToken)
            .ConfigureAwait(false);
        ReminderEstimate? estimateDto = null;
        if (estimates.GetValueOrDefault(reminderId) is { } estimateResolution)
        {
            var dueFor = occurrenceDate ?? row.NextDueDate ?? row.EndDate ?? row.StartDate;
            var rates = await LoadEstimateRatesAsync(
                    ledgerId, estimates, [(reminderId, (DateOnly?)dueFor)], cancellationToken)
                .ConfigureAwait(false);
            estimateDto = rates.Estimate(estimateResolution, dueFor);
        }

        return new ReminderDetail(
            row.Id, row.Action is null ? "bank" : "investment",
            row.Payee, headerMemo, headerTags, row.CheckNumber, row.Action,
            row.Rrule, row.StartDate, row.EndDate, row.NextDueDate, row.AutoCommitDaysBefore,
            row.IsActive, row.IsLoanReminder, row.Origin, row.SourceAccountId, legs,
            estimateDto);
    }

    /// <summary>Tag names in the order the register lists them.</summary>
    private static IReadOnlyList<string> TagNamesInOrder(IEnumerable<string> names) =>
        [.. names.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Create a BANK-shape reminder series: a template header
    /// (<c>is_recurring_template=true</c>) + source/counterpart legs (mirroring
    /// <see cref="TransactionsRepository.CreateAsync"/>) + the slim series row.
    /// Shape + account validation happens in the endpoint; this trusts it.
    /// </summary>
    public async Task<CreateReminderResult> CreateBankAsync(
        Guid ledgerId, CreateReminderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Notes is { } sent && sent.Side != request.SourceAccountId)
            return new CreateReminderResult(CreateOutcome.NotesRefused, null, null, NotFromSource());

        var templateHeaderId = Guid.NewGuid();
        // The template's legs are legs: their save recomputes the accounts they sit on,
        // so the ledger write lock (migration 275) is the transaction's first statement.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // Each posting's two legs, as a live create builds them (ADR-0114 D13), before
        // anything is added.
        var (amounts, refused) = await BuildTemplateAmountsAsync(
                ledgerId, request.SourceAccountId, request.Postings, stored: null, cancellationToken)
            .ConfigureAwait(false);
        if (refused is not null)
            return new CreateReminderResult(CreateOutcome.PostingRefused, null, null, PostingRefusal: refused);

        var header = NewTemplateHeader(
            templateHeaderId, ledgerId, request.SourceAccountId, request.StartDate,
            request.Payee, request.CheckNumber, action: null);
        _db.TxnHeaders.Add(header);

        var legs = new List<TxnLegRow>();
        for (var i = 0; i < request.Postings.Count; i++)
            legs.AddRange(AddBankTemplateLegs(templateHeaderId, ledgerId, request.SourceAccountId, request.Postings[i], amounts[i], i));

        if (await SeedTemplateNotesAsync(
                ledgerId, header, legs,
                request.Notes?.Intent() ?? AnnotationIntent<string>.Untouched(request.SourceAccountId),
                cancellationToken).ConfigureAwait(false) is { } refusal)
            return new CreateReminderResult(CreateOutcome.NotesRefused, null, null, refusal);

        var reminderId = await AddSeriesRowAsync(
            ledgerId, templateHeaderId, request.SourceAccountId, request.Rrule, request.StartDate,
            request.EndDate, request.AutoCommitDaysBefore, cancellationToken,
            estimateSampleCount: request.EstimateSampleCount).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateReminderResult(CreateOutcome.Ok, reminderId, null);
    }

    /// <summary>Outcome of <see cref="CreateManagedLoanReminderAsync"/>.</summary>
    public enum ManagedLoanReminderResult
    {
        Ok, LoanTermsMissing, AlreadyExists,

        /// <summary>The paying account, or a real interest or escrow account, is in another
        /// currency than the loan's (ADR-0114 D13); nothing is written.</summary>
        CurrencyMismatch,
    }

    /// <summary>Result + the new reminder id (null on failure), and on
    /// <see cref="ManagedLoanReminderResult.CurrencyMismatch"/> the two currencies.</summary>
    public sealed record ManagedLoanReminderOutcome(
        ManagedLoanReminderResult Result, Guid? ReminderId, LoanPaymentCurrencies? Currencies = null);

    /// <summary>
    /// Create the managed payment reminder for a loan account (ADR-0050 ext). The
    /// template legs post to (loan · interest · escrow) with PLACEHOLDER amounts —
    /// the real principal/interest/escrow split is computed live from loan_terms +
    /// the loan's balance as of the payment's own date (ADR-0078,
    /// <see cref="ComputeLoanSplitsAsync"/>).
    /// Flags the series <c>is_loan_reminder</c> + links <c>loan_account_id</c> (one
    /// per loan, enforced by the partial-unique index + the AlreadyExists guard).
    /// Cadence is derived from the loan's payments-per-year on the supplied start
    /// day. The caller validates the loan account + the bank source account.
    /// </summary>
    public async Task<ManagedLoanReminderOutcome> CreateManagedLoanReminderAsync(
        Guid ledgerId, Guid loanAccountId, Guid sourceAccountId, DateOnly startDate,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) before the checks: the template's legs
        // recompute the accounts they sit on, and under the lock two creates for one loan
        // take turns, so the second finds the first's reminder.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var terms = await _db.LoanTerms.AsNoTracking()
            .FirstOrDefaultAsync(t => t.LedgerId == ledgerId && t.AccountId == loanAccountId, cancellationToken)
            .ConfigureAwait(false);
        // Needs the interest + escrow target accounts to build the split legs.
        if (terms is null || terms.InterestAccountId is null || terms.EscrowAccountId is null)
            return new(ManagedLoanReminderResult.LoanTermsMissing, null);

        var exists = await _db.RecurringTransactions.AsNoTracking()
            .AnyAsync(r => r.LedgerId == ledgerId && r.LoanAccountId == loanAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (exists) return new(ManagedLoanReminderResult.AlreadyExists, null);

        // A managed payment stays in the loan's currency (ADR-0114 D13): interest and escrow
        // are computed in it, so the paying account and a real interest or escrow account
        // must be kept in it. A category takes the paying account's currency, as every
        // category line does, and is not judged.
        if (await LoanPaymentAcrossCurrenciesAsync(
                    ledgerId, sourceAccountId,
                    new LoanPaymentAccounts(loanAccountId, terms.InterestAccountId, terms.EscrowAccountId),
                    cancellationToken)
                .ConfigureAwait(false) is { } crossed)
            return new(ManagedLoanReminderResult.CurrencyMismatch, null, crossed);

        // Placeholder amounts: principal/interest a -1 stub (overridden live), the
        // escrow leg its stored amount. All negative = an outflow from the source.
        var escrow = terms.EscrowAmount > 0m ? terms.EscrowAmount : 1m;
        var postings = new[]
        {
            new TransactionPosting { CounterpartyAccountId = loanAccountId, Amount = -1m },
            new TransactionPosting { CounterpartyAccountId = terms.InterestAccountId.Value, Amount = -1m },
            new TransactionPosting { CounterpartyAccountId = terms.EscrowAccountId.Value, Amount = -escrow },
        };

        // Setup across currencies was refused above (ADR-0114 D13). A template that reaches
        // here across currencies anyway is a bug, and fails rather than write a mirror.
        var (amounts, refusedPosting) = await BuildTemplateAmountsAsync(
                ledgerId, sourceAccountId, postings, stored: null, cancellationToken)
            .ConfigureAwait(false);
        if (refusedPosting is not null)
            throw new InvalidOperationException(
                $"A managed loan reminder's template was refused: {refusedPosting.Code}");

        var templateHeaderId = Guid.NewGuid();
        var header = NewTemplateHeader(
            templateHeaderId, ledgerId, sourceAccountId, startDate, "Loan payment",
            checkNumber: null, action: null);
        _db.TxnHeaders.Add(header);
        var legs = new List<TxnLegRow>();
        for (var i = 0; i < postings.Length; i++)
            legs.AddRange(AddBankTemplateLegs(templateHeaderId, ledgerId, sourceAccountId, postings[i], amounts[i], i));
        // No notes; the plan still names the owner: the bank source, on every posting.
        if (await SeedTemplateNotesAsync(
                ledgerId, header, legs, AnnotationIntent<string>.Untouched(sourceAccountId), cancellationToken)
                .ConfigureAwait(false) is { } refusal)
            throw new InvalidOperationException($"A managed loan reminder's template was refused: {refusal.Code}");
        var reminderId = await AddSeriesRowAsync(
            ledgerId, templateHeaderId, sourceAccountId,
            LoanReminderRrule(terms.PaymentsPerYear, startDate), startDate,
            endDate: null, autoCommitDaysBefore: null, cancellationToken,
            isLoanReminder: true, loanAccountId: loanAccountId).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ManagedLoanReminderResult.Ok, reminderId);
    }

    /// <summary>An RFC-5545 cadence for a loan's managed reminder derived from its
    /// payments-per-year, anchored on the start day. 12 = monthly (the default).</summary>
    private static string LoanReminderRrule(int paymentsPerYear, DateOnly start) => paymentsPerYear switch
    {
        52 => "FREQ=WEEKLY",
        26 => "FREQ=WEEKLY;INTERVAL=2",
        4 => "FREQ=MONTHLY;INTERVAL=3",
        _ => $"FREQ=MONTHLY;BYMONTHDAY={start.Day}",
    };

    /// <summary>
    /// Create an INVESTMENT-shape reminder series. The template legs are built
    /// via the SHARED <see cref="InvestmentTransactionsRepository.BuildTemplateLegsAsync"/>
    /// (identical validation + construction as a live investment create, minus
    /// holdings/lots). On a shape-validation failure nothing is written.
    /// </summary>
    public async Task<CreateReminderResult> CreateInvestmentAsync(
        Guid ledgerId, CreateInvestmentReminderRequest request, CancellationToken cancellationToken = default)
    {
        var templateHeaderId = Guid.NewGuid();
        // The ledger write lock (migration 275) is the transaction's first statement,
        // before the validation reads: the template's legs recompute the accounts and
        // positions they sit on. A shape failure returns before anything is written, and
        // the transaction rolls back on dispose.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // BuildTemplateLegsAsync only reads for validation + returns un-tracked
        // legs; PostedAt on the request is unused by it (the template's
        // posted_at is the series start, set on the header below).
        var legsResult = await _investmentTxns
            .BuildTemplateLegsAsync(ledgerId, templateHeaderId, request.Transaction, alreadyPresent: null, cancellationToken)
            .ConfigureAwait(false);
        if (legsResult.Failure is { } f)
            return new CreateReminderResult(CreateOutcome.ShapeFailure, null, f,
                InvestmentFailureDetail: legsResult.Detail);

        var header = NewTemplateHeader(
            templateHeaderId, ledgerId, request.Transaction.BrokerageAccountId, request.StartDate,
            request.Transaction.Payee, request.Transaction.CheckNumber,
            action: request.Transaction.Action);
        _db.TxnHeaders.Add(header);
        _db.TxnLegs.AddRange(legsResult.Legs);

        // The event's one memo and tag set, from its brokerage: the brokerage leg of
        // a one-posting event, the header of a multi-posting one.
        if (await SeedTemplateNotesAsync(
                ledgerId, header, legsResult.Legs, InvestmentIntent(request.Transaction), cancellationToken)
                .ConfigureAwait(false) is { } refusal)
            return new CreateReminderResult(CreateOutcome.NotesRefused, null, null, refusal);

        var reminderId = await AddSeriesRowAsync(
            ledgerId, templateHeaderId, request.Transaction.BrokerageAccountId, request.Rrule,
            request.StartDate, request.EndDate, request.AutoCommitDaysBefore, cancellationToken)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateReminderResult(CreateOutcome.Ok, reminderId, null);
    }

    /// <summary>
    /// Every account this series' template legs already sit on; empty when the
    /// series is not in the ledger or has no template.
    /// </summary>
    /// <remarks>
    /// For the inactive-account gate on a postings edit, the same set the
    /// transaction PATCH passes from its header (see
    /// <see cref="TransactionsRepository.LegAccountIdsAsync"/>, which this reads
    /// through). An edit resends every line, so without it a line left on a
    /// deactivated account looks exactly like one moved onto it.
    /// </remarks>
    public async Task<IReadOnlySet<Guid>> TemplateLegAccountIdsAsync(
        Guid ledgerId, Guid reminderId, CancellationToken cancellationToken = default)
    {
        var templateHeaderId = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId)
            .Select(r => r.TemplateHeaderId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return templateHeaderId is { } id
            ? await _transactions.LegAccountIdsAsync(ledgerId, id, cancellationToken).ConfigureAwait(false)
            : new HashSet<Guid>();
    }

    /// <summary>
    /// The stored pair of each line of the series' template, keyed by the id of its leg on
    /// <paramref name="sourceAccountId"/> (<see cref="PostingLegPair.StoredPairs"/>); empty
    /// when the series has no template. Read before an edit, which rebuilds the template's
    /// legs with new ids, so the posting validation can pass a line resent as stored
    /// (ADR-0114 D15).
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, PostingLegPair.StoredPair>> TemplateStoredPairsAsync(
        Guid ledgerId, Guid reminderId, Guid sourceAccountId, CancellationToken cancellationToken = default)
    {
        var templateHeaderId = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId)
            .Select(r => r.TemplateHeaderId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return templateHeaderId is { } id
            ? await _transactions.StoredPairsAsync(ledgerId, id, sourceAccountId, cancellationToken).ConfigureAwait(false)
            : new Dictionary<Guid, PostingLegPair.StoredPair>();
    }

    /// <summary>
    /// Edit a BANK-shape series: recurrence scalars (partial) + the template
    /// transaction shape. <see cref="EditReminderRequest.Postings"/>, when
    /// supplied, drops + rebuilds the template legs (no lots/overrides to
    /// reconcile). Rejects an investment series with <c>ShapeMismatch</c>, and postings
    /// entered from an account other than the stored source with <c>SourceImmutable</c>.
    /// </summary>
    public async Task<EditReminderResult> EditBankAsync(
        Guid ledgerId, Guid reminderId, EditReminderRequest request, CancellationToken cancellationToken = default)
    {
        // The template's legs and its date recompute the accounts they sit on, so the
        // ledger write lock (migration 275) is the transaction's first statement.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new EditReminderResult(EditOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new EditReminderResult(EditOutcome.NotMaterialized, null);

        var header = await _db.TxnHeaders.FirstAsync(h => h.Id == templateHeaderId, cancellationToken)
            .ConfigureAwait(false);
        if (header.Action is not null) return new EditReminderResult(EditOutcome.ShapeMismatch, null);
        if (request.Postings is { } named && named.SourceAccountId != StoredSource(series, header))
            return new EditReminderResult(EditOutcome.SourceImmutable, null);

        // Eligibility BEFORE the edit is applied, against the EFFECTIVE shape: the
        // request's postings when it replaces them, the stored template's when it does
        // not. Checking after would leave the flag set on a rejected request.
        if (request.EstimateSampleCount is not null && !request.ClearEstimate)
        {
            var effectivePostings = request.Postings is { } patch
                ? patch.Items.Count
                : await _db.TxnLegs.AsNoTracking()
                    .Where(l => l.HeaderId == templateHeaderId)
                    .Select(l => l.PostingIndex)
                    .Distinct()
                    .CountAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!EstimateIsEligible(series.IsLoanReminder, effectivePostings))
                return new EditReminderResult(EditOutcome.EstimateNotEligible, null);
        }

        var scheduleChanged = ApplyRecurrenceEdits(series, request.Rrule, request.StartDate,
            request.ClearEndDate, request.EndDate, request.ClearAutoCommit, request.AutoCommitDaysBefore,
            request.ClearEstimate, request.EstimateSampleCount);

        // Re-check the EFFECTIVE range after applying the partial edit (a
        // single-sided start/end change must not invert it). The DB CHECK is
        // the backstop; this returns a clean 422. Tx disposes without commit.
        if (series.EndDate is { } bankEnd && bankEnd < series.StartDate)
            return new EditReminderResult(EditOutcome.EndBeforeStart, null);

        if (request.Payee is not null) header.Payee = request.Payee;
        if (request.CheckNumber is not null) header.CheckNumber = request.CheckNumber;
        header.PostedAt = series.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        if (request.Postings is { } postings)
        {
            if (request.Notes is { } sent && sent.Side != postings.SourceAccountId)
                return new EditReminderResult(EditOutcome.NotesRefused, null, NotFromSource());
            var stored = await AnnotationWriter.LoadAsync(_db, header, cancellationToken,
                    postings.Items.Select(i => i.CounterpartyAccountId).Append(postings.SourceAccountId))
                .ConfigureAwait(false);
            if (LineIdRefusal(stored.Legs, postings) is { } idRefusal)
                return new EditReminderResult(idRefusal, null);
            // A line named by id is checked against its stored pair, so a pair resent as
            // stored passes the nonzero and sign rules (ADR-0114 D13).
            var (amounts, refused) = await BuildTemplateAmountsAsync(
                    ledgerId, postings.SourceAccountId, postings.Items,
                    PostingLegPair.StoredPairs(stored.Legs, postings.SourceAccountId), cancellationToken)
                .ConfigureAwait(false);
            if (refused is not null)
                return new EditReminderResult(EditOutcome.PostingRefused, null, PostingRefusal: refused);
            await ReplaceTemplateLegsAsync(templateHeaderId, cancellationToken).ConfigureAwait(false);
            var legs = new List<TxnLegRow>();
            for (var i = 0; i < postings.Items.Count; i++)
                legs.AddRange(AddBankTemplateLegs(templateHeaderId, ledgerId, postings.SourceAccountId, postings.Items[i], amounts[i], i));

            // The template is owned by its source, which the rebuilt legs put on
            // every posting. A line named by id keeps its notes; any other is new (see
            // RebuiltSurvivors).
            var after = AnnotationWriter.Shape(postings.SourceAccountId, null,
                legs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id)), stored.Facts);
            var change = new ShapeChange<Guid>(stored.Shape, stored.Notes, after,
                RebuiltSurvivors(stored, postings));
            var intent = request.Notes?.Intent() ?? AnnotationIntent<string>.Untouched(postings.SourceAccountId);
            if (await PlanTemplateNotesAsync(ledgerId, header, legs, change, intent, cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
                return new EditReminderResult(EditOutcome.NotesRefused, null, refusal);
        }
        else if (request.Notes is { } notesOnly)
        {
            // Written from the series source, as on create; a series stored without
            // one is written from its template's owner.
            if (StoredSource(series, header) is { } source && notesOnly.Side != source)
                return new EditReminderResult(EditOutcome.NotesRefused, null, NotFromSource());
            var stored = await AnnotationWriter.LoadAsync(_db, header, cancellationToken).ConfigureAwait(false);
            if (await PlanTemplateNotesAsync(ledgerId, header, stored.Legs,
                    ShapeChange<Guid>.Unchanged(stored.Shape, stored.Notes), notesOnly.Intent(), cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
                return new EditReminderResult(EditOutcome.NotesRefused, null, refusal);
        }

        if (scheduleChanged)
            series.NextDueDate = await ComputeNextDueAsync(
                reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EditReminderResult(EditOutcome.Ok, null);
    }

    /// <summary>
    /// Edit an INVESTMENT-shape series. When <see cref="EditInvestmentReminderRequest.Transaction"/>
    /// is supplied, the template legs are rebuilt via the shared
    /// <see cref="InvestmentTransactionsRepository.BuildTemplateLegsAsync"/>
    /// (replace-all). Rejects a bank series with <c>ShapeMismatch</c>, and a shape on a
    /// brokerage other than the stored source with <c>SourceImmutable</c>.
    /// </summary>
    public async Task<EditReminderResult> EditInvestmentAsync(
        Guid ledgerId, Guid reminderId, EditInvestmentReminderRequest request, CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) is the transaction's first statement,
        // before the reads the edit is checked and built from, as the bank edit takes it.
        await using var tx = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new EditReminderResult(EditOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new EditReminderResult(EditOutcome.NotMaterialized, null);

        var header = await _db.TxnHeaders.FirstAsync(h => h.Id == templateHeaderId, cancellationToken)
            .ConfigureAwait(false);
        if (header.Action is null) return new EditReminderResult(EditOutcome.ShapeMismatch, null);
        if (request.Transaction is { } named && named.BrokerageAccountId != StoredSource(series, header))
            return new EditReminderResult(EditOutcome.SourceImmutable, null);

        // Validate + build the replacement legs (read-only) before anything is written,
        // so a shape failure writes nothing.
        IReadOnlyList<TxnLegRow>? newLegs = null;
        if (request.Transaction is { } txnReq)
        {
            // As the bank edit: an inactive account the template already sits on
            // stays editable, one newly chosen is refused.
            var alreadyPresent = await TemplateLegAccountIdsAsync(ledgerId, reminderId, cancellationToken)
                .ConfigureAwait(false);
            var legsResult = await _investmentTxns
                .BuildTemplateLegsAsync(ledgerId, templateHeaderId, txnReq, alreadyPresent, cancellationToken)
                .ConfigureAwait(false);
            if (legsResult.Failure is { } f)
                return new EditReminderResult(EditOutcome.ShapeFailure, f, InvestmentFailureDetail: legsResult.Detail);
            newLegs = legsResult.Legs;
        }

        var scheduleChanged = ApplyRecurrenceEdits(series, request.Rrule, request.StartDate,
            request.ClearEndDate, request.EndDate, request.ClearAutoCommit, request.AutoCommitDaysBefore);

        // Effective-range guard (see EditBankAsync): a single-sided edit must
        // not invert [start, end]. Tx disposes without commit on rejection.
        if (series.EndDate is { } invEnd && invEnd < series.StartDate)
            return new EditReminderResult(EditOutcome.EndBeforeStart, null);

        if (request.Transaction is { } t && newLegs is not null)
        {
            var stored = await AnnotationWriter.LoadAsync(_db, header, cancellationToken,
                    newLegs.Select(l => l.AccountId))
                .ConfigureAwait(false);
            await ReplaceTemplateLegsAsync(templateHeaderId, cancellationToken).ConfigureAwait(false);
            _db.TxnLegs.AddRange(newLegs);
            header.Action = t.Action;
            header.Payee = t.Payee;
            header.CheckNumber = t.CheckNumber;

            // As the bank edit: the event's brokerage, the series source, owns the
            // template, and the event keeps its one memo and tag set through the
            // rebuild (the planner moves it between the brokerage leg and the header when the posting
            // count changes); a counterpart's own value follows its (account, role).
            var after = AnnotationWriter.Shape(t.BrokerageAccountId, t.Action,
                newLegs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id, l.PostingRole)),
                stored.Facts);
            var change = new ShapeChange<Guid>(stored.Shape, stored.Notes, after,
                ShapeChange<Guid>.ByAccountAndRole(stored.Shape, after));
            if (await PlanTemplateNotesAsync(ledgerId, header, newLegs, change, InvestmentIntent(t), cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
                return new EditReminderResult(EditOutcome.NotesRefused, null, refusal);
        }
        header.PostedAt = series.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        if (scheduleChanged)
            series.NextDueDate = await ComputeNextDueAsync(
                reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EditReminderResult(EditOutcome.Ok, null);
    }

    /// <summary>
    /// Soft disable/enable a series via a single-column update (no template /
    /// balance interaction). Mirrors <c>AccountsRepository.SetIsActiveAsync</c>.
    /// </summary>
    public async Task<ActiveOutcome> SetActiveAsync(
        Guid ledgerId, Guid reminderId, bool active, CancellationToken cancellationToken = default)
    {
        var n = await _db.RecurringTransactions
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active), cancellationToken)
            .ConfigureAwait(false);
        return n == 1 ? ActiveOutcome.Ok : ActiveOutcome.NotFound;
    }

    /// <summary>Outcome of <see cref="UnskipAsync"/>.</summary>
    public enum UnskipOutcome { NotFound, NotMaterialized, NotSkipped, Ok }

    /// <summary>The un-skip, and where the series cursor landed afterwards.</summary>
    public sealed record UnskipResult(UnskipOutcome Outcome, DateOnly? NextDueDate);

    /// <summary>
    /// Undo a skip on one occurrence, putting the slot back to un-acted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three error messages instruct users to do this.</b> All three fire paths
    /// reject a suppressed slot with "That occurrence was skipped; un-skip it before
    /// firing"; without a route and an affordance a mis-clicked Skip would be
    /// permanent and the message would send people hunting for a control that does
    /// not exist.
    /// </para>
    /// <para>
    /// <b>It removes ONE slot's exception, not a cascade's worth.</b> Skipping cascades:
    /// it writes an exception for every earlier un-acted occurrence too. Un-skip
    /// deliberately does not undo that sweep, because it cannot know which of those rows
    /// the user meant to keep — and each is independently un-skippable, which is the
    /// composable answer rather than a guess. A user who cascaded over six months and
    /// wants one occurrence back gets exactly that one.
    /// </para>
    /// <para>
    /// Takes the same slot lock as fire and skip (mig 218). Un-skip changes whether the
    /// slot is suppressed, so a fire racing it would otherwise read a stale answer to the
    /// only question that decides whether money moves.
    /// </para>
    /// </remarks>
    public async Task<UnskipResult> UnskipAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new UnskipResult(UnskipOutcome.NotFound, null);
        if (series.TemplateHeaderId is null) return new UnskipResult(UnskipOutcome.NotMaterialized, null);

        var exception = await _db.RecurringOccurrenceExceptions
            .FirstOrDefaultAsync(
                e => e.RecurringTransactionId == reminderId && e.OccurrenceDate == occurrenceDate,
                cancellationToken)
            .ConfigureAwait(false);
        if (exception is null) return new UnskipResult(UnskipOutcome.NotSkipped, series.NextDueDate);

        _db.RecurringOccurrenceExceptions.Remove(exception);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The cursor has to move: the slot is un-acted again, and if it is the earliest
        // such slot the series is now due EARLIER than it was a moment ago. Recomputed
        // rather than adjusted, for the reason ComputeNextDueAsync exists — it is the one
        // definition of "earliest occurrence that is neither fired nor skipped".
        series.NextDueDate = await ComputeNextDueAsync(
                reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken)
            .ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new UnskipResult(UnskipOutcome.Ok, series.NextDueDate);
    }

    /// <summary>
    /// Skip one occurrence (ADR-0047 D6): record a suppression row + advance the
    /// cursor. Idempotent (a duplicate skip is a no-op). Refuses an
    /// already-fired occurrence (<c>AlreadyFired</c>) — skip and fire are
    /// mutually exclusive per slot. Writes no header + never touches cash.
    /// </summary>
    public async Task<SkipResult> SkipAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate, Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Same slot lock the fire path takes, and the reason it exists: this method
        // reads txn_headers then writes an exception row, while FireAsync reads the
        // exception rows then writes a header. Two tables, so no constraint can exclude
        // the pair — interleaved, both pass their checks and the occurrence ends up
        // fired AND skipped, which for auto-post means posting money the user has just
        // said not to post.
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new SkipResult(SkipOutcome.NotFound, null);
        if (series.TemplateHeaderId is null) return new SkipResult(SkipOutcome.NotMaterialized, null);

        var alreadyFired = await _db.TxnHeaders.AsNoTracking()
            .AnyAsync(h => h.RecurringTransactionId == reminderId
                           && h.OccurrenceDate == occurrenceDate
                           && !h.IsRecurringTemplate, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyFired) return new SkipResult(SkipOutcome.AlreadyFired, null);

        var exists = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .AnyAsync(e => e.RecurringTransactionId == reminderId
                           && e.OccurrenceDate == occurrenceDate, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            _db.RecurringOccurrenceExceptions.Add(new RecurringOccurrenceExceptionRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledgerId,
                RecurringTransactionId = reminderId,
                OccurrenceDate = occurrenceDate,
                CreatedByUserId = createdByUserId,
            });

        // Catch-up (ADR-0047 §9.2 / ADR-0049): skipping this occurrence also marks
        // every earlier un-acted occurrence as skipped, clearing the overdue
        // backlog from the calendar + cursor. An earlier already-fired occurrence
        // is preserved.
        var cascaded = await CascadeSkipEarlierAsync(
            ledgerId, reminderId, series.Rrule, series.StartDate, occurrenceDate,
            createdByUserId, cancellationToken).ConfigureAwait(false);

        // Persist the skip + cascade first so the cursor recompute (which queries
        // the exception table) sees them within this transaction.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        series.NextDueDate = await ComputeNextDueAsync(
            reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SkipResult(SkipOutcome.Ok, series.NextDueDate,
            cascaded.Count, cascaded.Count > 0 ? cascaded[0] : null);
    }

    // ----- shared helpers -----

    /// <summary>
    /// A template header owned by the series' source account (mig 263) — the account
    /// whose legs the template's every posting carries, the brokerage for an
    /// investment reminder.
    /// </summary>
    /// <remarks>Its memo and tags are placed by the write primitive
    /// (<see cref="SeedTemplateNotesAsync"/>), as a live transaction's are.</remarks>
    private static TxnHeaderRow NewTemplateHeader(
        Guid id, Guid ledgerId, Guid sourceAccountId, DateOnly startDate,
        string? payee, string? checkNumber, string? action)
    {
        var header = TxnHeaderRow.NewTemplate(id, ledgerId, sourceAccountId);
        header.Payee = payee;
        header.CheckNumber = checkNumber;
        header.PostedAt = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // NOT NULL since mig 189.
        header.TransactedAt = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        header.IsPending = false;
        header.IsHidden = false;
        header.IsMergedInto = null;
        header.Action = action;
        header.RecurringTransactionId = null;
        header.OccurrenceDate = null;
        return header;
    }

    /// <summary>
    /// Each template posting's two leg amounts, built by
    /// <see cref="PostingLegPair.Build"/> as a live create builds them, or the first
    /// posting refused (ADR-0114 D13). The accounts are read once.
    /// </summary>
    /// <param name="stored">An edit's stored pairs by the id of their source leg, so a
    /// line named by id and resent as stored passes the nonzero and sign rules; null on a
    /// create.</param>
    private async Task<(IReadOnlyList<PostingLegPair.LegAmounts> Amounts, PostingLegPair.Refusal? Refused)>
        BuildTemplateAmountsAsync(
            Guid ledgerId, Guid sourceAccountId, IReadOnlyList<TransactionPosting> postings,
            IReadOnlyDictionary<Guid, PostingLegPair.StoredPair>? stored, CancellationToken cancellationToken)
    {
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId, postings.Select(p => p.CounterpartyAccountId).Append(sourceAccountId), cancellationToken)
            .ConfigureAwait(false);
        var amounts = new PostingLegPair.LegAmounts[postings.Count];
        for (var i = 0; i < postings.Count; i++)
        {
            var posting = postings[i];
            PostingLegPair.StoredPair? pair =
                stored is not null && posting.LegId is { } id && stored.TryGetValue(id, out var p) ? p : null;
            var (built, refused) = PostingLegPair.Build(sourceAccountId, posting, i, sides, pair);
            if (refused is not null) return ([], refused);
            amounts[i] = built;
        }
        return (amounts, null);
    }

    private TxnLegRow[] AddBankTemplateLegs(
        Guid headerId, Guid ledgerId, Guid sourceAccountId, TransactionPosting posting,
        PostingLegPair.LegAmounts amounts, int postingIndex)
    {
        // Source-side leg carries the signed amount; the counterparty leg carries what
        // PostingLegPair.Build gave it: -amount, so the posting sums to zero (ADR-0019),
        // or, between real accounts in two currencies, its own counterpartyAmount
        // (ADR-0114 D13), as TransactionsRepository.AddPostingLegs writes a live
        // posting. A fire clones these legs as they stand. Notes are placed by the plan.
        var source = new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = sourceAccountId,
            PostingIndex = postingIndex,
            Amount = amounts.Source,
        };
        var counterparty = new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = posting.CounterpartyAccountId,
            PostingIndex = postingIndex,
            Amount = amounts.Counterparty,
        };
        _db.TxnLegs.Add(source);
        _db.TxnLegs.Add(counterparty);
        return [source, counterparty];
    }

    /// <summary>
    /// Plan and place a new template's notes through the write primitive, written
    /// from <paramref name="intent"/>'s side, the series' source. Returns the refusal,
    /// or null once the plan is in the change tracker.
    /// </summary>
    private async Task<AnnotationViolation?> SeedTemplateNotesAsync(
        Guid ledgerId, TxnHeaderRow header, IReadOnlyList<TxnLegRow> legs,
        AnnotationIntent<string> intent, CancellationToken cancellationToken)
    {
        var facts = await AnnotationWriter.AccountFactsAsync(_db, legs.Select(l => l.AccountId), cancellationToken)
            .ConfigureAwait(false);
        // Built with the owner it is about to have, since an event's investment side
        // is its owner's.
        var shape = AnnotationWriter.Shape(intent.Side, header.Action,
            legs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id, l.PostingRole)), facts);
        var resolved = await AnnotationWriter.ResolveAsync(_db, ledgerId, intent, cancellationToken).ConfigureAwait(false);
        var plan = AnnotationPlanner.Seed(shape, resolved);
        if (!plan.IsValid) return plan.Violations[0];
        await AnnotationWriter.ApplyAsync(_db, header, legs, plan, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Plan and place an edited template's notes through the write primitive. Returns
    /// the refusal, or null once the plan is in the change tracker.
    /// </summary>
    private async Task<AnnotationViolation?> PlanTemplateNotesAsync(
        Guid ledgerId, TxnHeaderRow header, IReadOnlyList<TxnLegRow> legsAfter,
        ShapeChange<Guid> change, AnnotationIntent<string> intent, CancellationToken cancellationToken)
    {
        var resolved = await AnnotationWriter.ResolveAsync(_db, ledgerId, intent, cancellationToken).ConfigureAwait(false);
        var plan = AnnotationPlanner.Plan(change, resolved);
        if (!plan.IsValid) return plan.Violations[0];
        await AnnotationWriter.ApplyAsync(_db, header, legsAfter, plan, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The series' source account: the account its template is entered from (a bank
    /// template's every posting, an investment template's brokerage). It is set when the
    /// reminder is created, and an edit must name it. A series stored without one is
    /// its template's owner's.
    /// </summary>
    private static Guid? StoredSource(RecurringTransactionRow series, TxnHeaderRow template) =>
        series.SourceAccountId ?? template.OriginatingAccountId;

    /// <summary>
    /// Refuses item ids that do not name a line of the stored template: each must be the
    /// id of a stored leg on the series source (the edit's source, checked to be the
    /// stored one), and no two items may name the same one. Refused rather than read as
    /// a new line, as the bank PATCH refuses them, so an id that names nothing never
    /// costs a line its notes in silence.
    /// </summary>
    private static EditOutcome? LineIdRefusal(IReadOnlyList<TxnLegRow> storedLegs, PatchReminderPostings postings)
    {
        var sourceLegs = storedLegs.Where(l => l.AccountId == postings.SourceAccountId).Select(l => l.Id).ToHashSet();
        var named = new HashSet<Guid>();
        foreach (var item in postings.Items)
        {
            if (item.LegId is not { } id) continue;
            if (!sourceLegs.Contains(id)) return EditOutcome.PostingLegNotInTemplate;
            if (!named.Add(id)) return EditOutcome.PostingLegIdDuplicated;
        }
        return null;
    }

    /// <summary>
    /// Which old template leg each rebuilt one replaces, so its memo and tags carry
    /// over. A line is a stored one only when its item names that line's source leg by
    /// id (<see cref="LineIdRefusal"/> has checked every id). The source is the stored
    /// one and a counterparty is never the source, so a named line's rebuilt source leg
    /// replaces its stored source leg, and its rebuilt other leg replaces its stored
    /// other leg, whatever that leg's account is now: a recategorized line keeps its
    /// override. A line sent without an id is new and starts blank, wherever it sits;
    /// position and account never identify a line.
    /// </summary>
    private static IReadOnlyDictionary<LegKey, LegKey> RebuiltSurvivors(
        AnnotationWriter.Stored stored, PatchReminderPostings postings)
    {
        var oldKeyById = stored.Shape.Legs.Where(l => l.LegId is not null).ToDictionary(l => l.LegId!.Value, l => l.Key);
        var survivors = new Dictionary<LegKey, LegKey>();

        for (var i = 0; i < postings.Items.Count; i++)
        {
            if (postings.Items[i].LegId is not { } legId) continue;
            var oldSource = oldKeyById[legId];
            survivors[new LegKey(i, postings.SourceAccountId)] = oldSource;
            if (stored.Shape.OtherOf(oldSource) is { } other)
                survivors[new LegKey(i, postings.Items[i].CounterpartyAccountId)] = other.Key;
        }
        return survivors;
    }

    /// <summary>A bank template's notes are written from the series source, the account
    /// every posting is entered from.</summary>
    private static AnnotationViolation NotFromSource() => new(
        AnnotationViolationCodes.SideNotOnTransaction,
        "The notes must be written from the account the postings are entered from.");

    /// <summary>An investment template's notes as the planner reads them, as a live event's are.</summary>
    private static AnnotationIntent<string> InvestmentIntent(CreateInvestmentTransactionRequest t) =>
        InvestmentTransactionsRepository.NotesIntent(t.BrokerageAccountId, t);

    private async Task<Guid> AddSeriesRowAsync(
        Guid ledgerId, Guid templateHeaderId, Guid sourceAccountId, string rrule,
        DateOnly startDate, DateOnly? endDate, int? autoCommitDaysBefore, CancellationToken cancellationToken,
        bool isLoanReminder = false, Guid? loanAccountId = null, int? estimateSampleCount = null)
    {
        var reminderId = Guid.NewGuid();
        var nextDue = await ComputeNextDueAsync(reminderId, rrule, startDate, endDate, cancellationToken)
            .ConfigureAwait(false);
        _db.RecurringTransactions.Add(new RecurringTransactionRow
        {
            Id = reminderId,
            LedgerId = ledgerId,
            ExternalId = null,                 // manual series: NULL (the partial-unique excludes NULL)
            Rrule = rrule,
            SourcePayload = null,
            AutoCommitDaysBefore = autoCommitDaysBefore,
            // Mig 220. Eligibility (single-posting, non-loan) is enforced at the
            // endpoint, which is the only layer that can see the whole request; this
            // writer just records what was allowed.
            EstimateSampleCount = estimateSampleCount,
            TemplateHeaderId = templateHeaderId,
            SourceAccountId = sourceAccountId,  // originating account (drives the agenda amount)
            StartDate = startDate,
            EndDate = endDate,
            NextDueDate = nextDue,
            LastAcknowledgedDate = null,
            IsLoanReminder = isLoanReminder,   // managed loan-payment reminder → split computed live
            LoanAccountId = loanAccountId,     // the loan this is the managed payment for (mig 168)
            IsActive = true,
            Origin = "manual",
        });
        return reminderId;
    }

    /// <summary>A header and the account whose register a reminder reads it from: the header's originating account.</summary>
    private sealed record SeenFrom(Guid HeaderId, Guid? OriginatingAccountId);

    /// <summary>
    /// Each header's memo as its originating account's register shows it, from the
    /// stored presentation (<see cref="StoredNotesQuery.MemosOnAccountsAsync"/>).
    /// </summary>
    /// <remarks>A header with legs always names an originating account that holds one
    /// of them (migration 263's invariant), so a header that names none has no legs: no
    /// side presents it, and the reminder shows its own memo (<see cref="LeglessNotesAsync"/>).</remarks>
    private async Task<IReadOnlyDictionary<Guid, string?>> MemosAsync(
        IEnumerable<SeenFrom> headers, CancellationToken cancellationToken)
    {
        var all = headers.Distinct().ToList();
        var entries = all
            .Where(h => h.OriginatingAccountId is not null)
            .Select(h => (h.HeaderId, h.OriginatingAccountId!.Value))
            .ToList();
        var memos = (await StoredNotesQuery.MemosOnAccountsAsync(_db, entries, cancellationToken).ConfigureAwait(false))
            .ToDictionary(n => n.Key, n => n.Value);
        var legless = await LeglessNotesAsync(
                all.Where(h => h.OriginatingAccountId is null).Select(h => h.HeaderId).ToList(), cancellationToken)
            .ConfigureAwait(false);
        foreach (var (header, own) in legless) memos[header] = own.Memo;
        return memos;
    }

    /// <summary>A legless template's own memo and tags (<see cref="LeglessNotesAsync"/>).</summary>
    private sealed record LeglessNotes(string? Memo, IReadOnlyList<string> Tags);

    /// <summary>
    /// The own memo and tags of each of <paramref name="headerIds"/>, headers with no
    /// legs: with no side to present them, a reminder's list, agenda and editor show the
    /// transaction's notes as they were written.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LeglessNotes>> LeglessNotesAsync(
        IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken)
    {
        if (headerIds.Count == 0) return new Dictionary<Guid, LeglessNotes>();
        var memos = await _db.TxnHeaders.AsNoTracking()
            .Where(h => headerIds.Contains(h.Id) && !_db.TxnLegs.Any(l => l.HeaderId == h.Id))
            .ToDictionaryAsync(h => h.Id, h => h.Memo, cancellationToken)
            .ConfigureAwait(false);
        if (memos.Count == 0) return new Dictionary<Guid, LeglessNotes>();
        var ids = memos.Keys.ToList();
        var tags = (await (
                from ht in _db.TxnHeaderTags.AsNoTracking()
                where ids.Contains(ht.HeaderId)
                join t in _db.Tags.AsNoTracking() on ht.TagId equals t.Id
                select new { ht.HeaderId, t.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.HeaderId, x => x.Name);
        return memos.ToDictionary(
            m => m.Key, m => new LeglessNotes(m.Value, [.. tags[m.Key]]));
    }

    /// <summary>
    /// Can this series hold an estimate: a single posting, and not a loan?
    /// </summary>
    /// <remarks>
    /// One predicate for the two exclusions, and the SAME one the resolver applies when
    /// it decides whether to produce an amount. Storing a flag the resolver would refuse
    /// to honour is how a reminder ends up configured to estimate and permanently unable
    /// to.
    /// </remarks>
    private static bool EstimateIsEligible(bool isLoanReminder, int postingCount) =>
        !isLoanReminder && postingCount <= 1;

    private static bool ApplyRecurrenceEdits(
        RecurringTransactionRow series, string? rrule, DateOnly? startDate,
        bool clearEndDate, DateOnly? endDate, bool clearAutoCommit, int? autoCommitDaysBefore,
        bool clearEstimate = false, int? estimateSampleCount = null)
    {
        var scheduleChanged = false;
        if (rrule is not null) { series.Rrule = rrule; scheduleChanged = true; }
        if (startDate is { } sd) { series.StartDate = sd; scheduleChanged = true; }
        if (clearEndDate) { series.EndDate = null; scheduleChanged = true; }
        else if (endDate is { } ed) { series.EndDate = ed; scheduleChanged = true; }
        if (clearAutoCommit) series.AutoCommitDaysBefore = null;
        else if (autoCommitDaysBefore is { } ac) series.AutoCommitDaysBefore = ac;
        if (clearEstimate) series.EstimateSampleCount = null;
        else if (estimateSampleCount is { } es) series.EstimateSampleCount = es;
        return scheduleChanged;
    }

    private async Task ReplaceTemplateLegsAsync(Guid templateHeaderId, CancellationToken cancellationToken)
    {
        var oldLegs = await _db.TxnLegs
            .Where(l => l.HeaderId == templateHeaderId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.TxnLegs.RemoveRange(oldLegs);
    }

    /// <summary>
    /// The next-due cursor: the earliest expanded occurrence that is after the
    /// acknowledged floor (<c>last_acknowledged_date</c> — Moneydance's ack date
    /// on import) and neither already fired nor skipped (ADR-0047 §9.2 robust
    /// semantics — a far-future skip doesn't regress the near cursor). The floor
    /// is what keeps an imported reminder that has run for years from stranding the
    /// cursor on its first occurrence: it lands on the first occurrence after the
    /// MD ack date. Clamped to the series end date. Null for a custom (no-rrule)
    /// series, or once the series has no open slot left. Delegates the date math
    /// to the shared <see cref="NextDueCalculator"/>.
    /// </summary>
    private async Task<DateOnly?> ComputeNextDueAsync(
        Guid reminderId, string? rrule, DateOnly startDate, DateOnly? endDate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return null;

        var fired = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && !h.IsRecurringTemplate
                        && h.OccurrenceDate != null)
            .Select(h => h.OccurrenceDate!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var skipped = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .Where(ex => ex.RecurringTransactionId == reminderId)
            .Select(ex => ex.OccurrenceDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // last_acknowledged_date is the import floor — occurrences on/before it
        // were acknowledged in Moneydance and are not re-proposed; fired/skipped
        // are the per-slot acts in Coffer. The cursor math is shared with the
        // importer (NextDueCalculator) so both seed it identically (ADR-0051).
        var lastAck = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId)
            .Select(r => r.LastAcknowledgedDate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return NextDueCalculator.NextDue(
            _expander, rrule, startDate, endDate,
            consumedThrough: lastAck,
            consumedDates: fired.Concat(skipped).ToHashSet());
    }

    /// <summary>
    /// Catch-up (ADR-0047 §9.2 / ADR-0049): mark every occurrence strictly BEFORE
    /// <paramref name="onDate"/> that is neither fired nor skipped as skipped, so
    /// acting on a later slot clears the overdue backlog (the cursor then jumps
    /// past it). Bounded by the series' range <c>[start, onDate)</c>. Adds the
    /// exception rows to the tracked context — the caller persists them in its
    /// transaction before recomputing the cursor. Returns the skipped dates
    /// earliest-first; never re-skips an already-fired or already-skipped slot
    /// (a prior fired occurrence carries real cash and is preserved), and never
    /// skips occurrences on/before the acknowledged floor
    /// (<c>last_acknowledged_date</c>) — those were handled in Moneydance, so an
    /// imported reminder's pre-import history is not retro-skipped. A custom
    /// (no-rrule) series has nothing to expand.
    /// </summary>
    /// <summary>
    /// Take the transaction-scoped advisory lock on one (series, occurrence) slot
    /// (migration 218), so the fire and skip paths cannot interleave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fire and skip write to DIFFERENT tables — a <c>txn_header</c> and a
    /// <c>recurring_occurrence_exception</c> — each after reading the other's. No
    /// constraint can span the pair, so interleaved they both pass their checks and the
    /// slot ends up fired AND skipped: the app posts money the user just told it not to.
    /// The unique index added by the same migration does not help here; only a lock both
    /// paths take does.
    /// </para>
    /// <para>
    /// MUST be called inside the caller's transaction. The lock is transaction-scoped,
    /// so taking it outside one acquires and releases it immediately and guards nothing.
    /// Enumerating the query is what takes it.
    /// </para>
    /// </remarks>
    private async Task LockOccurrenceSlotAsync(
        Guid reminderId, DateOnly occurrenceDate, CancellationToken cancellationToken)
    {
        _ = await _db.ReminderOccurrenceLock(reminderId, occurrenceDate)
            .Select(r => r.Locked)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Is this the unique-occurrence index rejecting a second fire for the same slot?
    /// </summary>
    /// <remarks>
    /// Named on the CONSTRAINT, not just on SQLSTATE 23505. A bare 23505 check would
    /// also swallow a duplicate on any other unique index this write touches and report
    /// the fire as successfully idempotent when something else entirely went wrong.
    /// </remarks>
    private static bool IsDuplicateOccurrence(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "uq_txn_headers_recurring_occurrence",
        };

    /// <summary>
    /// The header that won a race for this slot.
    /// </summary>
    /// <remarks>
    /// Called after a duplicate-key rollback, so the change tracker still holds the
    /// entities the losing attempt added; it is cleared first or the next SaveChanges on
    /// this context would try to insert them again.
    /// </remarks>
    private async Task<Guid?> WinningOccurrenceHeaderIdAsync(
        Guid reminderId, DateOnly occurrenceDate, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        return await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && h.OccurrenceDate == occurrenceDate
                        && !h.IsRecurringTemplate)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DateOnly>> CascadeSkipEarlierAsync(
        Guid ledgerId, Guid reminderId, string? rrule, DateOnly startDate, DateOnly onDate,
        Guid? createdByUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return Array.Empty<DateOnly>();

        var earlier = _expander.Expand(rrule, startDate, startDate, onDate)
            .Where(d => d < onDate)
            .ToList();
        if (earlier.Count == 0) return Array.Empty<DateOnly>();

        var fired = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == reminderId
                        && !h.IsRecurringTemplate
                        && h.OccurrenceDate != null
                        && h.OccurrenceDate < onDate)
            .Select(h => h.OccurrenceDate!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var skipped = await _db.RecurringOccurrenceExceptions.AsNoTracking()
            .Where(e => e.RecurringTransactionId == reminderId && e.OccurrenceDate < onDate)
            .Select(e => e.OccurrenceDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The acknowledged floor (Moneydance's ack date on import): occurrences
        // on/before it were handled in MD, so a catch-up must NOT skip them —
        // otherwise firing an imported reminder would mark years of phantom
        // backlog. Only un-acted slots strictly AFTER the floor are caught up.
        var lastAck = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId)
            .Select(r => r.LastAcknowledgedDate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var consumed = fired.Concat(skipped).ToHashSet();
        var toSkip = earlier
            .Where(d => (lastAck is not { } ack || d > ack) && !consumed.Contains(d))
            .OrderBy(d => d).ToList();
        foreach (var d in toSkip)
            _db.RecurringOccurrenceExceptions.Add(new RecurringOccurrenceExceptionRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledgerId,
                RecurringTransactionId = reminderId,
                OccurrenceDate = d,
                CreatedByUserId = createdByUserId,
            });
        return toSkip;
    }

    // ----- Loan amortization (ADR-0050 D3/D4) --------------------------------

    private sealed record LoanSplit(
        Guid LoanAccountId, Guid? InterestAccountId, Guid? EscrowAccountId,
        decimal Principal, decimal Interest, decimal Escrow);

    private static readonly IReadOnlyDictionary<Guid, LoanSplit> EmptyLoanSplits =
        new Dictionary<Guid, LoanSplit>();
    private static readonly IReadOnlyDictionary<Guid, decimal> EmptyLoanAmounts =
        new Dictionary<Guid, decimal>();

    /// <summary>
    /// The posted_at a fired occurrence (or an adjusted fire's posted date) is stamped
    /// with: that date at midnight UTC, the way the register stores a date. The loan
    /// split is priced as of the same instant, so the two cannot drift.
    /// </summary>
    private static DateTime PaymentPostedAt(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>One loan's terms and its loan account's currency, which every figure
    /// computed from the terms lands in (ADR-0114 D15).</summary>
    private sealed record LoanTermsInCurrency(LoanTermsRow Terms, string CurrencyCode);

    /// <summary>Every loan's terms in <paramref name="ledgerId"/>, each with its loan
    /// account's currency, in one read.</summary>
    private async Task<List<LoanTermsInCurrency>> LoanTermsWithCurrencyAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .Join(_db.Accounts.AsNoTracking(), t => t.AccountId, a => a.Id,
                  (t, a) => new { Terms = t, a.CurrencyCode })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(x => new LoanTermsInCurrency(x.Terms, x.CurrencyCode))
        .ToList();

    /// <summary>The periodic payment (principal + interest) one loan's terms set, rounded
    /// up to one minor unit of the loan's currency when it is computed (ADR-0114 D15). A
    /// fixed payment is the stored figure.</summary>
    private static decimal LoanPayment(LoanTermsRow t, string currencyCode) =>
        t.PaymentIsComputed
            ? LoanAmortization.PeriodicPayment(
                t.OriginalPrincipal, t.AnnualInterestRate, t.PaymentCount, t.PaymentsPerYear, currencyCode)
            : (t.FixedPayment ?? 0m);

    /// <summary>
    /// The split of a payment posted at <paramref name="paymentPostedAt"/> for every
    /// loan account in the ledger, keyed by loan account id. Interest = balance owed ×
    /// periodic rate; principal = payment − interest; escrow is the stored amount.
    /// Empty when the ledger has no loans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owed is the loan's balance as of the payment's own date</b> (ADR-0078,
    /// 2026-10-21): the register's running balance just before the payment, in its
    /// <c>(posted_at, seq)</c> order. Every row with an earlier posted_at counts, and so
    /// does a row on the same instant, which the payment follows by seq because it takes
    /// the next one when it is posted. Nothing after it counts. That is
    /// <c>account_balance_as_of_instants</c> at the payment's posted_at
    /// (<c>posted_at &lt;= as_of</c>, ties included), the as-of read net-worth history
    /// uses. No "today" enters it, in any zone: neither the caller's date, nor when or
    /// where the auto-post job runs, nor who fires the occurrence can move it. A payment
    /// fired ahead and dated before this one counts, since its date is earlier; a payment
    /// dated after this one never does.
    /// </para>
    /// <para>
    /// The stored running balance, never a raw re-sum: a re-sum of txn_legs here once
    /// double-counted merged-away duplicate payments (is_merged_into) and ignored leg
    /// overrides, which under-stated the balance and so under-charged interest /
    /// over-paid principal on the split (ADR-0034).
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, LoanSplit>> ComputeLoanSplitsAsync(
        Guid ledgerId, DateTime paymentPostedAt, CancellationToken cancellationToken)
    {
        var terms = await LoanTermsWithCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0) return EmptyLoanSplits;

        var loanAccountIds = terms.Select(x => x.Terms.AccountId).ToArray();
        var balanceByAccount = (await _db.AccountBalanceAsOfInstants(ledgerId, [paymentPostedAt], loanAccountIds)
                .Select(b => new { b.AccountId, b.Balance })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.AccountId, b => b.Balance);

        var map = new Dictionary<Guid, LoanSplit>(terms.Count);
        foreach (var (t, currencyCode) in terms)
        {
            var balance = balanceByAccount.GetValueOrDefault(t.AccountId);
            var owed = balance < 0m ? -balance : 0m;   // liability: negative balance = amount owed
            // Interest rounds at the loan's places, and the principal is the remainder, so
            // the two sum to the payment in the loan's currency (ADR-0114 D15).
            var (interest, principal) = LoanAmortization.PeriodSplit(
                owed, LoanPayment(t, currencyCode), t.AnnualInterestRate, t.PaymentsPerYear, currencyCode);
            map[t.AccountId] = new LoanSplit(
                t.AccountId, t.InterestAccountId, t.EscrowAccountId, principal, interest, t.EscrowAmount);
        }
        return map;
    }

    /// <summary>Why a series that asked for an estimate has not got one.</summary>
    /// <remarks>
    /// Stable codes rather than prose: the SPA renders each one differently, and a
    /// sentence composed here would be a translation string living in the wrong layer.
    /// </remarks>
    internal static class EstimateUnavailable
    {
        /// <summary>No committed, non-hidden occurrence exists yet.</summary>
        public const string NoHistory = "no-history";

        /// <summary>The average came out to 0.00.</summary>
        public const string ZeroEstimate = "zero-estimate";

        /// <summary>The average's direction contradicts the template's.</summary>
        public const string SignMismatch = "sign-mismatch";

        /// <summary>More than one posting — no single amount to estimate.</summary>
        public const string HasSplit = "has-split";

        /// <summary>A loan series: its amount is computed from the loan, not guessed.</summary>
        public const string LoanDriven = "loan-driven";

        /// <summary>Not a plain bank-shape template.</summary>
        public const string NotBankShape = "not-bank-shape";

        /// <summary>The series has no source account to net against.</summary>
        public const string NoSourceAccount = "no-source-account";

        /// <summary>A transfer between real accounts in different currencies whose other
        /// side has no derived rate for the occurrence's due date (ADR-0114 D13): a stored
        /// leg of it is missing.</summary>
        public const string RateUnavailable = "rate-unavailable";
    }

    /// <summary>One series' resolved estimate of its source side, or the reason it has
    /// none.</summary>
    /// <param name="Crossing">On a transfer between real accounts in different currencies,
    /// the two currencies and the other account, whose side is estimated per occurrence
    /// (<see cref="EstimateRates.Estimate"/>, ADR-0114 D13); null otherwise.</param>
    internal sealed record EstimateResolution(
        int RequestedSampleCount,
        int AvailableSampleCount,
        decimal? Amount,
        string? UnavailableReason,
        EstimateCrossing? Crossing = null);

    /// <summary>The two sides of an estimating transfer between real accounts in different
    /// currencies: the source's currency, and the other account with its own.</summary>
    internal sealed record EstimateCrossing(string SourceCurrency, Guid OtherAccountId, string OtherCurrency);

    /// <summary>
    /// The derived rates the other side of every estimate between currencies reads, loaded
    /// once for a read (<see cref="LoadEstimateRatesAsync"/>), and the one helper that turns
    /// a series' source estimate into an occurrence's two figures.
    /// </summary>
    /// <remarks>
    /// The agenda, the reminder detail, the manage list, the clone fire and Cash ahead all
    /// read an estimate through <see cref="Estimate"/>, so the figure shown, the figure
    /// planned and the figure posted cannot be computed differently.
    /// </remarks>
    internal sealed class EstimateRates
    {
        /// <summary>No estimate between currencies is in play: nothing was loaded.</summary>
        public static readonly EstimateRates None = new(null, string.Empty);

        private readonly ExchangeRateTable? _table;
        private readonly string _reporting;

        internal EstimateRates(ExchangeRateTable? table, string reporting)
        {
            _table = table;
            _reporting = reporting;
        }

        /// <summary>
        /// The estimate of one occurrence of the series <paramref name="series"/> resolves,
        /// due <paramref name="due"/> (null: at the latest rate, as a forecast day converts).
        /// </summary>
        /// <remarks>
        /// <para>On a transfer between currencies the other side is the source estimate
        /// negated, at the derived rate the one rate rule gives the due date
        /// (<see cref="RateDay.Of(DateOnly, DateOnly)"/>), rounded once at the other
        /// account's places, half away from zero (<see cref="DerivedRate.Convert"/>,
        /// ADR-0114 D13, D15). A rate is only read, never written.</para>
        /// <para>With no rate for the day there is no estimate: no amount on either side,
        /// <see cref="EstimateUnavailable.RateUnavailable"/>, and each missing stored leg.
        /// An other side that rounds to zero at its account's places is no estimate either
        /// (<see cref="EstimateUnavailable.ZeroEstimate"/>): money that leaves one account
        /// arrives in the other, so neither side of the pair can be zero.</para>
        /// <para>Every other series' estimate is its source estimate as resolved, with no
        /// other side: the post writes its negation.</para>
        /// </remarks>
        public ReminderEstimate Estimate(EstimateResolution series, DateOnly? due)
        {
            ArgumentNullException.ThrowIfNull(series);
            if (series.Amount is not { } source || series.Crossing is not { } crossing)
                return new ReminderEstimate(
                    series.RequestedSampleCount, series.AvailableSampleCount, series.Amount, series.UnavailableReason);

            var table = _table ?? throw new InvalidOperationException(
                "An estimate between currencies was read without its rates loaded (LoadEstimateRatesAsync).");
            var at = due is { } d ? RateDay.Of(d, table.ServerToday) : RateDay.Latest();
            var derived = table.Derived(crossing.SourceCurrency, crossing.OtherCurrency, _reporting, at);
            if (derived.Convert(-source) is not { } other)
                return new ReminderEstimate(
                    series.RequestedSampleCount, series.AvailableSampleCount, null,
                    EstimateUnavailable.RateUnavailable, null, derived.Unavailable);
            if (other == 0m)
                return new ReminderEstimate(
                    series.RequestedSampleCount, series.AvailableSampleCount, null, EstimateUnavailable.ZeroEstimate);
            return new ReminderEstimate(
                series.RequestedSampleCount, series.AvailableSampleCount, source, null, other);
        }
    }

    /// <summary>
    /// Loads, in one read, the derived rate each of <paramref name="occurrences"/> needs for
    /// the other side of its estimate (<see cref="EstimateRates.Estimate"/>): one for every
    /// occurrence of a series whose estimate crosses currencies, at its due date by the one
    /// rate rule, or at the latest rate when the date is null.
    /// </summary>
    /// <remarks>Sends nothing when no estimate in <paramref name="estimates"/> crosses
    /// currencies with an amount to convert; otherwise the ledger's reporting currency and
    /// one <see cref="ExchangeRateLookup.LoadAsync"/>, however many occurrences.</remarks>
    private async Task<EstimateRates> LoadEstimateRatesAsync(
        Guid ledgerId, IReadOnlyDictionary<Guid, EstimateResolution> estimates,
        IEnumerable<(Guid SeriesId, DateOnly? Due)> occurrences, CancellationToken cancellationToken)
    {
        var wanted = occurrences
            .Where(o => estimates.GetValueOrDefault(o.SeriesId) is { Amount: not null, Crossing: not null })
            .ToList();
        if (wanted.Count == 0) return EstimateRates.None;

        var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var (seriesId, due) in wanted)
        {
            var crossing = estimates[seriesId].Crossing!;
            needs.AddDerived(
                crossing.SourceCurrency, crossing.OtherCurrency, reporting,
                due is { } d ? RateDay.Of(d, needs.ServerToday) : RateDay.Latest());
        }
        var table = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        return new EstimateRates(table, reporting);
    }

    /// <summary>
    /// Every estimating series' estimate at the latest derived rate, as Cash ahead plans a
    /// series once for every occurrence (ADR-0114 D13, ADR-0109 D8), with its source
    /// estimate as resolved: the one helper the agenda and the fire read too.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, (EstimateResolution Series, ReminderEstimate AtLatestRate)>>
        LatestEstimatesAsync(Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var estimates = await ResolveEstimatesAsync(ledgerId, null, cancellationToken).ConfigureAwait(false);
        if (estimates.Count == 0) return new Dictionary<Guid, (EstimateResolution, ReminderEstimate)>();
        var rates = await LoadEstimateRatesAsync(
                ledgerId, estimates, estimates.Keys.Select(id => (id, (DateOnly?)null)), cancellationToken)
            .ConfigureAwait(false);
        return estimates.ToDictionary(e => e.Key, e => (e.Value, rates.Estimate(e.Value, null)));
    }

    private static readonly IReadOnlyDictionary<Guid, EstimateResolution> EmptyEstimates =
        new Dictionary<Guid, EstimateResolution>();

    /// <summary>
    /// The estimate for every estimating series on the ledger — or for one, when
    /// <paramref name="seriesId"/> is given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The single authority.</b> Eligibility, the average and the rounding live here
    /// and nowhere else, so the agenda, the reminder detail and the FIRE path cannot
    /// disagree about what a reminder is worth. A second copy of the predicate is a
    /// second answer: two paths that each filter the samples themselves drift apart, and
    /// the same reminder then shows one amount and posts another.
    /// </para>
    /// <para>
    /// <b>Batched, never per-series.</b> Four queries regardless of how many series
    /// estimate, because the agenda resolves a whole month at once and a per-series query
    /// inside that loop is an N+1 on every page load. The first query is also the
    /// early-out: a ledger with no estimating series pays one indexed probe
    /// (idx_recurring_transactions_estimated) and stops.
    /// </para>
    /// <para>
    /// <b>Rounds ONCE.</b> The SQL returns a sum and a count; the division and its single
    /// rounding happen here, at the source account's currency's places, half away from
    /// zero (<see cref="CurrencyMinorUnits.RoundAwayFromZero"/>, ADR-0114 D15). Rounding in
    /// SQL and again on write is the double-rounding defect migration 209 was written to
    /// remove.
    /// </para>
    /// <para>
    /// <b>Per series.</b> The source estimate does not depend on the date. The other side of
    /// a transfer between currencies does, so this resolves only which accounts and
    /// currencies it crosses (<see cref="EstimateResolution.Crossing"/>), and
    /// <see cref="EstimateRates.Estimate"/> sets it per occurrence (ADR-0114 D13).
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, EstimateResolution>> ResolveEstimatesAsync(
        Guid ledgerId, Guid? seriesId, CancellationToken cancellationToken)
    {
        var configured = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId
                        && r.EstimateSampleCount != null
                        && (seriesId == null || r.Id == seriesId))
            .Join(_db.TxnHeaders.AsNoTracking(),
                  r => r.TemplateHeaderId, h => h.Id,
                  (r, h) => new
                  {
                      r.Id,
                      Requested = r.EstimateSampleCount!.Value,
                      r.SourceAccountId,
                      // The currency the estimate is in and rounds at: its source account's.
                      SourceCurrency = _db.Accounts
                          .Where(a => a.Id == r.SourceAccountId)
                          .Select(a => a.CurrencyCode)
                          .FirstOrDefault(),
                      TemplateAction = h.Action,
                      TemplateHeaderId = h.Id,
                  })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (configured.Count == 0) return EmptyEstimates;

        // Each template line with its account's type and currency: the source line's
        // currency is the places the estimate rounds at, and the other line's says whether
        // the series crosses currencies.
        var templateIds = configured.Select(c => c.TemplateHeaderId).ToList();
        var legs = await _db.TxnLegs.AsNoTracking()
            .Where(l => templateIds.Contains(l.HeaderId))
            .Join(_db.Accounts.AsNoTracking(), l => l.AccountId, a => a.Id,
                  (l, a) => new { l.HeaderId, l.AccountId, l.PostingIndex, l.Amount, a.AccountType, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var legsByTemplate = legs.GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // The SAME predicate the loan display override uses, by construction: its keys
        // are the series it drives. Computed after the early-out, so a ledger with no
        // estimating series never pays for it.
        var loanDriven = (await ComputeLoanAmountBySeriesAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false)).Keys.ToHashSet();

        var samples = (await _db.ReminderEstimateSamples(ledgerId, seriesId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(x => x.RecurringTransactionId, x => (x.SampleCount, x.SampleSum));

        var resolved = new Dictionary<Guid, EstimateResolution>(configured.Count);
        foreach (var c in configured)
        {
            samples.TryGetValue(c.Id, out var sample);
            var templateLegs = legsByTemplate.GetValueOrDefault(c.TemplateHeaderId, new());

            var reason =
                c.SourceAccountId is null ? EstimateUnavailable.NoSourceAccount
                : c.TemplateAction is not null ? EstimateUnavailable.NotBankShape
                : loanDriven.Contains(c.Id) ? EstimateUnavailable.LoanDriven
                : templateLegs.Select(l => l.PostingIndex).Distinct().Count() > 1
                    ? EstimateUnavailable.HasSplit
                : null;

            decimal? amount = null;
            EstimateCrossing? crossing = null;
            if (reason is null)
            {
                // A transfer between two real accounts in different currencies: its other
                // side is estimated per occurrence (ADR-0114 D13). The source is a real
                // account by construction; a category's line mirrors it.
                var other = templateLegs.FirstOrDefault(l => l.AccountId != c.SourceAccountId);
                if (c.SourceCurrency is { } sourceCurrency
                    && other is not null
                    && !string.Equals(other.AccountType, "category", StringComparison.Ordinal)
                    && !string.Equals(other.CurrencyCode, sourceCurrency, StringComparison.Ordinal))
                    crossing = new EstimateCrossing(sourceCurrency, other.AccountId, other.CurrencyCode);

                if (sample.SampleCount == 0)
                {
                    reason = EstimateUnavailable.NoHistory;
                }
                else
                {
                    // Once, at the source account's places, half away from zero (ADR-0114
                    // D15). A source with no currency to read cannot be reached (the source
                    // is a stored account of the ledger); it would round at the default
                    // places, as any code outside the table does.
                    var averaged = CurrencyMinorUnits.RoundAwayFromZero(
                        sample.SampleSum / sample.SampleCount, c.SourceCurrency ?? string.Empty);

                    // A timer commits this unattended, so both of these REFUSE rather
                    // than post. Zero is not a bill, and a direction that contradicts
                    // the template would move money the wrong way — which no amount of
                    // averaging makes defensible.
                    var templateNet = templateLegs
                        .Where(l => l.AccountId == c.SourceAccountId)
                        .Sum(l => l.Amount);

                    if (averaged == 0m)
                        reason = EstimateUnavailable.ZeroEstimate;
                    else if (templateNet != 0m && Math.Sign(averaged) != Math.Sign(templateNet))
                        reason = EstimateUnavailable.SignMismatch;
                    else
                        amount = averaged;
                }
            }

            resolved[c.Id] = new EstimateResolution(
                c.Requested, sample.SampleCount, amount, reason, crossing);
        }

        return resolved;
    }

    /// <summary>
    /// The two leg amounts for one estimated single-posting occurrence: the source leg
    /// carries the estimated net, the counterpart its negation, so the posting still sums
    /// to zero; or, on a transfer between currencies, the other side's own estimate
    /// (<paramref name="counterpart"/>, <see cref="EstimateRates.Estimate"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <see cref="LoanPostingAmounts"/>, and shared by the
    /// display path and the fire path for the same reason it is — two implementations of
    /// "what is this occurrence worth" is how the loan feature came to have a display
    /// that overrides series the write path leaves alone.
    /// </remarks>
    private static (decimal Source, decimal Counterpart) EstimatePostingAmounts(
        decimal estimatedSourceNet, decimal? counterpart) =>
        (estimatedSourceNet, counterpart ?? -estimatedSourceNet);

    /// <summary>
    /// Maps each loan-driven series id → the source-side full payment (a negative
    /// outflow) = −(principal + interest + escrow). A series is loan-driven when its
    /// template has a leg on a loan account.
    /// </summary>
    /// <remarks>
    /// Principal is the payment less the interest, so principal + interest is the
    /// periodic payment whatever the balance owed, and the full payment reads no balance
    /// at all: the agenda and the manage list show the same figure for every
    /// occurrence. Only the division between principal and interest depends on the
    /// payment's date (<see cref="ComputeLoanSplitsAsync"/>).
    /// </remarks>
    /// <summary>
    /// The series whose un-posted amount is a loan's computed payment: the keys of
    /// <see cref="ComputeLoanAmountBySeriesAsync"/>, so a reader that places an
    /// occurrence the agenda lists knows which amount the agenda gave it.
    /// </summary>
    internal async Task<IReadOnlySet<Guid>> LoanDrivenSeriesAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        (await ComputeLoanAmountBySeriesAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            .Keys.ToHashSet();

    private async Task<IReadOnlyDictionary<Guid, decimal>> ComputeLoanAmountBySeriesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var terms = await LoanTermsWithCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0) return EmptyLoanAmounts;

        var fullPaymentByLoan = terms.ToDictionary(
            x => x.Terms.AccountId, x => LoanPayment(x.Terms, x.CurrencyCode) + x.Terms.EscrowAmount);
        var loanAccountIds = fullPaymentByLoan.Keys.ToHashSet();
        var pairs = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.IsActive && r.TemplateHeaderId != null)
            .Join(_db.TxnLegs.AsNoTracking(), r => r.TemplateHeaderId, l => l.HeaderId,
                  (r, l) => new { SeriesId = r.Id, l.AccountId })
            .Where(x => loanAccountIds.Contains(x.AccountId))
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<Guid, decimal>();
        foreach (var p in pairs)
            if (fullPaymentByLoan.TryGetValue(p.AccountId, out var fullPayment))
                map[p.SeriesId] = -fullPayment;
        return map;
    }

    /// <summary>
    /// The override amounts for ONE loan posting group: the counterpart leg gets
    /// its principal / interest / escrow component, the source leg gets the
    /// negation (so the posting still sums to zero). Null when the counterpart
    /// isn't a recognized loan component — a non-loan posting on the same
    /// reminder is left untouched. Shared by the display path
    /// (<see cref="ApplyLoanSplitToLegsAsync"/>) and the clone-fire path
    /// (<see cref="ComputeLoanLegOverridesAsync"/>) so they can't diverge.
    /// </summary>
    private static (decimal Source, decimal Counterpart)? LoanPostingAmounts(
        LoanSplit split, Guid counterpartAccountId)
    {
        decimal? component =
            counterpartAccountId == split.LoanAccountId ? split.Principal
            : counterpartAccountId == split.InterestAccountId ? split.Interest
            : counterpartAccountId == split.EscrowAccountId ? split.Escrow
            : null;
        return component is { } c ? (-c, c) : null;
    }

    /// <summary>
    /// Replace a loan reminder's (mostly-zero) template leg amounts with the
    /// computed principal / interest / escrow split (display / prefill path).
    /// Non-loan postings are left untouched.
    /// </summary>
    private async Task<List<ReminderLegDto>> ApplyLoanSplitToLegsAsync(
        Guid ledgerId, Guid? sourceAccountId, List<ReminderLegDto> legs, DateTime paymentPostedAt,
        CancellationToken cancellationToken)
    {
        if (sourceAccountId is not { } sourceId) return legs;
        var splits = await ComputeLoanSplitsAsync(ledgerId, paymentPostedAt, cancellationToken).ConfigureAwait(false);
        if (splits.Count == 0) return legs;

        var loanLeg = legs.FirstOrDefault(l => l.AccountId != sourceId && splits.ContainsKey(l.AccountId));
        if (loanLeg is null || !splits.TryGetValue(loanLeg.AccountId, out var split)) return legs;

        return legs
            .GroupBy(l => l.PostingIndex)
            .SelectMany(g =>
            {
                var src = g.FirstOrDefault(l => l.AccountId == sourceId);
                var cp = g.FirstOrDefault(l => l.AccountId != sourceId);
                if (src is null || cp is null) return g.AsEnumerable();
                if (LoanPostingAmounts(split, cp.AccountId) is not { } amts) return g.AsEnumerable();
                return new[] { cp with { Amount = amts.Counterpart }, src with { Amount = amts.Source } };
            })
            .ToList();
    }

    /// <summary>
    /// Per-template-leg amount overrides (keyed by template leg id) for a loan
    /// reminder materialized via the verbatim-clone fire path
    /// (<see cref="FireAsync"/>) — the same principal/interest/escrow split the
    /// detail/agenda surface, so a clone fire commits real cash rather than the
    /// near-zero template legs. (<see cref="FireBankAsync"/> already commits the
    /// split because its postings are prefilled from <see cref="GetDetailAsync"/>.)
    /// Null when the series isn't loan-driven (clone the template as-is).
    /// </summary>
    /// <summary>
    /// Leg amounts for an ESTIMATED single-posting occurrence, keyed by template leg id —
    /// the same shape <see cref="ComputeLoanLegOverridesAsync"/> returns, so the fire
    /// path applies both through one code path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means "no estimate to apply", which the caller must treat as a REFUSAL rather
    /// than a reason to fall back to the template amount — see
    /// <see cref="FireOutcome.EstimateUnavailableForFire"/>. Returning null for both "not
    /// estimating" and "estimate unavailable" would collapse that distinction, so the
    /// caller checks <c>EstimateSampleCount</c> first and only asks this when the series
    /// really is estimating.
    /// </para>
    /// <para>
    /// Single-posting by construction (the eligibility gate refuses splits), so there is
    /// exactly one source leg and one counterpart. <see cref="EstimatePostingAmounts"/>
    /// gives the pair, and it is the same helper the display path uses — one definition
    /// of what an estimated occurrence is worth.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, decimal>?> ComputeEstimateLegOverridesAsync(
        Guid ledgerId, RecurringTransactionRow series, List<TxnLegRow> templateLegs, DateOnly occurrenceDate,
        CancellationToken cancellationToken)
    {
        if (series.SourceAccountId is not { } sourceId) return null;

        var resolved = await ResolveEstimatesAsync(ledgerId, series.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.TryGetValue(series.Id, out var resolution)) return null;

        // The occurrence's own estimate: on a transfer between currencies its other side
        // at the due date's derived rate, or none when that rate is missing (ADR-0114 D13).
        var rates = await LoadEstimateRatesAsync(
                ledgerId, resolved, [(series.Id, (DateOnly?)occurrenceDate)], cancellationToken)
            .ConfigureAwait(false);
        var estimate = rates.Estimate(resolution, occurrenceDate);
        if (estimate.Amount is not { } estimatedNet) return null;

        return EstimateLegOverrides(sourceId, templateLegs, estimatedNet, estimate.CounterpartyAmount);
    }

    /// <summary>The estimated occurrence's leg amounts by template leg id, or null when
    /// the template has no source leg and counterpart to carry them. Shared by the fire
    /// and the auto-post scan's places fact.</summary>
    /// <param name="counterpart">The other side's own estimate on a transfer between
    /// currencies; null for the negation.</param>
    private static IReadOnlyDictionary<Guid, decimal>? EstimateLegOverrides(
        Guid sourceId, IReadOnlyList<TxnLegRow> templateLegs, decimal estimatedNet, decimal? counterpart)
    {
        var src = templateLegs.FirstOrDefault(l => l.AccountId == sourceId);
        var cp = templateLegs.FirstOrDefault(l => l.AccountId != sourceId);
        if (src is null || cp is null) return null;

        var (sourceAmount, counterpartAmount) = EstimatePostingAmounts(estimatedNet, counterpart);
        return new Dictionary<Guid, decimal>
        {
            [src.Id] = sourceAmount,
            [cp.Id] = counterpartAmount,
        };
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>?> ComputeLoanLegOverridesAsync(
        Guid ledgerId, Guid? sourceAccountId, List<TxnLegRow> templateLegs, DateTime paymentPostedAt,
        CancellationToken cancellationToken)
    {
        if (sourceAccountId is not { } sourceId) return null;
        var splits = await ComputeLoanSplitsAsync(ledgerId, paymentPostedAt, cancellationToken).ConfigureAwait(false);
        return LoanLegOverrides(sourceId, templateLegs, splits);
    }

    /// <summary>The loan split's leg amounts by template leg id, or null when no posting of
    /// the template is a loan component. Shared by the fire and the auto-post scan's places
    /// fact.</summary>
    private static IReadOnlyDictionary<Guid, decimal>? LoanLegOverrides(
        Guid sourceId, IReadOnlyList<TxnLegRow> templateLegs, IReadOnlyDictionary<Guid, LoanSplit> splits)
    {
        if (splits.Count == 0) return null;

        var loanLeg = templateLegs.FirstOrDefault(l => l.AccountId != sourceId && splits.ContainsKey(l.AccountId));
        if (loanLeg is null || !splits.TryGetValue(loanLeg.AccountId, out var split)) return null;

        var overrides = new Dictionary<Guid, decimal>();
        foreach (var g in templateLegs.GroupBy(l => l.PostingIndex))
        {
            var src = g.FirstOrDefault(l => l.AccountId == sourceId);
            var cp = g.FirstOrDefault(l => l.AccountId != sourceId);
            if (src is null || cp is null) continue;
            if (LoanPostingAmounts(split, cp.AccountId) is not { } amts) continue;
            overrides[cp.Id] = amts.Counterpart;
            overrides[src.Id] = amts.Source;
        }
        return overrides.Count > 0 ? overrides : null;
    }
}
