using System.Text.Json;

namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// Loads a Moneydance JSON export into in-memory <see cref="MdExport"/>
/// records. Reads the entire file into a <see cref="JsonDocument"/>; a
/// large real-world export (tens of MB) parses in well under a second on modern
/// hardware.
/// </summary>
/// <remarks>
/// This comment used to say memory wasn't worth worrying about for a one-shot
/// self-hosted import. That was wrong by roughly an order of magnitude, and an
/// 80 MB / 74k-item export duly OOM'd the 1 GiB API container — not because the
/// document was large but because every field was <c>Clone()</c>d into its own
/// <c>JsonDocument</c>: 1.74M of them. Each item's fields are now views into ONE
/// document (see <see cref="MdItem"/>) held in a per-item dictionary, so the
/// document plus those dictionaries is the whole cost — and the dictionary stays,
/// because dropping it for <c>TryGetProperty</c> lookups saved memory and made the
/// same import 15x slower (<see cref="MdItem"/> has the measurements).
/// Full materialization of the item LIST is still deliberate: <c>ImportAsync</c>
/// enumerates <c>AllItems</c> end to end nine or ten times, and the order has a
/// hard cycle — <c>AccountMapper.ComputeInputs</c> must see every <c>txn</c>
/// before any <c>acct</c> row can be mapped, while <c>txn</c> mapping needs the
/// account map that pass produces. So a streaming rewrite could not be
/// single-pass either; it would need at least two reads of the source plus a
/// restructuring of every step.
/// </remarks>
public static class MdItemReader
{
    // Comments and trailing commas should not appear in MD exports, but
    // tolerating them costs nothing and helps with hand-edited test fixtures.
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Read a Moneydance export from a stream. The returned <see cref="MdExport"/>
    /// OWNS the parsed document and must be disposed once the import is done.
    /// </summary>
    public static MdExport Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Parse from a byte[] we size ourselves rather than handing the stream to
        // JsonDocument. The stream overload rents from ArrayPool, which rounds up
        // to a power of two — an 80.5 MB export gets a 128 MB buffer, 48 MB of it
        // waste. The ReadOnlyMemory overload uses our array directly with no copy,
        // so the document's byte cost is exactly the file size. The array must stay
        // unmodified for the document's lifetime, which it does: nothing else can
        // reach it.
        var bytes = ReadAllBytes(stream);
        return Own(JsonDocument.Parse(bytes, ParseOptions));
    }

    /// <summary>Read a Moneydance export from a file path.</summary>
    public static MdExport ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Read a Moneydance export from a JSON string (test convenience).</summary>
    public static MdExport ReadString(string json)
        => Own(JsonDocument.Parse(json, ParseOptions));

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            var exact = new byte[remaining];
            stream.ReadExactly(exact);
            return exact;
        }

        // Unknown length: MemoryStream over-allocates while growing, so this path
        // costs more transiently. Both real callers (a file and a buffered form
        // upload) are seekable and take the exact path above.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Hand the document to the MdExport that will own it, disposing it if the
    /// export turns out to be malformed. Without this, every validation throw in
    /// <see cref="ReadDocument"/> would abandon the parsed document — and
    /// ImportEndpoints treats InvalidDataException as an ordinary user-facing
    /// "bad file" response, so that leak would be on a routine path.
    /// </summary>
    private static MdExport Own(JsonDocument document)
    {
        try
        {
            return ReadDocument(document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static MdExport ReadDocument(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                $"Moneydance export root must be a JSON object; got {root.ValueKind}.");

        if (!root.TryGetProperty("metadata", out var metadataElement) ||
            metadataElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Moneydance export is missing the 'metadata' object.");

        if (!root.TryGetProperty("all_items", out var allItemsElement) ||
            allItemsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Moneydance export is missing the 'all_items' array.");

        var metadata = ReadMetadata(metadataElement);
        var items = new List<MdItem>(capacity: allItemsElement.GetArrayLength());

        // One field-name pool for the whole read. An export draws its field names
        // from a small fixed vocabulary, but JsonProperty.Name hands back a freshly
        // allocated string every time — so without pooling a 74k-item export retains
        // ~1.7M name strings that are duplicates of a couple of hundred. Ordinal
        // because JSON keys are case-sensitive.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var index = -1;
        foreach (var element in allItemsElement.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"all_items[{index}] is not a JSON object (got {element.ValueKind}).");

            items.Add(ReadItem(element, index, names));
        }

        return new MdExport(metadata, items, document);
    }

    private static MdMetadata ReadMetadata(JsonElement element)
    {
        return new MdMetadata(
            Exporter: element.TryGetProperty("exporter", out var exporter) && exporter.ValueKind == JsonValueKind.String
                ? exporter.GetString() ?? string.Empty : string.Empty,
            MoneydanceBuild: element.TryGetProperty("moneydance_build", out var build) && build.ValueKind == JsonValueKind.Number
                ? build.GetInt32() : 0,
            ExportDate: element.TryGetProperty("export_date", out var exportDate) && exportDate.ValueKind == JsonValueKind.Number
                ? exportDate.GetInt32() : 0,
            FileName: element.TryGetProperty("file_name", out var fileName) && fileName.ValueKind == JsonValueKind.String
                ? fileName.GetString() ?? string.Empty : string.Empty);
    }

    private static MdItem ReadItem(
        JsonElement element, int index, Dictionary<string, string> names)
    {
        // capacity 24: real MD items average ~23 fields, so this avoids a rehash
        // per item without over-allocating.
        var fields = new Dictionary<string, JsonElement>(capacity: 24, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // NOT property.Value.Clone(). The element is a view into the document
            // that MdExport owns and keeps alive for the whole import; cloning would
            // allocate an independent JsonDocument per field — 1.74M of them on a
            // real export, which is what ran the container out of memory.
            var name = property.Name;
            if (names.TryGetValue(name, out var pooled)) name = pooled;
            else names[name] = name;

            fields[name] = property.Value;
        }

        var id = StringField(fields, "id");
        var objType = StringField(fields, "obj_type");

        if (string.IsNullOrEmpty(id))
            throw new InvalidDataException($"all_items[{index}] is missing 'id'.");
        if (string.IsNullOrEmpty(objType))
            throw new InvalidDataException($"all_items[{index}] (id={id}) is missing 'obj_type'.");

        // Only the obj_types that actually persist provider_raw_payload pay for it.
        // GetRawText allocates the item's whole JSON as UTF-16; doing that for all
        // 73,918 items cost 160 MB, and 31k of them (csnap especially) never read it.
        // Eager rather than lazy so it is materialized exactly once per item.
        var rawJson = objType is "txn" or "acct" or "reminder"
            ? element.GetRawText()
            : string.Empty;

        return new MdItem(id, objType, fields, rawJson);
    }

    private static string? StringField(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;


}
