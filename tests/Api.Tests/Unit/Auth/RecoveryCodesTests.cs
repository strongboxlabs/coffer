using Coffer.Api.Auth.Webauthn;

namespace Coffer.Api.Tests.Unit.Auth;

/// <summary>
/// Pure unit checks for <see cref="RecoveryCodes"/>. No DB access — the
/// generator and verifier are deterministic given inputs (modulo Argon2's
/// random salt, which makes verification non-trivial to short-circuit).
/// </summary>
public sealed class RecoveryCodesTests
{
    [Fact]
    public void Generate_returns_ten_distinct_plaintext_codes_and_matching_hashes()
    {
        var (plaintext, hashes) = RecoveryCodes.Generate();

        Assert.Equal(RecoveryCodes.CodesPerSet, plaintext.Count);
        Assert.Equal(RecoveryCodes.CodesPerSet, hashes.Count);
        Assert.Equal(plaintext.Count, plaintext.Distinct().Count());

        // Every hash is a well-formed Argon2id PHC string.
        foreach (var phc in hashes)
            Assert.StartsWith("$argon2id$v=19$m=", phc, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_accepts_the_paired_plaintext()
    {
        var (plaintext, hashes) = RecoveryCodes.Generate();

        for (var i = 0; i < plaintext.Count; i++)
            Assert.True(RecoveryCodes.Verify(plaintext[i], hashes[i]),
                $"code {i} should verify against its paired hash");
    }

    [Fact]
    public void Verify_rejects_a_different_plaintext()
    {
        var (plaintext, hashes) = RecoveryCodes.Generate();
        // Cross-pair: code[0] against hash[1] etc. should never match.
        Assert.False(RecoveryCodes.Verify(plaintext[0], hashes[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_rejects_empty_input(string? presented)
    {
        var (_, hashes) = RecoveryCodes.Generate();
        Assert.False(RecoveryCodes.Verify(presented!, hashes[0]));
    }

    [Fact]
    public void Verify_rejects_garbage_phc_string()
    {
        Assert.False(RecoveryCodes.Verify("ABCDE-FGHJK", "not-a-phc"));
    }

    [Fact]
    public void Verify_normalises_user_input_so_dashes_and_case_dont_matter()
    {
        // Generated codes embed a dash in the middle for readability; verify
        // that pasting it back with dashes/lowercase/whitespace also works.
        var (plaintext, hashes) = RecoveryCodes.Generate();
        var asTyped = plaintext[0].ToLowerInvariant().Replace("-", " ");
        Assert.True(RecoveryCodes.Verify(asTyped, hashes[0]));
    }

    [Fact]
    public void Generated_plaintext_uses_unambiguous_alphabet()
    {
        var (plaintext, _) = RecoveryCodes.Generate();
        // Crockford-base32 minus i/l/o/u — none of those should appear.
        const string forbidden = "ILOU";
        foreach (var code in plaintext)
        {
            var letters = code.Where(c => char.IsLetter(c));
            Assert.DoesNotContain(letters,
                c => forbidden.Contains(char.ToUpperInvariant(c)));
        }
    }
}
