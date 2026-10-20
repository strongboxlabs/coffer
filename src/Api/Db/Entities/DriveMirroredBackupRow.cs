namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>drive_mirrored_backups</c> (mig 251, FU-064): a backup this install
/// put on Google Drive. The mirror deletes a remote backup only if it is listed here.
/// Service-role only.
/// </summary>
internal sealed class DriveMirroredBackupRow
{
    public string BackupId { get; init; } = string.Empty;
    public DateTime MirroredAt { get; init; }
    /// <summary>Set when the operator deleted the backup here, so the Drive copy goes
    /// without waiting for retention.</summary>
    public DateTime? DeletedAt { get; set; }
}
