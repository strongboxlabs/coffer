using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Moneydance's numeric <c>sec_type</c> codes, with the subtype where it decides the
/// vehicle, become Coffer's (asset_class, vehicle_type) pair (ADR-0016, amendment of
/// 2026-11-18; ADR-0067). Every row of the table, an unknown code, and the absent code.
/// </summary>
public sealed class MdSecurityTypeTests
{
    [Theory]
    // code, subtype, asset_class, vehicle_type
    [InlineData("0", null,                   "cash",         "cd")]
    [InlineData("1", null,                   "fixed_income", "bond")]
    [InlineData("1", "Income",               "fixed_income", "bond")]
    [InlineData("2", "Exchange Traded Fund", null,           "etf")]
    [InlineData("2", "Money Market",         "cash",         "money_market")]
    [InlineData("2", "Balanced / Targeted",  null,           "mutual_fund")]
    [InlineData("2", "U.S. Stocks",          null,           "mutual_fund")]
    [InlineData("2", null,                   null,           "mutual_fund")]
    [InlineData("3", "Exchange Traded Fund", null,           "etf")]
    [InlineData("3", "U.S. Stocks",          "equity",       "stock")]
    [InlineData("3", null,                   "equity",       "stock")]
    [InlineData("3", "Money Market",         "equity",       "stock")]   // money market is a fund subtype only
    [InlineData("4", null,                   "alternative",  "option")]
    public void Each_code_maps_to_its_class_and_vehicle(
        string code, string? subtype, string? expectedClass, string expectedVehicle)
    {
        var c = MdSecurityType.Translate(code, subtype);

        Assert.Equal(expectedClass, c.AssetClass);
        Assert.Equal(expectedVehicle, c.VehicleType);
        Assert.True(c.Recognised);
        Assert.True(c.Classified);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("Stock")]      // a name, not a code: Moneydance writes codes
    [InlineData("Mutual Fund")]
    public void An_unknown_code_is_other_and_not_recognised(string code)
    {
        var c = MdSecurityType.Translate(code, "Exchange Traded Fund");

        Assert.Null(c.AssetClass);
        Assert.Equal("other", c.VehicleType);
        Assert.False(c.Recognised);
        Assert.True(c.Classified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_code_leaves_the_security_unclassified(string? code)
    {
        var c = MdSecurityType.Translate(code, "Exchange Traded Fund");

        Assert.Null(c.AssetClass);
        Assert.Null(c.VehicleType);
        Assert.True(c.Recognised);
        Assert.False(c.Classified);
    }

    [Fact]
    public void The_subtype_names_are_moneydances_own()
    {
        // The names in Moneydance's secsubtypes item; a generator writing an export
        // uses these constants, so a rename here reaches both sides.
        Assert.Equal("Exchange Traded Fund", MdSecurityType.ExchangeTradedFundSubtype);
        Assert.Equal("Money Market", MdSecurityType.MoneyMarketSubtype);
        Assert.Equal(
            new[] { "0", "1", "2", "3", "4" },
            new[] { MdSecurityType.Cd, MdSecurityType.Bond, MdSecurityType.MutualFund, MdSecurityType.Stock, MdSecurityType.Option });
    }
}
