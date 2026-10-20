using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coffer.Api.Snapshots;

/// <summary>
/// Envelope of a LEGACY v1 snapshot (ADR-0037): the in-scope graph as a
/// table-name → row-objects dictionary, gzip-compressed on
/// <see cref="Coffer.Api.Db.Entities.LedgerSnapshotRow.Content"/>. Only read,
/// never written — <c>LedgerSnapshotsRepository.RestoreAsync</c> deserializes it
/// for the few snapshots taken before mig 179.
/// </summary>
/// <remarks>
/// <para>Since mig 179 capture and restore run server-side (v2 content_json, then
/// v3 chunked <c>ledger_snapshot_parts</c> from mig 193) and nothing passes
/// through this type. The set of captured tables is
/// <c>fn_ledger_snapshot_part_names()</c> in the database, and nothing else:
/// <c>SchemaDriftGuardTests</c> fails when a ledger-scoped table is neither in it
/// nor explicitly excluded.</para>
/// </remarks>
public sealed class LedgerSnapshotPayload
{
    /// <summary>Format discriminator. Distinct from
    /// <see cref="SchemaVersion"/> — bumps when the snapshot envelope
    /// itself evolves (e.g. encryption layer added, columns added to
    /// the envelope). Phase 1 only writes / reads
    /// <c>coffer-snapshot-v1</c>.</summary>
    [JsonPropertyName("snapshotFormat")]
    public string SnapshotFormat { get; set; } = CurrentFormat;

    public const string CurrentFormat = "coffer-snapshot-v1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("ledgerId")]
    public Guid LedgerId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Table-name → array of row objects, one object per row. Row
    /// objects use Postgres column names as keys (the shape
    /// <c>to_jsonb(t.*)</c> produces); restorer feeds them straight
    /// into <c>jsonb_populate_recordset</c> on the target table.
    /// </summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, JsonArray> Tables { get; set; } = new(StringComparer.Ordinal);
}
