using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// The v3 archive header and the key-derivation bounds (FU-072). Before v3 nothing
/// authenticated the header: a version could be downgraded, a fingerprint swapped or
/// stripped, and that is what let an edited archive steer key adoption. And the KDF
/// parameters in it were used as read, so a crafted file could ask Argon2 for any amount of
/// memory and time, uncancellably.
/// </summary>
public sealed class BackupCryptoFormatTests
{
    // Header layout: magic(9) · version(1) · salt(16) · mem(4) · iters(4) · par(4) · fpLen(1) · fp
    private const int VersionAt = 9;
    private const int MemAt = 26;
    private const int ItersAt = 30;
    private const int ParAt = 34;
    private const int FpLenAt = 38;
    private const int FpAt = 39;

    private static readonly byte[] Fingerprint = KekFingerprint.Compute(RandomNumberGenerator.GetBytes(32));

    private static async Task<byte[]> V3Async(byte[] plaintext, byte[]? fingerprint = null)
    {
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plaintext), "pw", output, fingerprint ?? Fingerprint);
        return output.ToArray();
    }

    private static async Task<byte[]> LegacyAsync(byte version, byte[] plaintext, byte[]? fingerprint)
    {
        using var output = new MemoryStream();
        await BackupCrypto.EncryptCoreAsync(
            new MemoryStream(plaintext), "pw", output, fingerprint ?? default(ReadOnlyMemory<byte>), version, default);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] archive, string passphrase = "pw")
    {
        using var output = new MemoryStream();
        await BackupCrypto.DecryptAsync(new MemoryStream(archive), passphrase, output);
        return output.ToArray();
    }

    [Fact]
    public async Task New_backups_are_written_as_v3_and_round_trip()
    {
        var plaintext = RandomNumberGenerator.GetBytes(150_000);   // several chunks
        var archive = await V3Async(plaintext);

        Assert.Equal(3, archive[VersionAt]);
        Assert.Equal(plaintext, await DecryptAsync(archive));
        Assert.Equal(Fingerprint, await BackupCrypto.ReadKekFingerprintAsync(new MemoryStream(archive)));
    }

    [Fact]
    public async Task A_v3_archive_whose_fingerprint_was_swapped_does_not_decrypt()
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(1000));
        var other = KekFingerprint.Compute(RandomNumberGenerator.GetBytes(32));
        other.CopyTo(archive, FpAt);   // same length, different key

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(archive));
    }

    [Fact]
    public async Task A_v3_archive_whose_fingerprint_was_stripped_does_not_decrypt()
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(1000));
        int fpLen = archive[FpLenAt];
        var stripped = archive.Take(FpLenAt).Append((byte)0).Concat(archive.Skip(FpAt + fpLen)).ToArray();

        // It now reads as "no fingerprint", which a pre-flight would take at face value...
        Assert.Empty(await BackupCrypto.ReadKekFingerprintAsync(new MemoryStream(stripped)));
        // ...and the passphrase check that follows it refuses the archive.
        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(stripped));
    }

    [Fact]
    public async Task A_v3_archive_downgraded_to_v2_does_not_decrypt()
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(1000));
        archive[VersionAt] = 2;   // same layout, but v2's chunks were sealed without the header

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(archive));
    }

    [Fact]
    public async Task A_v3_archive_with_an_edited_kdf_parameter_does_not_decrypt()
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(1000));
        // Inside the bounds, so it is not refused for that: the tags refuse it instead.
        BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(ItersAt, 4), 2);

        await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(archive));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Backups_taken_before_v3_still_restore(byte version)
    {
        // Every backup on disk and on Drive today is v1 or v2.
        var plaintext = RandomNumberGenerator.GetBytes(80_000);
        var fp = version == 2 ? Fingerprint : null;
        var archive = await LegacyAsync(version, plaintext, fp);

        Assert.Equal(version, archive[VersionAt]);
        Assert.Equal(plaintext, await DecryptAsync(archive));
        Assert.Equal(fp ?? [], await BackupCrypto.ReadKekFingerprintAsync(new MemoryStream(archive)));
    }

    [Theory]
    [InlineData(MemAt, int.MaxValue)]        // ~2 TiB of Argon2 memory
    [InlineData(MemAt, 4 * 65536 + 1)]       // just past the bound
    [InlineData(MemAt, 4)]                   // below Argon2's own floor
    [InlineData(ItersAt, 1_000_000_000)]     // a billion passes
    [InlineData(ItersAt, 0)]
    [InlineData(ParAt, 0)]
    [InlineData(ParAt, 64)]
    public async Task Key_derivation_settings_outside_the_bounds_are_refused_before_deriving(int at, int value)
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(100));
        BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(at, 4), value);

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(archive));
        clock.Stop();

        Assert.Contains("key-derivation settings", ex.Message);
        // Refused from the header alone; deriving with these would take minutes or OOM.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        // The pre-flight read (no passphrase) refuses it too, so it never reaches a derive.
        await Assert.ThrowsAsync<BackupDecryptException>(() =>
            BackupCrypto.ReadKekFingerprintAsync(new MemoryStream(archive)));
    }

    [Fact]
    public async Task The_settings_every_writer_has_used_are_within_the_bounds()
    {
        // 65536 KiB / 3 / 1 is what this and every earlier writer emitted; a bound that
        // refused them would make every existing backup unrestorable.
        var archive = await V3Async(RandomNumberGenerator.GetBytes(100));

        Assert.Equal(65536, BinaryPrimitives.ReadInt32BigEndian(archive.AsSpan(MemAt, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(archive.AsSpan(ItersAt, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(archive.AsSpan(ParAt, 4)));
        Assert.NotEmpty(await DecryptAsync(archive));
    }

    [Fact]
    public async Task An_unknown_format_version_names_the_way_out()
    {
        var archive = await V3Async(RandomNumberGenerator.GetBytes(100));
        archive[VersionAt] = 9;

        var ex = await Assert.ThrowsAsync<BackupDecryptException>(() => DecryptAsync(archive));
        Assert.Contains("newer Coffer", ex.Message);
    }
}
