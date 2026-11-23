namespace Coffer.Api.Contracts;

/// <summary>
/// One recurring-reminder SERIES for the management list (ADR-0047).
/// <c>GET /api/ledgers/{ledgerId}/reminders</c>. The transaction shape
/// (accounts / amount / splits) lives on the template header+legs and is
/// loaded by the per-series detail endpoint (a later slice); the list shows
/// the payee + recurrence + schedule. <see cref="Rrule"/> is the raw RFC-5545
/// string — the SPA renders it human-readable.
/// </summary>
public sealed record ReminderSummary(
    Guid Id,
    string? Payee,
    string? Memo,
    // Source-side net of the template (the cash impact on the originating
    // account: negative = outflow, positive = inflow) - the figure the agenda
    // shows next to each series (MD parity). Computed from the template legs;
    // 0 for a custom/no-leg reminder.
    decimal Amount,
    string? Rrule,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly? NextDueDate,
    int? AutoCommitDaysBefore,
    bool IsActive,
    bool IsLoanReminder,
    string Origin,

    // Mig 220: the series' estimate, so the manage list agrees with the agenda.
    ReminderEstimate? Estimate = null,

    // The currency Amount is in: the series' source account's, as the agenda's
    // occurrences carry it. Null when the series has no single source, whose
    // Amount is the 0 above and no figure in any currency.
    string? CurrencyCode = null);

/// <summary>
/// One entry in the upcoming agenda/calendar (ADR-0047):
/// <c>GET /api/ledgers/{ledgerId}/reminders/upcoming?from&amp;to</c>. One of a
/// <c>scheduled</c> occurrence (already materialized — a committed header), a
/// <c>skipped</c> slot (a read-only trail of a skipped/catch-up occurrence,
/// ADR-0049 D11), or a <c>reminder</c> slot (computed from the series' RRULE,
/// not yet acted). v1 is reminder-driven (series occurrences); a general
/// future-transaction calendar is a later enhancement.
/// </summary>
/// <summary>
/// An estimated amount, present only on a series that opted in (migration 220).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Amount"/> null means there is no estimate</b> and the surface is showing
/// the template amount; <see cref="UnavailableReason"/> says why. Non-null means the
/// surface's own amount field IS this estimate.
/// </para>
/// <para>
/// It travels as its own record rather than by rewriting the amount in place, because
/// "this number is a guess" is not derivable from the number. Every amount-bearing
/// reminder contract is a bare decimal with no provenance today, and the loan feature got
/// away without a marker only because <c>IsLoanReminder</c> flags the entire series.
/// </para>
/// <para>
/// <see cref="AvailableSampleCount"/> is shown to the user, not just used internally: an
/// average of one occurrence and an average of twelve deserve different confidence, and
/// the user is the only one who can judge which.
/// </para>
/// <para>
/// On a transfer between real accounts in different currencies, the other side is
/// estimated too (ADR-0114 D13): <see cref="CounterpartyAmount"/> is <see cref="Amount"/>
/// converted at the derived rate for the occurrence's due date, rounded once at the other
/// account's places, half away from zero. It is resolved per occurrence, so a series' two
/// occurrences can differ, and one can have it while another has none. With no rate for
/// the day, <see cref="Amount"/> is null too, <see cref="UnavailableReason"/> is
/// <c>rate-unavailable</c> and <see cref="RateUnavailable"/> names each missing stored leg.
/// </para>
/// </remarks>
/// <param name="UnavailableReason">Why there is no estimate: <c>no-history</c>,
/// <c>zero-estimate</c>, <c>sign-mismatch</c>, <c>has-split</c>, <c>loan-driven</c>,
/// <c>not-bank-shape</c>, <c>no-source-account</c>, or <c>rate-unavailable</c> (a transfer
/// between currencies with no derived rate for the occurrence's due date).</param>
/// <param name="CounterpartyAmount">The other side's estimate, signed, in the other
/// account's currency; null when the series is not a transfer between currencies, or
/// <paramref name="Amount"/> is null.</param>
/// <param name="RateUnavailable">On <c>rate-unavailable</c>, each missing stored leg in
/// the direction it is stored (account currency into the reporting currency) and the due
/// date; absent from the JSON otherwise.</param>
public sealed record ReminderEstimate(
    int RequestedSampleCount,
    int AvailableSampleCount,
    decimal? Amount,
    string? UnavailableReason,
    decimal? CounterpartyAmount = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

public sealed record UpcomingOccurrence(
    DateOnly Date,
    string Kind,
    Guid ReminderId,
    Guid? HeaderId,
    string? Payee,
    string? Memo,
    // Source-side net of this occurrence (a fired occurrence carries its own
    // committed net; a reminder slot carries the template's net). Negative =
    // outflow, positive = inflow. Drives the calendar/agenda amount (MD parity).
    decimal Amount,
    // The series' next-due cursor (earliest un-acted occurrence). The SPA uses
    // it to tell whether acting on an un-fired slot will catch-up (cascade-skip)
    // earlier occurrences — true when SeriesNextDue < Date — so the form can
    // warn inline (ADR-0047 §9.2). Null for a custom series.
    DateOnly? SeriesNextDue,

    // The series' estimated amount, when it asked for one (mig 220).
    // Null on a FIRED occurrence: that carries the real committed figure, and an
    // estimate of a settled fact is noise.
    ReminderEstimate? Estimate = null,

    // The series' source account, with its name and currency carried here so a
    // deactivated source still has both (a client's default account list drops
    // inactive accounts). All three are null when the series has no single source.
    Guid? SourceAccountId = null,
    string? SourceAccountName = null,
    string? CurrencyCode = null,

    // Null when the series has no auto-post days; otherwise whether the auto-post
    // job posts its occurrences, by the job's own predicate.
    UpcomingAutoPost? AutoPost = null);

/// <summary>
/// An occurrence's series has auto-post days. <see cref="ExcludedReason"/> is null
/// when the job posts its occurrences, else why it leaves them for a person:
/// <c>no-lines</c>, <c>investment</c>, <c>inactive-account</c>,
/// <c>loan-across-currencies</c> or <c>template-amount-does-not-fit</c>, the first that
/// applies in that order (<c>AutoPostExclusion</c>, ADR-0097).
/// </summary>
public sealed record UpcomingAutoPost(string? ExcludedReason);

/// <summary>
/// Body for <c>POST /api/ledgers/{ledgerId}/reminders/{id}/fire/bank</c> —
/// adjust-at-post for a BANK series: the EDITED transaction (one source account
/// + N postings, INCL. splits) committed as the occurrence (ADR-0049). Mirrors
/// <see cref="FireInvestmentReminderRequest"/>; the server commits it through
/// the live bank create. <see cref="PostedDate"/> defaults to the occurrence date.
/// </summary>
public sealed class FireBankReminderRequest
{
    public DateOnly OccurrenceDate { get; init; }
    public Guid SourceAccountId { get; init; }
    public IReadOnlyList<TransactionPosting> Postings { get; init; } = Array.Empty<TransactionPosting>();
    public string? Payee { get; init; }
    public string? CheckNumber { get; init; }
    public DateOnly? PostedDate { get; init; }

    /// <summary>The occurrence's memo and tags, as on a bank create
    /// (<see cref="CreateTransactionRequest.Notes"/>), written from
    /// <see cref="SourceAccountId"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TransactionNotesRequest? Notes { get; init; }
}

/// <summary>
/// Body for <c>POST /api/ledgers/{ledgerId}/reminders/{id}/fire/investment</c> —
/// adjust-at-post for an INVESTMENT series. The edited transaction (same
/// action × field shape the live investment editor speaks) is committed as the
/// occurrence with real holdings/lots (ADR-0049). <c>PostedAt</c> on the
/// embedded request is honored as the committed date.
/// </summary>
public sealed class FireInvestmentReminderRequest
{
    public DateOnly OccurrenceDate { get; init; }
    public CreateInvestmentTransactionRequest Transaction { get; init; } = new();
}

/// <summary>Response for both fire routes — the committed header materialized (or, when the
/// occurrence was already fired, the existing one). <see cref="SkippedEarlierCount"/>
/// is the catch-up tally: earlier un-acted occurrences this fire also marked
/// skipped (ADR-0047 §9.2), with <see cref="SkippedEarlierFrom"/> the earliest
/// of them — the SPA surfaces "also skipped N earlier" from these.</summary>
public sealed record FireReminderResponse(
    Guid HeaderId, int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null);

// ----------------------------------------------------------------------
// Mutation surface (ADR-0047 slice — manual authoring). Create/edit fork
// by transaction shape (bank vs investment), mirroring the live
// /transactions vs /investment-transactions split; disable + skip are
// shape-agnostic. The template header+legs are built through the SAME
// validated construction the live editors use (PostingValidation for bank;
// InvestmentTransactionsRepository.BuildTemplateLegsAsync for investment),
// then flagged is_recurring_template so they never touch balances/holdings.
// ----------------------------------------------------------------------

/// <summary>
/// <c>POST /api/ledgers/{ledgerId}/reminders</c> — create a manual BANK-shape
/// reminder series: recurrence metadata + a transaction shape (one
/// <see cref="SourceAccountId"/> + N postings, identical to
/// <see cref="CreateTransactionRequest"/>). The server materializes the
/// template header (<c>is_recurring_template=true</c>) + legs + the
/// <c>recurring_transactions</c> row in one transaction.
/// </summary>
public sealed class CreateReminderRequest
{
    /// <summary>RFC-5545 recurrence rule; required, validated by the expander.</summary>
    public string Rrule { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    /// <summary>NULL = manual approve; N ≥ 0 = auto-commit N days before due
    /// (the firing worker is a later slice). Negative is rejected.</summary>
    public int? AutoCommitDaysBefore { get; init; }

    public string? Payee { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>The register account every posting's source-side leg goes to;
    /// must be a bank-shape account in this ledger.</summary>
    public Guid SourceAccountId { get; init; }
    public IReadOnlyList<TransactionPosting> Postings { get; init; } = Array.Empty<TransactionPosting>();

    /// <summary>The template's memo and tags, as on a bank create
    /// (<see cref="CreateTransactionRequest.Notes"/>), written from
    /// <see cref="SourceAccountId"/>. Every occurrence posted from the template
    /// starts with them.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TransactionNotesRequest? Notes { get; init; }

    /// <summary>
    /// NULL = use the template amount. N (1-24) = the amount is the average of the last
    /// N committed occurrences of this series (mig 220).
    /// </summary>
    /// <remarks>
    /// Only for a SINGLE-posting, non-loan series. Both exclusions are rejected rather
    /// than silently ignored: a split has no single amount to estimate, and a loan
    /// payment is computed from its terms, its split from the loan's balance as of the
    /// payment's own date — real information an average would only degrade.
    /// </remarks>
    public int? EstimateSampleCount { get; init; }
}

/// <summary>
/// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}</c> — edit a
/// BANK-shape series. PARTIAL on the recurrence scalars (null = leave
/// unchanged); <see cref="Postings"/> replaces the template's legs wholesale
/// when supplied (null = legs untouched). Disable/enable is a SEPARATE
/// sub-route. Already-committed occurrences are never retroactively mutated.
/// </summary>
public sealed class EditReminderRequest
{
    public string? Rrule { get; init; }
    public DateOnly? StartDate { get; init; }
    /// <summary>Explicit clear for the nullable end date (a null
    /// <see cref="EndDate"/> already means "unchanged").</summary>
    public bool ClearEndDate { get; init; }
    public DateOnly? EndDate { get; init; }
    /// <summary>Explicit clear for the nullable auto-commit value.</summary>
    public bool ClearAutoCommit { get; init; }
    public int? AutoCommitDaysBefore { get; init; }

    public string? Payee { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>When supplied, the template's legs are dropped + rebuilt from
    /// this list (the template has no lots). An item keeps a stored line's memo and tags
    /// only by naming, in <see cref="TransactionPosting.LegId"/>, that line's leg on the
    /// series source (<see cref="ReminderLegDto.LegId"/>); an item without one is a new
    /// line and starts blank. An id that names no such leg, or names one twice, is
    /// refused. Null = legs untouched.</summary>
    public PatchReminderPostings? Postings { get; init; }

    /// <summary>The template's memo and tags, with the presence rule of
    /// <see cref="PatchTransactionRequest.Notes"/>, written from the series' source
    /// (<see cref="PatchReminderPostings.SourceAccountId"/> when postings are sent).
    /// Omitted: no note changes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TransactionNotesRequest? Notes { get; init; }

    /// <summary>Explicit clear for the nullable estimate (mig 220).</summary>
    /// <remarks>
    /// Mirrors <see cref="ClearAutoCommit"/> because a null scalar on this PATCH body
    /// already means "unchanged", so there is no other way to say "stop estimating".
    /// </remarks>
    public bool ClearEstimate { get; init; }

    /// <summary>Turn estimating on, or change N. See <see cref="CreateReminderRequest.EstimateSampleCount"/>.</summary>
    public int? EstimateSampleCount { get; init; }
}

/// <summary>Replace-all postings sub-shape for <see cref="EditReminderRequest"/>.</summary>
public sealed class PatchReminderPostings
{
    /// <summary>The account every posting is entered from, as on create. It must be the
    /// series' stored source (<see cref="ReminderDetail.SourceAccountId"/>): a reminder's
    /// source is set when it is created, and an edit naming another is refused with
    /// <c>reminder-source-immutable</c>.</summary>
    public Guid SourceAccountId { get; init; }
    public IReadOnlyList<TransactionPosting> Items { get; init; } = Array.Empty<TransactionPosting>();
}

/// <summary>
/// <c>POST /api/ledgers/{ledgerId}/reminders/investment</c> — create a manual
/// INVESTMENT-shape reminder series: recurrence metadata wrapping the SAME
/// action × field shape the live investment editor speaks
/// (<see cref="CreateInvestmentTransactionRequest"/>). The template legs are
/// built via <c>InvestmentTransactionsRepository.BuildTemplateLegsAsync</c> —
/// identical validation + leg construction as a live investment create, minus
/// holdings/lots (a template never touches them).
/// </summary>
/// <remarks>
/// On the embedded <see cref="Transaction"/>, <c>PostedAt</c> is IGNORED (the
/// template's posted_at is derived from <see cref="StartDate"/>) and
/// <c>ProviderSecurityHint</c> is IGNORED (provider mappings don't apply to a
/// template). Every other field drives the template shape.
/// </remarks>
public sealed class CreateInvestmentReminderRequest
{
    public string Rrule { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? AutoCommitDaysBefore { get; init; }
    public CreateInvestmentTransactionRequest Transaction { get; init; } = new();
}

/// <summary>
/// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}/investment</c> —
/// edit an INVESTMENT-shape series. PARTIAL on the recurrence scalars; when
/// <see cref="Transaction"/> is supplied it REPLACES the template's
/// transaction shape wholesale (ADR-0025/0029 replace-all semantics), null =
/// shape untouched. Same <c>PostedAt</c>/<c>ProviderSecurityHint</c>-ignored
/// rule as create.
/// </summary>
public sealed class EditInvestmentReminderRequest
{
    public string? Rrule { get; init; }
    public DateOnly? StartDate { get; init; }
    public bool ClearEndDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool ClearAutoCommit { get; init; }
    public int? AutoCommitDaysBefore { get; init; }

    /// <summary>The replacement shape. Its <c>BrokerageAccountId</c> must be the series'
    /// stored source: a reminder's brokerage is set when it is created, and an edit naming
    /// another is refused with <c>reminder-source-immutable</c>.</summary>
    public CreateInvestmentTransactionRequest? Transaction { get; init; }
}

/// <summary>
/// <c>PATCH /api/ledgers/{ledgerId}/reminders/{reminderId}/active</c> — soft
/// disable/enable, mirroring the accounts <c>/active</c> sub-route. A disabled
/// series stays in the list (so it can be re-enabled) but drops out of the
/// upcoming agenda.
/// </summary>
public sealed class SetReminderActiveRequest
{
    public bool Active { get; init; }
}

/// <summary>
/// <c>POST /api/ledgers/{ledgerId}/reminders/{reminderId}/skip</c> — suppress
/// one occurrence (ADR-0047 D6). Idempotent; rejected if that occurrence was
/// already fired.
/// </summary>
public sealed class SkipReminderRequest
{
    public DateOnly OccurrenceDate { get; init; }
}

/// <summary>200 body for skip — echoes the advanced cursor so the SPA can
/// refresh the list's "Next due" without a refetch. <see cref="SkippedEarlierCount"/>
/// is the catch-up tally (earlier un-acted occurrences this skip also marked
/// skipped, ADR-0047 §9.2), with <see cref="SkippedEarlierFrom"/> the earliest.</summary>
public sealed record SkipReminderResponse(
    DateOnly OccurrenceDate, DateOnly? NextDueDate,
    int SkippedEarlierCount = 0, DateOnly? SkippedEarlierFrom = null);

/// <summary>
/// <c>DELETE /api/ledgers/{ledgerId}/reminders/{reminderId}/skip?occurrenceDate=</c> —
/// the slot is un-acted again, and the series cursor may have moved EARLIER.
/// </summary>
/// <remarks>
/// No cascade count, unlike <see cref="SkipReminderResponse"/>. Un-skip touches exactly
/// the one slot asked for: skipping sweeps earlier un-acted occurrences, and undoing that
/// sweep would mean guessing which of them the user wanted back.
/// </remarks>
public sealed record UnskipReminderResponse(
    DateOnly OccurrenceDate, DateOnly? NextDueDate);

/// <summary>
/// Per-series detail (<c>GET /api/ledgers/{ledgerId}/reminders/{reminderId}</c>,
/// also returned by create/edit). Series metadata + <see cref="Kind"/>
/// ("bank" | "investment", derived from the template's
/// <see cref="Action"/>) + the template's full leg list. The SPA reconstructs
/// the editor draft from the legs (the same way it does for live transactions
/// via <c>/transactions/{id}/legs</c>).
/// </summary>
/// <param name="Memo">The template header's own memo: the whole transaction's where
/// the header is in play, which the series source edits.</param>
/// <param name="Tags">The template header's own tags, beside <paramref name="Memo"/>;
/// never null. Where the header is not in play (a one-split, a one-posting event) the
/// notes are on the legs (<see cref="ReminderLegDto.LegTags"/>) and this set is empty.
/// The editor seeds its Tags field from the stored sets, so a picked tag restates them
/// instead of replacing one it never showed.</param>
public sealed record ReminderDetail(
    Guid Id,
    string Kind,
    string? Payee,
    string? Memo,
    IReadOnlyList<string> Tags,
    string? CheckNumber,
    string? Action,
    string? Rrule,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly? NextDueDate,
    int? AutoCommitDaysBefore,
    bool IsActive,
    bool IsLoanReminder,
    string Origin,
    // The series' originating account (mig 125): the bank editor's source /
    // the investment brokerage. The SPA splits the template legs against it
    // (ADR-0049 adjust-at-post). Null on a custom / pre-125 series.
    Guid? SourceAccountId,
    IReadOnlyList<ReminderLegDto> Legs,

    // Mig 220. The series' estimate, when it asked for one.
    //
    // NOT applied to Legs. The reminder EDITOR prefills from this same leg list and
    // PATCHes it back, so writing the estimate into the legs would make opening the
    // editor and saving replace the user's template amount with a computed average —
    // permanently. The estimate travels beside the legs and each surface chooses.
    ReminderEstimate? Estimate = null);

/// <summary>One template leg in a <see cref="ReminderDetail"/>. Carries the
/// bank fields (account, amount, memo) plus the investment metadata
/// (security/quantity/unit-price/role) when present.</summary>
/// <param name="LegId">The leg's id. A bank edit names each line it keeps by the id of
/// that line's leg on the series source (<see cref="TransactionPosting.LegId"/>), and
/// the line keeps its memo and tags only through it. A rebuild gives the legs new ids,
/// so they are read from the edit's response or a fresh detail.</param>
/// <param name="LegMemo">This leg's own memo.</param>
/// <param name="LegTags">This leg's own tags, beside <paramref name="LegMemo"/>; never
/// null. Every leg carries its own set, a line's other side included; the editor
/// edits the series source's.</param>
public sealed record ReminderLegDto(
    Guid LegId,
    Guid AccountId,
    string AccountName,
    int PostingIndex,
    decimal Amount,
    string? LegMemo,
    IReadOnlyList<string> LegTags,
    Guid? SecurityId,
    string? SecurityTicker,
    decimal? Quantity,
    decimal? UnitPrice,
    string? PostingRole);
