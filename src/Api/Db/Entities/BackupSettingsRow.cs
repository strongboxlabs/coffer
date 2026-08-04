namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>backup_settings</c> (mig 161, ADR-0074): the deployment-wide
/// singleton retention policy for backups. Service-role only. The GFS tiers here
/// are the single source of truth — they govern local backup pruning
/// (<c>BackupStore</c>) AND, transitively, the Google Drive mirror (which just
/// reflects the local set).
/// </summary>
internal sealed class BackupSettingsRow
{
    /// <summary>Always 1 (CHECK-enforced singleton).</summary>
    public short Id { get; init; } = 1;
    /// <summary>Keep every backup from the last this-many days (daily tier).</summary>
    public short RetentionDaily { get; set; }
    /// <summary>Beyond the daily window, keep the newest of each of the last
    /// this-many ISO weeks (weekly tier).</summary>
    public short RetentionWeekly { get; set; }
    /// <summary>Beyond the weekly window, keep the newest of each of the last
    /// this-many calendar months (monthly tier); older is pruned.</summary>
    public short RetentionMonthly { get; set; }
    public Guid? ConfiguredByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
