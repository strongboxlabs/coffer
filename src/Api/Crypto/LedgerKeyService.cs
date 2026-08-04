using System.Security.Cryptography;

namespace Coffer.Api.Crypto;

/// <summary>
/// Per-ledger envelope-encryption gateway (ADR-0026, refining
/// ADR-0014 §Layer 3). Generates Ledger Encryption Keys (LEKs)
/// wrapped by the deployment-level master KEK, and seals/opens
/// secrets with the unwrapped LEK.
/// </summary>
/// <remarks>
/// <para>Three operations:</para>
/// <list type="bullet">
///   <item><description><see cref="CreateWrappedLek"/> — generate
///   a fresh 256-bit LEK, wrap with the master KEK, return the
///   wire bytes ready to persist on the new
///   <c>ledgers.wrapped_lek</c> column.</description></item>
///   <item><description><see cref="Seal"/> — seal plaintext bytes
///   under the supplied wrapped LEK (typically read off the
///   ledgers row in the same transaction).</description></item>
///   <item><description><see cref="Open"/> — inverse of
///   <see cref="Seal"/>.</description></item>
/// </list>
///
/// <para>The LEK is unwrapped fresh on every Seal/Open call and
/// dropped at method return — no cross-request caching. Single
/// AES-GCM op; the cost is negligible relative to the I/O that
/// surrounds it (Postgres reads + outbound HTTP for SimpleFIN).
/// </para>
///
/// <para>All AES-GCM operations use 12-byte random nonces. The
/// wire format for both wrapped LEKs and sealed secrets is
/// <c>nonce(12) || ciphertext || tag(16)</c> — single
/// concatenated byte array, no separate columns.</para>
/// </remarks>
public sealed class LedgerKeyService
{
    private const int LekBytes = 32;        // AES-256 key
    private const int NonceBytes = 12;      // AES-GCM standard
    private const int TagBytes = 16;        // AES-GCM standard

    private readonly MasterKey _masterKey;

    public LedgerKeyService(MasterKey masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        _masterKey = masterKey;
    }

    /// <summary>The KEK id to persist alongside a freshly-wrapped
    /// LEK so master-KEK rotation knows which LEKs still need
    /// re-wrapping.</summary>
    public string CurrentKekId => _masterKey.Id;

    /// <summary>
    /// Generate a fresh LEK, wrap it under the current master KEK,
    /// and return the wire bytes for <c>ledgers.wrapped_lek</c>.
    /// </summary>
    public byte[] CreateWrappedLek()
    {
        Span<byte> lek = stackalloc byte[LekBytes];
        RandomNumberGenerator.Fill(lek);
        var wrapped = SealCore(_masterKey.KeyBytes, lek);
        CryptographicOperations.ZeroMemory(lek);
        return wrapped;
    }

    /// <summary>
    /// Seal <paramref name="plaintext"/> under the LEK unwrapped
    /// from <paramref name="wrappedLek"/>. Returns
    /// <c>nonce || ciphertext || tag</c>.
    /// </summary>
    public byte[] Seal(byte[] wrappedLek, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(wrappedLek);
        ArgumentNullException.ThrowIfNull(plaintext);
        Span<byte> lek = stackalloc byte[LekBytes];
        OpenCore(_masterKey.KeyBytes, wrappedLek, lek);
        try
        {
            return SealCore(lek, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lek);
        }
    }

    /// <summary>
    /// Inverse of <see cref="Seal"/>. Throws
    /// <see cref="CryptographicException"/> if the tag check fails
    /// (wrong LEK, tampered ciphertext, or corruption).
    /// </summary>
    public byte[] Open(byte[] wrappedLek, byte[] sealedBytes)
    {
        ArgumentNullException.ThrowIfNull(wrappedLek);
        ArgumentNullException.ThrowIfNull(sealedBytes);
        Span<byte> lek = stackalloc byte[LekBytes];
        OpenCore(_masterKey.KeyBytes, wrappedLek, lek);
        try
        {
            var plaintextLen = sealedBytes.Length - NonceBytes - TagBytes;
            if (plaintextLen < 0)
                throw new CryptographicException(
                    "Sealed payload is shorter than the AES-GCM framing.");
            var plaintext = new byte[plaintextLen];
            OpenCore(lek, sealedBytes, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lek);
        }
    }

    /// <summary>
    /// Seal <paramref name="plaintext"/> directly under the deployment
    /// master KEK — no per-ledger LEK. For deployment-global secrets that
    /// aren't owned by any single ledger (ADR-0060: the backup passphrase).
    /// The master KEK lives only in <c>COFFER_MASTER_KEK_BASE64</c> (env),
    /// never in the DB or a backup, so a stolen ciphertext alone is inert.
    /// Returns <c>nonce || ciphertext || tag</c>.
    /// </summary>
    public byte[] SealWithMasterKey(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return SealCore(_masterKey.KeyBytes, plaintext);
    }

    /// <summary>
    /// Inverse of <see cref="SealWithMasterKey"/>. Throws
    /// <see cref="CryptographicException"/> if the tag check fails (wrong
    /// master KEK, tampered ciphertext, or corruption).
    /// </summary>
    public byte[] OpenWithMasterKey(byte[] sealedBytes)
    {
        ArgumentNullException.ThrowIfNull(sealedBytes);
        var plaintextLen = sealedBytes.Length - NonceBytes - TagBytes;
        if (plaintextLen < 0)
            throw new CryptographicException(
                "Sealed payload is shorter than the AES-GCM framing.");
        var plaintext = new byte[plaintextLen];
        OpenCore(_masterKey.KeyBytes, sealedBytes, plaintext);
        return plaintext;
    }

    // -----------------------------------------------------------------
    // Internals — AES-GCM with random 12-byte nonce, layout
    //   nonce(12) || ciphertext(N) || tag(16)
    // -----------------------------------------------------------------

    private static byte[] SealCore(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        var ciphertext = output.AsSpan(NonceBytes, plaintext.Length);
        var tag = output.AsSpan(NonceBytes + plaintext.Length, TagBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    private static void OpenCore(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedBytes, Span<byte> plaintext)
    {
        if (sealedBytes.Length < NonceBytes + TagBytes)
            throw new CryptographicException(
                "Sealed payload is shorter than the AES-GCM framing.");
        var nonce = sealedBytes.Slice(0, NonceBytes);
        var ciphertextLen = sealedBytes.Length - NonceBytes - TagBytes;
        var ciphertext = sealedBytes.Slice(NonceBytes, ciphertextLen);
        var tag = sealedBytes.Slice(NonceBytes + ciphertextLen, TagBytes);
        if (plaintext.Length != ciphertextLen)
            throw new ArgumentException(
                "Plaintext span length must match ciphertext length.",
                nameof(plaintext));
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
    }
}
