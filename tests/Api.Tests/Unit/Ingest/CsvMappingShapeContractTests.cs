using System.Text.Json;
using System.Text.RegularExpressions;

using Coffer.Api.Contracts;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The parsed shape's decimal separator on the wire (ADR-0114 D15): the validate verdict
/// carries <c>decimalSeparator</c> in the document's own vocabulary, a version-1 document
/// reads as <c>point</c>, and the SPA's <c>CsvMappingShape</c> reads it through the
/// <c>CsvDecimalSeparatorName</c> union, so a saved layout loads back with its separator.
/// </summary>
public sealed class CsvMappingShapeContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_version_1_shape_goes_out_reading_with_a_point()
    {
        var shape = new CsvMappingShapeDto(
            Version: 1, Delimiter: "tab", HeaderRows: 0, FooterRows: 0, DateColumn: 1,
            DateFormat: "MM/dd/yyyy", PayeeColumn: 3, MemoColumn: null, AmountShape: "signed",
            AmountColumn: 2, AmountInvert: false, DebitColumn: null, CreditColumn: null);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(shape, Web));

        Assert.Equal("point", json.RootElement.GetProperty("decimalSeparator").GetString());
    }

    [Fact]
    public void The_web_shape_reads_the_separator_through_a_union_of_point_and_comma()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Web", "src", "lib", "types", "csvMapping.ts"));

        var union = Regex.Match(source, @"export type CsvDecimalSeparatorName\s*=([^;]+);");
        Assert.True(union.Success, "Could not find `export type CsvDecimalSeparatorName = ...;` in csvMapping.ts.");
        Assert.Equal(
            ["comma", "point"],
            Regex.Matches(union.Groups[1].Value, @"'([^']*)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal));

        var shape = Regex.Match(source, @"export interface CsvMappingShape\s*\{([^}]*)\}");
        Assert.True(shape.Success, "Could not find `export interface CsvMappingShape { ... }` in csvMapping.ts.");
        Assert.Matches(@"(?m)^\s*decimalSeparator:\s*CsvDecimalSeparatorName;", shape.Groups[1].Value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
