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
///           · [v2, v3] fpLen(1) · kekFingerprint(fpLen)
///   chunk*: isFinal(1) · ctLen(4) · nonce(12) · ciphertext(ctLen) · tag(16)
///   AAD per chunk = [v3] header · seq(8) · isFinal(1)
///                   [v1, v2] seq(8) · isFinal(1)
///
/// v2 (ADR-0071 D4) appends a KEK fingerprint to the header for the restore-time
/// cross-install check; v1 has no fingerprint. v3 (FU-072) has v2's layout and binds the
/// WHOLE header into every chunk's associated data, so a header that was changed — a
/// fingerprint swapped or stripped, a version downgraded, a KDF parameter altered — fails
/// every tag. Before v3 the header was authenticated by nothing, which is what let an
/// edited archive steer key adoption. Writers emit v3; readers accept all three.
///
/// The KDF parameters ride in the header so a future parameter bump can still
/// decrypt old artifacts. They are BOUNDED before use (see <see cref="MaxMemoryKib"/>):
/// a header is attacker-controlled until its tags verify, and Argon2 cannot be cancelled,
/// so unbounded parameters let one crafted file pin a core or exhaust memory.
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "COFFERBAK"u8.ToArray();
    // v1: no KEK fingerprint. v2 (ADR-0071 D4): header appends fpLen(1)·fingerprint.
    // Readers accept both; a writer emits v2 iff a fingerprint is supplied.
    private const byte FormatV1 = 1;
    private const byte FormatV2 = 2;
    // v3 (FU-072): v2's layout, with the whole header bound into each chunk's AAD.
    private const byte FormatV3 = 3;

    // Argon2id parameters (ADR-0037): 64 MiB, 3 passes, single lane.
    private const int MemoryKib = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;

    // What a header's KDF parameters may ask for. Every writer so far has emitted exactly
    // 65536 KiB / 3 / 1; the bounds give a future bump four times the headroom on memory
    // and more on time, and stop there. They exist because the parameters are read BEFORE
    // anything is authenticated and Argon2 runs to completion uncancellably: a crafted
    // header asking for 4 TiB or a billion passes is a denial of service on the one
    // endpoint a setup-token holder can reach (FU-072).
    internal const int MaxMemoryKib = 4 * MemoryKib;
    internal const int MaxIterations = 16;
    internal const int MaxParallelism = 4;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Plaintext block size (64 KiB). One block in flight at a time.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Encrypt with no KEK fingerprint (format v3, empty fingerprint).</summary>
    public static Task EncryptAsync(
        Stream input,
        string passphrase,
        Stream output,
        CancellationToken cancellationToken = default)
        => EncryptAsync(input, passphrase, output, kekFingerprint: default, cancellationToken);

    /// <summary>
    /// Encrypt <paramref name="input"/> into <paramref name="output"/> under
    /// <paramref name="passphrase"/>. When <paramref name="kekFingerprint"/> is
    /// non-empty it's recorded in the header (ADR-0071 D4; format v3 binds it, FU-072) for the
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

        await EncryptCoreAsync(input, passphrase, output, kekFingerprint, FormatV3, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The writer, for any format. Production writes v3 only; the older versions are
    /// written here so tests can prove they still READ, which every backup taken before
    /// v3 depends on.
    /// </summary>
    internal static async Task EncryptCoreAsync(
        Stream input,
        string passphrase,
        Stream output,
        ReadOnlyMemory<byte> kekFingerprint,
        byte version,
        CancellationToken cancellationToken)
    {
        if (version is not (FormatV1 or FormatV2 or FormatV3))
            throw new ArgumentOutOfRangeException(nameof(version));
        if (version == FormatV1 && kekFingerprint.Length > 0)
            throw new ArgumentException("v1 carries no fingerprint.", nameof(kekFingerprint));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(passphrase, salt, MemoryKib, Iterations, Parallelism);
        try
        {
            var header = BuildHeader(version, salt, kekFingerprint);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            var boundHeader = version == FormatV3 ? header : [];

            using var gcm = new AesGcm(key, TagBytes);

            // One-block read-ahead so each chunk knows whether it's the last.
            var current = await ReadBlockAsync(input, cancellationToken).ConfigureAwait(false);
            ulong seq = 0;
            do
            {
                var next = await ReadBlockAsync(input, cancellationToken).ConfigureAwait(false);
                var isFinal = next.Length == 0;
                await WriteChunkAsync(output, gcm, boundHeader, seq, isFinal, current, cancellationToken)
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

        var head = await ReadHeaderAsync(input, cancellationToken).ConfigureAwait(false);
        var key = DeriveKey(passphrase, head.Salt, head.MemKib, head.Iters, head.Par);
        var boundHeader = head.Version == FormatV3 ? head.Raw : [];
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
                    gcm.Decrypt(nonce, ciphertext, tag, plaintext, Aad(boundHeader, seq, isFinal));
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

    /// <summary>Per-chunk associated data: the header (v3; empty before), then the
    /// sequence number and the final flag.</summary>
    private static byte[] Aad(byte[] boundHeader, ulong seq, byte isFinal)
    {
        var aad = new byte[boundHeader.Length + 9];
        boundHeader.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(boundHeader.Length, 8), seq);
        aad[boundHeader.Length + 8] = isFinal;
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

    private static byte[] BuildHeader(byte version, byte[] salt, ReadOnlyMemory<byte> fingerprint)
    {
        if (fingerprint.Length > byte.MaxValue)
            throw new ArgumentException("A fingerprint longer than 255 bytes cannot be recorded.", nameof(fingerprint));
        var hasFpField = version != FormatV1;
        var baseLen = Magic.Length + 1 + SaltBytes + 12;
        var header = new byte[hasFpField ? baseLen + 1 + fingerprint.Length : baseLen];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = version;
        salt.CopyTo(header, Magic.Length + 1);
        var p = Magic.Length + 1 + SaltBytes;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p, 4), MemoryKib);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p + 4, 4), Iterations);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p + 8, 4), Parallelism);
        if (hasFpField)
        {
            header[baseLen] = (byte)fingerprint.Length;
            fingerprint.Span.CopyTo(header.AsSpan(baseLen + 1));
        }
        return header;
    }

    private sealed record Header(
        byte Version, byte[] Salt, int MemKib, int Iters, int Par, byte[] Fingerprint, byte[] Raw);

    private static async Task<Header> ReadHeaderAsync(Stream input, CancellationToken ct)
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
        if (version is not (FormatV1 or FormatV2 or FormatV3))
            throw new BackupDecryptException(
                $"Unsupported backup format version {version}. A backup made by a newer Coffer "
                + "needs that version (or newer) to restore it.");

        var salt = header.AsSpan(Magic.Length + 1, SaltBytes).ToArray();
        var p = Magic.Length + 1 + SaltBytes;
        var memKib = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p, 4));
        var iters = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p + 4, 4));
        var par = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(p + 8, 4));
        if (memKib < 8 * Math.Max(par, 1) || memKib > MaxMemoryKib
            || iters < 1 || iters > MaxIterations
            || par < 1 || par > MaxParallelism)
            throw new BackupDecryptException(
                $"Backup header asks for key-derivation settings outside what Coffer writes "
                + $"(memory {memKib} KiB, {iters} passes, {par} lanes); refusing to derive with them.");

        var fingerprint = Array.Empty<byte>();
        var raw = header;
        if (version is FormatV2 or FormatV3)
        {
            var lenByte = new byte[1];
            await ReadExactlyOrThrowAsync(input, lenByte, ct).ConfigureAwait(false);
            int fpLen = lenByte[0];
            if (fpLen > 0)
            {
                fingerprint = new byte[fpLen];
                await ReadExactlyOrThrowAsync(input, fingerprint, ct).ConfigureAwait(false);
            }
            raw = [.. header, lenByte[0], .. fingerprint];
        }
        return new Header(version, salt, memKib, iters, par, fingerprint, raw);
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
        var head = await ReadHeaderAsync(input, cancellationToken).ConfigureAwait(false);
        return head.Fingerprint;
    }

    private static async Task WriteChunkAsync(
        Stream output, AesGcm gcm, byte[] boundHeader, ulong seq, bool isFinal, byte[] plaintext,
        CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, Aad(boundHeader, seq, (byte)(isFinal ? 1 : 0)));

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
