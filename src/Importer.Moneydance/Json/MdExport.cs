using System.Text.Json;

namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// In-memory representation of a Moneydance JSON export file.
/// The export is a single top-level object with three keys: <c>metadata</c>,
/// <c>all_items</c>, and <c>local_settings</c>. We model the first two
/// strongly; <c>local_settings</c> is preserved verbatim and currently unused.
/// </summary>
/// <remarks>
/// <para>
/// OWNS the parsed <see cref="JsonDocument"/> that every <see cref="MdItem"/> is a
/// view into, which is why this is <see cref="IDisposable"/>. Items are readable
/// only until this is disposed; the alternative — copying each item's fields out
/// so they could outlive the document — cost three times the memory and OOM'd a
/// 1 GiB container on a real export (see <see cref="MdItem"/>).
/// </para>
/// <para>
/// Dispose ONCE, at the point the whole import is finished. The in-app import is
/// fire-and-forget (<c>ImportJobRunner</c>), so the HTTP request that parsed the
/// export is NOT that point: ownership transfers to the background task, which
/// disposes in a <c>finally</c>. Disposing at the end of the request would kill
/// the document mid-import.
/// </para>
/// </remarks>
public sealed class MdExport : IDisposable
{
    private readonly JsonDocument? _document;

    /// <param name="document">
    /// The parsed export. Ownership transfers to this instance — the caller must
    /// not dispose it. Null for an export assembled in a test without a document,
    /// whose items are then not readable.
    /// </param>
    public MdExport(MdMetadata metadata, IReadOnlyList<MdItem> allItems, JsonDocument? document = null)
    {
        Metadata = metadata;
        AllItems = allItems;
        _document = document;
    }

    public MdMetadata Metadata { get; }

    public IReadOnlyList<MdItem> AllItems { get; }

    public void Dispose() => _document?.Dispose();
}

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
