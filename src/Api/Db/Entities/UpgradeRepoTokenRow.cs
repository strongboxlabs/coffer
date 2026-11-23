namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>upgrade_repo_token</c> (mig 269, ADR-0110): the private-fork release token,
/// sealed under the master KEK. Singleton; service-role only. Absent means no token.
/// </summary>
internal sealed class UpgradeRepoTokenRow
{
    /// <summary>Always 1 (CHECK-enforced singleton).</summary>
    public short Id { get; init; } = 1;
    /// <summary>The token, sealed under the master KEK.</summary>
    public byte[] TokenCiphertext { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
