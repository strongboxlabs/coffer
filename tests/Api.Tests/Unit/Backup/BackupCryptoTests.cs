using System.Security.Cryptography;

using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// Unit tests for the chunked AEAD backup envelope (ADR-0060). Pure crypto —
/// no DB. Covers round-trip across the chunk boundary, plus the failure modes
/// the format must reject: wrong passphrase, tampering, truncation, and a
/// non-Coffer / wrong-version header.
/// </summary>
public sealed class BackupCryptoTests
{
    private const string Pass = "correct horse battery staple";

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, string passphrase)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(input, passphrase, output);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] envelope, string passphrase)
    {
        using var input = new MemoryStream(envelope);
        using var output = new MemoryStream();
        await BackupCrypto.DecryptAsync(input, passphrase, output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]          // empty
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(65535)]      // just under one chunk
    [InlineData(65536)]      // exactly one chunk
    [InlineData(65537)]      // just over — forces a second chunk
    [InlineData(200_000)]    // several chunks
    public async Task Round_trips_plaintext_of_any_size(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var envelope = await EncryptAsync(plaintext, Pass);
        var decrypted = await DecryptAsync(envelope, Pass);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task Ciphertext_differs_from_plaintext_and_is_nondeterministic()
    {
        var plaintext = RandomNumberGenerator.GetBytes(4096);

        var a = await EncryptAsync(plaintext, Pass);
        var b = await EncryptAsync(plaintext, Pass);

        Assert.NotEqual(plaintext, a);
        // Fresh salt + nonces each run → two encryptions never match.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Wrong_passphrase_is_rejected()
    {
        var envelope = await EncryptAsync(RandomNumberGenerator.GetBytes(5000), Pass);

        await Assert.ThrowsAsync<BackupDecryptException>(
            () => DecryptAsync(envelope, "not the passphrase"));
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var envelope = await EncryptAsync(RandomNumberGenerator.GetBytes(5000), Pass);
        // Flip a bit deep in the body (past the header), inside chunk data.
        envelope[^20] ^= 0xFF;

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(envelope, Pass));
    }

    [Fact]
    public async Task Truncated_stream_is_rejected()
    {
        var envelope = await EncryptAsync(RandomNumberGenerator.GetBytes(200_000), Pass);
        var truncated = envelope.AsSpan(0, envelope.Length - 100).ToArray();

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(truncated, Pass));
    }

    [Fact]
    public async Task Trailing_data_after_the_final_chunk_is_rejected()
    {
        var envelope = await EncryptAsync(RandomNumberGenerator.GetBytes(100), Pass);
        var withJunk = envelope.Concat(new byte[] { 0, 1, 2, 3 }).ToArray();

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(withJunk, Pass));
    }

    [Fact]
    public async Task Non_coffer_input_is_rejected()
    {
        var notABackup = RandomNumberGenerator.GetBytes(500);

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(notABackup, Pass));
    }
}
