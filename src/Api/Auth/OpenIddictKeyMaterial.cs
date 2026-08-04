using System.Security.Cryptography;

using Microsoft.IdentityModel.Tokens;

namespace Coffer.Api.Auth;

/// <summary>
/// Persistent signing + encryption keys for the OpenIddict AS (ADR-0063 §D2,
/// decision: persistent keys, not ephemeral dev certs). Generated once into the
/// data directory and reloaded on every boot, so issued tokens survive container
/// restarts (ephemeral keys would invalidate every token on each restart).
/// </summary>
/// <remarks>
/// The key files hold private material; they live under the same data volume as
/// the database and backups, with the same trust boundary. RSA-2048 for signing
/// (RS256), a 256-bit symmetric key for token encryption (reference-token
/// payloads). On a key-compromise the operator deletes the files and restarts —
/// new keys are minted and all outstanding tokens become unverifiable.
/// </remarks>
public static class OpenIddictKeyMaterial
{
    public sealed record Keys(RsaSecurityKey Signing, SymmetricSecurityKey Encryption);

    /// <summary>Load the keys from <paramref name="directory"/>, generating and
    /// persisting them on first run.</summary>
    public static Keys LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var signingPath = Path.Combine(directory, "signing.pem");
        var encryptionPath = Path.Combine(directory, "encryption.key");

        var rsa = RSA.Create(2048);
        if (File.Exists(signingPath))
        {
            rsa.ImportFromPem(File.ReadAllText(signingPath));
        }
        else
        {
            File.WriteAllText(signingPath, rsa.ExportRSAPrivateKeyPem());
        }

        byte[] encryption;
        if (File.Exists(encryptionPath))
        {
            encryption = Convert.FromBase64String(File.ReadAllText(encryptionPath).Trim());
        }
        else
        {
            encryption = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(encryptionPath, Convert.ToBase64String(encryption));
        }

        return new Keys(
            new RsaSecurityKey(rsa) { KeyId = "coffer-oidc-sign-1" },
            new SymmetricSecurityKey(encryption) { KeyId = "coffer-oidc-enc-1" });
    }
}
