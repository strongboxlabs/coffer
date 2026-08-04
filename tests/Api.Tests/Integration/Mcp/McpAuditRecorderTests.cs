using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// ADR-0081 D3 + ADR-0086 — <see cref="McpAuditRecorder"/> maintains the two-phase
/// <c>mcp_tool_invocations</c> audit via the service role: a <c>pending</c> attempt
/// row written before the tool runs, finalized to <c>ok</c>/<c>error</c>/<c>cancelled</c>
/// after. Also exercises migration 178 + the EF mapping + the lifecycle CHECK
/// constraints (a column typo or a bad constraint fails here, not just at build).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpAuditRecorderTests
{
    private readonly PostgresFixture _fixture;

    public McpAuditRecorderTests(PostgresFixture fixture) => _fixture = fixture;

    private McpAuditRecorder NewRecorder() =>
        new(new ServiceDbContextFactory(Options.Create(
            new ApiOptions { ServiceConnectionString = _fixture.ServiceConnectionString })));

    private async Task<McpToolInvocationRow> ReadRowAsync(Guid id)
    {
        await using var db = _fixture.NewDbContext();
        return await db.McpToolInvocations.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task RecordAttempt_writes_a_pending_row_before_the_tool_runs()
    {
        // The attempt is written BEFORE the tool, so a call that then hangs / times
        // out / crashes still leaves a visible row (ADR-0086 attempt integrity).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var args = new Dictionary<string, JsonElement>
        {
            ["ledgerId"] = JsonSerializer.SerializeToElement(ledger.LedgerId.ToString()),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { "reimbursable" }),
        };

        var id = await NewRecorder().RecordAttemptAsync(
            ledger.UserId, "set_transaction_tags", args, traceId: "trace-abc");

        var row = await ReadRowAsync(id);
        Assert.Equal(InvocationStatus.Pending, row.Status);   // pending, not yet finalized
        Assert.Null(row.CompletedAt);                          // no completion instant while pending
        Assert.Null(row.Result);
        Assert.Equal("trace-abc", row.TraceId);                // correlation id stored
        Assert.Equal(ledger.LedgerId, row.LedgerId);           // best-effort ledgerId lift
        Assert.Contains("reimbursable", row.Arguments);        // arguments serialized
        Assert.NotEqual(default, row.CreatedAt);               // DB-assigned default now()
    }

    [Fact]
    public async Task RecordAttempt_handles_null_args_and_missing_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var id = await NewRecorder().RecordAttemptAsync(
            ledger.UserId, "merge_category", arguments: null, traceId: null);

        var row = await ReadRowAsync(id);
        Assert.Equal(InvocationStatus.Pending, row.Status);
        Assert.Null(row.Arguments);
        Assert.Null(row.LedgerId);
        Assert.Null(row.TraceId);
    }

    [Fact]
    public async Task Finalize_ok_marks_a_terminal_success()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var recorder = NewRecorder();
        var id = await recorder.RecordAttemptAsync(ledger.UserId, "add_price", arguments: null, traceId: null);

        await recorder.FinalizeAsync(id, InvocationStatus.Ok, "priced 1");

        var row = await ReadRowAsync(id);
        Assert.Equal(InvocationStatus.Ok, row.Status);
        Assert.Equal("priced 1", row.Result);
        Assert.NotNull(row.CompletedAt);                        // terminal ⇒ completion instant set
    }

    [Fact]
    public async Task Finalize_error_marks_a_terminal_error()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var recorder = NewRecorder();
        var id = await recorder.RecordAttemptAsync(ledger.UserId, "merge_category", arguments: null, traceId: null);

        await recorder.FinalizeAsync(id, InvocationStatus.Error, "KindMismatch");

        var row = await ReadRowAsync(id);
        Assert.Equal(InvocationStatus.Error, row.Status);       // status is the sole outcome field
        Assert.Equal("KindMismatch", row.Result);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task Finalize_cancelled_is_a_distinct_terminal_state()
    {
        // The state that would have answered the incident: a client timeout /
        // cancellation is 'cancelled', not 'error' and not a lost row (ADR-0086).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var recorder = NewRecorder();
        var id = await recorder.RecordAttemptAsync(
            ledger.UserId, "convert_in_kind_transfer", arguments: null, traceId: null);

        await recorder.FinalizeAsync(id, InvocationStatus.Cancelled, "cancelled");

        var row = await ReadRowAsync(id);
        Assert.Equal(InvocationStatus.Cancelled, row.Status);   // cancelled ≠ error, a distinct terminal state
        Assert.NotNull(row.CompletedAt);
    }
}
