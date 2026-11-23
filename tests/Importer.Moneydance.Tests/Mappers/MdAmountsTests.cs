using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// How one Moneydance split becomes the two legs of a posting. The shapes are those of
/// a Moneydance export; these fixtures have a euro base and accounts in euros, dollars
/// and yen, and every value here is synthetic. A split carries <c>pamt</c> in the
/// parent account's currency and <c>samt</c> in the split account's, each at its own
/// currency's places.
/// </summary>
public sealed class MdAmountsTests
{
    private static readonly AccountRef Yen = new(Guid.NewGuid(), "bank", AmountDecimals: 0);
    private static readonly AccountRef Dollars = new(Guid.NewGuid(), "bank", AmountDecimals: 2);
    private static readonly AccountRef Euros = new(Guid.NewGuid(), "bank", AmountDecimals: 2);
    private static readonly AccountRef EuroCategory = new(Guid.NewGuid(), "category", AmountDecimals: 2);

    private static MdSplit Split(long pamt, long samt) =>
        new(Index: 0, Id: "s-1", AcctId: "a-x", SplitAmount: samt, ParentAmount: pamt,
            Description: null, InvestSplitType: null, Status: null, Tags: null, OldId: null);

    [Fact]
    public void A_category_leg_carries_the_real_sides_amount_not_the_split_amount()
    {
        // A dollar account pays a euro category: pamt is in dollars, samt in euros (the
        // category's own currency). Coffer reads the category's line in dollars, the
        // currency of the account on the other side, so it carries the pamt, negated.
        var (origin, category) = MdAmounts.Posting(Split(pamt: -2000, samt: 1800), Dollars, EuroCategory);

        Assert.Equal(-20.00m, origin);
        Assert.Equal(20.00m, category);
    }

    [Fact]
    public void A_yen_account_is_scaled_by_none_and_its_category_leg_follows_it()
    {
        // dec 0: a 0-place account's pamt is read as whole units, not divided by a hundred;
        // the euro samt is not read.
        var (origin, category) = MdAmounts.Posting(Split(pamt: -2400, samt: 1500), Yen, EuroCategory);

        Assert.Equal(-2400m, origin);
        Assert.Equal(2400m, category);
    }

    [Fact]
    public void A_category_as_the_parent_takes_the_real_sides_split_amount()
    {
        var (origin, real) = MdAmounts.Posting(Split(pamt: 1800, samt: -2000), EuroCategory, Dollars);

        Assert.Equal(20.00m, origin);
        Assert.Equal(-20.00m, real);
    }

    [Fact]
    public void Two_real_accounts_each_keep_their_own_amount_in_their_own_currency()
    {
        // A transfer between currencies keeps each side's own figure. Nothing converts it;
        // the posting is a two-amount pair (ADR-0114 D13), which does not sum to zero and
        // which the header-balance check passes because its amounts are nonzero and
        // opposite in sign.
        var (origin, counterpart) = MdAmounts.Posting(Split(pamt: -5000, samt: 4500), Dollars, Euros);

        Assert.Equal(-50.00m, origin);
        Assert.Equal(45.00m, counterpart);
    }

    [Fact]
    public void A_three_place_account_keeps_thousandths_and_its_category_leg_follows_it()
    {
        // ADR-0114 D15: a 3-place currency's pamt is thousandths; the category leg carries
        // the same three places, never rounded to the category's own two.
        var dinars = new AccountRef(Guid.NewGuid(), "bank", AmountDecimals: 3);

        var (origin, category) = MdAmounts.Posting(Split(pamt: -12_345, samt: 3_500), dinars, EuroCategory);

        Assert.Equal(-12.345m, origin);
        Assert.Equal(12.345m, category);
    }

    [Fact]
    public void An_account_whose_places_were_never_resolved_cannot_be_scaled()
    {
        var unresolved = new AccountRef(Guid.NewGuid(), "bank");

        Assert.Throws<InvalidOperationException>(() => MdAmounts.Posting(Split(-100, 100), unresolved, EuroCategory));
    }
}
