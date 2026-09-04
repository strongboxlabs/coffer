using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Loans;
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

    public RemindersRepository(
        AppDbContext db,
        RecurrenceExpander expander,
        InvestmentTransactionsRepository investmentTxns,
        TransactionsRepository transactions)
    {
        _db = db;
        _expander = expander;
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
        /// (mig 220).
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
    }

    public sealed record FireResult(
        FireOutcome Outcome, Guid? HeaderId,
        // Catch-up (ADR-0047 §9.2): earlier un-acted occurrences this fire also
        // marked skipped, and the earliest of them (for the SPA's confirm/notice).
        int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null);

    /// <summary>Result of <see cref="FireInvestmentAsync"/>. On
    /// <see cref="FireOutcome.ShapeFailure"/>, <see cref="InvestmentFailure"/>
    /// carries the investment validation code to map to a 422.</summary>
    public sealed record FireInvestmentResult(
        FireOutcome Outcome, Guid? HeaderId,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure = null,
        int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null);

    // Reminder mutation outcomes (ADR-0047 slice — manual authoring).
    public enum CreateOutcome { Ok, ShapeFailure }
    public enum EditOutcome
    {
        Ok, NotFound, NotMaterialized, ShapeMismatch, ShapeFailure, EndBeforeStart,

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
    /// map (bank create validates in the endpoint, so it never sets it).</summary>
    public sealed record CreateReminderResult(
        CreateOutcome Outcome,
        Guid? ReminderId,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure);

    public sealed record EditReminderResult(
        EditOutcome Outcome,
        InvestmentTransactionsRepository.CreateFailure? InvestmentFailure);

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
        var list = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.TemplateHeaderId != null)
            .Join(
                _db.TxnHeaders.AsNoTracking(),
                r => r.TemplateHeaderId,
                h => h.Id,
                (r, h) => new { r, h })
            .OrderBy(x => x.r.IsActive ? 0 : 1)
            .ThenBy(x => x.h.Payee)
            .Select(x => new ReminderSummary(
                x.r.Id,
                x.h.Payee,
                x.h.Memo,
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
                x.r.Origin))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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

        return list
            .Select(s =>
            {
                // Loan first: computed from the loan's terms and current balance, which
                // outranks an average. The resolver refuses estimates on loan series
                // anyway, so this ordering is belt and braces.
                if (loanAmounts.TryGetValue(s.Id, out var loanAmount))
                    return s with { Amount = loanAmount };

                var e = estimates.GetValueOrDefault(s.Id);
                if (e is null) return s;

                return s with
                {
                    Amount = e.Amount ?? s.Amount,
                    Estimate = new ReminderEstimate(
                        e.RequestedSampleCount, e.AvailableSampleCount,
                        e.Amount, e.UnavailableReason),
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
                    h.Payee, h.Memo,
                    // Template source-side net (legs on the series' source account).
                    Amount = _db.TxnLegs
                        .Where(l => l.HeaderId == h.Id && l.AccountId == r.SourceAccountId)
                        .Sum(l => (decimal?)l.Amount) ?? 0m,
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
                x.h.Payee, x.h.Memo, x.NextDueDate,
                // The fired occurrence's committed net on the series' source account.
                Amount = _db.TxnLegs
                    .Where(l => l.HeaderId == x.h.Id && l.AccountId == x.SourceAccountId)
                    .Sum(l => (decimal?)l.Amount) ?? 0m,
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

        var result = new List<UpcomingOccurrence>(firedRows.Count + series.Count);

        // Materialized occurrences -> "scheduled".
        foreach (var f in firedRows)
            result.Add(new UpcomingOccurrence(
                f.Date, "scheduled", f.ReminderId, f.Id, f.Payee, f.Memo, f.Amount, f.NextDueDate));

        // Un-fired series slots (expansion clipped at the series end date when
        // earlier than the window end): a skipped slot -> "skipped" (read-only),
        // everything else -> "reminder". Fired slots are emitted above.
        foreach (var s in series)
        {
            // Precedence is not arbitrary: a loan amount is COMPUTED from the loan's
            // terms and current balance, so it outranks an average of recent history.
            // The resolver refuses estimates on loan series anyway, so this ordering is
            // belt and braces rather than the only guard.
            var estimate = estimates.GetValueOrDefault(s.Id);
            var amount = loanAmountBySeries.TryGetValue(s.Id, out var loanAmount) ? loanAmount
                : estimate?.Amount ?? s.Amount;
            var estimateDto = estimate is null
                ? null
                : new ReminderEstimate(
                    estimate.RequestedSampleCount, estimate.AvailableSampleCount,
                    estimate.Amount, estimate.UnavailableReason);
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
                var kind = skippedKeys.Contains((s.Id, date)) ? "skipped" : "reminder";
                result.Add(new UpcomingOccurrence(
                    date, kind, s.Id, null, s.Payee, s.Memo, amount, s.NextDueDate,
                    estimateDto));
            }
        }

        return result.OrderBy(x => x.Date).ThenBy(x => x.Payee).ToList();
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
        // acdays per series, plus the two shape facts the policy needs. Investment shape
        // is the template header carrying an action (ADR-0022); a bank template has none.
        var configured = await _db.RecurringTransactions.AsNoTracking()
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
                      IsInvestmentShape = h.Action != null,
                  })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (configured.Count == 0)
            return new Coffer.Api.Reminders.AutoPostScanResult(Array.Empty<Coffer.Api.Reminders.AutoPostCandidate>(), 0);

        var byId = configured.ToDictionary(
            c => c.Id,
            c => (Acdays: Math.Clamp(c.Acdays, 0, maxAutoCommitDaysBefore),
                  c.IsLoanReminder,
                  c.IsInvestmentShape));

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
                o.ReminderId, o.Date, cfg.Acdays, cfg.IsInvestmentShape, cfg.IsLoanReminder));
        }

        // Oldest first. A backlog posted newest-first would advance the series cursor past
        // slots still un-acted, and ComputeNextDueAsync would then have to walk back.
        return new Coffer.Api.Reminders.AutoPostScanResult(
            due.OrderBy(d => d.OccurrenceDate).ThenBy(d => d.ReminderId).ToList(), tooOld);
    }

    /// <summary>
    /// Materialize one occurrence (ADR-0047 D5): clone the series' template
    /// header + legs into a LIVE committed header dated at
    /// <paramref name="occurrenceDate"/>, stamped with the series id +
    /// occurrence date, and advance the series cursor. Idempotent — firing the
    /// same (series, date) twice returns the already-materialized header rather
    /// than creating a duplicate. The committed header is a normal transaction
    /// (NOT a template) so it flows through balances/holdings/register; the
    /// recompute interceptors fire on SaveChanges.
    /// </summary>
    public async Task<FireResult> FireAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate, Guid? createdByUserId,
        Coffer.Api.Reminders.ReminderCatchUp catchUp,
        CancellationToken cancellationToken = default)
    {
        // The transaction opens BEFORE the checks, and the slot lock is the first thing
        // in it. Previously the idempotency read and the skip read ran outside any
        // transaction and the insert opened its own further down, so the checks were
        // not even repeatable-read against the write they were guarding.
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
        var templateLegs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == templateHeaderId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Loan reminders (ADR-0050): the template legs are near-zero (MD computes
        // principal/interest live), so substitute the computed split before
        // cloning — a verbatim-clone fire must commit the same real cash the
        // agenda/detail show, not the placeholder template amounts.
        var loanOverrides = series.IsLoanReminder
            ? await ComputeLoanLegOverridesAsync(ledgerId, series.SourceAccountId, templateLegs, cancellationToken)
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
                    ledgerId, series, templateLegs, cancellationToken)
                .ConfigureAwait(false);
            if (legOverrides is null)
                return new FireResult(FireOutcome.EstimateUnavailableForFire, null);
        }

        // Clone the template into a committed occurrence + advance the cursor
        // atomically (two SaveChanges: persist the occurrence, then recompute
        // the skip-aware cursor, which reads committed headers). Adjust-at-post
        // (edited values) goes through FireBankAsync / FireInvestmentAsync.
        // The transaction was opened above, with the slot lock, so the checks and
        // this write are one unit.
        var newHeaderId = Guid.NewGuid();
        _db.TxnHeaders.Add(new TxnHeaderRow
        {
            Id = newHeaderId,
            LedgerId = ledgerId,
            Origin = "manual",
            ExternalId = null,
            Payee = template.Payee,
            Memo = template.Memo,
            PostedAt = occurrenceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            // NOT NULL since mig 189: no distinct tax date is stored as the posted date.
            TransactedAt = occurrenceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckNumber = template.CheckNumber,
            IsPending = false,
            IsHidden = false,
            IsMergedInto = null,
            Action = template.Action,
            IsRecurringTemplate = false,
            RecurringTransactionId = reminderId,
            OccurrenceDate = occurrenceDate,
        });
        foreach (var leg in templateLegs)
        {
            _db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = newHeaderId,
                LedgerId = ledgerId,
                AccountId = leg.AccountId,
                PostingIndex = leg.PostingIndex,
                LegMemo = leg.LegMemo,
                Amount = legOverrides is not null && legOverrides.TryGetValue(leg.Id, out var overrideAmount)
                    ? overrideAmount
                    : leg.Amount,
                SecurityId = leg.SecurityId,
                Quantity = leg.Quantity,
                UnitPrice = leg.UnitPrice,
                PostingRole = leg.PostingRole,
            });
        }

        // Catch-up (ADR-0047 §9.2 / ADR-0049): firing this occurrence also marks
        // every earlier un-acted occurrence as skipped, so the calendar + cursor
        // don't strand an overdue backlog. (The user chose Post to cascade like
        // Skip; the SPA surfaces the count.) An earlier ALREADY-FIRED occurrence
        // is preserved (real cash) — only un-acted slots are caught up.
        //
        // NOT for a scheduled run. See ReminderCatchUp: a timer cascading here would
        // mark occurrences skipped that nobody chose to skip, which converts a bounded
        // catch-up window into permanent data loss.
        var cascaded = catchUp == Coffer.Api.Reminders.ReminderCatchUp.SkipEarlierUnacted
            ? await CascadeSkipEarlierAsync(
                ledgerId, reminderId, series.Rrule, series.StartDate, occurrenceDate,
                createdByUserId, cancellationToken).ConfigureAwait(false)
            : Array.Empty<DateOnly>();

        try
        {
            // Persist the fired occurrence + the cascade FIRST so the cursor recompute
            // counts them as consumed (ComputeNextDueAsync reads committed headers +
            // exceptions via AsNoTracking).
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Advance the cursor to the earliest occurrence that is neither fired nor
            // skipped (ADR-0047). After the cascade everything on/before this date is
            // consumed, so the cursor lands on the first occurrence after it.
            series.NextDueDate = await ComputeNextDueAsync(
                reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateOccurrence(ex))
        {
            // Someone else committed this slot between our idempotency read and this
            // write. The lock above makes that unreachable for two callers that both
            // take it — this is the backstop for one that does not, or for a second
            // process, and it is what turns the index from a 500 into the SAME answer
            // the sequential path gives: the slot is fired, here is its header.
            //
            // Deliberately NOT reporting the cascade counts. A loser cascaded nothing;
            // the winner's cascade is already committed and is not ours to describe.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var winner = await WinningOccurrenceHeaderIdAsync(
                reminderId, occurrenceDate, cancellationToken).ConfigureAwait(false);
            return new FireResult(FireOutcome.Ok, winner);
        }

        return new FireResult(FireOutcome.Ok, newHeaderId,
            cascaded.Count, cascaded.Count > 0 ? cascaded[0] : null);
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
        // Transaction first, slot lock inside it, then the checks — see FireAsync and
        // migration 218. The reads below decide whether to write; outside a transaction
        // they were not repeatable-read against that write, and without the lock this
        // path could interleave with a skip on the same slot.
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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

        // Managed loan reminder: the split is authoritative. Recompute it from the
        // loan terms + the current balance and override the client's posting
        // amounts (matched by counterparty account), so a stale or hand-edited
        // client can't post a wrong principal/interest/escrow division. Read
        // BEFORE CreateAsync so "owed" is the balance going into this occurrence.
        var postings = request.Postings;
        if (series.IsLoanReminder && series.LoanAccountId is { } managedLoanId)
        {
            var splits = await ComputeLoanSplitsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
            if (splits.TryGetValue(managedLoanId, out var split))
                postings = request.Postings.Select(p => new TransactionPosting
                {
                    CounterpartyAccountId = p.CounterpartyAccountId,
                    LegMemo = p.LegMemo,
                    Amount = p.CounterpartyAccountId == split.LoanAccountId ? -split.Principal
                        : p.CounterpartyAccountId == split.InterestAccountId ? -split.Interest
                        : p.CounterpartyAccountId == split.EscrowAccountId ? -split.Escrow
                        : p.Amount,
                }).ToList();
        }

        // Reuse the live bank create (source/counterpart legs for every posting,
        // incl. splits; balances via the interceptor), stamped to this
        // occurrence; it JOINS this transaction (does not commit).
        var postedAt = (request.PostedDate ?? occurrenceDate)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var newHeaderId = await _transactions.CreateAsync(
            ledgerId, request.SourceAccountId, postedAt, request.Payee, request.Memo,
            request.CheckNumber, null, postings, null,
            recurringTransactionId: reminderId, occurrenceDate: occurrenceDate,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Catch-up (ADR-0047 §9.2): mark earlier un-acted slots skipped.
        var cascaded = await CascadeSkipEarlierAsync(
            ledgerId, reminderId, series.Rrule, series.StartDate, occurrenceDate,
            createdByUserId, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        series.NextDueDate = await ComputeNextDueAsync(
            reminderId, series.Rrule, series.StartDate, series.EndDate, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FireResult(FireOutcome.Ok, newHeaderId,
            cascaded.Count, cascaded.Count > 0 ? cascaded[0] : null);
    }

    /// <summary>
    /// Adjust-at-post for an INVESTMENT series (ADR-0049): commit the EDITED
    /// investment transaction as this occurrence, REUSING the live investment
    /// create path (<see cref="InvestmentTransactionsRepository.CreateAsync"/>)
    /// so holdings + lots + balances are built exactly as a normal investment
    /// transaction — stamped to the series + slot. Idempotent; refuses a skipped
    /// slot; runs the same catch-up cascade + cursor advance as the clone fire,
    /// all in ONE transaction (CreateAsync joins it via its ambient-tx check).
    /// Rejects a bank series (<see cref="FireOutcome.ShapeMismatch"/>).
    /// </summary>
    public async Task<FireInvestmentResult> FireInvestmentAsync(
        Guid ledgerId, Guid reminderId, DateOnly occurrenceDate,
        CreateInvestmentTransactionRequest request, Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        // Transaction first, slot lock inside it, then the checks — see FireAsync and
        // migration 218. The reads below decide whether to write; outside a transaction
        // they were not repeatable-read against that write, and without the lock this
        // path could interleave with a skip on the same slot.
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockOccurrenceSlotAsync(reminderId, occurrenceDate, cancellationToken)
            .ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new FireInvestmentResult(FireOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new FireInvestmentResult(FireOutcome.NotMaterialized, null);

        // The investment fire route serves investment series only (a bank series
        // posts through /fire with a FireBankOverride).
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
            return new FireInvestmentResult(FireOutcome.ShapeFailure, null, failure);

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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

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
    public async Task<ReminderDetail?> GetDetailAsync(
        Guid ledgerId, Guid reminderId, CancellationToken cancellationToken = default)
    {
        var row = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId && r.LedgerId == ledgerId && r.TemplateHeaderId != null)
            .Join(_db.TxnHeaders.AsNoTracking(), r => r.TemplateHeaderId, h => h.Id, (r, h) => new { r, h })
            .Select(x => new
            {
                x.r.Id, x.r.Rrule, x.r.StartDate, x.r.EndDate, x.r.NextDueDate,
                x.r.AutoCommitDaysBefore, x.r.IsActive, x.r.IsLoanReminder, x.r.Origin, x.r.SourceAccountId,
                x.h.Payee, x.h.Memo, x.h.CheckNumber, x.h.Action, TemplateHeaderId = x.h.Id,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var legs = await (
            from l in _db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == row.TemplateHeaderId)
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            join s in _db.Securities.AsNoTracking() on l.SecurityId equals s.Id into secs
            from s in secs.DefaultIfEmpty()
            orderby l.PostingIndex, l.Amount
            select new ReminderLegDto(
                l.AccountId, a.Name, l.PostingIndex, l.Amount, l.LegMemo,
                l.SecurityId, s != null ? s.Ticker : null, l.Quantity, l.UnitPrice, l.PostingRole))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Loan reminders (ADR-0050): replace the mostly-zero template legs with
        // the computed principal/interest/escrow split so the occurrence dialog
        // prefills real, editable values (and the calendar/agenda agree).
        if (row.IsLoanReminder)
            legs = await ApplyLoanSplitToLegsAsync(ledgerId, row.SourceAccountId, legs, cancellationToken)
                .ConfigureAwait(false);

        // Mig 220. Resolved for this ONE series (the function takes a series scope), and
        // deliberately not folded into the leg amounts — see ReminderDetail.Estimate.
        var estimateResolution = (await ResolveEstimatesAsync(ledgerId, reminderId, cancellationToken)
            .ConfigureAwait(false)).GetValueOrDefault(reminderId);
        var estimateDto = estimateResolution is null
            ? null
            : new ReminderEstimate(
                estimateResolution.RequestedSampleCount, estimateResolution.AvailableSampleCount,
                estimateResolution.Amount, estimateResolution.UnavailableReason);

        return new ReminderDetail(
            row.Id, row.Action is null ? "bank" : "investment",
            row.Payee, row.Memo, row.CheckNumber, row.Action,
            row.Rrule, row.StartDate, row.EndDate, row.NextDueDate, row.AutoCommitDaysBefore,
            row.IsActive, row.IsLoanReminder, row.Origin, row.SourceAccountId, legs,
            estimateDto);
    }

    /// <summary>
    /// Create a BANK-shape reminder series: a template header
    /// (<c>is_recurring_template=true</c>) + source/counterpart legs (mirroring
    /// <see cref="TransactionsRepository.CreateAsync"/>) + the slim series row.
    /// Shape + account validation happens in the endpoint; this trusts it.
    /// </summary>
    public async Task<CreateReminderResult> CreateBankAsync(
        Guid ledgerId, CreateReminderRequest request, CancellationToken cancellationToken = default)
    {
        var templateHeaderId = Guid.NewGuid();
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _db.TxnHeaders.Add(NewTemplateHeader(
            templateHeaderId, ledgerId, request.StartDate,
            request.Payee, request.Memo, request.CheckNumber, action: null));

        for (var i = 0; i < request.Postings.Count; i++)
            AddBankTemplateLegs(templateHeaderId, ledgerId, request.SourceAccountId, request.Postings[i], i);

        var reminderId = await AddSeriesRowAsync(
            ledgerId, templateHeaderId, request.SourceAccountId, request.Rrule, request.StartDate,
            request.EndDate, request.AutoCommitDaysBefore, cancellationToken,
            estimateSampleCount: request.EstimateSampleCount).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateReminderResult(CreateOutcome.Ok, reminderId, null);
    }

    /// <summary>Outcome of <see cref="CreateManagedLoanReminderAsync"/>.</summary>
    public enum ManagedLoanReminderResult { Ok, LoanTermsMissing, AlreadyExists }

    /// <summary>Result + the new reminder id (null on failure).</summary>
    public sealed record ManagedLoanReminderOutcome(ManagedLoanReminderResult Result, Guid? ReminderId);

    /// <summary>
    /// Create the managed payment reminder for a loan account (ADR-0050 ext). The
    /// template legs post to (loan · interest · escrow) with PLACEHOLDER amounts —
    /// the real principal/interest/escrow split is computed live from loan_terms +
    /// the loan balance at fire/display time (<see cref="ComputeLoanSplitsAsync"/>).
    /// Flags the series <c>is_loan_reminder</c> + links <c>loan_account_id</c> (one
    /// per loan, enforced by the partial-unique index + the AlreadyExists guard).
    /// Cadence is derived from the loan's payments-per-year on the supplied start
    /// day. The caller validates the loan account + the bank source account.
    /// </summary>
    public async Task<ManagedLoanReminderOutcome> CreateManagedLoanReminderAsync(
        Guid ledgerId, Guid loanAccountId, Guid sourceAccountId, DateOnly startDate,
        CancellationToken cancellationToken = default)
    {
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

        // Placeholder amounts: principal/interest a -1 stub (overridden live), the
        // escrow leg its stored amount. All negative = an outflow from the source.
        var escrow = terms.EscrowAmount > 0m ? terms.EscrowAmount : 1m;
        var postings = new[]
        {
            new TransactionPosting { CounterpartyAccountId = loanAccountId, Amount = -1m },
            new TransactionPosting { CounterpartyAccountId = terms.InterestAccountId.Value, Amount = -1m },
            new TransactionPosting { CounterpartyAccountId = terms.EscrowAccountId.Value, Amount = -escrow },
        };

        var templateHeaderId = Guid.NewGuid();
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _db.TxnHeaders.Add(NewTemplateHeader(
            templateHeaderId, ledgerId, startDate, "Loan payment", memo: null, checkNumber: null, action: null));
        for (var i = 0; i < postings.Length; i++)
            AddBankTemplateLegs(templateHeaderId, ledgerId, sourceAccountId, postings[i], i);
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
        // BuildTemplateLegsAsync only reads for validation + returns un-tracked
        // legs; PostedAt on the request is unused by it (the template's
        // posted_at is the series start, set on the header below).
        var legsResult = await _investmentTxns
            .BuildTemplateLegsAsync(ledgerId, templateHeaderId, request.Transaction, cancellationToken)
            .ConfigureAwait(false);
        if (legsResult.Failure is { } f)
            return new CreateReminderResult(CreateOutcome.ShapeFailure, null, f);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _db.TxnHeaders.Add(NewTemplateHeader(
            templateHeaderId, ledgerId, request.StartDate,
            request.Transaction.Payee, request.Transaction.Memo, request.Transaction.CheckNumber,
            action: request.Transaction.Action));
        _db.TxnLegs.AddRange(legsResult.Legs);

        var reminderId = await AddSeriesRowAsync(
            ledgerId, templateHeaderId, request.Transaction.BrokerageAccountId, request.Rrule,
            request.StartDate, request.EndDate, request.AutoCommitDaysBefore, cancellationToken)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateReminderResult(CreateOutcome.Ok, reminderId, null);
    }

    /// <summary>
    /// Edit a BANK-shape series: recurrence scalars (partial) + the template
    /// transaction shape. <see cref="EditReminderRequest.Postings"/>, when
    /// supplied, drops + rebuilds the template legs (no lots/overrides to
    /// reconcile). Rejects an investment series with <c>ShapeMismatch</c>.
    /// </summary>
    public async Task<EditReminderResult> EditBankAsync(
        Guid ledgerId, Guid reminderId, EditReminderRequest request, CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new EditReminderResult(EditOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new EditReminderResult(EditOutcome.NotMaterialized, null);

        var header = await _db.TxnHeaders.FirstAsync(h => h.Id == templateHeaderId, cancellationToken)
            .ConfigureAwait(false);
        if (header.Action is not null) return new EditReminderResult(EditOutcome.ShapeMismatch, null);

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
        if (request.Memo is not null) header.Memo = request.Memo;
        if (request.CheckNumber is not null) header.CheckNumber = request.CheckNumber;
        header.PostedAt = series.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        if (request.Postings is { } postings)
        {
            series.SourceAccountId = postings.SourceAccountId;   // source may change with the legs
            await ReplaceTemplateLegsAsync(templateHeaderId, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < postings.Items.Count; i++)
                AddBankTemplateLegs(templateHeaderId, ledgerId, postings.SourceAccountId, postings.Items[i], i);
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
    /// (replace-all). Rejects a bank series with <c>ShapeMismatch</c>.
    /// </summary>
    public async Task<EditReminderResult> EditInvestmentAsync(
        Guid ledgerId, Guid reminderId, EditInvestmentReminderRequest request, CancellationToken cancellationToken = default)
    {
        var series = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == reminderId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null) return new EditReminderResult(EditOutcome.NotFound, null);
        if (series.TemplateHeaderId is not { } templateHeaderId)
            return new EditReminderResult(EditOutcome.NotMaterialized, null);

        var header = await _db.TxnHeaders.FirstAsync(h => h.Id == templateHeaderId, cancellationToken)
            .ConfigureAwait(false);
        if (header.Action is null) return new EditReminderResult(EditOutcome.ShapeMismatch, null);

        // Validate + build the replacement legs (read-only) BEFORE opening the
        // write transaction, so a shape failure writes nothing.
        IReadOnlyList<TxnLegRow>? newLegs = null;
        if (request.Transaction is { } txnReq)
        {
            var legsResult = await _investmentTxns
                .BuildTemplateLegsAsync(ledgerId, templateHeaderId, txnReq, cancellationToken)
                .ConfigureAwait(false);
            if (legsResult.Failure is { } f) return new EditReminderResult(EditOutcome.ShapeFailure, f);
            newLegs = legsResult.Legs;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var scheduleChanged = ApplyRecurrenceEdits(series, request.Rrule, request.StartDate,
            request.ClearEndDate, request.EndDate, request.ClearAutoCommit, request.AutoCommitDaysBefore);

        // Effective-range guard (see EditBankAsync): a single-sided edit must
        // not invert [start, end]. Tx disposes without commit on rejection.
        if (series.EndDate is { } invEnd && invEnd < series.StartDate)
            return new EditReminderResult(EditOutcome.EndBeforeStart, null);

        if (request.Transaction is { } t && newLegs is not null)
        {
            series.SourceAccountId = t.BrokerageAccountId;   // source (brokerage) may change
            await ReplaceTemplateLegsAsync(templateHeaderId, cancellationToken).ConfigureAwait(false);
            _db.TxnLegs.AddRange(newLegs);
            header.Action = t.Action;
            header.Payee = t.Payee;
            header.Memo = t.Memo;
            header.CheckNumber = t.CheckNumber;
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
    /// <b>Three error messages have instructed users to do this since before it
    /// existed.</b> All three fire paths reject a suppressed slot with "That occurrence
    /// was skipped; un-skip it before firing", and there was no route and no affordance —
    /// so a mis-clicked Skip was permanent and the message sent people hunting for a
    /// control nobody had built. The message was never the bug.
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

    private static TxnHeaderRow NewTemplateHeader(
        Guid id, Guid ledgerId, DateOnly startDate,
        string? payee, string? memo, string? checkNumber, string? action) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        Origin = "manual",          // manual ⇔ provider_key NULL ⇔ external_id may be NULL (CHECKs, mig 107/109)
        ExternalId = null,
        Payee = payee,
        Memo = memo,
        CheckNumber = checkNumber,
        PostedAt = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        // NOT NULL since mig 189.
        TransactedAt = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        IsPending = false,
        IsHidden = false,
        IsMergedInto = null,
        Action = action,
        IsRecurringTemplate = true, // invisible to balances/holdings/register (ADR-0048)
        RecurringTransactionId = null,
        OccurrenceDate = null,
    };

    private void AddBankTemplateLegs(
        Guid headerId, Guid ledgerId, Guid sourceAccountId, TransactionPosting posting, int postingIndex)
    {
        // Source-side leg carries the signed amount + leg memo; the server
        // writes the paired counterparty as -amount so the posting sums to zero
        // (ADR-0019) — identical to TransactionsRepository.AddPostingLegs.
        _db.TxnLegs.Add(new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = sourceAccountId,
            PostingIndex = postingIndex,
            Amount = posting.Amount,
            LegMemo = posting.LegMemo,
        });
        _db.TxnLegs.Add(new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = posting.CounterpartyAccountId,
            PostingIndex = postingIndex,
            Amount = -posting.Amount,
        });
    }

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

    /// <summary>
    /// Can this series hold an estimate: a single posting, and not a loan?
    /// </summary>
    /// <remarks>
    /// One predicate for the two exclusions, and the SAME one the resolver applies when
    /// it decides whether to produce an amount. Storing a flag the resolver would refuse
    /// to honour is how a reminder ends up configured to estimate and permanently unable
    /// to — the shape of failure auto-post shipped with.
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
    /// is what keeps an imported reminder running since 2015 from stranding the
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
    /// The current per-occurrence split for every loan account in the ledger,
    /// keyed by loan account id. Interest = current balance owed × periodic
    /// rate; principal = payment − interest; escrow is the stored amount. The
    /// "owed" balance is the account's canonical current balance (the register's
    /// balance_after, account_current_balances / mig 133 — never a raw re-sum),
    /// so it excludes merged-away duplicates + hidden events and honors leg
    /// overrides. Empty when the ledger has no loans.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LoanSplit>> ComputeLoanSplitsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var terms = await _db.LoanTerms.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0) return EmptyLoanSplits;

        var loanAccountIds = terms.Select(t => t.AccountId).ToList();

        // Owed = the account's canonical current balance (the register's
        // balance_after, account_current_balances / mig 133). A raw re-sum of
        // txn_legs here silently diverged from the register — it double-counted
        // merged-away duplicate payments (is_merged_into) and ignored leg
        // overrides — which under-stated the balance and so under-charged
        // interest / over-paid principal on the split. Reading the canonical
        // view keeps the split on the single source of truth (ADR-0034).
        var balanceByAccount = (await _db.AccountCurrentBalances.AsNoTracking()
                .Where(b => loanAccountIds.Contains(b.AccountId))
                .Select(b => new { b.AccountId, b.Balance })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.AccountId, b => b.Balance);

        var map = new Dictionary<Guid, LoanSplit>(terms.Count);
        foreach (var t in terms)
        {
            var balance = balanceByAccount.GetValueOrDefault(t.AccountId);
            var owed = balance < 0m ? -balance : 0m;   // liability: negative balance = amount owed
            var payment = t.PaymentIsComputed
                ? LoanAmortization.PeriodicPayment(
                    t.OriginalPrincipal, t.AnnualInterestRate, t.PaymentCount, t.PaymentsPerYear)
                : (t.FixedPayment ?? 0m);
            var (interest, principal) = LoanAmortization.PeriodSplit(
                owed, payment, t.AnnualInterestRate, t.PaymentsPerYear);
            map[t.AccountId] = new LoanSplit(
                t.AccountId, t.InterestAccountId, t.EscrowAccountId, principal, interest, t.EscrowAmount);
        }
        return map;
    }

    /// <summary>
    /// Maps each loan-driven series id → the source-side full payment (a
    /// negative outflow) = −(principal + interest + escrow). A series is
    /// loan-driven when its template has a leg on a loan account.
    /// </summary>
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
    }

    /// <summary>One series' resolved estimate, or the reason it has none.</summary>
    internal sealed record EstimateResolution(
        int RequestedSampleCount,
        int AvailableSampleCount,
        decimal? Amount,
        string? UnavailableReason);

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
    /// disagree about what a reminder is worth. The loan feature learned this the hard
    /// way: it has two overrides, and they do not apply the same predicate.
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
    /// rounding to 2dp happen here, at the scale <c>ck_txn_legs_amount_scale_2</c> pins.
    /// Rounding in SQL and again on write is the double-rounding defect migration 209 was
    /// written to remove.
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
                      TemplateAction = h.Action,
                      TemplateHeaderId = h.Id,
                  })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (configured.Count == 0) return EmptyEstimates;

        var templateIds = configured.Select(c => c.TemplateHeaderId).ToList();
        var legs = await _db.TxnLegs.AsNoTracking()
            .Where(l => templateIds.Contains(l.HeaderId))
            .Select(l => new { l.HeaderId, l.AccountId, l.PostingIndex, l.Amount })
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
            if (reason is null)
            {
                if (sample.SampleCount == 0)
                {
                    reason = EstimateUnavailable.NoHistory;
                }
                else
                {
                    var averaged = decimal.Round(
                        sample.SampleSum / sample.SampleCount, 2, MidpointRounding.ToEven);

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
                c.Requested, sample.SampleCount, amount, reason);
        }

        return resolved;
    }

    /// <summary>
    /// The two leg amounts for one estimated single-posting occurrence: the source leg
    /// carries the estimated net, the counterpart its negation, so the posting still sums
    /// to zero.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <see cref="LoanPostingAmounts"/>, and shared by the
    /// display path and the fire path for the same reason it is — two implementations of
    /// "what is this occurrence worth" is how the loan feature came to have a display
    /// that overrides series the write path leaves alone.
    /// </remarks>
    private static (decimal Source, decimal Counterpart) EstimatePostingAmounts(
        decimal estimatedSourceNet) => (estimatedSourceNet, -estimatedSourceNet);

    private async Task<IReadOnlyDictionary<Guid, decimal>> ComputeLoanAmountBySeriesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var splits = await ComputeLoanSplitsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        if (splits.Count == 0) return EmptyLoanAmounts;

        var loanAccountIds = splits.Keys.ToHashSet();
        var pairs = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.IsActive && r.TemplateHeaderId != null)
            .Join(_db.TxnLegs.AsNoTracking(), r => r.TemplateHeaderId, l => l.HeaderId,
                  (r, l) => new { SeriesId = r.Id, l.AccountId })
            .Where(x => loanAccountIds.Contains(x.AccountId))
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<Guid, decimal>();
        foreach (var p in pairs)
            if (splits.TryGetValue(p.AccountId, out var s))
                map[p.SeriesId] = -(s.Principal + s.Interest + s.Escrow);
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
        Guid ledgerId, Guid? sourceAccountId, List<ReminderLegDto> legs, CancellationToken cancellationToken)
    {
        if (sourceAccountId is not { } sourceId) return legs;
        var splits = await ComputeLoanSplitsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
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
        Guid ledgerId, RecurringTransactionRow series, List<TxnLegRow> templateLegs,
        CancellationToken cancellationToken)
    {
        if (series.SourceAccountId is not { } sourceId) return null;

        var resolved = await ResolveEstimatesAsync(ledgerId, series.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.TryGetValue(series.Id, out var estimate)) return null;
        if (estimate.Amount is not { } estimatedNet) return null;

        var src = templateLegs.FirstOrDefault(l => l.AccountId == sourceId);
        var cp = templateLegs.FirstOrDefault(l => l.AccountId != sourceId);
        if (src is null || cp is null) return null;

        var (sourceAmount, counterpartAmount) = EstimatePostingAmounts(estimatedNet);
        return new Dictionary<Guid, decimal>
        {
            [src.Id] = sourceAmount,
            [cp.Id] = counterpartAmount,
        };
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>?> ComputeLoanLegOverridesAsync(
        Guid ledgerId, Guid? sourceAccountId, List<TxnLegRow> templateLegs, CancellationToken cancellationToken)
    {
        if (sourceAccountId is not { } sourceId) return null;
        var splits = await ComputeLoanSplitsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
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
