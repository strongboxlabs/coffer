namespace Coffer.Api.Ingest;

/// <summary>
/// The one rule for a grouping character in the whole part of an imported figure, shared
/// by the CSV layout reader and the QIF reader so the same text reads the same in both
/// (ADR-0114 D15).
/// </summary>
/// <remarks>
/// <para>Two groupings are accepted. Groups of three after a first group of one to three
/// digits, as in thousands grouping. Or lakh grouping: a first group of one or two
/// digits, groups of two, then a last group of three, with two grouping characters at
/// least. The first group never starts with a zero. Every group is digits only.</para>
/// <para>A lakh figure has two grouping characters at least and ends in three digits,
/// so a grouping character followed by two digits at the end never reads as grouping,
/// and a figure that carries a decimal character at most once cannot be read as
/// anything else.</para>
/// <para>Grouping is checked by hand rather than through a culture, because the API runs
/// with invariant globalization and a culture's grouping rules are looser than this.</para>
/// </remarks>
internal static class AmountGrouping
{
    /// <summary>
    /// True when <paramref name="whole"/>, the whole part of a figure with no sign and no
    /// decimal character, is grouped by <paramref name="groupChar"/> in one of the two
    /// accepted ways.
    /// </summary>
    internal static bool IsGrouped(string whole, char groupChar)
    {
        ArgumentNullException.ThrowIfNull(whole);
        var groups = whole.Split(groupChar);
        if (!groups.All(g => g.All(char.IsAsciiDigit))) return false;
        if (groups[0].Length == 0 || groups[0][0] == '0') return false;

        var inThrees = groups[0].Length <= 3 && groups.Skip(1).All(g => g.Length == 3);
        var lakh = groups.Length >= 3
                   && groups[0].Length <= 2
                   && groups[1..^1].All(g => g.Length == 2)
                   && groups[^1].Length == 3;
        return inThrees || lakh;
    }
}
