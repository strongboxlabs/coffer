using System.Security.Cryptography;
using System.Text;

namespace Coffer.Api.Auth;

/// <summary>
/// Mints and hashes MCP access tokens (ADR-0063). A token is a prefixed,
/// URL-safe random string; the DB stores only its SHA-256, so the plaintext —
/// shown to the user exactly once at issue — can't be recovered from a DB read.
/// Mirrors <see cref="Webauthn.SessionService"/>'s cookie contract.
/// </summary>
public static class McpTokenService
{
    /// <summary>
    /// Human-recognizable prefix so a leaked token is identifiable (and
    /// greppable by secret scanners) as a Coffer MCP token. Part of the hashed
    /// material — there is no separate parsing step.
    /// </summary>
    public const string Prefix = "coffer_mcp_";

    /// <summary>
    /// Generate a new token: <c>coffer_mcp_</c> + base64url(32 random bytes).
    /// Returns the plaintext (returned to the caller once) and its SHA-256
    /// (stored).
    /// </summary>
    public static (string Plaintext, byte[] Hash) Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var plaintext = Prefix + Base64UrlEncode(bytes);
        return (plaintext, Hash(plaintext));
    }

    /// <summary>
    /// SHA-256 of the presented token string (UTF-8). Verification is a
    /// byte-array equality check against the stored hash.
    /// </summary>
    public static byte[] Hash(string plaintext) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');
}
