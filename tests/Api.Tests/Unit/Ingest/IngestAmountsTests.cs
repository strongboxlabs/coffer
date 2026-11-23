using System.Globalization;

using Coffer.Api.Ingest;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The one ingest rounding rule (ADR-0114 D15): a provider's amount fitted to its account
/// currency's places, half away from zero, saying whether it rounded.
/// </summary>
public sealed class IngestAmountsTests
{
    /// <summary>Values are strings so each keeps the scale it is written with.</summary>
    [Theory]
    // A whole-unit currency: a half goes away from zero in both signs.
    [InlineData("0.5", "JPY", "1", true)]
    [InlineData("-0.5", "JPY", "-1", true)]
    [InlineData("2.5", "JPY", "3", true)]
    [InlineData("1234", "JPY", "1234", false)]
    // A two-place currency: a 3-decimal midpoint goes away from zero.
    [InlineData("1.005", "USD", "1.01", true)]
    [InlineData("-1.005", "USD", "-1.01", true)]
    [InlineData("0.125", "USD", "0.13", true)]
    [InlineData("-12.34", "USD", "-12.34", false)]
    // A three-place currency: a 4-decimal midpoint goes away from zero.
    [InlineData("1.2345", "KWD", "1.235", true)]
    [InlineData("-1.2345", "KWD", "-1.235", true)]
    [InlineData("1.234", "KWD", "1.234", false)]
    // A stored code the table refuses keeps the default two places: an N.A. entry, and
    // a fund unit whose exponent is past the ceiling.
    [InlineData("1.005", "XAU", "1.01", true)]
    [InlineData("1.2345", "CLF", "1.23", true)]
    public void A_value_fits_its_currencys_places_half_away_from_zero(
        string value, string currency, string expected, bool rounded)
    {
        var fitted = IngestAmounts.Fit(Parse(value), currency);

        Assert.Equal(Parse(expected), fitted.Amount);
        Assert.Equal(rounded, fitted.Rounded);
    }

    /// <summary>A figure with one more trailing zero than its currency has places fits as
    /// it is: rounded is asked of the value, never of its scale, so the row is not held
    /// for review and its scale is kept.</summary>
    [Theory]
    [InlineData("12.0", "JPY")]
    [InlineData("-12.340", "USD")]
    [InlineData("5.1000", "KWD")]
    public void A_value_that_fits_with_a_trailing_zero_is_not_rounded_and_keeps_its_scale(
        string value, string currency)
    {
        var fitted = IngestAmounts.Fit(Parse(value), currency);

        Assert.False(fitted.Rounded);
        Assert.Equal(value, fitted.Amount.ToString(CultureInfo.InvariantCulture));
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
