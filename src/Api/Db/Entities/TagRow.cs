namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for the per-ledger tag dictionary (<c>tags</c>). One row
/// per <c>(ledger_id, name)</c> per the unique constraint added in
/// migration 014. Created on first use by the importer
/// (<c>Coffer.Importer.Moneydance.Db.TagsRepository</c>) or, through the tag
/// dictionary (<c>TagDictionary</c>), by every annotation write path and the PATCH
/// transaction endpoint; or by name with nothing assigned through
/// <c>POST /tags</c> (<c>TagsRepository.CreateAsync</c>, the Tags tab).
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
