using System.Text;
using Coffer.Api.Auth;

namespace Coffer.Api.Tests.Unit.Auth;

/// <summary>
/// ADR-0089. The policy is deliberately permissive: an email, a handle, or a
/// name in any script. Only genuinely harmful input is refused.
/// </summary>
/// <remarks>
/// Every hostile character below is written as an explicit <c>\uXXXX</c> escape
/// rather than embedded literally. A literal U+202E or U+0000 in source survives
/// some editors and diff tools and not others; when it is silently dropped the
/// candidate becomes an ordinary valid string and the test flips to green for the
/// wrong reason. Escapes are also the only readable form — an invisible character
/// in a test case is unreviewable.
/// </remarks>
public sealed class UsernamePolicyTests
{
    [Theory]
    // The case that started this: an email address must be acceptable. It was
    // rejected by a client-only pattern that neither the API nor the invite form
    // enforced, so the FIRST user was refused what every invited user could use.
    [InlineData("ada.reyes@example.com")]
    [InlineData("ada.reyes+coffer@example.co.uk")]
    // Plain handles still fine.
    [InlineData("areyes")]
    [InlineData("ada_reyes")]
    [InlineData("ada-reyes")]
    // Mixed case is allowed — folding is storage's job (username_ci), not a
    // keyboard restriction.
    [InlineData("AdaReyes")]
    // Non-ASCII scripts, which the old [a-z0-9_-] rule made unusable.
    [InlineData("josé")]
    [InlineData("Иван")]
    [InlineData("山田太郎")]
    public void Accepts_reasonable_identifiers(string candidate)
    {
        Assert.True(
            UsernamePolicy.IsValid(UsernamePolicy.Normalize(candidate), out var error),
            $"expected '{candidate}' to be valid but got: {error}");
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("ab", "at least")]
    public void Rejects_empty_or_too_short(string candidate, string expectedInMessage)
    {
        Assert.False(
            UsernamePolicy.IsValid(UsernamePolicy.Normalize(candidate), out var error));
        Assert.Contains(expectedInMessage, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Whitespace anywhere: invisible padding and indistinguishable copy/paste
    // variants are a real hazard for something used to log in.
    [InlineData("ada reyes")]              // U+0020 SPACE
    [InlineData("ada\treyes")]             // U+0009 TAB
    [InlineData("ada\nreyes")]             // U+000A LINE FEED
    [InlineData("ada\u00A0reyes")]         // NO-BREAK SPACE — renders like a space
    // U+202E RIGHT-TO-LEFT OVERRIDE lets one username render as another.
    [InlineData("ada\u202Ereyes")]
    // Invisible formatting characters: two distinct usernames can look identical.
    [InlineData("ada\u200Dreyes")]         // ZERO WIDTH JOINER
    [InlineData("ada\u200Breyes")]         // ZERO WIDTH SPACE
    // C0 control.
    [InlineData("ada\u0000reyes")]
    public void Rejects_whitespace_and_invisible_characters(string candidate)
    {
        var normalized = UsernamePolicy.Normalize(candidate);

        // Guard the test itself: the input must still clear the length floor, so
        // the refusal is provably about the character and not about length.
        Assert.True(normalized.Length >= UsernamePolicy.MinLength,
            "test input collapsed — the rejection would not be attributable to the character");

        Assert.False(UsernamePolicy.IsValid(normalized, out var error));
        Assert.Contains("spaces or invisible control characters", error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_over_length()
    {
        var tooLong = new string('a', UsernamePolicy.MaxLength + 1);
        Assert.False(UsernamePolicy.IsValid(tooLong, out var error));
        Assert.Contains("at most", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_trims_surrounding_whitespace()
    {
        // Padding is the single most likely paste artefact, and it would
        // otherwise create an account nobody can type the name of.
        Assert.Equal("ada.reyes@example.com", UsernamePolicy.Normalize("  ada.reyes@example.com \n"));
    }

    [Fact]
    public void Normalize_unifies_composed_and_decomposed_accents()
    {
        // "José" precomposed (U+00E9) vs decomposed (e + U+0301 combining acute).
        // Different byte sequences that look identical; without NFC they'd be two
        // accounts, and a login typed on a different keyboard or OS could miss
        // the row entirely.
        const string precomposed = "josé";
        const string decomposed = "josé";
        Assert.NotEqual(precomposed, decomposed);   // genuinely different input

        Assert.Equal(
            UsernamePolicy.Normalize(precomposed),
            UsernamePolicy.Normalize(decomposed));
    }

    [Fact]
    public void Normalize_does_not_change_case()
    {
        // Case folding belongs to the DB collation, where it cannot be bypassed
        // and does not depend on the process culture. If Normalize lowercased
        // here, the displayed username would lose the capitalisation the user
        // chose — the convention is "store as typed, compare folded".
        Assert.Equal("AdaReyes", UsernamePolicy.Normalize("AdaReyes"));
    }

    [Fact]
    public void Normalize_output_is_NFC()
    {
        Assert.True(
            UsernamePolicy.Normalize("josé").IsNormalized(NormalizationForm.FormC));
    }
}
