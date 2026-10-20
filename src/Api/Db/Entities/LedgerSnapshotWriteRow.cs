namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the TVF wrapper
/// <c>ledger_snapshot_write(uuid, uuid) RETURNS TABLE(content_size_uncompressed integer)</c>
/// (migration 179). Captures the in-scope graph into <c>ledger_snapshots.content_json</c>
/// entirely server-side and returns the uncompressed byte size so the create path can
/// surface it without ever materialising the payload in the API process.
/// </summary>
public sealed class LedgerSnapshotWriteRow
{
    public int ContentSizeUncompressed { get; init; }
}
