using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdItemReaderTests
{
    private const string MinimalExport = """
        {
          "metadata": {
            "exporter": "Moneydance 2024.4 (5253)",
            "moneydance_build": 5253,
            "export_date": 20260508,
            "file_name": "vbdata"
          },
          "all_items": [
            {
              "obj_type": "acct",
              "id": "a-1",
              "name": "Cash",
              "type": "b",
              "currid": "1d3b7c05",
              "ts": "1735253349825"
            },
            {
              "obj_type": "curr",
              "id": "c-1",
              "name": "US Dollar",
              "currid": "USD",
              "isbase": "y"
            }
          ]
        }
        """;

    [Fact]
    public void Parses_metadata_and_two_items()
    {
        var export = MdItemReader.ReadString(MinimalExport);

        Assert.Equal("Moneydance 2024.4 (5253)", export.Metadata.Exporter);
        Assert.Equal(5253, export.Metadata.MoneydanceBuild);
        Assert.Equal(20260508, export.Metadata.ExportDate);
        Assert.Equal("vbdata", export.Metadata.FileName);
        Assert.Equal(2, export.AllItems.Count);
    }

    [Fact]
    public void Items_carry_id_obj_type_and_full_field_dict()
    {
        var export = MdItemReader.ReadString(MinimalExport);
        var acct = export.AllItems.Single(i => i.ObjType == "acct");

        Assert.Equal("a-1", acct.Id);
        Assert.Equal("Cash", acct.GetString("name"));
        Assert.Equal("b", acct.GetString("type"));
        Assert.Equal("1d3b7c05", acct.GetString("currid"));
        Assert.True(acct.Fields.ContainsKey("ts"));
    }

    [Fact]
    public void Rejects_root_that_is_not_an_object()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MdItemReader.ReadString("[]"));
        Assert.Contains("must be a JSON object", ex.Message);
    }

    [Fact]
    public void Rejects_missing_metadata()
    {
        const string json = """{ "all_items": [] }""";
        var ex = Assert.Throws<InvalidDataException>(() => MdItemReader.ReadString(json));
        Assert.Contains("metadata", ex.Message);
    }

    [Fact]
    public void Rejects_missing_all_items()
    {
        const string json = """{ "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"} }""";
        var ex = Assert.Throws<InvalidDataException>(() => MdItemReader.ReadString(json));
        Assert.Contains("all_items", ex.Message);
    }

    [Fact]
    public void Rejects_item_missing_id()
    {
        const string json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                { "obj_type": "acct", "name": "no id" }
              ]
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => MdItemReader.ReadString(json));
        Assert.Contains("missing 'id'", ex.Message);
    }

    [Fact]
    public void Rejects_item_missing_obj_type()
    {
        const string json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                { "id": "x", "name": "no obj_type" }
              ]
            }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => MdItemReader.ReadString(json));
        Assert.Contains("missing 'obj_type'", ex.Message);
    }

    [Fact]
    public void Item_helpers_handle_string_encoded_numbers()
    {
        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {
                  "obj_type": "acct", "id": "a-1", "name": "x", "type": "b",
                  "sbal": "30062",
                  "rate": "1.04567",
                  "is_inactive": "y",
                  "hide": "no"
                }
              ]
            }
            """;
        var item = MdItemReader.ReadString(json).AllItems[0];

        Assert.Equal(30062, item.GetLong("sbal"));
        Assert.Equal(30062, item.GetInt("sbal"));
        Assert.Equal(1.04567m, item.GetDecimal("rate"));
        Assert.True(item.GetBool("is_inactive"));
        Assert.False(item.GetBool("hide"));
        Assert.Null(item.GetLong("missing"));
        Assert.Null(item.GetBool("name"));   // "x" is not a recognized boolean
    }
}
