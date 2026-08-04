using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coffer.Api.Snapshots;

/// <summary>
/// Serialised shape of a ledger snapshot payload (ADR-0037). Top-level
/// envelope carrying the in-scope graph as a table-name → row-objects
/// dictionary. Persisted gzip-compressed on
/// <see cref="Coffer.Api.Db.Entities.LedgerSnapshotRow.Content"/>.
/// </summary>
/// <remarks>
/// <para>The walker (<see cref="LedgerSnapshotSerializer"/>) produces
/// the payload by running <c>SELECT to_jsonb(t.*) FROM &lt;table&gt; WHERE
/// ledger_id = …</c> per in-scope table. The restorer
/// (<see cref="LedgerSnapshotRestorer"/>) reverses it via
/// <c>jsonb_populate_recordset</c>. No per-table C# DTOs — column lists
/// stay implicit in <c>to_jsonb</c> + <c>jsonb_populate_recordset</c>,
/// so a schema change on an in-scope table doesn't ripple through the
/// snapshot code.</para>
///
/// <para>The schema-version stamp gates compatibility: Phase 1 refuses
/// cross-version restore. The in-scope table list (whitelist) is
/// hard-coded in <see cref="InScopeTables"/>; adding a new in-scope
/// table is a one-line change there + an integration test.</para>
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

    /// <summary>
    /// Hard-coded whitelist of in-scope table names per ADR-0037
    /// §Scope. Order matters for restore — parents before children
    /// so FK references resolve. Mutating this list IS a snapshot-
    /// format change (existing snapshots stay readable, but new ones
    /// carry more / fewer tables); bump <see cref="CurrentFormat"/>
    /// if you do so cross-version restore can refuse cleanly.
    /// </summary>
    public static readonly IReadOnlyList<string> InScopeTables = new[]
    {
        // Root (referenced by everything per-ledger). The `ledgers`
        // row itself is NOT included in the payload — we restore
        // INTO an existing ledger row that the caller scopes to.
        "accounts",
        "securities",
        "user_account_groups",
        // Children of accounts / securities (depend on the above).
        "account_external_ids",
        "security_prices",
        // security_splits — corporate-action records (B0.7); used
        // by the holdings cost-basis recompute. Added in mig 112.
        "security_splits",
        "holdings",
        "user_account_group_members",
        // recurring_transactions — user-curated schedules
        // (ADR-0010); reference accounts (source + optional target).
        // Added in mig 112 (the omission in mig 111 caused a
        // restore-time FK violation when accounts couldn't be wiped
        // while a recurring row still referenced them).
        "recurring_transactions",
        // recurring_occurrence_exceptions — per-(series, date) skip
        // suppressions (ADR-0047 D6). Reference recurring_transactions, so they
        // restore AFTER it. Added in mig 125 alongside the snapshot-function
        // edits (the live round-trip is driven by fn_ledger_snapshot_payload /
        // _restore; this list stays aligned per the mig 112 lesson).
        "recurring_occurrence_exceptions",
        // Transaction graph.
        "txn_headers",
        "txn_legs",
        // Lots reference legs.
        "lots",
        // Overrides depend on headers / legs.
        "txn_header_overrides",
        "txn_leg_overrides",
        // Tags + joins (tags before joins).
        "tags",
        "txn_header_tags",
        // Provider mappings reference securities.
        "provider_security_mappings",
    };
}
