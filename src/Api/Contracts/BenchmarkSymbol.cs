using System.Text.RegularExpressions;

namespace Coffer.Api.Contracts;

/// <summary>
/// What a benchmark symbol is (ADR-0114 D1, D2): a Yahoo symbol as the add-time check stores
/// it, upper-cased. One rule for every place a symbol arrives: the benchmark add, a saved
/// report spec's pick, and the returns-series request on REST and MCP.
/// </summary>
public static partial class BenchmarkSymbol
{
    /// <summary>The longest symbol accepted. Yahoo's run to about a dozen characters.</summary>
    public const int MaxLength = 32;

    /// <summary>The characters a Yahoo symbol is made of, once upper-cased: letters,
    /// digits, and <c>. - ^ = _</c> (<c>VWRL.L</c>, <c>BRK-B</c>, <c>^GSPC</c>).</summary>
    [GeneratedRegex(@"^[A-Z0-9.\-\^=_]+$")]
    private static partial Regex Shape();

    /// <summary>True when <paramref name="symbol"/> is a symbol as stored: non-blank, at most
    /// <see cref="MaxLength"/> characters, upper-case, and only a symbol's characters. Nothing
    /// is trimmed or upper-cased here; the add normalizes what a user typed before asking.</summary>
    public static bool IsValid(string? symbol) =>
        !string.IsNullOrEmpty(symbol) && symbol.Length <= MaxLength && Shape().IsMatch(symbol);
}
