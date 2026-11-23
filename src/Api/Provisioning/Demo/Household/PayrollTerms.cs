namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The household's pay, withholding, benefits and retirement-saving terms, and its
/// yearly tax settlement and IRA terms.
/// </summary>
/// <remarks>
/// Every figure and name here is an invented build parameter, kept in code only.
/// Amounts are cents; rates are percents.
/// </remarks>
public static class PayrollTerms
{
    // --- employers -------------------------------------------------------------

    /// <summary>Earner A's employer until the job change.</summary>
    public const string OldEmployerA = "Thornbury Logistics";

    /// <summary>Earner A's employer from the job change.</summary>
    public const string NewEmployerA = "Calloway Instruments";

    /// <summary>Earner B's employer.</summary>
    public const string EmployerB = "Ashgrove Analytics";

    /// <summary>The bank's payee string for a payroll deposit from
    /// <paramref name="employer"/>.</summary>
    public static string BankPayrollPayee(string employer) => employer.ToUpperInvariant() + " PAYROLL";

    /// <summary>The bank's memo string on a payroll deposit.</summary>
    public const string BankPayrollMemo = "DIRECT DEP PPD";

    // --- the job change (L14, L15) ---------------------------------------------

    /// <summary>The life month of A's last paycheck from the old employer, which
    /// carries a paid-time-off payout; the month-end pay date that follows is missed.</summary>
    public const int LastOldPaycheckMonth = 14;

    /// <summary>The life month of A's first paycheck from the new employer, with the
    /// raise and a sign-on bonus.</summary>
    public const int FirstNewPaycheckMonth = HouseholdTerms.NewPlanAOpens;

    /// <summary>The paid-time-off payout on A's last old paycheck.</summary>
    public const long PtoPayout = 2_350_00L;

    /// <summary>The sign-on bonus on A's first new paycheck.</summary>
    public const long SignOnBonus = 5_000_00L;

    // --- salaries ----------------------------------------------------------------

    /// <summary>Earner A's yearly salary before the job change.</summary>
    public const long SalaryA = 84_000_00L;

    /// <summary>Earner A's raise at the job change, as a multiplier.</summary>
    public const decimal RaiseAtJobChange = 1.12m;

    /// <summary>Earner A is paid twice a month.</summary>
    public const int PaychecksPerYearA = 24;

    /// <summary>Earner B's yearly salary at the window start.</summary>
    public const long SalaryB = 68_000_00L;

    /// <summary>Earner B's raise each January, as a multiplier.</summary>
    public const decimal JanuaryRaiseB = 1.03m;

    /// <summary>Earner B is paid every other Friday.</summary>
    public const int PaychecksPerYearB = 26;

    /// <summary>A Friday that fixes B's biweekly cycle, whatever the window.</summary>
    public static readonly DateOnly PayCycleAnchorB = new(2021, 1, 1);

    /// <summary>B's December bonus, as a percent of the year's salary.</summary>
    public const decimal DecemberBonusPercentB = 6m;

    /// <summary>The nominal day of December B's bonus is paid.</summary>
    public const int DecemberBonusDay = 12;

    // --- withholding ---------------------------------------------------------------

    /// <summary>Federal withholding on regular pay.</summary>
    public const decimal FederalPercent = 12m;

    /// <summary>Federal withholding on supplemental pay (bonuses, payouts).</summary>
    public const decimal SupplementalFederalPercent = 22m;

    /// <summary>State withholding.</summary>
    public const decimal StatePercent = 4.5m;

    /// <summary>Social Security.</summary>
    public const decimal SocialSecurityPercent = 6.2m;

    /// <summary>Medicare.</summary>
    public const decimal MedicarePercent = 1.45m;

    // --- benefits ------------------------------------------------------------------

    /// <summary>B's health premium per paycheck before the high-deductible plan.</summary>
    public const long HealthPremiumB = 140_00L;

    /// <summary>B's premium on the high-deductible plan, from the HSA's month.</summary>
    public const long HdhpPremiumB = 95_00L;

    /// <summary>B's premium on family cover, from the birth.</summary>
    public const long FamilyPremiumB = 210_00L;

    /// <summary>The life month cover moves to family.</summary>
    public const int FamilyCoverFrom = HouseholdTerms.PaymentPlanOpens;

    /// <summary>The leg tag on the health premium line.</summary>
    public const string BenefitsTag = "Benefits";

    /// <summary>B's HSA contribution per paycheck, from the HSA's month.</summary>
    public const long HsaPerPaycheckB = 150_00L;

    // --- retirement ----------------------------------------------------------------

    /// <summary>A's deferral, as a percent of regular pay.</summary>
    public const decimal DeferralPercentA = 6m;

    /// <summary>B's deferral, as a percent of regular pay.</summary>
    public const decimal DeferralPercentB = 8m;

    /// <summary>The old plan matches this percent of the deferral…</summary>
    public const decimal OldPlanMatchRate = 50m;

    /// <summary>…on deferrals up to this percent of pay.</summary>
    public const decimal OldPlanMatchCap = 6m;

    /// <summary>The new plan matches this percent of the deferral…</summary>
    public const decimal NewPlanMatchRate = 100m;

    /// <summary>…on deferrals up to this percent of pay.</summary>
    public const decimal NewPlanMatchCap = 4m;

    /// <summary>B's plan matches this percent of the deferral…</summary>
    public const decimal PlanBMatchRate = 100m;

    /// <summary>…on deferrals up to this percent of pay.</summary>
    public const decimal PlanBMatchCap = 4m;

    /// <summary>The IRA contribution made each April after the rollover.</summary>
    public const long IraContribution = 6_500_00L;

    /// <summary>The nominal day of April it is sent.</summary>
    public const int IraContributionDay = 10;

    // --- tax settlement --------------------------------------------------------------

    /// <summary>The calendar month the federal refund arrives in.</summary>
    public const int FederalRefundMonth = 3;

    /// <summary>The smallest federal refund.</summary>
    public const long FederalRefundMin = 400_00L;

    /// <summary>The largest federal refund.</summary>
    public const long FederalRefundMax = 1_900_00L;

    /// <summary>The calendar month the state return settles in.</summary>
    public const int StateSettlementMonth = 4;

    /// <summary>The chance a year's state return is a refund rather than a balance due.</summary>
    public const decimal StateRefundChance = 0.5m;

    /// <summary>The smallest state settlement.</summary>
    public const long StateSettlementMin = 60_00L;

    /// <summary>The largest state settlement.</summary>
    public const long StateSettlementMax = 450_00L;
}
