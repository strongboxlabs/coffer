using System.Security.Cryptography;

using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// ADR-0071 D4: the KEK fingerprint helper + the v2 <c>.cofferbak</c> header
/// that carries it. Pure crypto — no DB.
/// </summary>
public sealed class BackupCryptoFingerprintTests
{
    [Fact]
    public void Fingerprint_is_deterministic_key_dependent_and_fixed_length()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);

        var fp1 = KekFingerprint.Compute(k1);
        Assert.Equal(KekFingerprint.Bytes, fp1.Length);
        Assert.Equal(fp1, KekFingerprint.Compute(k1));            // deterministic
        Assert.NotEqual(fp1, KekFingerprint.Compute(k2));         // key-dependent
        Assert.True(KekFingerprint.Matches(fp1, KekFingerprint.Compute(k1)));
        Assert.False(KekFingerprint.Matches(fp1, KekFingerprint.Compute(k2)));
    }

    [Fact]
    public async Task V2_backup_records_the_fingerprint_and_still_round_trips()
    {
        var fp = KekFingerprint.Compute(RandomNumberGenerator.GetBytes(32));
        var plaintext = RandomNumberGenerator.GetBytes(200_000);   // spans multiple chunks

        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plaintext), "pass-123", encrypted, fp);

        // The fingerprint is readable WITHOUT the passphrase (pre-flight check).
        encrypted.Position = 0;
        Assert.Equal(fp, await BackupCrypto.ReadKekFingerprintAsync(encrypted));

        // Full decrypt is unaffected.
        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await BackupCrypto.DecryptAsync(encrypted, "pass-123", decrypted);
        Assert.Equal(plaintext, decrypted.ToArray());
    }

    [Fact]
    public async Task V1_backup_reports_no_fingerprint_but_still_decrypts()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);

        using var encrypted = new MemoryStream();
        // 3-arg overload => legacy v1 header, no fingerprint (existing artifacts).
        await BackupCrypto.EncryptAsync(new MemoryStream(plaintext), "pw", encrypted);

        encrypted.Position = 0;
        Assert.Empty(await BackupCrypto.ReadKekFingerprintAsync(encrypted));

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await BackupCrypto.DecryptAsync(encrypted, "pw", decrypted);
        Assert.Equal(plaintext, decrypted.ToArray());
    }

    [Fact]
    public async Task Wrong_passphrase_still_fails_on_a_v2_backup()
    {
        var fp = KekFingerprint.Compute(RandomNumberGenerator.GetBytes(32));
        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(1000)), "right", encrypted, fp);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await Assert.ThrowsAsync<BackupDecryptException>(() =>
            BackupCrypto.DecryptAsync(encrypted, "wrong", decrypted));
    }
}
