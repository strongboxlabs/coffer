namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The card statements: the original card's yearly fee and its prorated refund,
/// each statement's autopay, and the original card's final statement paid off
/// before it closes in <see cref="HouseholdTerms.ClassicCardCloses"/>.
/// </summary>
/// <remarks>
/// <para><b>Autopay.</b> For each statement closed by AsOf, the full balance at the
/// close is paid from Joint Checking on <see cref="DemoCards.AutopayDate"/>. Every
/// statement before it was paid in full, so that balance is exactly the postings of
/// the period. Statements are paid in date order, because a payment lands in the
/// next period. Both legs carry a status (the status rule runs per leg).</para>
///
/// <para><b>The original card's last statement.</b> Its fee refund posts before the
/// final close; the final statement is paid in full a few days after it closes,
/// early, inside the closing month, and the card ends at zero.</para>
///
/// <para>This pattern posts after every pattern that spends on a card.</para>
/// </remarks>
public sealed class CardsPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "cards";

    /// <summary>The original card's yearly fee.</summary>
    public const long AnnualFee = 95_00L;

    /// <summary>The life month (mod 12) the fee is charged in.</summary>
    public const int FeeMonthOfYear = 8;

    /// <summary>The day of the closing month the fee refund posts.</summary>
    public const int RefundDay = 12;

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 80;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var classic = book.Account(DemoAccountRoles.ClassicCard);
        var fees = book.Base.Category(MdCategoryType.Expense, "Bank Charge", "Service Charges");

        // The yearly fee, and at the close its unused months back.
        var lastFeeMonth = -1;
        for (var month = FeeMonthOfYear; month <= HouseholdTerms.ClassicCardCloses; month += 12)
        {
            var date = calendar.DayInMonth(month, DemoCards.ClassicCloseDay + 1);
            DemoCards.PurchaseOn(this, book, classic, date, "Annual membership fee", "ANNUAL MEMBERSHIP FEE", fees, AnnualFee);
            lastFeeMonth = month;
        }
        var unused = 12 - (HouseholdTerms.ClassicCardCloses - lastFeeMonth);
        var refundDate = calendar.DayInMonth(HouseholdTerms.ClassicCardCloses, RefundDay);
        book.Mark(DemoEvents.ClassicFeeRefund, DemoCards.PurchaseOn(this, book, classic, refundDate,
            "Annual fee refund", "ANNUAL FEE REFUND PRORATED", fees, -DemoAmortization.RoundCents(AnnualFee * unused / 12m),
            "Prorated on closing the card"));

        Autopay(calendar, book, classic);
        Autopay(calendar, book, book.Account(DemoAccountRoles.RewardsCard));
        book.AddCheck(PatternName, DemoCards.Problems);
    }

    private void Autopay(DemoCalendar calendar, DemoBook book, DemoAccount card)
    {
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var finalClose = card.Role == DemoAccountRoles.ClassicCard ? DemoCards.ClassicFinalClose(calendar) : (DateOnly?)null;
        foreach (var period in book.StatementPeriods(card.Id))
        {
            var final = period.ClosesOn == finalClose;
            var date = final
                ? DemoCalendar.OnOrNextBusinessDay(period.ClosesOn.AddDays(3))
                : DemoCards.AutopayDate(period.ClosesOn);
            if (!calendar.Contains(date)) continue;
            if (final) card.ActiveThrough = date;

            var owed = -book.BalanceAtEndOf(card.Id, period.ClosesOn);
            if (owed <= 0) continue;
            var txn = book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = card.Name + (final ? " final payment" : " autopay"),
                Memo = DemoCards.PaymentMemo(period.ClosesOn),
                BankPayee = card.Name.ToUpperInvariant() + (final ? " ONLINE PMT" : " AUTOPAY"),
                Splits = [DemoSplit.Bank(card.Id, owed)],
            });
            book.Mark(DemoCards.PaymentMark, txn);
            if (final) book.Mark(DemoEvents.ClassicPayoff, txn);
        }
    }
}
