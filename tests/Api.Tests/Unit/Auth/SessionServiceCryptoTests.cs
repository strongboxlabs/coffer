using Coffer.Api.Auth.Webauthn;

namespace Coffer.Api.Tests.Unit.Auth;

/// <summary>
/// Pure unit checks for the cookie value generator and hasher on
/// <see cref="SessionService"/>. The crypto round-trip is deterministic
/// given the inputs, so no DB or HTTP host is involved.
/// </summary>
public sealed class SessionServiceCryptoTests
{
    [Fact]
    public void GenerateCookieValue_returns_url_safe_base64_and_a_32_byte_hash()
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();

        Assert.NotEmpty(plaintext);
        Assert.DoesNotContain('+', plaintext);
        Assert.DoesNotContain('/', plaintext);
        Assert.DoesNotContain('=', plaintext);
        Assert.Equal(32, hash.Length);     // SHA-256 output
    }

    [Fact]
    public void HashCookieValue_round_trips_against_GenerateCookieValue()
    {
        var (plaintext, expected) = SessionService.GenerateCookieValue();
        var actual = SessionService.HashCookieValue(plaintext);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GenerateCookieValue_produces_distinct_values_per_call()
    {
        var (a, _) = SessionService.GenerateCookieValue();
        var (b, _) = SessionService.GenerateCookieValue();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashCookieValue_throws_FormatException_on_garbage_input()
    {
        Assert.Throws<FormatException>(() => SessionService.HashCookieValue("not base64 url"));
    }
}
