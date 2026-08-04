using System.Text.Json;

using Coffer.Api.Mcp;

namespace Coffer.Api.Tests.Unit.Mcp;

/// <summary>
/// ADR-0081 D3 — the pure, unit-testable core of the MCP write audit: argument
/// summarization (bounded serialize + best-effort ledgerId lift) and the audit
/// scope (<see cref="McpWriteTools.ToolNames"/> = exactly the write tools). The SDK
/// wiring that routes tool calls through the filter is dev-validated (per the
/// project's MCP test convention); the persistence is covered by the integration
/// <c>McpAuditRecorderTests</c>.
/// </summary>
public sealed class McpAuditTests
{
    [Fact]
    public void SummarizeArguments_is_null_for_null_or_empty()
    {
        var (json1, ledger1) = McpAuditRecorder.SummarizeArguments(null);
        Assert.Null(json1);
        Assert.Null(ledger1);

        var (json2, ledger2) = McpAuditRecorder.SummarizeArguments(new Dictionary<string, JsonElement>());
        Assert.Null(json2);
        Assert.Null(ledger2);
    }

    [Fact]
    public void SummarizeArguments_lifts_ledgerId_and_serializes_the_rest()
    {
        var id = Guid.NewGuid();
        var args = new Dictionary<string, JsonElement>
        {
            ["ledgerId"] = JsonSerializer.SerializeToElement(id.ToString()),
            ["name"] = JsonSerializer.SerializeToElement("Groceries"),
        };

        var (json, ledgerId) = McpAuditRecorder.SummarizeArguments(args);

        Assert.Equal(id, ledgerId);
        Assert.Contains("Groceries", json);
        Assert.Contains("ledgerId", json);
    }

    [Fact]
    public void SummarizeArguments_ignores_a_non_guid_ledgerId()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["ledgerId"] = JsonSerializer.SerializeToElement(12345),   // a number, not a GUID string
        };

        var (_, ledgerId) = McpAuditRecorder.SummarizeArguments(args);

        Assert.Null(ledgerId);
    }

    [Fact]
    public void SummarizeArguments_bounds_a_huge_argument_blob()
    {
        var big = new string('x', McpAuditRecorder.MaxArgumentsLength + 500);
        var args = new Dictionary<string, JsonElement> { ["blob"] = JsonSerializer.SerializeToElement(big) };

        var (json, _) = McpAuditRecorder.SummarizeArguments(args);

        Assert.NotNull(json);
        Assert.EndsWith("[truncated]", json);
        Assert.True(json!.Length <= McpAuditRecorder.MaxArgumentsLength + 20);
    }

    [Fact]
    public void ToolNames_is_exactly_the_write_surface()
    {
        // The audit records only writes; the scope is the write tools' own names.
        Assert.Contains("set_account_taxstatus", McpWriteTools.ToolNames);
        Assert.Contains("set_transaction_tags", McpWriteTools.ToolNames);
        // Slice D additions (tag lifecycle + manual prices) are write tools, so audited.
        Assert.Contains("rename_tag", McpWriteTools.ToolNames);
        Assert.Contains("delete_tag", McpWriteTools.ToolNames);
        Assert.Contains("add_price", McpWriteTools.ToolNames);
        // Split-posting recategorize (ADR-0068) is a write tool, so audited.
        Assert.Contains("set_split_posting_category", McpWriteTools.ToolNames);
        // Read tools are out of scope (they live in other classes, not audited).
        Assert.DoesNotContain("list_tags", McpWriteTools.ToolNames);
        Assert.DoesNotContain("list_ledgers", McpWriteTools.ToolNames);
        // The full known write surface (the guard-completeness test covers each of these):
        // 13 from ADR-0068/0081 + 7 from Slice D (4 tag lifecycle + 3 manual price)
        // + 1 split-posting recategorize.
        Assert.Equal(21, McpWriteTools.ToolNames.Count);
    }
}
