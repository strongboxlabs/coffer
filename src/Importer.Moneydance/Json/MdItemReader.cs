using System.Text.Json;

namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// Loads a Moneydance JSON export into in-memory <see cref="MdExport"/>
/// records. Reads the entire file into a <see cref="JsonDocument"/>; a
/// large real-world export (tens of MB) parses in well under a second on modern
/// hardware. A streaming reader could be added later if memory ever
/// becomes a concern, but for a one-shot self-hosted import it isn't
/// worth the complexity.
/// </summary>
public static class MdItemReader
{
    /// <summary>Read a Moneydance export from a stream.</summary>
    public static MdExport Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            // Comments and trailing commas should not appear in MD exports,
            // but tolerating them costs nothing and helps with hand-edited
            // test fixtures.
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return ReadDocument(document);
    }

    /// <summary>Read a Moneydance export from a file path.</summary>
    public static MdExport ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Read a Moneydance export from a JSON string (test convenience).</summary>
    public static MdExport ReadString(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return ReadDocument(document);
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
        var index = -1;
        foreach (var element in allItemsElement.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"all_items[{index}] is not a JSON object (got {element.ValueKind}).");

            items.Add(ReadItem(element, index));
        }

        return new MdExport(metadata, items);
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

    private static MdItem ReadItem(JsonElement element, int index)
    {
        string? id = null;
        string? objType = null;
        var fields = new Dictionary<string, JsonElement>(capacity: 16, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // Clone() makes the JsonElement independent of the parent JsonDocument
            // lifetime, so the caller can dispose the document and keep the items.
            var cloned = property.Value.Clone();
            fields[property.Name] = cloned;
            switch (property.Name)
            {
                case "id":
                    if (cloned.ValueKind == JsonValueKind.String) id = cloned.GetString();
                    break;
                case "obj_type":
                    if (cloned.ValueKind == JsonValueKind.String) objType = cloned.GetString();
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
            throw new InvalidDataException($"all_items[{index}] is missing 'id'.");
        if (string.IsNullOrEmpty(objType))
            throw new InvalidDataException($"all_items[{index}] (id={id}) is missing 'obj_type'.");

        // Mig 109 / ADR-0035 §3: capture the raw JSON text exactly as
        // it appears in the export so the importer can persist it
        // verbatim to txn_headers.provider_raw_payload (txn items
        // only — other obj_types currently don't use it).
        return new MdItem(id, objType, fields, RawJson: element.GetRawText());
    }
}
