namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>oidc_server_keys</c> (mig 253, FU-069): the OAuth server's signing
/// and encryption keys, each sealed under the master KEK. Singleton; service-role only.
/// Read once at boot by <c>OpenIddictKeyMaterial</c>, re-wrapped by rotation.
/// </summary>
internal sealed class OidcServerKeysRow
{
    /// <summary>Always 1 (CHECK-enforced singleton).</summary>
    public short Id { get; init; } = 1;
    /// <summary>The RSA-2048 signing key as PKCS#1 DER, sealed under the master KEK.</summary>
    public byte[] SigningKeyCiphertext { get; set; } = [];
    /// <summary>The 256-bit token-encryption key, sealed under the master KEK.</summary>
    public byte[] EncryptionKeyCiphertext { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
