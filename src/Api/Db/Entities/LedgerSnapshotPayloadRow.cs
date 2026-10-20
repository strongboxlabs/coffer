namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the TVF wrapper
/// <c>ledger_snapshot_payload(uuid) RETURNS TABLE(payload jsonb)</c>
/// (migration 111 / ADR-0037). Repository invokes via LINQ + projects
/// <see cref="Payload"/> as a JSON string for the C# side to gzip.
/// </summary>
public sealed class LedgerSnapshotPayloadRow
{
    /// <summary>The full snapshot tables-object as serialised JSON.
    /// Postgres returns jsonb; the Npgsql provider materialises it
    /// to <c>string</c> on the C# side.</summary>
    public string Payload { get; init; } = string.Empty;
}
