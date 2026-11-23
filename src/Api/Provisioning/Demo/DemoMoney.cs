using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>Amount formatting for the export, from cents, never through a culture.</summary>
public static class DemoMoney
{
    /// <summary>Cents as a major-unit decimal with two places, and a leading minus
    /// when negative.</summary>
    public static string Major(long cents)
    {
        var negative = cents < 0;
        var magnitude = negative ? -(decimal)cents : cents;
        var whole = decimal.Truncate(magnitude / 100m);
        var fraction = magnitude - whole * 100m;
        return (negative ? "-" : string.Empty)
            + whole.ToString("0", CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>A decimal in invariant culture, with no trailing zeros added.</summary>
    public static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>An integer in invariant culture.</summary>
    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
