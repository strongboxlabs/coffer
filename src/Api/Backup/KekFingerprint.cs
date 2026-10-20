using System.Security.Cryptography;

namespace Coffer.Api.Backup;

/// <summary>
/// A short, one-way fingerprint of the Master KEK (ADR-0071 D4). Written into
/// the <c>.cofferbak</c> header on create and compared on restore, so a backup
/// sealed under a <em>different</em> KEK — the cross-install migration case — is
/// flagged before the destructive apply.
///
/// HMAC-SHA256 keyed by the KEK bytes over a fixed domain-separation label,
/// truncated to 16 bytes: it identifies the KEK without revealing it, and two
/// different KEKs produce different fingerprints even when they share the same
/// KEK <em>id</em> (both "v1"). The KEK itself is never placed in a backup — a
/// fingerprint can only confirm match/mismatch, never reconstruct the key.
/// </summary>
public static class KekFingerprint
{
    /// <summary>Truncated fingerprint length in bytes.</summary>
    public const int Bytes = 16;

    private static readonly byte[] Label = "coffer-kek-fingerprint"u8.ToArray();

    /// <summary>Compute the fingerprint of a 32-byte Master KEK.</summary>
    public static byte[] Compute(byte[] kekBytes)
    {
        ArgumentNullException.ThrowIfNull(kekBytes);
        using var hmac = new HMACSHA256(kekBytes);
        var full = hmac.ComputeHash(Label);
        return full[..Bytes];
    }

    /// <summary>Constant-time comparison of two fingerprints.</summary>
    public static bool Matches(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);
}
