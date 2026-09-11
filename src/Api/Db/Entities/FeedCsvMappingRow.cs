namespace Coffer.Api.Db.Entities;

/// <summary>
/// One reusable, hand-editable description of an institution's delimited export
/// (mig 222).
/// </summary>
/// <remarks>
/// The definition is YAML TEXT, stored as its author wrote it — comments, key order and
/// formatting included. Nothing here is a parsed field: the database deliberately holds
/// a document, and <c>CsvMappingValidator</c> is the gate that decides whether it means
/// anything. Read this row, validate the text, use the result; never infer the shape
/// from anything on this type.
/// </remarks>
public sealed class FeedCsvMappingRow
{
    public Guid Id { get; set; }

    public Guid LedgerId { get; set; }

    /// <summary>
    /// What the picker lists, unique per ledger, case-insensitively. A real column and
    /// not a key inside the document: it is queried and enforced on, and a value that is
    /// both queryable and hand-editable inside a blob has two sources of truth.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The YAML source, exactly as written.</summary>
    public string DefinitionYaml { get; set; } = string.Empty;

    /// <summary>
    /// Which validator understood this document when it was saved. Lets a stored mapping
    /// keep working when a later version adds a required key.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
