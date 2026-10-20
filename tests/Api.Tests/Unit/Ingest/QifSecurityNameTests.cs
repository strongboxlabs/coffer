using System.Diagnostics;
using System.Text.RegularExpressions;

using Coffer.Api.Ingest.Qif;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// Splitting a QIF security field into a name and a trailing <c>(CODE)</c> ticker hint
/// (ADR-0038). It was a regex that took quadratic time on a name of many parentheses,
/// read from an uploaded file (FU-092); the scan that replaced it must give the regex's
/// answer on every input.
/// </summary>
public sealed class QifSecurityNameTests
{
    /// <summary>The regex the scan replaced, kept as the reference.</summary>
    private static readonly Regex Reference = new(@"\(([^)]+)\)\s*$");

    private static (string? Name, string? Hint) ByReference(string? security)
    {
        if (string.IsNullOrWhiteSpace(security)) return (null, null);
        var trimmed = security.Trim();
        var match = Reference.Match(trimmed);
        if (!match.Success) return (trimmed, null);
        var hint = match.Groups[1].Value.Trim();
        var name = trimmed[..match.Index].Trim();
        return (name.Length > 0 ? name : trimmed, hint.Length > 0 ? hint : null);
    }

    [Theory]
    [InlineData("BOND FUND(BFND)", "BOND FUND", "BFND")]
    [InlineData("BOND FUND (BFND)  ", "BOND FUND", "BFND")]
    [InlineData("Foo (A (B)", "Foo", "A (B")]   // the leftmost ( after the last )
    [InlineData("Plain name", "Plain name", null)]
    [InlineData("(BFND)", "(BFND)", "BFND")]    // nothing before it: the whole value is the name
    [InlineData("Fund ()", "Fund ()", null)]
    [InlineData("Fund (x))", "Fund (x))", null)]
    [InlineData("Fund ( )", "Fund", null)]
    public void Splits_a_trailing_code_off_the_name(string security, string name, string? hint)
    {
        Assert.Equal((name, hint), QifFileProvider.SplitSecurity(security));
        Assert.Equal(ByReference(security), QifFileProvider.SplitSecurity(security));
    }

    [Fact]
    public void Gives_the_regexs_answer_on_twenty_thousand_random_names()
    {
        var random = new Random(20261019);
        const string alphabet = "ab() \t";
        for (var i = 0; i < 20_000; i++)
        {
            var chars = new char[random.Next(0, 14)];
            for (var j = 0; j < chars.Length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];
            var s = new string(chars);
            Assert.True(ByReference(s) == QifFileProvider.SplitSecurity(s), $"differs on \"{s}\"");
        }
    }

    [Fact]
    public void A_name_of_many_parentheses_takes_linear_time()
    {
        // Two hundred thousand of them: the regex retried from each one to the end, about
        // 2·10^10 steps. The scan reads the string once.
        var hostile = new string('(', 200_000) + "x";
        var watch = Stopwatch.StartNew();
        Assert.Equal((hostile, (string?)null), QifFileProvider.SplitSecurity(hostile));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }
}
