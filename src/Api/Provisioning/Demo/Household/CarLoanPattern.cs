namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The car loan: a payment on the 8th of each month (the next
/// business day when markets are shut) from Joint Checking with two lines, interest
/// on Loan &gt; Interest and principal on the loan, until the payoff month, when one
/// row pays the month's interest and the whole remaining principal and the loan
/// closes at zero. Loan &gt; Payment is never used.
/// </summary>
/// <remarks>
/// The window opens partway through the loan: its opening balance is the iterated
/// balance after the payments made before the window, so the first payment in the
/// window is the one after them and the rows follow the loan's own terms.
/// </remarks>
public sealed class CarLoanPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "car-loan";

    /// <summary>The lender.</summary>
    public const string Lender = "Torvanne Motor Finance";

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 62;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var loan = book.Account(DemoAccountRoles.CarLoan);
        var interestCategory = book.Base.Category(MdCategoryType.Expense, "Loan", "Interest");
        var terms = HouseholdTerms.CarLoan;

        for (var month = 0; month <= HouseholdTerms.CarLoanCloses; month++)
        {
            var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, HouseholdTerms.CarLoanPaymentDay));
            if (!calendar.Contains(date)) continue;
            var number = HouseholdTerms.CarLoanPaymentsBeforeWindow + month + 1;
            var owed = terms.BalanceAfter(number - 1);
            var interest = terms.InterestOn(owed);
            var payoff = month == HouseholdTerms.CarLoanCloses;
            var principal = payoff ? owed : terms.Payment - interest;
            if (payoff) loan.ActiveThrough = date;

            var txn = book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = Lender,
                Memo = payoff
                    ? "Loan paid off"
                    : "Auto loan payment " + number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " of " + terms.Payments.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BankPayee = "TORVANNE MTR FIN ACH",
                Splits =
                [
                    DemoSplit.Bank(interestCategory, interest, "Interest"),
                    DemoSplit.Bank(loan.Id, principal, "Principal"),
                ],
            });
            if (payoff) book.Mark(DemoEvents.CarPayoff, txn);
        }

        book.AddCheck(PatternName, b =>
            b.BalanceAtEndOf(loan.Id, calendar.AsOf) == 0 ? [] : ["the car loan does not end at zero"]);
    }
}
