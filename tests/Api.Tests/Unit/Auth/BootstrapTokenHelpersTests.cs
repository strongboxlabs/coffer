using Coffer.Api.Db.Services;

namespace Coffer.Api.Tests.Unit.Auth;

/// <summary>
/// Pure unit checks for the static token-encoding helpers on
/// <see cref="BootstrapTokenService"/>. The token plumbing is small
/// enough to test without DB I/O — round-trip <see cref="BootstrapTokenService.GenerateToken"/>
/// → <see cref="BootstrapTokenService.HashToken"/> and assert the hash matches.
/// </summary>
public sealed class BootstrapTokenHelpersTests
{
    [Fact]
    public void GenerateToken_returns_url_safe_base64_and_a_32_byte_hash()
    {
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();

        Assert.NotEmpty(plaintext);
        Assert.DoesNotContain('+', plaintext);
        Assert.DoesNotContain('/', plaintext);
        Assert.DoesNotContain('=', plaintext);
        Assert.Equal(32, hash.Length);     // SHA-256 output
    }

    [Fact]
    public void HashToken_round_trips_against_GenerateToken()
    {
        var (plaintext, expected) = BootstrapTokenService.GenerateToken();
        var actual = BootstrapTokenService.HashToken(plaintext);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GenerateToken_produces_distinct_tokens_per_call()
    {
        var (a, _) = BootstrapTokenService.GenerateToken();
        var (b, _) = BootstrapTokenService.GenerateToken();
        Assert.NotEqual(a, b);
    }
}
