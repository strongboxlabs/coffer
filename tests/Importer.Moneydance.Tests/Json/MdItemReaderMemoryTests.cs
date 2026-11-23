using System.Text;

using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Tests.Json;

/// <summary>
/// Guards the memory cost of reading an export, and the two contracts that keep
/// it low: fields are read from one shared document, and the export owns that
/// document's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// An export small on disk can OOM the 1 GiB API container if <c>ReadItem</c> calls
/// <c>JsonElement.Clone()</c> on every one of its fields: each clone is an independent
/// <c>JsonDocument</c> — object, byte buffer and metadata db apiece — so memory grows
/// with the field count, not the file size.
/// </para>
/// <para>
/// The route matters because the obvious fixes are both wrong. Decoding every field
/// to a CLR string still PEAKS high, because the document and the strings must
/// coexist during the read — peak, not retained, is what kills the container.
/// Dropping the per-item dictionary and reading fields straight off the element cuts
/// retained memory but makes the import far slower, because TryGetProperty is a
/// linear scan. What works is a dictionary of views into one document: no clones,
/// O(1) lookup.
/// </para>
/// <para>
/// A fixture-sized export never approaches the container's limit, so an absolute
/// allocation ceiling over the suite's fixtures cannot see this. The metric
/// here is deliberately <em>per item</em> — scale-free, so a few thousand synthetic
/// items detect a per-field regression that would only OOM at a large export's
/// scale, and it stays fast.
/// </para>
/// <para>
/// <see cref="GC.GetTotalMemory(bool)"/> counts every thread's heap, so a test
/// allocating in parallel lands inside the measurement. The class runs in a
/// collection with parallelization off: xunit runs it after the parallel
/// collections, with nothing else running.
/// </para>
/// </remarks>
[Collection(MemoryMeasurementCollection.Name)]
public sealed class MdItemReaderMemoryTests
{
    private const int Items = 4_000;
    private const int FieldsPerItem = 24;

    /// <summary>
    /// Retained bytes per item, export rooted. Measured on this exact harness
    /// (24 fields, a <c>csnap</c> obj_type so no RawJson is captured, which isolates
    /// per-FIELD cost): 3670 as views into one shared document, 9738 with a
    /// per-field <c>Clone()</c>. The bound sits between them with room for
    /// allocator variation, so it fails on a return to cloning and not on noise.
    /// </summary>
    private const double MaxRetainedBytesPerItem = 5_500;

    [Fact]
    public void Read_retains_a_bounded_number_of_bytes_per_item()
    {
        var json = BuildSyntheticExport(Items, FieldsPerItem);

        // The source string is rooted across both readings, so it cancels out and
        // the delta is the MdExport graph alone.
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var export = MdItemReader.ReadString(json);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.Equal(Items, export.AllItems.Count);

        var perItem = (after - before) / (double)Items;

        // KeepAlive AFTER the second reading: without it the JIT may drop the
        // reference early and the measurement silently becomes zero.
        GC.KeepAlive(export);
        export.Dispose();

        Assert.True(
            perItem < MaxRetainedBytesPerItem,
            $"Read retained {perItem:F0} bytes per item, over the {MaxRetainedBytesPerItem:F0} "
            + "budget. Something is now allocated per field — a JsonElement.Clone(), a "
            + "per-item dictionary, or eagerly materialized field strings.");
    }

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

    /// <summary>
    /// An export shaped like Moneydance's format: every value a JSON string
    /// (Moneydance encodes even numbers and booleans that way), with short
    /// field names and short-to-medium values.
    /// </summary>
    private static string BuildSyntheticExport(int items, int fieldsPerItem)
    {
        var sb = new StringBuilder(items * fieldsPerItem * 24);
        sb.Append("""
            {
              "metadata": { "exporter": "Moneydance 2024.4 (5253)", "moneydance_build": 5253,
                            "export_date": 20260101, "file_name": "synthetic" },
              "all_items": [
            """);

        for (var i = 0; i < items; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"obj_type\":\"csnap\",\"id\":\"csnap-").Append(i).Append('"');
            for (var f = 0; f < fieldsPerItem; f++)
            {
                sb.Append(",\"field_").Append(f).Append("\":\"")
                  .Append(-100_000 + (i * 7) + f).Append('"');
            }
            sb.Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }
}

/// <summary>
/// A collection that runs alone, for tests that measure the process heap.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurementCollection
{
    public const string Name = "Memory measurement";
}
