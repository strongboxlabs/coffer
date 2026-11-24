using System.Text;

using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Tests.Stress;

/// <summary>
/// What reading an export retains per item, read from the managed heap. A measurement, not
/// a functional test: its reading follows the machine's allocator and collector, so it runs
/// in the stress lane, on demand, and in no build. The every-build guard against the
/// regression it measures is
/// <c>Json.MdItemReaderDocumentTests.An_item_is_unreadable_once_the_export_is_disposed</c>.
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
