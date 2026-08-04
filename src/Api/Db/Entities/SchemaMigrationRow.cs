namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for the DbUp-managed <c>__schema_migrations</c> table.
/// Read-only from the API side — DbUp owns the writes. Surfaced
/// so the snapshots repository can stamp the current schema version
/// onto each snapshot at create time (ADR-0037 §Schema-version
/// compatibility).
/// </summary>
public sealed class SchemaMigrationRow
{
    public int SchemaVersionsId { get; init; }
    public string ScriptName { get; init; } = string.Empty;
    public DateTime Applied { get; init; }
}
