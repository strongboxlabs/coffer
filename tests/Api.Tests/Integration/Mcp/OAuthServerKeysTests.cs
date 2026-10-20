using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenIddict.Server;

using Coffer.Api.Auth;
using Coffer.Api.Crypto;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The OAuth server's signing and encryption keys live in the database, sealed under the
/// master key (FU-069). They were files no backup carried, so a restore onto a new host
/// signed every MCP client out; and a torn <c>signing.pem</c> bricked boot.
/// </summary>
/// <remarks>
/// Each test starts with no stored pair and ends with none, so the next ApiFactory boot in
/// the collection stores one this install opens, as a first boot would.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OAuthServerKeysTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"coffer-oauth-keys-{Guid.NewGuid():N}");

    public OAuthServerKeysTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The key the app runs under — ApiFactory pins 32 zero bytes.</summary>
    private static LedgerKeyService LocalKeys => new(new MasterKey(new byte[32], "v1"));

    private static LedgerKeyService ForeignKeys
    {
        get
        {
            var bytes = new byte[32];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 7);
            return new LedgerKeyService(new MasterKey(bytes, "source-v1"));
        }
    }

    public Task InitializeAsync() => ClearAsync();

    public async Task DisposeAsync()
    {
        await ClearAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task ClearAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.OidcServerKeys.ExecuteDeleteAsync();
    }

    private OpenIddictKeyMaterial NewMaterial(IReadOnlySet<string>? published = null) =>
        new(_fixture.NewServiceFactory(), TimeProvider.System, NullLogger<OpenIddictKeyMaterial>.Instance,
            published ?? OpenIddictKeyMaterial.PublishedKeyFingerprints);

    private static byte[] Modulus(OpenIddictKeyMaterial m) => m.Current.Signing.Rsa.ExportParameters(false).Modulus!;

    private string SigningPath => Path.Combine(_dir, OpenIddictKeyMaterial.SigningFileName);
    private string EncryptionPath => Path.Combine(_dir, OpenIddictKeyMaterial.EncryptionFileName);

    /// <summary>The files as earlier releases wrote them.</summary>
    private void WriteLegacy(RSA? signing, byte[]? encryption)
    {
        Directory.CreateDirectory(_dir);
        if (signing is not null) File.WriteAllText(SigningPath, signing.ExportRSAPrivateKeyPem());
        if (encryption is not null) File.WriteAllText(EncryptionPath, Convert.ToBase64String(encryption));
    }

    [Fact]
    public async Task A_first_boot_mints_a_pair_and_the_next_boot_loads_the_same_one()
    {
        var first = NewMaterial();
        Assert.True(await first.LoadAsync(LocalKeys, _dir));

        var second = NewMaterial();
        Assert.True(await second.LoadAsync(LocalKeys, _dir));

        Assert.Equal(Modulus(first), Modulus(second));
        Assert.Equal(first.Current.Encryption.Key, second.Current.Encryption.Key);
        Assert.Equal(OpenIddictKeyMaterial.SigningKeyId, second.Current.Signing.KeyId);
        Assert.Equal(OpenIddictKeyMaterial.EncryptionKeyId, second.Current.Encryption.KeyId);
    }

    [Fact]
    public async Task The_key_files_of_an_earlier_release_move_into_the_database_and_are_deleted()
    {
        using var rsa = RSA.Create(2048);
        var encryption = RandomNumberGenerator.GetBytes(32);
        WriteLegacy(rsa, encryption);

        var material = NewMaterial();
        Assert.True(await material.LoadAsync(LocalKeys, _dir));

        // The same keys, so connected clients stay signed in across the upgrade.
        Assert.Equal(rsa.ExportParameters(false).Modulus, Modulus(material));
        Assert.Equal(encryption, material.Current.Encryption.Key);
        Assert.False(Directory.Exists(_dir), "the plaintext key files (and their directory) should be gone");

        // Stored sealed under the master key, not as the plaintext.
        await using (var db = _fixture.NewDbContext())
        {
            var row = await db.OidcServerKeys.AsNoTracking().SingleAsync();
            Assert.Equal(rsa.ExportRSAPrivateKey(), LocalKeys.OpenWithMasterKey(row.SigningKeyCiphertext));
            Assert.Equal(encryption, LocalKeys.OpenWithMasterKey(row.EncryptionKeyCiphertext));
        }

        var next = NewMaterial();
        Assert.True(await next.LoadAsync(LocalKeys, _dir));
        Assert.Equal(Modulus(material), Modulus(next));
    }

    [Fact]
    public async Task A_torn_signing_file_is_replaced_and_the_encryption_key_still_moves_in()
    {
        // What a crash part-way through the old non-atomic write left, and what bricked boot.
        using var rsa = RSA.Create(2048);
        var encryption = RandomNumberGenerator.GetBytes(32);
        WriteLegacy(null, encryption);
        var pem = rsa.ExportRSAPrivateKeyPem();
        File.WriteAllText(SigningPath, pem[..(pem.Length / 2)]);

        var material = NewMaterial();
        Assert.True(await material.LoadAsync(LocalKeys, _dir));

        Assert.NotEqual(rsa.ExportParameters(false).Modulus, Modulus(material));
        Assert.Equal(encryption, material.Current.Encryption.Key);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task A_torn_encryption_file_is_replaced_and_the_signing_key_still_moves_in()
    {
        using var rsa = RSA.Create(2048);
        var encryption = RandomNumberGenerator.GetBytes(32);
        WriteLegacy(rsa, null);
        File.WriteAllText(EncryptionPath, Convert.ToBase64String(encryption)[..20]);   // 15 bytes

        var material = NewMaterial();
        Assert.True(await material.LoadAsync(LocalKeys, _dir));

        Assert.Equal(rsa.ExportParameters(false).Modulus, Modulus(material));
        Assert.NotEqual(encryption, material.Current.Encryption.Key);
        Assert.Equal(32, material.Current.Encryption.Key.Length);
    }

    [Fact]
    public async Task The_pair_published_in_early_snapshots_is_replaced_not_moved_in()
    {
        // No published key can sit in a tracked file, so a generated pair stands in for it.
        using var rsa = RSA.Create(2048);
        var encryption = RandomNumberGenerator.GetBytes(32);
        WriteLegacy(rsa, encryption);
        var published = new HashSet<string>
        {
            OpenIddictKeyMaterial.Fingerprint(rsa.ExportSubjectPublicKeyInfo()),
            OpenIddictKeyMaterial.Fingerprint(encryption),
        };

        var material = NewMaterial(published);
        Assert.True(await material.LoadAsync(LocalKeys, _dir));

        Assert.NotEqual(rsa.ExportParameters(false).Modulus, Modulus(material));
        Assert.NotEqual(encryption, material.Current.Encryption.Key);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void The_published_fingerprints_are_two_sha256_digests()
    {
        // A typo here would make the refusal above match nothing, silently.
        Assert.Equal(2, OpenIddictKeyMaterial.PublishedKeyFingerprints.Count);
        Assert.All(OpenIddictKeyMaterial.PublishedKeyFingerprints,
            f => Assert.Matches("^[0-9a-f]{64}$", f));
    }

    [Fact]
    public async Task A_pair_sealed_under_another_master_key_is_neither_loaded_nor_overwritten()
    {
        // As the master key file being swapped outside a restore leaves it (a restore's
        // reconciliation removes such a pair first).
        Assert.True(await NewMaterial().LoadAsync(ForeignKeys, _dir));
        byte[] stored;
        await using (var db = _fixture.NewDbContext())
            stored = (await db.OidcServerKeys.AsNoTracking().SingleAsync()).SigningKeyCiphertext;
        using var rsa = RSA.Create(2048);
        WriteLegacy(rsa, RandomNumberGenerator.GetBytes(32));

        var material = NewMaterial();
        Assert.False(await material.LoadAsync(LocalKeys, _dir));

        Assert.Throws<InvalidOperationException>(() => material.Current);
        await using (var db = _fixture.NewDbContext())
            Assert.Equal(stored, (await db.OidcServerKeys.AsNoTracking().SingleAsync()).SigningKeyCiphertext);
        Assert.True(File.Exists(SigningPath), "nothing should be deleted while the keys do not open");
    }

    [Fact]
    public async Task Reset_replaces_the_pair()
    {
        var before = NewMaterial();
        Assert.True(await before.LoadAsync(LocalKeys, _dir));

        await NewMaterial().ResetAsync(LocalKeys);

        var after = NewMaterial();
        Assert.True(await after.LoadAsync(LocalKeys, _dir));
        Assert.NotEqual(Modulus(before), Modulus(after));
        Assert.NotEqual(before.Current.Encryption.Key, after.Current.Encryption.Key);
    }

    [Fact]
    public async Task OpenIddict_gets_the_credentials_its_own_AddSigningKey_and_AddEncryptionKey_register()
    {
        var material = NewMaterial();
        Assert.True(await material.LoadAsync(LocalKeys, _dir));

        var services = new ServiceCollection();
        services.AddOpenIddict().AddServer(o =>
        {
            o.AddSigningKey(material.Current.Signing);
            o.AddEncryptionKey(material.Current.Encryption);
        });
        await using var provider = services.BuildServiceProvider();
        var reference = new OpenIddictServerOptions();
        foreach (var configure in provider.GetServices<IConfigureOptions<OpenIddictServerOptions>>())
            configure.Configure(reference);

        var ours = new OpenIddictServerOptions();
        new OpenIddictKeysFromDatabase(material).Configure(ours);

        var referenceSigning = Assert.Single(reference.SigningCredentials);
        var ourSigning = Assert.Single(ours.SigningCredentials);
        Assert.Same(referenceSigning.Key, ourSigning.Key);
        Assert.Equal(referenceSigning.Algorithm, ourSigning.Algorithm);
        Assert.Equal(referenceSigning.Digest, ourSigning.Digest);

        var referenceEncryption = Assert.Single(reference.EncryptionCredentials);
        var ourEncryption = Assert.Single(ours.EncryptionCredentials);
        Assert.Same(referenceEncryption.Key, ourEncryption.Key);
        Assert.Equal(referenceEncryption.Alg, ourEncryption.Alg);
        Assert.Equal(referenceEncryption.Enc, ourEncryption.Enc);
    }
}
