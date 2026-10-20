namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for the per-ledger tag dictionary (<c>tags</c>). One row
/// per <c>(ledger_id, name)</c> per the unique constraint added in
/// migration 014. Created on first use by either the importer
/// (<c>Coffer.Importer.Moneydance.Db.TagsRepository</c>) or, since
/// slice 2c.6b, the PATCH transaction endpoint via the
/// <c>tags: string[]</c> body field.
/// </summary>
internal sealed class TagRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    /// <summary>
    /// Display name. Case-preserving but matched case-insensitively
    /// within the ledger — the lookup goes through
    /// <c>lower(name)</c> so the first user-supplied casing wins on
    /// create and survives subsequent references.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional display colour (<c>#rrggbb</c>, lower-cased). Mutable
    /// so the Tags-management recolor can set it; <c>null</c> renders as the
    /// default gray.</summary>
    public string? Color { get; set; }
    public DateTime CreatedAt { get; init; }
}
