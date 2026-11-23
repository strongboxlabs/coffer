namespace Coffer.Api.Db.Entities;

/// <summary>Keyless result of <c>snapshot_ledger_lock(uuid)</c> (migration 247):
/// true once the per-ledger snapshot-create lock is held by this session.</summary>
public sealed class SnapshotLedgerLockRow
{
    public bool Locked { get; init; }
}

/// <summary>Keyless result of <c>snapshot_ledger_unlock(uuid)</c> (migration 247):
/// whether this session held the lock it released.</summary>
public sealed class SnapshotLedgerUnlockRow
{
    public bool Unlocked { get; init; }
}

/// <summary>Keyless result of <c>ledger_snapshot_stored_sizes(uuid)</c> (migration 271):
/// the bytes one snapshot occupies as stored, compression included.</summary>
public sealed class SnapshotStoredSizeRow
{
    public Guid SnapshotId { get; init; }
    public long StoredBytes { get; init; }
}
