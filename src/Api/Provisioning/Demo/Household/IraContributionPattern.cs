namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The yearly IRA contribution: each April after the
/// rollover month, a one-split transfer from Joint Checking into the Rollover IRA,
/// moved to the next business day. <see cref="InvestmentsPattern"/> buys the IRA's
/// mix with it the business day after it lands, so this pattern posts before it.
/// </summary>
public sealed class IraContributionPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "ira-contribution";

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 40;

    /// <summary>The life months the contribution is made in: every April after the
    /// rollover month.</summary>
    public static IEnumerable<int> ContributionMonths(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        for (var month = HouseholdTerms.RolloverMonth + 1; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            if (calendar.MonthStart(month).Month == 4) yield return month;
        }
    }

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var ira = book.Account(DemoAccountRoles.RolloverIra);

        foreach (var month in ContributionMonths(calendar))
        {
            var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, PayrollTerms.IraContributionDay));
            if (!calendar.Contains(date)) continue;
            book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = "Transfer to " + ira.Name,
                Memo = "IRA contribution",
                Splits = [DemoSplit.Bank(ira.Id, PayrollTerms.IraContribution)],
            });
        }
    }
}
