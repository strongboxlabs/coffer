namespace Coffer.Api.Crypto;

/// <summary>
/// Singleton holder for the deployment-level master KEK (ADR-0014
/// §Layer 4, refined by ADR-0026). Loaded once at API startup from
/// the <c>COFFER_MASTER_KEK_BASE64</c> environment variable; stays
/// in-process memory only.
/// </summary>
/// <remarks>
/// <para>The <see cref="Id"/> ("v1", "v2", …) tags every wrapped
/// LEK so master-KEK rotation can target the rows still wrapped
/// under an older KEK. The rotation tooling is a follow-up; v1
/// ships with a single KEK.</para>
///
/// <para>Why a singleton instead of an <c>IOptions</c> binding: the
/// master KEK is *secret material*, not configuration. Keeping it
/// out of the standard config tree prevents accidental
/// inclusion in <c>appsettings.json</c> commits or log dumps.</para>
/// </remarks>
public sealed class MasterKey
{
    /// <summary>The 32-byte AES-GCM key.</summary>
    public byte[] KeyBytes { get; }

    /// <summary>Identifier persisted on every wrapped LEK
    /// (<c>ledgers.lek_kek_id</c>). v1 is the launch identifier;
    /// rotation introduces v2 etc.</summary>
    public string Id { get; }

    public MasterKey(byte[] keyBytes, string id)
    {
        ArgumentNullException.ThrowIfNull(keyBytes);
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (keyBytes.Length != 32)
            throw new ArgumentException(
                $"Master KEK must be 32 bytes (got {keyBytes.Length}).",
                nameof(keyBytes));
        KeyBytes = keyBytes;
        Id = id;
    }
}
