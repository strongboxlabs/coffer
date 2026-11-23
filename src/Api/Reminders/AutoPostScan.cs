namespace Coffer.Api.Reminders;

/// <summary>
/// One occurrence the auto-post job may post, with the facts the policy needs.
/// </summary>
/// <param name="ReminderId">The series.</param>
/// <param name="OccurrenceDate">The slot. Also half of the idempotency key (mig 218).</param>
/// <param name="AutoCommitDaysBefore">
/// The series' acdays, already clamped to the configured ceiling.
/// </param>
/// <param name="IsInvestmentShape">
/// The series' template header carries an <c>action</c>, so firing it means holdings and
/// lots rather than a bank pair.
/// </param>
/// <param name="IsLoanReminder">
/// A managed loan series: the split is computed from the loan terms and the loan's
/// balance as of the occurrence's own date (ADR-0078, 2026-10-21), not read off the
/// template.
/// </param>
/// <param name="HasNoLines">
/// The series' template has no lines, so it names no account to post from and firing it
/// is refused.
/// </param>
/// <param name="UsesInactiveAccount">
/// A line of the series' template sits on an inactive account, so posting it would put a
/// new transaction on an account the user retired, and firing it is refused.
/// </param>
/// <param name="Payee">
/// The template's payee: the reminder's name as the reminders list shows it. Carried so a
/// reminder that cannot be posted can be named to the user.
/// </param>
/// <param name="TemplateAmountDoesNotFit">
/// A figure the fire would write, a template line's own or the loan split's or the
/// estimate's in its place, has more places than its currency allows, so firing it is
/// refused (ADR-0114 D15).
/// </param>
/// <param name="LoanAcrossCurrencies">
/// A managed loan series whose template posts to a real account in another currency than
/// the loan's, so firing it is refused and it can never post (ADR-0114 D13).
/// </param>
public sealed record AutoPostCandidate(
    Guid ReminderId,
    DateOnly OccurrenceDate,
    int AutoCommitDaysBefore,
    bool IsInvestmentShape,
    bool IsLoanReminder,
    bool HasNoLines,
    bool UsesInactiveAccount,
    string? Payee,
    bool TemplateAmountDoesNotFit,
    bool LoanAcrossCurrencies)
{
    /// <summary>Why the job will not post this occurrence, or null when it will
    /// (<see cref="AutoPostExclusion"/>).</summary>
    public string? ExcludedReason =>
        AutoPostExclusion.ReasonFor(
            IsInvestmentShape, HasNoLines, UsesInactiveAccount, TemplateAmountDoesNotFit, LoanAcrossCurrencies);
}

/// <summary>
/// Why the auto-post job leaves a series' occurrences for a person to post.
/// </summary>
/// <remarks>
/// <para>One predicate for the job and for every reader that says whether an
/// occurrence will post by itself, so the two can never disagree. Each reason is a
/// series property: the template's shape and accounts, not the date.</para>
/// <para>The order is the job's: a template with no lines first (firing it is refused
/// whatever its shape), then an investment shape (holdings and lots, which the
/// verbatim clone does not build), then a line on an inactive account (the fire refuses
/// it before it judges any figure), then a managed loan payment across currencies (refused
/// before its split is priced), then a figure that does not fit its currency. Each
/// reason is counted under its own key in the job's backlog event:
/// <c>template_has_no_lines</c>, <c>investment_series_not_supported</c>,
/// <c>template_uses_inactive_account</c>, <c>managed_loan_across_currencies</c> and
/// <c>template_amount_does_not_fit</c>.</para>
/// </remarks>
public static class AutoPostExclusion
{
    /// <summary>The template has no lines, so it names no account to post from.</summary>
    public const string NoLines = "no-lines";

    /// <summary>An investment-shape series.</summary>
    public const string Investment = "investment";

    /// <summary>A line of the template sits on an inactive account.</summary>
    public const string InactiveAccount = "inactive-account";

    /// <summary>A managed loan series whose paying account, or real escrow account, is in
    /// another currency than the loan's, so firing it is refused and it can never post
    /// (ADR-0114 D13).</summary>
    public const string LoanAcrossCurrencies = "loan-across-currencies";

    /// <summary>A figure the fire would write, a template line's own or the loan split's or
    /// the estimate's in its place, has more places than its currency allows, so firing it
    /// is refused until the reminder or the loan terms are edited (ADR-0114 D15). Judged
    /// by the fire's own predicate, so the scan and the fire agree.</summary>
    public const string TemplateAmountDoesNotFit = "template-amount-does-not-fit";

    /// <summary>The reason, or null when the job posts the series' occurrences.</summary>
    public static string? ReasonFor(
        bool isInvestmentShape, bool hasNoLines, bool usesInactiveAccount, bool templateAmountDoesNotFit,
        bool loanAcrossCurrencies) =>
        hasNoLines ? NoLines
        : isInvestmentShape ? Investment
        : usesInactiveAccount ? InactiveAccount
        : loanAcrossCurrencies ? LoanAcrossCurrencies
        : templateAmountDoesNotFit ? TemplateAmountDoesNotFit
        : null;
}

/// <summary>
/// What one scan for auto-postable occurrences found.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TooOldToPost"/> is separate from <see cref="Due"/> and is the reason the
/// scan looks further back than it is willing to post. A catch-up window has to be
/// bounded — a ledger down for a month must not wake up and post thirty occurrences —
/// but a bound whose effect is invisible reads to the operator as "nothing was due". So
/// the scan counts one window beyond what it will act on, and the count is published.
/// </para>
/// <para>
/// It is a COUNT, not a list. The occurrences are still open, still on the calendar, and
/// still actionable by hand; enumerating dates into a notification that may be delivered
/// to a webhook would put a ledger's payment schedule somewhere the ledger cannot reach.
/// </para>
/// </remarks>
public sealed record AutoPostScanResult(
    IReadOnlyList<AutoPostCandidate> Due,
    int TooOldToPost);
