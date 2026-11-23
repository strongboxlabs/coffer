namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>backup_pins</c> (mig 144, ADR-0062; kind and label from mig 268,
/// ADR-0110): a "never delete" pin on a backup artifact, keyed by its id (the
/// <c>.cofferbak</c> stem, shared by the local file + its Drive copy). Kind is
/// <c>admin</c> or <c>upgrade</c>; an upgrade pin carries the label the backup list shows.
/// Service-role only.
/// </summary>
internal sealed class BackupPinRow
{
    public string ArtifactId { get; init; } = string.Empty;
    public Guid? PinnedByUserId { get; set; }
    public DateTime CreatedAt { get; init; }
    public string Kind { get; set; } = BackupPinKinds.Admin;
    public string? Label { get; set; }
}

/// <summary>The values of <c>backup_pins.kind</c>.</summary>
public static class BackupPinKinds
{
    public const string Admin = "admin";
    public const string Upgrade = "upgrade";
}
