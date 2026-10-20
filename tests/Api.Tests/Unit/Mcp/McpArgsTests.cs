using Coffer.Api.Contracts;
using Coffer.Api.Mcp;

namespace Coffer.Api.Tests.Unit.Mcp;

/// <summary>
/// <see cref="McpArgs.ParseEnum{TEnum}"/> is the fail-loud guard for MCP string
/// enum params (ADR-0063 §D4): an unrecognized value must ERROR with the valid
/// list, never silently coerce to a default (which would return a different
/// report than the model asked for).
/// </summary>
public sealed class McpArgsTests
{
    [Theory]
    [InlineData("spending", ReportMeasure.Spending)]
    [InlineData("Income", ReportMeasure.Income)]
    [InlineData("NET", ReportMeasure.Net)]
    public void ParseEnum_parses_known_values_case_insensitively(string value, ReportMeasure expected) =>
        Assert.Equal(expected, McpArgs.ParseEnum<ReportMeasure>(value, "measure"));

    [Fact]
    public void ParseEnum_throws_listing_valid_values_on_unknown()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => McpArgs.ParseEnum<ReportMeasure>("savings", "measure"));

        // The message must name the bad value, the param, and the valid options so
        // the model can retry — not silently return spending.
        Assert.Contains("savings", ex.Message);
        Assert.Contains("measure", ex.Message);
        Assert.Contains("spending", ex.Message);
        Assert.Contains("income", ex.Message);
        Assert.Contains("net", ex.Message);
    }

    [Fact]
    public void ParseEnum_rejects_out_of_range_numeric()
    {
        // Enum.TryParse accepts numeric strings and would produce an undefined
        // member; the IsDefined guard must reject it.
        Assert.Throws<ArgumentException>(
            () => McpArgs.ParseEnum<ReportMeasure>("99", "measure"));
    }

    [Fact]
    public void ParseEnum_rejects_empty()
    {
        Assert.Throws<ArgumentException>(
            () => McpArgs.ParseEnum<ReportMeasure>("", "measure"));
    }
}
