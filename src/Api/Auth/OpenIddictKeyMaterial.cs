using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using OpenIddict.Server;

using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Auth;

/// <summary>
/// Persistent signing + encryption keys for the OpenIddict AS (ADR-0063 §D2: persistent
/// keys, not ephemeral dev certs, so issued tokens survive restarts). Kept in
/// <c>oidc_server_keys</c>, sealed under the master KEK (FU-069), loaded once at boot
/// before the server serves, and handed to OpenIddict by
/// <see cref="OpenIddictKeysFromDatabase"/>.
/// </summary>
/// <remarks>
/// <para>They used to be files under <c>data/openiddict</c>. No backup carried them, so a
/// restore onto a new host invalidated every MCP token (ID2004); and they were written
/// 0644 and non-atomically, so a torn <c>signing.pem</c> bricked boot. In the database
/// they travel with every backup, rotation re-wraps them, and post-restore reconciliation
/// replaces a pair sealed under another install's key.</para>
/// <para>The first boot with the table moves the files in and deletes them, so connected
/// clients stay signed in across the upgrade. A key is minted instead when its file cannot
/// be read as a key — what a torn write leaves — or when it is the pair that was published
/// in early public snapshots (operations.md, Secrets), which no install should keep.</para>
/// <para>On a key compromise, <c>coffer-api reset-oauth-keys</c> replaces the pair; after
/// a restart every MCP client must re-authorize. Web sign-in does not use these keys.</para>
/// </remarks>
public sealed class OpenIddictKeyMaterial
{
    public const string SigningKeyId = "coffer-oidc-sign-1";
    public const string EncryptionKeyId = "coffer-oidc-enc-1";
    internal const string SigningFileName = "signing.pem";
    internal const string EncryptionFileName = "encryption.key";
    private const short SingletonId = 1;

    /// <summary>
    /// SHA-256 (hex) of the SubjectPublicKeyInfo of the signing key, and of the raw
    /// encryption key, that were committed and published. Anyone can read them, so an
    /// import that finds either mints a replacement instead of moving it in.
    /// </summary>
    internal static readonly IReadOnlySet<string> PublishedKeyFingerprints =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "942c2c0bd185b786ba94bd6d687da89d155e525a780eabbf42ce54e415841f8d",   // signing.pem
            "1ad58c7afcb446bd508d8eca9e0d885128bdae65b373b6ed84332abd9eae9b24",   // encryption.key
        };

    public sealed record Keys(RsaSecurityKey Signing, SymmetricSecurityKey Encryption);

    private readonly ServiceDbContextFactory _factory;
    private readonly TimeProvider _clock;
    private readonly ILogger<OpenIddictKeyMaterial> _logger;
    private readonly IReadOnlySet<string> _published;
    private Keys? _current;

    public OpenIddictKeyMaterial(
        ServiceDbContextFactory factory, TimeProvider clock, ILogger<OpenIddictKeyMaterial> logger)
        : this(factory, clock, logger, PublishedKeyFingerprints)
    {
    }

    /// <param name="published">Fingerprints to refuse on import. A test passes its own key's,
    /// since the published pair cannot sit in a tracked file.</param>
    internal OpenIddictKeyMaterial(
        ServiceDbContextFactory factory, TimeProvider clock, ILogger<OpenIddictKeyMaterial> logger,
        IReadOnlySet<string> published)
    {
        _factory = factory;
        _clock = clock;
        _logger = logger;
        _published = published;
    }

    /// <summary>The keys loaded at boot.</summary>
    /// <exception cref="InvalidOperationException">Boot did not load them.</exception>
    public Keys Current => Volatile.Read(ref _current) ?? throw new InvalidOperationException(
        "The OAuth server keys are not loaded. They load at boot, before the server serves; "
        + "if the boot log says they do not open under this install's master key, restore that "
        + "key or run `coffer-api reset-oauth-keys` (every MCP client then re-authorizes).");

    /// <summary>
    /// Boot: load the pair from the database. With none stored, move it in from
    /// <paramref name="legacyDirectory"/>, minting whatever is missing or unusable there.
    /// Key files left in that directory are deleted once the database holds a pair that
    /// opens.
    /// </summary>
    /// <returns>False, with <see cref="Current"/> left unset and the files kept, when the
    /// stored pair does not open under this install's master key.</returns>
    public async Task<bool> LoadAsync(
        LedgerKeyService keys, string legacyDirectory, CancellationToken ct = default)
    {
        var row = await ReadRowAsync(ct).ConfigureAwait(false)
            ?? await StoreAsync(keys, ReadLegacy(legacyDirectory), replace: false, ct).ConfigureAwait(false);

        Keys loaded;
        try
        {
            loaded = Open(keys, row);
        }
        catch (CryptographicException ex)
        {
            // Reconciliation deletes a restored pair it cannot open, so this is a master
            // key that changed outside a restore. Overwriting would destroy the right pair
            // if the operator then mounts the right key, so nothing is written here.
            _logger.LogError(ex,
                "The OAuth server keys do not open under this install's master key, so MCP "
                + "sign-in is unavailable. Restore the master key they were sealed under, or "
                + "run `coffer-api reset-oauth-keys` to replace them.");
            return false;
        }

        DeleteLegacyFiles(legacyDirectory);
        Volatile.Write(ref _current, loaded);
        return true;
    }

    /// <summary>
    /// Replace the stored pair with a freshly minted one (the operator's remedy for a key
    /// compromise). A running server keeps the old pair until it restarts.
    /// </summary>
    public Task ResetAsync(LedgerKeyService keys, CancellationToken ct = default) =>
        StoreAsync(keys, new Material(MintSigning(), MintEncryption()), replace: true, ct);

    private async Task<OidcServerKeysRow?> ReadRowAsync(CancellationToken ct)
    {
        await using var db = _factory.Create();
        return await db.OidcServerKeys.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == SingletonId, ct).ConfigureAwait(false);
    }

    private async Task<OidcServerKeysRow> StoreAsync(
        LedgerKeyService keys, Material material, bool replace, CancellationToken ct)
    {
        try
        {
            // Sealed and saved under one lease, so a rotation cannot start between the two
            // and strand the pair under the retired key (FU-078).
            using var sealing = keys.EnterSealing();
            await using var db = _factory.Create();
            var row = replace
                ? await db.OidcServerKeys.FirstOrDefaultAsync(r => r.Id == SingletonId, ct).ConfigureAwait(false)
                : null;
            if (row is null)
            {
                row = new OidcServerKeysRow { Id = SingletonId };
                db.OidcServerKeys.Add(row);
            }
            row.SigningKeyCiphertext = keys.SealWithMasterKey(material.SigningDer);
            row.EncryptionKeyCiphertext = keys.SealWithMasterKey(material.Encryption);
            row.CreatedAt = _clock.GetUtcNow().UtcDateTime;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (!replace
                && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // Another process stored a pair first. Theirs is the pair; this one is dropped.
                return await ReadRowAsync(ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The OAuth key row vanished after a conflicting insert.", ex);
            }
            return row;
        }
        finally
        {
            material.Clear();
        }
    }

    private static Keys Open(LedgerKeyService keys, OidcServerKeysRow row)
    {
        var der = keys.OpenWithMasterKey(row.SigningKeyCiphertext);
        var encryption = keys.OpenWithMasterKey(row.EncryptionKeyCiphertext);
        try
        {
            // Not disposed: the security key holds it for the life of the process.
            var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(der, out _);
            return new Keys(
                new RsaSecurityKey(rsa) { KeyId = SigningKeyId },
                new SymmetricSecurityKey(encryption.ToArray()) { KeyId = EncryptionKeyId });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(encryption);
        }
    }

    // --- the files earlier releases kept ---------------------------------------------

    private Material ReadLegacy(string directory) => new(
        ReadLegacySigning(Path.Combine(directory, SigningFileName)) ?? MintSigning(),
        ReadLegacyEncryption(Path.Combine(directory, EncryptionFileName)) ?? MintEncryption());

    /// <summary>The key in <paramref name="path"/> as PKCS#1 DER, or null to mint one.
    /// A file that cannot be READ (permissions, I/O) throws instead: it may hold a good key,
    /// and minting would let the delete that follows destroy it.</summary>
    private byte[]? ReadLegacySigning(string path)
    {
        if (!File.Exists(path)) return null;
        var pem = File.ReadAllText(path);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (IsPublished(rsa.ExportSubjectPublicKeyInfo()))
            {
                _logger.LogWarning(
                    "{Path} holds the OAuth signing key that was published in early public "
                    + "snapshots; minting a new one instead of keeping it. MCP clients will "
                    + "need to re-authorize.", path);
                return null;
            }
            _logger.LogInformation("Moving the OAuth signing key from {Path} into the database.", path);
            return rsa.ExportRSAPrivateKey();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "{Path} is not a readable RSA private key (a torn write leaves this); minting a "
                + "new signing key. MCP clients will need to re-authorize.", path);
            return null;
        }
    }

    private byte[]? ReadLegacyEncryption(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        byte[] key;
        try
        {
            key = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex,
                "{Path} is not a base64 key (a torn write leaves this); minting a new token "
                + "encryption key. MCP clients will need to re-authorize.", path);
            return null;
        }
        if (key.Length != 32)
        {
            _logger.LogWarning(
                "{Path} holds {Length} bytes, not a 256-bit key; minting a new token encryption "
                + "key. MCP clients will need to re-authorize.", path, key.Length);
            return null;
        }
        if (IsPublished(key))
        {
            _logger.LogWarning(
                "{Path} holds the token encryption key that was published in early public "
                + "snapshots; minting a new one instead of keeping it.", path);
            return null;
        }
        _logger.LogInformation("Moving the OAuth token encryption key from {Path} into the database.", path);
        return key;
    }

    private void DeleteLegacyFiles(string directory)
    {
        foreach (var name in new[] { SigningFileName, EncryptionFileName })
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                _logger.LogInformation("Deleted {Path}: the database holds the OAuth keys now.", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on every boot until it succeeds; the database copy is what is used.
                _logger.LogWarning(ex,
                    "Could not delete {Path}, a plaintext copy of an OAuth key the database now "
                    + "holds. Delete it by hand.", path);
            }
        }
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Left the empty directory {Directory} in place.", directory);
        }
    }

    private bool IsPublished(byte[] material) => _published.Contains(Fingerprint(material));

    /// <summary>Lower-case hex SHA-256: for the signing key, of its SubjectPublicKeyInfo
    /// (what <c>openssl rsa -pubout -outform DER | sha256sum</c> hashes).</summary>
    internal static string Fingerprint(byte[] material) =>
        Convert.ToHexStringLower(SHA256.HashData(material));

    private static byte[] MintSigning()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKey();
    }

    private static byte[] MintEncryption() => RandomNumberGenerator.GetBytes(32);

    private sealed record Material(byte[] SigningDer, byte[] Encryption)
    {
        public void Clear()
        {
            CryptographicOperations.ZeroMemory(SigningDer);
            CryptographicOperations.ZeroMemory(Encryption);
        }
    }
}

/// <summary>
/// Gives OpenIddict the keys <see cref="OpenIddictKeyMaterial"/> loaded at boot.
/// <c>AddSigningKey</c>/<c>AddEncryptionKey</c> take the key while services are being
/// registered, before migrations have run and before anything has been opened; this runs
/// when OpenIddict first reads its options, at the first OAuth request.
/// </summary>
internal sealed class OpenIddictKeysFromDatabase : IConfigureOptions<OpenIddictServerOptions>
{
    private readonly OpenIddictKeyMaterial _material;

    public OpenIddictKeysFromDatabase(OpenIddictKeyMaterial material) => _material = material;

    public void Configure(OpenIddictServerOptions options)
    {
        var keys = _material.Current;
        // The credentials AddSigningKey and AddEncryptionKey register for an RSA and a
        // 256-bit symmetric key; a test holds the two equal.
        options.SigningCredentials.Add(new SigningCredentials(keys.Signing, SecurityAlgorithms.RsaSha256));
        options.EncryptionCredentials.Add(new EncryptingCredentials(
            keys.Encryption, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512));
    }
}
