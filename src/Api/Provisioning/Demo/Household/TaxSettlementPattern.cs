namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The yearly tax settlement: each spring a federal refund lands
/// in Joint Checking as a NEGATIVE posting on Tax &gt; Federal Income Tax, and the
/// state return settles a month later, a negative posting on Tax &gt; State Income
/// Tax in a refund year and a positive one (a payment) in a balance-due year.
/// </summary>
/// <remarks>
/// A refund is a reduction of the tax withheld, not income, so it sits on the tax
/// category it reduces; a report of tax paid nets it. The day and the amount are
/// drawn from the pattern's own stream for that month.
/// </remarks>
public sealed class TaxSettlementPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "tax-settlement";

    /// <summary>The federal refund's payee.</summary>
    public const string FederalRefundPayee = "Federal tax refund";

    /// <summary>The state refund's payee.</summary>
    public const string StateRefundPayee = "State tax refund";

    /// <summary>The state balance-due payment's payee.</summary>
    public const string StatePaymentPayee = "State tax payment";

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 20;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var federal = book.Base.Category(MdCategoryType.Expense, "Tax", "Federal Income Tax");
        var state = book.Base.Category(MdCategoryType.Expense, "Tax", "State Income Tax");

        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var monthStart = calendar.MonthStart(month);
            if (monthStart.Month == PayrollTerms.FederalRefundMonth)
            {
                var stream = rng.Stream(Name + ":federal", monthStart);
                var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, (int)stream.NextLong(2, 26)));
                var refund = stream.NextMinor(PayrollTerms.FederalRefundMin, PayrollTerms.FederalRefundMax);
                if (calendar.Contains(date))
                {
                    book.Post(this, new DemoTxn
                    {
                        AccountId = checking,
                        Date = date,
                        Description = FederalRefundPayee,
                        Memo = "Federal return",
                        BankPayee = "TAX REFUND FEDERAL",
                        Splits = [DemoSplit.Bank(federal, -refund)],
                    });
                }
            }

            if (monthStart.Month == PayrollTerms.StateSettlementMonth)
            {
                var stream = rng.Stream(Name + ":state", monthStart);
                var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, (int)stream.NextLong(2, 26)));
                var amount = stream.NextMinor(PayrollTerms.StateSettlementMin, PayrollTerms.StateSettlementMax);
                var refund = stream.Chance(PayrollTerms.StateRefundChance);
                if (calendar.Contains(date))
                {
                    book.Post(this, new DemoTxn
                    {
                        AccountId = checking,
                        Date = date,
                        Description = refund ? StateRefundPayee : StatePaymentPayee,
                        Memo = "State return",
                        BankPayee = refund ? "TAX REFUND STATE" : "STATE TAX PAYMENT",
                        Splits = [DemoSplit.Bank(state, refund ? -amount : amount)],
                    });
                }
            }
        }
    }
}
