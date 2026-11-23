namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Both earners' pay: every
/// paycheck as one bank-route multi-split on Joint Checking whose origin is the net
/// pay, B's December bonus, and each plan's employer match on the investment route.
/// </summary>
/// <remarks>
/// <para><b>Pay dates.</b> A is paid on the 15th and the last day of the month, B
/// every other Friday on a cycle fixed by <see cref="PayrollTerms.PayCycleAnchorB"/>;
/// a date markets are shut moves to the business day before.</para>
///
/// <para><b>Lines</b> (each line's own <c>desc</c> is its label): gross pay on Salary
/// &gt; Paycheck; any supplemental pay on Salary &gt; Bonus; federal and state
/// withholding on pay less the deferral and the pre-tax deductions; Social Security
/// and Medicare on pay less the pre-tax deductions only, so the deferral does not
/// reduce them; B's health premium with the leg tag
/// <see cref="PayrollTerms.BenefitsTag"/>; the 401(k) deferral as a transfer leg to
/// the earner's current plan; from the HSA's month, B's HSA contribution as a
/// transfer leg. Supplemental pay is withheld at the supplemental federal rate and
/// takes no deferral. At most eight lines.</para>
///
/// <para><b>The job change.</b> A's last paycheck from the old employer carries a
/// paid-time-off payout, and the month-end pay date after it is missed. The first
/// paycheck from the new employer carries the raise and a sign-on bonus, and the
/// deferral moves to the new plan.</para>
///
/// <para><b>Employer match.</b> On each payday with a deferral, an <c>inc</c> row on
/// the plan: a zero <c>sec</c> split on its target-date holding and an <c>inc</c>
/// split on Salary &gt; Employer Matching. <see cref="InvestmentsPattern"/> invests
/// the deferral and the match the next business day, so this pattern posts before it.</para>
/// </remarks>
public sealed class PayrollPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "payroll";

    /// <summary>A paycheck row's memo.</summary>
    public const string PaycheckMemo = "Direct deposit";

    /// <summary>The bonus row's memo.</summary>
    public const string BonusMemo = "Year-end bonus";

    /// <summary>A match row's memo.</summary>
    public const string MatchMemo = "Employer match";

    /// <summary>The gross pay line's label.</summary>
    public const string GrossLabel = "Gross pay";

    /// <summary>The paid-time-off payout line's label.</summary>
    public const string PtoLabel = "Paid time off payout";

    /// <summary>The sign-on bonus line's label.</summary>
    public const string SignOnLabel = "Sign-on bonus";

    /// <summary>The December bonus line's label.</summary>
    public const string BonusLabel = "Bonus";

    /// <summary>The federal withholding line's label.</summary>
    public const string FederalLabel = "Federal withholding";

    /// <summary>The state withholding line's label.</summary>
    public const string StateLabel = "State withholding";

    /// <summary>The Social Security line's label.</summary>
    public const string SocialSecurityLabel = "Social Security";

    /// <summary>The Medicare line's label.</summary>
    public const string MedicareLabel = "Medicare";

    /// <summary>The health premium line's label.</summary>
    public const string HealthLabel = "Health premium";

    /// <summary>The deferral line's label.</summary>
    public const string DeferralLabel = "401(k) deferral";

    /// <summary>The HSA line's label.</summary>
    public const string HsaLabel = "HSA contribution";

    private readonly DemoMarket _market;

    /// <summary>The pattern over the Demo's market (the match rows name a holding).</summary>
    public PayrollPattern(DemoMarket market) => _market = market ?? throw new ArgumentNullException(nameof(market));

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 10;

    /// <summary>The pay of one paycheck, before it becomes lines.</summary>
    /// <param name="Regular">Regular gross pay, deferred and withheld at the regular rate.</param>
    /// <param name="Supplemental">Supplemental pay on Salary &gt; Bonus.</param>
    /// <param name="DeferralPercent">The earner's deferral percent of regular pay.</param>
    /// <param name="Premium">The pre-tax health premium.</param>
    /// <param name="Hsa">The pre-tax HSA contribution.</param>
    public readonly record struct Pay(long Regular, long Supplemental, decimal DeferralPercent, long Premium, long Hsa)
    {
        /// <summary>The deferral: its percent of regular pay, to the cent.</summary>
        public long Deferral => DemoAmortization.RoundCents(Regular * DeferralPercent / 100m);

        /// <summary>Pay less the pre-tax deductions: the Social Security and
        /// Medicare base. The deferral is not taken off.</summary>
        public long FicaBase => Regular + Supplemental - Premium - Hsa;

        /// <summary>Federal withholding: the regular rate on regular pay less the
        /// deferral and the pre-tax deductions, the supplemental rate on supplemental pay.</summary>
        public long Federal =>
            DemoAmortization.RoundCents((Regular - Deferral - Premium - Hsa) * PayrollTerms.FederalPercent / 100m)
            + DemoAmortization.RoundCents(Supplemental * PayrollTerms.SupplementalFederalPercent / 100m);

        /// <summary>State withholding on pay less the deferral and the pre-tax deductions.</summary>
        public long State => DemoAmortization.RoundCents((FicaBase - Deferral) * PayrollTerms.StatePercent / 100m);

        /// <summary>Social Security on the FICA base.</summary>
        public long SocialSecurity => DemoAmortization.RoundCents(FicaBase * PayrollTerms.SocialSecurityPercent / 100m);

        /// <summary>Medicare on the FICA base.</summary>
        public long Medicare => DemoAmortization.RoundCents(FicaBase * PayrollTerms.MedicarePercent / 100m);

        /// <summary>What lands in checking.</summary>
        public long Net => Regular + Supplemental - Federal - State - SocialSecurity - Medicare - Premium - Deferral - Hsa;
    }

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        new Run(this, calendar, book, new DemoInvestmentDesk(book, _market)).Post();
    }

    /// <summary>A's yearly salary in life month <paramref name="lifeMonth"/>.</summary>
    public static long SalaryAIn(int lifeMonth) =>
        lifeMonth < PayrollTerms.FirstNewPaycheckMonth
            ? PayrollTerms.SalaryA
            : DemoAmortization.RoundCents(PayrollTerms.SalaryA * PayrollTerms.RaiseAtJobChange);

    /// <summary>B's yearly salary on <paramref name="date"/>: the starting salary
    /// raised once for each January in the window up to it.</summary>
    public static long SalaryBOn(DemoCalendar calendar, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var salary = (decimal)PayrollTerms.SalaryB;
        var through = calendar.LifeMonthOf(date);
        for (var month = 1; month <= through; month++)
        {
            if (calendar.MonthStart(month).Month == 1) salary *= PayrollTerms.JanuaryRaiseB;
        }
        return DemoAmortization.RoundCents(salary);
    }

    /// <summary>B's health premium per paycheck in life month <paramref name="lifeMonth"/>.</summary>
    public static long PremiumBIn(int lifeMonth) =>
        lifeMonth >= PayrollTerms.FamilyCoverFrom ? PayrollTerms.FamilyPremiumB
        : lifeMonth >= HouseholdTerms.HsaOpens ? PayrollTerms.HdhpPremiumB
        : PayrollTerms.HealthPremiumB;

    /// <summary>The match on a deferral: the plan's rate on deferrals up to its cap.</summary>
    public static long Match(long regular, decimal deferralPercent, decimal matchRate, decimal matchCap)
    {
        var matched = deferralPercent < matchCap ? deferralPercent : matchCap;
        return DemoAmortization.RoundCents(regular * matched / 100m * matchRate / 100m);
    }

    // One run of the pattern over one book.
    private sealed class Run(PayrollPattern pattern, DemoCalendar calendar, DemoBook book, DemoInvestmentDesk desk)
    {
        private readonly string _checking = book.Account(DemoAccountRoles.JointChecking).Id;
        private readonly string _paycheck = book.Base.Category(MdCategoryType.Income, "Salary", "Paycheck");
        private readonly string _bonus = book.Base.Category(MdCategoryType.Income, "Salary", "Bonus");
        private readonly string _matching = book.Base.Category(MdCategoryType.Income, "Salary", "Employer Matching");
        private readonly string _federal = book.Base.Category(MdCategoryType.Expense, "Tax", "Federal Income Tax");
        private readonly string _state = book.Base.Category(MdCategoryType.Expense, "Tax", "State Income Tax");
        private readonly string _socialSecurity = book.Base.Category(MdCategoryType.Expense, "Tax", "Social Security Tax");
        private readonly string _medicare = book.Base.Category(MdCategoryType.Expense, "Tax", "Medicare Tax");
        private readonly string _health = book.Base.Category(MdCategoryType.Expense, "Insurance", "Health");

        public void Post()
        {
            EarnerA();
            EarnerB();
            DecemberBonuses();
        }

        // Semi-monthly; the job change at the old plan's last month.
        private void EarnerA()
        {
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                foreach (var nominalDay in new[] { 15, 31 })
                {
                    var monthEnd = nominalDay == 31;
                    if (month == PayrollTerms.LastOldPaycheckMonth && monthEnd) continue; // the missed pay date
                    var date = DemoCalendar.OnOrPreviousBusinessDay(calendar.DayInMonth(month, nominalDay));
                    if (!calendar.Contains(date)) continue;

                    var old = month <= PayrollTerms.LastOldPaycheckMonth;
                    var (supplemental, label) = (month, monthEnd) switch
                    {
                        (PayrollTerms.LastOldPaycheckMonth, false) => (PayrollTerms.PtoPayout, PtoLabel),
                        (PayrollTerms.FirstNewPaycheckMonth, false) => (PayrollTerms.SignOnBonus, SignOnLabel),
                        _ => (0L, null),
                    };
                    var pay = new Pay(
                        DemoAmortization.RoundCents((decimal)SalaryAIn(month) / PayrollTerms.PaychecksPerYearA),
                        supplemental, PayrollTerms.DeferralPercentA, Premium: 0, Hsa: 0);
                    var plan = old ? DemoAccountRoles.OldPlanA : DemoAccountRoles.NewPlanA;
                    var employer = old ? PayrollTerms.OldEmployerA : PayrollTerms.NewEmployerA;
                    Paycheck(date, employer, PaycheckMemo, pay, label, plan);
                    PostMatch(date, employer, plan, Match(pay.Regular, pay.DeferralPercent,
                        old ? PayrollTerms.OldPlanMatchRate : PayrollTerms.NewPlanMatchRate,
                        old ? PayrollTerms.OldPlanMatchCap : PayrollTerms.NewPlanMatchCap));
                }
            }
        }

        // Every other Friday, on a cycle fixed outside the window.
        private void EarnerB()
        {
            var first = calendar.WindowStart;
            var offset = (first.DayNumber - PayrollTerms.PayCycleAnchorB.DayNumber) % 14;
            if (offset < 0) offset += 14;
            var friday = offset == 0 ? first : first.AddDays(14 - offset);

            for (; friday <= calendar.AsOf; friday = friday.AddDays(14))
            {
                var date = DemoCalendar.OnOrPreviousBusinessDay(friday);
                if (!calendar.Contains(date)) continue;
                var month = calendar.LifeMonthOf(date);
                var pay = new Pay(
                    DemoAmortization.RoundCents((decimal)SalaryBOn(calendar, date) / PayrollTerms.PaychecksPerYearB),
                    Supplemental: 0, PayrollTerms.DeferralPercentB, PremiumBIn(month),
                    month >= HouseholdTerms.HsaOpens ? PayrollTerms.HsaPerPaycheckB : 0);
                Paycheck(date, PayrollTerms.EmployerB, PaycheckMemo, pay, supplementalLabel: null, DemoAccountRoles.PlanB);
                PostMatch(date, PayrollTerms.EmployerB, DemoAccountRoles.PlanB,
                    Match(pay.Regular, pay.DeferralPercent, PayrollTerms.PlanBMatchRate, PayrollTerms.PlanBMatchCap));
            }
        }

        // B's bonus each December: supplemental pay only, no deferral, no premium.
        private void DecemberBonuses()
        {
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                if (calendar.MonthStart(month).Month != 12) continue;
                var date = DemoCalendar.OnOrPreviousBusinessDay(calendar.DayInMonth(month, PayrollTerms.DecemberBonusDay));
                if (!calendar.Contains(date)) continue;
                var bonus = DemoAmortization.RoundCents(
                    SalaryBOn(calendar, date) * PayrollTerms.DecemberBonusPercentB / 100m);
                Paycheck(date, PayrollTerms.EmployerB, BonusMemo,
                    new Pay(Regular: 0, bonus, DeferralPercent: 0m, Premium: 0, Hsa: 0), BonusLabel, planRole: null);
            }
        }

        private void Paycheck(DateOnly date, string employer, string memo, Pay pay, string? supplementalLabel, string? planRole)
        {
            var splits = new List<DemoSplit>();
            void Line(string accountId, long amountOnAccount, string label, string? tags = null)
            {
                if (amountOnAccount != 0) splits.Add(DemoSplit.Bank(accountId, amountOnAccount, label, tags));
            }

            Line(_paycheck, -pay.Regular, GrossLabel);
            Line(_bonus, -pay.Supplemental, supplementalLabel ?? BonusLabel);
            Line(_federal, pay.Federal, FederalLabel);
            Line(_state, pay.State, StateLabel);
            Line(_socialSecurity, pay.SocialSecurity, SocialSecurityLabel);
            Line(_medicare, pay.Medicare, MedicareLabel);
            Line(_health, pay.Premium, HealthLabel, PayrollTerms.BenefitsTag);
            if (planRole is not null) Line(book.Account(planRole).Id, pay.Deferral, DeferralLabel);
            if (pay.Hsa != 0) Line(book.Account(DemoAccountRoles.FamilyHsa).Id, pay.Hsa, HsaLabel);

            if (pay.Net <= 0) throw new InvalidOperationException($"a paycheck on {date:yyyy-MM-dd} nets nothing");
            book.Post(pattern, new DemoTxn
            {
                AccountId = _checking,
                Date = date,
                Description = employer,
                Memo = memo,
                BankPayee = PayrollTerms.BankPayrollPayee(employer),
                BankMemo = PayrollTerms.BankPayrollMemo,
                Splits = splits,
            });
        }

        private void PostMatch(DateOnly date, string employer, string planRole, long match)
        {
            if (match <= 0) return;
            book.Post(pattern, desk.Inc(planRole, DemoSecurities.TargetDate, date, match, _matching, employer, MatchMemo));
        }
    }
}
