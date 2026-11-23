using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// Pay, withholding, benefits and retirement saving: both earners'
/// paychecks as balanced bank-route multi-splits on Joint Checking, the job change,
/// the December bonus, the employer match on the investment route and the buys that
/// follow it, the yearly IRA contribution, the spring tax settlement, and the HSA's
/// medical spending.
/// </summary>
public sealed class DemoPayrollTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    private static readonly Lazy<DemoSimulation> Sim =
        new(() => DemoExportGenerator.Run(DemoSpec.Full(AsOf), DemoExportGenerator.Patterns));

    private static readonly Lazy<DemoBuild> Build = new(() => DemoExportGenerator.Generate(DemoSpec.Full(AsOf)));

    private static DemoBook Book => Sim.Value.Book;

    private static readonly string[] Employers =
        [PayrollTerms.OldEmployerA, PayrollTerms.NewEmployerA, PayrollTerms.EmployerB];

    // --- shape -------------------------------------------------------------------------

    [Fact]
    public void Every_paycheck_is_a_balanced_multisplit_under_the_cap()
    {
        var checking = Book.Account(DemoAccountRoles.JointChecking).Id;
        var paychecks = Paychecks().ToList();
        Assert.NotEmpty(paychecks);

        foreach (var paycheck in paychecks)
        {
            Assert.Equal(checking, paycheck.AccountId);
            Assert.InRange(paycheck.Splits.Count, 5, 8);
            Assert.True(paycheck.Splits.Count <= DemoBook.MaxSplits);
            Assert.All(paycheck.Splits, s => Assert.Equal(-s.SplitAmount, s.ParentAmount));

            var net = paycheck.Splits.Sum(s => s.ParentAmount);
            var pay = -paycheck.Splits.Where(s => IsSalary(s.AccountId)).Sum(s => s.SplitAmount);
            var deductions = paycheck.Splits.Where(s => !IsSalary(s.AccountId)).Sum(s => s.SplitAmount);
            Assert.True(net > 0, $"the paycheck on {paycheck.Date} nets nothing");
            Assert.Equal(pay - deductions, net);
            Assert.All(paycheck.Splits, s => Assert.False(string.IsNullOrEmpty(s.Description)));
        }

        // The same in the written rows: each balances to zero and none passes the
        // importer's split cap.
        using var export = Build.Value.ToExport();
        var written = export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From)
            .Where(t => t.AcctId == checking && Employers.Contains(t.Description)).ToList();
        Assert.Equal(paychecks.Count, written.Count);
        Assert.All(written, t =>
        {
            Assert.True(t.Splits.Count <= 32);
            Assert.Equal(0L, t.Splits.Sum(s => s.ParentAmount + s.SplitAmount));
        });
    }

    [Fact]
    public void The_deferral_reduces_income_tax_withholding_but_not_FICA()
    {
        var deferred = Paychecks().Where(p => Line(p, PayrollPattern.DeferralLabel) != 0).ToList();
        Assert.NotEmpty(deferred);

        foreach (var paycheck in deferred)
        {
            var gross = -Line(paycheck, PayrollPattern.GrossLabel);
            var supplemental = -Line(paycheck, PayrollPattern.PtoLabel) - Line(paycheck, PayrollPattern.SignOnLabel);
            var deferral = Line(paycheck, PayrollPattern.DeferralLabel);
            var pretax = Line(paycheck, PayrollPattern.HealthLabel) + Line(paycheck, PayrollPattern.HsaLabel);
            var ficaBase = gross + supplemental - pretax;

            Assert.Equal(Cents(ficaBase * 6.2m / 100m), Line(paycheck, PayrollPattern.SocialSecurityLabel));
            Assert.Equal(Cents(ficaBase * 1.45m / 100m), Line(paycheck, PayrollPattern.MedicareLabel));
            Assert.NotEqual(Cents((ficaBase - deferral) * 6.2m / 100m), Line(paycheck, PayrollPattern.SocialSecurityLabel));
            Assert.Equal(
                Cents((gross - deferral - pretax) * 12m / 100m) + Cents(supplemental * 22m / 100m),
                Line(paycheck, PayrollPattern.FederalLabel));
            Assert.Equal(Cents((ficaBase - deferral) * 4.5m / 100m), Line(paycheck, PayrollPattern.StateLabel));
        }

        // A spot check worked from the constants alone: B's last paycheck, on the
        // high-deductible or family plan with an HSA line.
        var last = Paychecks().Last(p => p.Description == PayrollTerms.EmployerB && p.Memo == PayrollPattern.PaycheckMemo);
        var bGross = Cents(PayrollPattern.SalaryBOn(Book.Calendar, last.Date) / 26m);
        Assert.Equal(bGross, -Line(last, PayrollPattern.GrossLabel));
        Assert.Equal(Cents((bGross - 210_00L - 150_00L) * 0.062m), Line(last, PayrollPattern.SocialSecurityLabel));
        Assert.Equal(Cents(bGross * 0.08m), Line(last, PayrollPattern.DeferralLabel));
    }

    // --- schedules -----------------------------------------------------------------------

    [Fact]
    public void Earner_A_is_paid_twice_a_month_and_changes_job_with_one_missed_pay_date()
    {
        var calendar = Book.Calendar;
        var expected = new List<DateOnly>();
        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            foreach (var day in new[] { 15, 31 })
            {
                if (month == PayrollTerms.LastOldPaycheckMonth && day == 31) continue;
                var date = DemoCalendar.OnOrPreviousBusinessDay(calendar.DayInMonth(month, day));
                if (calendar.Contains(date)) expected.Add(date);
            }
        }

        var paychecks = Paychecks()
            .Where(p => p.Description is PayrollTerms.OldEmployerA or PayrollTerms.NewEmployerA)
            .OrderBy(p => p.Date).ToList();
        Assert.Equal(expected, paychecks.Select(p => p.Date));

        var oldPlan = Book.Account(DemoAccountRoles.OldPlanA).Id;
        var newPlan = Book.Account(DemoAccountRoles.NewPlanA).Id;
        foreach (var paycheck in paychecks)
        {
            var month = calendar.LifeMonthOf(paycheck.Date);
            var old = month <= PayrollTerms.LastOldPaycheckMonth;
            Assert.Equal(old ? PayrollTerms.OldEmployerA : PayrollTerms.NewEmployerA, paycheck.Description);
            Assert.Equal(old ? oldPlan : newPlan,
                paycheck.Splits.Single(s => s.Description == PayrollPattern.DeferralLabel).AccountId);
        }

        var bonus = Book.Base.Category(MdCategoryType.Income, "Salary", "Bonus");
        var lastOld = paychecks.Last(p => p.Description == PayrollTerms.OldEmployerA);
        var firstNew = paychecks.First(p => p.Description == PayrollTerms.NewEmployerA);
        Assert.Equal(PayrollTerms.LastOldPaycheckMonth, calendar.LifeMonthOf(lastOld.Date));
        Assert.Equal(-PayrollTerms.PtoPayout, lastOld.Splits.Single(s => s.AccountId == bonus).SplitAmount);
        Assert.Equal(PayrollTerms.FirstNewPaycheckMonth, calendar.LifeMonthOf(firstNew.Date));
        Assert.Equal(-PayrollTerms.SignOnBonus, firstNew.Splits.Single(s => s.AccountId == bonus).SplitAmount);
        Assert.True(-Line(firstNew, PayrollPattern.GrossLabel) > -Line(lastOld, PayrollPattern.GrossLabel));
        Assert.Equal(Cents(PayrollTerms.SalaryA * 1.12m / 24m), -Line(firstNew, PayrollPattern.GrossLabel));
        // The month-end after the last old paycheck is missed: nothing until the new job.
        Assert.Equal(firstNew, paychecks[paychecks.IndexOf(lastOld) + 1]);
    }

    [Fact]
    public void Earner_B_is_paid_every_other_Friday_with_benefits_that_change_at_the_HSA_and_the_birth()
    {
        var calendar = Book.Calendar;
        var hsa = Book.Account(DemoAccountRoles.FamilyHsa).Id;
        var paychecks = Paychecks()
            .Where(p => p.Description == PayrollTerms.EmployerB && p.Memo == PayrollPattern.PaycheckMemo)
            .OrderBy(p => p.Date).ToList();

        var fridays = new List<DateOnly>();
        for (var friday = PayrollTerms.PayCycleAnchorB; friday <= AsOf; friday = friday.AddDays(14))
        {
            var date = DemoCalendar.OnOrPreviousBusinessDay(friday);
            if (calendar.Contains(date)) fridays.Add(date);
        }
        Assert.Equal(fridays, paychecks.Select(p => p.Date));

        foreach (var paycheck in paychecks)
        {
            var month = calendar.LifeMonthOf(paycheck.Date);
            var health = paycheck.Splits.Single(s => s.Description == PayrollPattern.HealthLabel);
            Assert.Equal(PayrollTerms.BenefitsTag, health.Tags);
            Assert.Equal(month >= 40 ? 210_00L : month >= 36 ? 95_00L : 140_00L, health.SplitAmount);

            var hsaLine = paycheck.Splits.SingleOrDefault(s => s.Description == PayrollPattern.HsaLabel);
            if (month >= HouseholdTerms.HsaOpens)
            {
                Assert.NotNull(hsaLine);
                Assert.Equal(hsa, hsaLine.AccountId);
                Assert.Equal(150_00L, hsaLine.SplitAmount);
            }
            else
            {
                Assert.Null(hsaLine);
            }
        }

        // The gross steps up once each January, and only then.
        for (var i = 1; i < paychecks.Count; i++)
        {
            var before = -Line(paychecks[i - 1], PayrollPattern.GrossLabel);
            var after = -Line(paychecks[i], PayrollPattern.GrossLabel);
            var januaryCrossed = paychecks[i].Date.Year != paychecks[i - 1].Date.Year;
            if (januaryCrossed) Assert.True(after > before, $"no raise by {paychecks[i].Date}");
            else Assert.Equal(before, after);
        }
    }

    [Fact]
    public void B_gets_a_December_bonus_withheld_at_the_supplemental_rate_with_no_deferral()
    {
        var calendar = Book.Calendar;
        var bonuses = Paychecks().Where(p => p.Memo == PayrollPattern.BonusMemo).ToList();
        var decembers = Enumerable.Range(0, DemoCalendar.CurrentLifeMonth + 1)
            .Where(m => calendar.MonthStart(m).Month == 12)
            .Count(m => DemoCalendar.OnOrPreviousBusinessDay(calendar.DayInMonth(m, PayrollTerms.DecemberBonusDay)) <= AsOf);
        Assert.Equal(decembers, bonuses.Count);

        foreach (var bonus in bonuses)
        {
            Assert.Equal(12, bonus.Date.Month);
            Assert.Equal(
                [PayrollPattern.BonusLabel, PayrollPattern.FederalLabel, PayrollPattern.StateLabel,
                 PayrollPattern.SocialSecurityLabel, PayrollPattern.MedicareLabel],
                bonus.Splits.Select(s => s.Description));
            var amount = -Line(bonus, PayrollPattern.BonusLabel);
            Assert.Equal(Cents(PayrollPattern.SalaryBOn(calendar, bonus.Date) * 0.06m), amount);
            Assert.Equal(Cents(amount * 0.22m), Line(bonus, PayrollPattern.FederalLabel));
        }
    }

    // --- the match and what follows --------------------------------------------------------

    [Fact]
    public void Each_deferral_is_matched_on_the_investment_route_and_invested_the_next_business_day()
    {
        var matching = Book.Base.Category(MdCategoryType.Income, "Salary", "Employer Matching");
        var matches = Book.Transactions
            .Where(t => t.Pattern == PayrollPattern.PatternName && t.Route == DemoRoute.Investment).ToList();
        Assert.NotEmpty(matches);

        foreach (var match in matches)
        {
            var plan = Book.AccountById(match.AccountId)!;
            Assert.Equal("inc", match.InvestTxnType);
            Assert.Equal("xfrtp_miscincexp", match.XferType);
            Assert.Equal(2, match.Splits.Count);
            var sec = match.Splits[0];
            Assert.Equal("sec", sec.SplitType);
            Assert.Equal(DemoHoldings.Holding(Book, plan.Role, DemoSecurities.TargetDate).Id, sec.AccountId);
            Assert.Equal(0L, sec.ParentAmount);
            Assert.Equal(0L, sec.SplitAmount);
            var inc = match.Splits[1];
            Assert.Equal("inc", inc.SplitType);
            Assert.Equal(matching, inc.AccountId);
            Assert.True(inc.ParentAmount > 0);
            Assert.Equal(-inc.ParentAmount, inc.SplitAmount);
        }

        foreach (var paycheck in Paychecks().Where(p => Line(p, PayrollPattern.DeferralLabel) != 0))
        {
            var planId = paycheck.Splits.Single(s => s.Description == PayrollPattern.DeferralLabel).AccountId;
            var plan = Book.AccountById(planId)!;
            var gross = -Line(paycheck, PayrollPattern.GrossLabel);
            var (rate, cap) = plan.Role switch
            {
                DemoAccountRoles.OldPlanA => (50m, 6m),
                DemoAccountRoles.NewPlanA => (100m, 4m),
                _ => (100m, 4m),
            };
            var deferralPercent = plan.Role == DemoAccountRoles.PlanB ? 8m : 6m;
            var expected = Cents(gross * (deferralPercent < cap ? deferralPercent : cap) / 100m * rate / 100m);
            Assert.Contains(matches, m => m.AccountId == planId && m.Date == paycheck.Date && m.Splits[1].ParentAmount == expected);

            var buyDay = DemoCalendar.NextBusinessDayAfter(paycheck.Date);
            if (buyDay > AsOf || !plan.IsActiveOn(buyDay)) continue;
            var bought = Book.Transactions
                .Where(t => t.AccountId == planId && t.Date == buyDay && t.InvestTxnType == "buy")
                .Select(t => Book.AccountById(t.Splits[0].AccountId)!.CurrencyId).ToHashSet();
            Assert.Contains(Sim.Value.Market.CurrencyId(DemoSecurities.TargetDate), bought);
            if (plan.Role == DemoAccountRoles.PlanB)
                Assert.Contains(Sim.Value.Market.CurrencyId(DemoSecurities.StableValue), bought);
        }
    }

    [Fact]
    public void The_IRA_takes_a_contribution_each_April_after_the_rollover_and_buys_its_mix()
    {
        var calendar = Book.Calendar;
        var ira = Book.Account(DemoAccountRoles.RolloverIra).Id;
        var contributions = Book.Transactions.Where(t => t.Pattern == IraContributionPattern.PatternName).ToList();
        Assert.Equal(IraContributionPattern.ContributionMonths(calendar).Count(), contributions.Count);
        Assert.NotEmpty(contributions);

        foreach (var contribution in contributions)
        {
            Assert.Equal(4, contribution.Date.Month);
            Assert.True(calendar.LifeMonthOf(contribution.Date) > HouseholdTerms.RolloverMonth);
            Assert.Equal(DemoRoute.Bank, contribution.Route);
            Assert.Equal(ira, Assert.Single(contribution.Splits).AccountId);

            var buyDay = DemoCalendar.NextBusinessDayAfter(contribution.Date);
            var buys = Book.Transactions.Count(t => t.AccountId == ira && t.Date == buyDay && t.InvestTxnType == "buy");
            Assert.Equal(3, buys);
        }
    }

    [Fact]
    public void Spring_tax_settlements_post_on_the_tax_categories()
    {
        var federal = Book.Base.Category(MdCategoryType.Expense, "Tax", "Federal Income Tax");
        var state = Book.Base.Category(MdCategoryType.Expense, "Tax", "State Income Tax");
        var rows = Book.Transactions.Where(t => t.Pattern == TaxSettlementPattern.PatternName).ToList();

        var refunds = rows.Where(t => t.Description == TaxSettlementPattern.FederalRefundPayee).ToList();
        Assert.Equal(5, refunds.Count);
        Assert.All(refunds, t =>
        {
            Assert.Equal(PayrollTerms.FederalRefundMonth, t.Date.Month);
            var split = Assert.Single(t.Splits);
            Assert.Equal(federal, split.AccountId);
            Assert.True(split.SplitAmount < 0, "a federal refund is a negative tax posting");
            Assert.True(t.Splits.Sum(s => s.ParentAmount) > 0, "a refund is a deposit");
        });

        var settlements = rows.Where(t => t.Splits[0].AccountId == state).ToList();
        Assert.Equal(5, settlements.Count);
        Assert.All(settlements, t => Assert.Equal(
            t.Description == TaxSettlementPattern.StateRefundPayee, t.Splits[0].SplitAmount < 0));
    }

    [Fact]
    public void HSA_medical_spending_is_investment_route_exp_and_never_overdraws()
    {
        var calendar = Book.Calendar;
        var hsa = Book.Account(DemoAccountRoles.FamilyHsa);
        var holding = DemoHoldings.Holding(Book, hsa.Role, DemoSecurities.Vti).Id;
        var rows = Book.Transactions.Where(t => t.Pattern == HsaMedicalPattern.PatternName).ToList();
        Assert.True(rows.Count > 10);

        foreach (var row in rows)
        {
            Assert.Equal(hsa.Id, row.AccountId);
            Assert.Equal("exp", row.InvestTxnType);
            Assert.Equal("xfrtp_miscincexp", row.XferType);
            Assert.True(calendar.LifeMonthOf(row.Date) >= HouseholdTerms.HsaOpens);
            Assert.Equal(holding, row.Splits[0].AccountId);
            Assert.Equal(0L, row.Splits[0].SplitAmount);
            Assert.Equal("exp", row.Splits[1].SplitType);
            Assert.True(row.Splits[1].SplitAmount > 0);
        }

        var bill = Assert.Single(rows, r => r.Memo == HsaMedicalPattern.DeliveryMemo);
        Assert.Equal(HsaMedicalPattern.DeliveryBillDate(calendar), bill.Date);
        var paid = HsaMedicalPattern.HsaShareOfDeliveryBill(Book);
        Assert.True(paid > 0 && paid % 100 == 0 && paid <= HouseholdTerms.DeliveryBillTotal);
        Assert.InRange(Book.BalanceAtEndOf(hsa.Id, bill.Date), 0L, 99L);

        Assert.True(Book.LowestDayEnd(hsa.Id, calendar.WindowStart, AsOf).Balance >= 0);
        Assert.DoesNotContain(Book.Transactions, t => t.AccountId == hsa.Id && t.InvestTxnType == "buy"
            && calendar.LifeMonthOf(t.Date) < InvestmentTerms.HsaBuysFrom);
        Assert.Empty(Book.FindProblems());
    }

    // --- helpers ------------------------------------------------------------------------------

    private static IEnumerable<DemoTxn> Paychecks() =>
        Book.Transactions.Where(t => t.Pattern == PayrollPattern.PatternName && t.Route == DemoRoute.Bank);

    private static bool IsSalary(string accountId) =>
        accountId == Book.Base.Category(MdCategoryType.Income, "Salary", "Paycheck")
        || accountId == Book.Base.Category(MdCategoryType.Income, "Salary", "Bonus");

    // The amount a labelled line moves its own account (samt); zero when absent.
    private static long Line(DemoTxn paycheck, string label) =>
        paycheck.Splits.Where(s => s.Description == label).Sum(s => s.SplitAmount);

    private static long Cents(decimal cents) => DemoAmortization.RoundCents(cents);
}
