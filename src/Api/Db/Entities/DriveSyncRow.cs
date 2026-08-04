namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>drive_sync</c> (mig 142, ADR-0062): the deployment-wide
/// singleton config for off-host backup sync to Google Drive. Service-role
/// only. The OAuth material (client_id + client_secret + refresh_token) is
/// sealed as one blob under the master KEK in <see cref="OauthCiphertext"/> —
/// never plaintext, re-wrapped by <c>rotate-kek</c>.
/// </summary>
internal sealed class DriveSyncRow
{
    /// <summary>Always 1 (CHECK-enforced singleton).</summary>
    public short Id { get; init; } = 1;
    public bool Enabled { get; set; }
    /// <summary>Sealed {client_id, client_secret, refresh_token} JSON
    /// (AES-GCM under the master KEK). Null until an admin connects.</summary>
    public byte[]? OauthCiphertext { get; set; }
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }
    /// <summary>Stable opaque per-install id (mig 143). Set once on first
    /// connect, kept across disconnect so the folder name is reused. Namespaces
    /// the Drive folder so installs sharing an OAuth client stay distinct.</summary>
    public string? InstallId { get; set; }
    public string? ConnectedEmail { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }
    public Guid? ConfiguredByUserId { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
