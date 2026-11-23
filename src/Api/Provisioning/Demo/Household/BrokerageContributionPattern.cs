namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The monthly Checking to Brokerage contribution: a one-split transfer entered in
/// the bank (no action, owned by the bank), about the twentieth, moved to the next
/// business day. <see cref="InvestmentsPattern"/> buys with it the business day
/// after it lands.
/// </summary>
/// <remarks>
/// It draws on Joint Checking, which <see cref="PayrollPattern"/> funds; it must
/// post before <see cref="InvestmentsPattern"/>.
/// </remarks>
public sealed class BrokerageContributionPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "brokerage-contribution";

    /// <summary>The monthly contribution, in cents.</summary>
    public const long MonthlyContribution = 600_00L;

    /// <summary>The nominal day of the month it is sent.</summary>
    public const int ContributionDay = 20;

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 40;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking);
        var brokerage = book.Account(DemoAccountRoles.Brokerage);

        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var day = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, ContributionDay));
            if (!calendar.Contains(day)) continue;
            book.Post(this, new DemoTxn
            {
                AccountId = checking.Id,
                Date = day,
                Description = "Transfer to Brokerage",
                Memo = "Monthly contribution",
                Splits = [DemoSplit.Bank(brokerage.Id, MonthlyContribution)],
            });
        }
    }
}
