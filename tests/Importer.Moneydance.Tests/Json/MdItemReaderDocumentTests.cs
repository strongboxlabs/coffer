using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Tests.Json;

/// <summary>
/// The two contracts that keep reading an export cheap: fields are read from one shared
/// document, and the export owns that document's lifetime.
/// </summary>
/// <remarks>
/// An item is a set of views into the export's one document, not copies of its fields
/// (a <c>JsonElement.Clone()</c> per field, or each decoded to a string up front, either of
/// which grows memory with the field count, not the file). So an item read after the export is
/// disposed fails: a copied field would still read. That makes
/// <see cref="An_item_is_unreadable_once_the_export_is_disposed"/> the every-build guard
/// against a return to copying, decided by the code's behaviour alone. What a read retains
/// per item is measured in the stress lane (<c>Stress.MdItemReaderMemoryTests</c>): a heap
/// reading follows the machine.
/// </remarks>
public sealed class MdItemReaderDocumentTests
{
    [Fact]
    public void An_item_is_unreadable_once_the_export_is_disposed()
    {
        var export = MdItemReader.ReadString(MinimalExport);
        var item = Assert.Single(export.AllItems);

        Assert.Equal("Cash", item.GetString("name"));

        export.Dispose();

        // Pins the cost of the shared-document design. Items are views, so reading
        // one after disposal MUST fail loudly — a quiet null here would turn a
        // lifetime bug into silently incomplete imported data, which is far worse
        // than a crash. The live hazard is the fire-and-forget import in
        // ImportJobRunner: it outlives the request that parsed the export, so
        // ownership sits with the background task, not the endpoint.
        Assert.Throws<ObjectDisposedException>(() => item.GetString("name"));
    }

    [Fact]
    public void A_non_string_field_is_present_but_does_not_convert()
    {
        // GetString deliberately declines to convert a value that was not a JSON
        // string, and Has still reports the key. Moneydance encodes its own numbers
        // and booleans AS strings, so relaxing this would start silently accepting
        // genuinely-numeric fields the importer has never treated as data.
        const string json = """
            {
              "metadata": { "exporter": "x", "moneydance_build": 1, "export_date": 20260101, "file_name": "f" },
              "all_items": [
                {
                  "obj_type": "acct",
                  "id": "a-1",
                  "text_amount": "-12345",
                  "real_number": -12345,
                  "real_bool": true,
                  "real_null": null,
                  "nested": { "a": 1 },
                  "list": [1, 2]
                }
              ]
            }
            """;

        using var export = MdItemReader.ReadString(json);
        var item = Assert.Single(export.AllItems);

        // A string-encoded number converts — this is how Moneydance stores amounts.
        Assert.Equal(-12345, item.GetLong("text_amount"));

        foreach (var key in new[] { "real_number", "real_bool", "real_null", "nested", "list" })
        {
            Assert.True(item.Has(key), $"'{key}' should still be reported as present.");
            Assert.Null(item.GetString(key));
            Assert.Null(item.GetLong(key));
            Assert.Null(item.GetBool(key));
        }
    }

    [Fact]
    public void Field_names_are_reported_in_document_order()
    {
        // AuditCommand walks FieldNames to spot attachment- and UDF-shaped keys, so
        // it has to see every field, not just the ones the typed views know about.
        using var export = MdItemReader.ReadString(MinimalExport);
        var item = Assert.Single(export.AllItems);

        Assert.Equal(new[] { "obj_type", "id", "name", "ts" }, item.FieldNames.ToArray());
    }

    private const string MinimalExport = """
        {
          "metadata": { "exporter": "x", "moneydance_build": 1, "export_date": 20260101, "file_name": "f" },
          "all_items": [
            { "obj_type": "acct", "id": "a-1", "name": "Cash", "ts": "1735689600000" }
          ]
        }
        """;
}
