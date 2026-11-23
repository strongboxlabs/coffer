using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

using static Coffer.Api.Db.Repositories.PostingLegPair;

namespace Coffer.Api.Tests.Unit.Db;

/// <summary>
/// <see cref="PostingLegPair.CheckSecondAmount"/>, the one second-amount rule for a pair
/// across currencies (ADR-0114 D13): neither side zero, signs opposite, and, like the
/// places rule, checked only for a pair the write changes.
/// </summary>
public sealed class PostingLegPairTests
{
    private static readonly Guid SourceAccount = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherAccount = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid ThirdAccount = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    [Theory]
    [InlineData("-10", "15")]
    [InlineData("10", "-15")]
    public void A_new_pair_with_opposite_nonzero_sides_passes(string first, string second) =>
        Assert.Equal(SecondAmountFault.None, CheckSecondAmount(
            new Leg(SourceAccount, Parse(first)), new Leg(OtherAccount, Parse(second))));

    [Theory]
    [InlineData("0", "15")]
    [InlineData("-10", "0")]
    [InlineData("0", "0")]
    public void A_new_pair_with_a_zero_side_is_refused_as_zero(string first, string second)
    {
        var fault = CheckSecondAmount(new Leg(SourceAccount, Parse(first)), new Leg(OtherAccount, Parse(second)));

        Assert.Equal(SecondAmountFault.Zero, fault);
        Assert.Equal(BusinessError.Codes.TransactionPostingTwoAmountsNonzero, BankCode(fault));
        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountsNonzero, InvestmentCode(fault));
    }

    [Theory]
    [InlineData("-10", "-15")]
    [InlineData("10", "15")]
    public void A_new_pair_with_same_signed_sides_is_refused_as_same_sign(string first, string second)
    {
        var fault = CheckSecondAmount(new Leg(SourceAccount, Parse(first)), new Leg(OtherAccount, Parse(second)));

        Assert.Equal(SecondAmountFault.SameSign, fault);
        Assert.Equal(BusinessError.Codes.TransactionPostingTwoAmountsSign, BankCode(fault));
        Assert.Equal(BusinessError.Codes.InvestmentTxnTransferAmountsSign, InvestmentCode(fault));
    }

    // A mirrored zero pair that a currency edit left across currencies: an edit that
    // changes only the payee or the date resends it as stored, and must pass.
    [Fact]
    public void A_stored_zero_pair_resent_unchanged_passes_from_either_side()
    {
        var stored = new StoredPair(new Leg(SourceAccount, 0m), new Leg(OtherAccount, 0m));

        Assert.Equal(SecondAmountFault.None, CheckSecondAmount(
            new Leg(SourceAccount, 0m), new Leg(OtherAccount, 0m), stored));
        // Opened from the other account's register, the source side is the other leg.
        Assert.Equal(SecondAmountFault.None, CheckSecondAmount(
            new Leg(OtherAccount, 0m), new Leg(SourceAccount, 0m), stored));
        // Equal by value, not by scale.
        Assert.Equal(SecondAmountFault.None, CheckSecondAmount(
            new Leg(SourceAccount, 0.00m), new Leg(OtherAccount, 0m), stored));
    }

    [Fact]
    public void A_stored_same_signed_pair_resent_unchanged_passes()
    {
        var stored = new StoredPair(new Leg(SourceAccount, -10m), new Leg(OtherAccount, -15m));

        Assert.Equal(SecondAmountFault.None, CheckSecondAmount(
            new Leg(SourceAccount, -10m), new Leg(OtherAccount, -15m), stored));
    }

    // Only a pair the write leaves as it was is exempt: a changed amount, or the same
    // amounts on another account, is checked like a new pair.
    [Fact]
    public void A_stored_pair_with_a_changed_amount_or_account_is_checked()
    {
        var stored = new StoredPair(new Leg(SourceAccount, 0m), new Leg(OtherAccount, 0m));

        Assert.Equal(SecondAmountFault.Zero, CheckSecondAmount(
            new Leg(SourceAccount, -10m), new Leg(OtherAccount, 0m), stored));
        Assert.Equal(SecondAmountFault.Zero, CheckSecondAmount(
            new Leg(SourceAccount, 0m), new Leg(ThirdAccount, 0m), stored));
    }

    // ---- Build: the one builder of a bank posting's two legs ---------------------------

    private static readonly Guid Category = Guid.Parse("00000000-0000-0000-0000-0000000000d4");

    private static readonly IReadOnlyDictionary<Guid, AccountSide> Sides = new Dictionary<Guid, AccountSide>
    {
        [SourceAccount] = new("bank", "USD"),
        [OtherAccount] = new("bank", "EUR"),
        [ThirdAccount] = new("bank", "USD"),
        [Category] = new("category", "EUR"),
    };

    private static Coffer.Api.Contracts.TransactionPosting Posting(Guid counterparty, decimal amount, decimal? second) =>
        new() { CounterpartyAccountId = counterparty, Amount = amount, CounterpartyAmount = second };

    // A mirrored zero pair that a move or a currency edit left across currencies is kept
    // as it is: an edit that resends it unchanged passes, from either register.
    [Fact]
    public void Build_keeps_a_stored_zero_pair_across_currencies_resent_unchanged_from_either_side()
    {
        var stored = new StoredPair(new Leg(SourceAccount, 0m), new Leg(OtherAccount, 0m));

        var (amounts, refused) = Build(SourceAccount, Posting(OtherAccount, 0m, 0m), 0, Sides, stored);
        Assert.Null(refused);
        Assert.Equal(new LegAmounts(0m, 0m), amounts);

        var (back, backRefused) = Build(OtherAccount, Posting(SourceAccount, 0m, 0m), 0, Sides, stored);
        Assert.Null(backRefused);
        Assert.Equal(new LegAmounts(0m, 0m), back);

        // The same pair as a new posting is refused.
        var (_, fresh) = Build(SourceAccount, Posting(OtherAccount, 0m, 0m), 0, Sides);
        Assert.Equal(BusinessError.Codes.TransactionPostingTwoAmountsNonzero, fresh!.Code);
    }

    [Fact]
    public void Build_drops_a_second_amount_on_a_category_line_or_a_pair_in_one_currency()
    {
        Assert.Equal((new LegAmounts(-10m, 10m), (Refusal?)null), Build(SourceAccount, Posting(Category, -10m, 7m), 0, Sides));
        Assert.Equal((new LegAmounts(-10m, 10m), (Refusal?)null), Build(SourceAccount, Posting(ThirdAccount, -10m, 7m), 0, Sides));
    }

    [Fact]
    public void Build_refuses_a_pair_across_currencies_with_no_second_amount_naming_the_posting()
    {
        var (_, refused) = Build(SourceAccount, Posting(OtherAccount, -10m, null), 2, Sides);

        Assert.Equal(BusinessError.Codes.TransactionPostingCounterpartyAmountRequired, refused!.Code);
        Assert.Equal(2, refused.PostingIndex);
    }

    private static decimal Parse(string value) =>
        decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
