using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace Coffer.Api.Backup;

/// <summary>
/// Passphrase-based streaming encryption for whole-DB backups (ADR-0060).
///
/// The payload (a <c>pg_dump</c> archive) is far too large to hold in memory,
/// so this is a chunked AEAD: the plaintext is split into fixed-size blocks,
/// each sealed independently with AES-256-GCM under a key derived from the
/// passphrase via Argon2id. Per-chunk associated data binds the chunk's
/// sequence number and a final-chunk flag, so an attacker can't reorder, drop,
/// duplicate, or truncate chunks without a tag failure. Nothing is buffered
/// beyond one block.
///
/// Wire format (all integers big-endian):
///   header: "COFFERBAK"(9) · version(1) · salt(16) · memKiB(4) · iters(4) · par(4)
///           · [v2 only] fpLen(1) · kekFingerprint(fpLen)
///   chunk*: isFinal(1) · ctLen(4) · nonce(12) · ciphertext(ctLen) · tag(16)
///   AAD per chunk = seq(8) · isFinal(1)
///
/// v2 (ADR-0071 D4) appends a KEK fingerprint to the header for the restore-time
/// cross-install check; v1 has no fingerprint. Readers accept both.
///
/// The KDF parameters ride in the header so a future parameter bump can still
/// decrypt old artifacts. There is no integrity check beyond the per-chunk
/// tags: a wrong passphrase, a flipped bit, or a missing/extra chunk all
/// surface as <see cref="BackupDecryptException"/>.
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "COFFERBAK"u8.ToArray();
    // v1: no KEK fingerprint. v2 (ADR-0071 D4): header appends fpLen(1)·fingerprint.
    // Readers accept both; a writer emits v2 iff a fingerprint is supplied.
    private const byte FormatV1 = 1;
    private const byte FormatV2 = 2;

    // Argon2id parameters (ADR-0037): 64 MiB, 3 passes, single lane.
    private const int MemoryKib = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Plaintext block size (64 KiB). One block in flight at a time.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Encrypt with no KEK fingerprint (format v1).</summary>
    public static Task EncryptAsync(
        Stream input,
        string passphrase,
        Stream output,
        CancellationToken cancellationToken = default)
        => EncryptAsync(input, passphrase, output, kekFingerprint: default, cancellationToken);

    /// <summary>
    /// Encrypt <paramref name="input"/> into <paramref name="output"/> under
    /// <paramref name="passphrase"/>. When <paramref name="kekFingerprint"/> is
    /// non-empty it's recorded in the header (format v2, ADR-0071 D4) for the
    /// restore-time KEK check. Streams; never holds more than one block.
    /// </summary>
    public static async Task EncryptAsync(
        Stream input,
        string passphrase,
        Stream output,
        ReadOnlyMemory<byte> kekFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(passphrase, salt, MemoryKib, Iterations, Parallelism);
        try
        {
            await WriteHeaderAsync(output, salt, kekFingerprint, cancellationToken).ConfigureAwait(false);

            using var gcm = new AesGcm(key, TagBytes);

            // One-block read-ahead so each chunk knows whether it's the last.
            var current = await ReadBlockAsync(input, cancellationToken).ConfigureAwait(false);
            ulong seq = 0;
            do
            {
                var next = await ReadBlockAsync(input, cancellationToken).ConfigureAwait(false);
                var isFinal = next.Length == 0;
                await WriteChunkAsync(output, gcm, seq, isFinal, current, cancellationToken)
                    .ConfigureAwait(false);
                seq++;
                current = next;
            }
            while (current.Length > 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypt a stream produced by <see cref="EncryptAsync"/> into
    /// <paramref name="output"/>. Throws <see cref="BackupDecryptException"/>
    /// on a wrong passphrase, tampering, or a truncated/altered stream.
    /// </summary>
    public static async Task DecryptAsync(
        Stream input,
        string passphrase,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var (salt, memKib, iters, par, _) = await ReadHeaderAsync(input, cancellationToken)
            .ConfigureAwait(false);
        var key = DeriveKey(passphrase, salt, memKib, iters, par);
        try
        {
            using var gcm = new AesGcm(key, TagBytes);

            ulong seq = 0;
            while (true)
            {
                var header = new byte[1 + 4 + NonceBytes];
                var read = await ReadUpToAsync(input, header, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    // Stream ended without a final chunk — truncated.
                    throw new BackupDecryptException(
                        "Backup is truncated (missing the final chunk).");
                if (read != header.Length)
                    throw new BackupDecryptException("Backup is truncated mid-chunk.");

                var isFinal = header[0];
                if (isFinal is not (0 or 1))
                    throw new BackupDecryptException("Backup is corrupt (bad chunk flag).");
                var ctLen = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
                if (ctLen < 0 || ctLen > ChunkBytes)
                    throw new BackupDecryptException("Backup is corrupt (bad chunk length).");
                var nonce = header.AsSpan(5, NonceBytes).ToArray();

                var ciphertext = new byte[ctLen];
                await ReadExactlyOrThrowAsync(input, ciphertext, cancellationToken)
                    .ConfigureAwait(false);
                var tag = new byte[TagBytes];
                await ReadExactlyOrThrowAsync(input, tag, cancellationToken)
                    .ConfigureAwait(false);

                var plaintext = new byte[ctLen];
                try
                {
                    gcm.Decrypt(nonce, ciphertext, tag, plaintext, Aad(seq, isFinal));
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new BackupDecryptException(
                        "Wrong passphrase or corrupt backup.", ex);
                }

                await output.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
                seq++;

                if (isFinal == 1)
                {
                    // Nothing must follow the final chunk.
                    if (await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                        throw new BackupDecryptException("Backup has trailing data after the final chunk.");
                    return;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Aad(ulong seq, byte isFinal)
    {
        var aad = new byte[9];
        BinaryPrimitives.WriteUInt64BigEndian(aad, seq);
        aad[8] = isFinal;
        return aad;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int memKib, int iters, int par)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            MemorySize = memKib,
            Iterations = iters,
            DegreeOfParallelism = par,
        };
        return argon2.GetBytes(KeyBytes);
    }

    private static async Task WriteHeaderAsync(
        Stream output, byte[] salt, ReadOnlyMemory<byte> fingerprint, CancellationToken ct)
    {
        var hasFp = fingerprint.Length > 0;
        var baseLen = Magic.Length + 1 + SaltBytes + 12;
        var header = new byte[hasFp ? baseLen + 1 + fingerprint.Length : baseLen];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = hasFp ? FormatV2 : FormatV1;
        salt.CopyTo(header, Magic.Length + 1);
        var p = Magic.Length + 1 + SaltBytes;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p, 4), MemoryKib);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p + 4, 4), Iterations);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p + 8, 4), Parallelism);
        if (hasFp)
        {
            header[baseLen] = (byte)fingerprint.Length;
            fingerprint.Span.CopyTo(header.AsSpan(baseLen + 1));
        }
        await output.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static async Task<(byte[] Salt, int MemKib, int Iters, int Par, byte[] Fingerprint)> ReadHeaderAsync(
        Stream input, CancellationToken ct)
    {
        var header = new byte[Magic.Length + 1 + SaltBytes + 12];
        try
        {
            await ReadExactlyOrThrowAsync(input, header, ct).ConfigureAwait(false);
        }
        catch (BackupDecryptException)
        {
            throw new BackupDecryptException("Not a Coffer backup (header too short).");
        }

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new BackupDecryptException("Not a Coffer backup (bad magic).");
        var version = header[Magic.Length];
        if (version is not (FormatV1 or FormatV2))
            throw new BackupDecryptException($"Unsupported backup format version {version}.");

        var salt = header.AsSpan(Magic.Length + 1, SaltBytes).ToArray();
        var p = Magic.Length + 1 + SaltBytes;
        var memKib = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p, 4));
        var iters = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p + 4, 4));
        var par = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p + 8, 4));

        var fingerprint = Array.Empty<byte>();
        if (version == FormatV2)
        {
            var lenByte = new byte[1];
            await ReadExactlyOrThrowAsync(input, lenByte, ct).ConfigureAwait(false);
            int fpLen = lenByte[0];
            if (fpLen > 0)
            {
                fingerprint = new byte[fpLen];
                await ReadExactlyOrThrowAsync(input, fingerprint, ct).ConfigureAwait(false);
            }
        }
        return (salt, memKib, iters, par, fingerprint);
    }

    /// <summary>
    /// Read just the KEK fingerprint from a backup's header (ADR-0071 D4)
    /// without decrypting — no passphrase needed, so the restore endpoint can
    /// pre-flight the cross-install check. Returns an empty array for a v1
    /// backup (no fingerprint). Throws <see cref="BackupDecryptException"/> when
    /// the stream isn't a Coffer backup.
    /// </summary>
    public static async Task<byte[]> ReadKekFingerprintAsync(
        Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (_, _, _, _, fingerprint) = await ReadHeaderAsync(input, cancellationToken)
            .ConfigureAwait(false);
        return fingerprint;
    }

    private static async Task WriteChunkAsync(
        Stream output, AesGcm gcm, ulong seq, bool isFinal, byte[] plaintext, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, Aad(seq, (byte)(isFinal ? 1 : 0)));

        var frameHeader = new byte[1 + 4 + NonceBytes];
        frameHeader[0] = (byte)(isFinal ? 1 : 0);
        BinaryPrimitives.WriteInt32BigEndian(frameHeader.AsSpan(1, 4), plaintext.Length);
        nonce.CopyTo(frameHeader, 5);

        await output.WriteAsync(frameHeader, ct).ConfigureAwait(false);
        await output.WriteAsync(ciphertext, ct).ConfigureAwait(false);
        await output.WriteAsync(tag, ct).ConfigureAwait(false);
    }

    /// <summary>Read up to <see cref="ChunkBytes"/> bytes; short only at EOF.</summary>
    private static async Task<byte[]> ReadBlockAsync(Stream input, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        var total = 0;
        while (total < ChunkBytes)
        {
            var n = await input.ReadAsync(buffer.AsMemory(total, ChunkBytes - total), ct)
                .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total == ChunkBytes ? buffer : buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>Fill <paramref name="buffer"/>; return bytes read (0 = clean EOF at start).</summary>
    private static async Task<int> ReadUpToAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task ReadExactlyOrThrowAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        if (await ReadUpToAsync(input, buffer, ct).ConfigureAwait(false) != buffer.Length)
            throw new BackupDecryptException("Backup is truncated mid-chunk.");
    }
}

/// <summary>
/// Thrown when a backup can't be decrypted — wrong passphrase, tampering,
/// truncation, or an unrecognized/unsupported envelope.
/// </summary>
public sealed class BackupDecryptException : Exception
{
    public BackupDecryptException(string message) : base(message) { }
    public BackupDecryptException(string message, Exception inner) : base(message, inner) { }
}
