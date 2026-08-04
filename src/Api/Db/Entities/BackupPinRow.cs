namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>backup_pins</c> (mig 144, ADR-0062): an admin "never delete"
/// pin on a backup artifact, keyed by its id (the <c>.cofferbak</c> stem, shared
/// by the local file + its Drive copy). Service-role only.
/// </summary>
internal sealed class BackupPinRow
{
    public string ArtifactId { get; init; } = string.Empty;
    public Guid? PinnedByUserId { get; set; }
    public DateTime CreatedAt { get; init; }
}
