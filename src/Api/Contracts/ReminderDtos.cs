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
    string Origin);

/// <summary>
/// One entry in the upcoming agenda/calendar (ADR-0047):
/// <c>GET /api/ledgers/{ledgerId}/reminders/upcoming?from&amp;to</c>. One of a
/// <c>scheduled</c> occurrence (already materialized — a committed header), a
/// <c>skipped</c> slot (a read-only trail of a skipped/catch-up occurrence,
/// ADR-0049 D11), or a <c>reminder</c> slot (computed from the series' RRULE,
/// not yet acted). v1 is reminder-driven (series occurrences); a general
/// future-transaction calendar is a later enhancement.
/// </summary>
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
    DateOnly? SeriesNextDue);

/// <summary>
/// Body for <c>POST /api/ledgers/{ledgerId}/reminders/{id}/fire</c> — the
/// occurrence date to materialize into a committed transaction (ADR-0047 D5).
/// </summary>
public sealed class FireReminderRequest
{
    public DateOnly OccurrenceDate { get; init; }
}

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
    public string? Memo { get; init; }
    public string? CheckNumber { get; init; }
    public DateOnly? PostedDate { get; init; }
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

/// <summary>Response for fire — the committed header materialized (or, when the
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
    public string? Memo { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>The register account every posting's source-side leg goes to;
    /// must be a bank-shape account in this ledger.</summary>
    public Guid SourceAccountId { get; init; }
    public IReadOnlyList<TransactionPosting> Postings { get; init; } = Array.Empty<TransactionPosting>();
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
    public string? Memo { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>When supplied, the template's legs are dropped + rebuilt from
    /// this list (the template has no lots/overrides, so a clean rebuild is
    /// correct — no LegId reconcile needed). Null = legs untouched.</summary>
    public PatchReminderPostings? Postings { get; init; }
}

/// <summary>Replace-all postings sub-shape for <see cref="EditReminderRequest"/>.</summary>
public sealed class PatchReminderPostings
{
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
/// Per-series detail (<c>GET /api/ledgers/{ledgerId}/reminders/{reminderId}</c>,
/// also returned by create/edit). Series metadata + <see cref="Kind"/>
/// ("bank" | "investment", derived from the template's
/// <see cref="Action"/>) + the template's full leg list. The SPA reconstructs
/// the editor draft from the legs (the same way it does for live transactions
/// via <c>/transactions/{id}/legs</c>).
/// </summary>
public sealed record ReminderDetail(
    Guid Id,
    string Kind,
    string? Payee,
    string? Memo,
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
    IReadOnlyList<ReminderLegDto> Legs);

/// <summary>One template leg in a <see cref="ReminderDetail"/>. Carries the
/// bank fields (account, amount, memo) plus the investment metadata
/// (security/quantity/unit-price/role) when present.</summary>
public sealed record ReminderLegDto(
    Guid AccountId,
    string AccountName,
    int PostingIndex,
    decimal Amount,
    string? LegMemo,
    Guid? SecurityId,
    string? SecurityTicker,
    decimal? Quantity,
    decimal? UnitPrice,
    string? PostingRole);
