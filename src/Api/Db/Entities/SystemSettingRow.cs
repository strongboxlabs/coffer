namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>system_settings</c> (migration 147,
/// ADR-0063 §D8) — the deployment-global key/value settings store. Mutable
/// (init-only key, settable value/audit) because the admin endpoint upserts
/// the value in place. <see cref="ValueJson"/> is the raw JSONB text (e.g.
/// <c>"true"</c> / <c>"false"</c> for <c>mcp.enabled</c>); the repository owns
/// (de)serialization so callers work in typed values.
/// </summary>
public sealed class SystemSettingRow
{
    public string Key { get; init; } = string.Empty;
    public string ValueJson { get; set; } = "null";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
