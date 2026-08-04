namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// In-memory representation of a Moneydance JSON export file.
/// The export is a single top-level object with three keys: <c>metadata</c>,
/// <c>all_items</c>, and <c>local_settings</c>. We model the first two
/// strongly; <c>local_settings</c> is preserved verbatim and currently unused.
/// </summary>
public sealed record MdExport(
    MdMetadata Metadata,
    IReadOnlyList<MdItem> AllItems);

/// <summary>
/// Metadata block written by Moneydance at the top of every export. The
/// <c>extensions</c> array exists in the JSON but isn't used by the importer
/// and is intentionally not modelled.
/// </summary>
public sealed record MdMetadata(
    string Exporter,
    int MoneydanceBuild,
    int ExportDate,
    string FileName);
