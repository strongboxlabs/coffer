using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Audit;
using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// ADR-0081 D3 retention — <see cref="AuditRetentionService"/> prunes both audit logs
/// (<c>mcp_tool_invocations</c> and <c>ledger_operations</c>) to the configured window,
/// deleting aged rows and keeping recent ones.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditRetentionServiceTests
{
    private readonly PostgresFixture _fixture;

    public AuditRetentionServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PruneAsync_deletes_aged_rows_and_keeps_recent_in_both_logs()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var opts = Options.Create(new ApiOptions
        {
            ServiceConnectionString = _fixture.ServiceConnectionString,
            AuditRetentionDays = 180,
        });
        var recorder = new McpAuditRecorder(new ServiceDbContextFactory(opts));

        // Seed one aged row in each log...
        var oldId = await recorder.RecordAttemptAsync(ledger.UserId, "old_tool", arguments: null, traceId: null);
        await recorder.FinalizeAsync(oldId, InvocationStatus.Ok, "x");
        await using (var seed = _fixture.NewDbContext())
        {
            seed.LedgerOperations.Add(NewRun(ledger.LedgerId, "test-old"));
            await seed.SaveChangesAsync();
        }

        var aged = DateTime.UtcNow.AddDays(-200);
        await using (var age = _fixture.NewDbContext())
        {
            await age.McpToolInvocations.Where(r => r.UserId == ledger.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, aged));
            await age.LedgerOperations.Where(r => r.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.StartedAt, aged));
        }

        // ...and one fresh row in each (now).
        var newId = await recorder.RecordAttemptAsync(ledger.UserId, "new_tool", arguments: null, traceId: null);
        await recorder.FinalizeAsync(newId, InvocationStatus.Ok, "y");
        await using (var seed = _fixture.NewDbContext())
        {
            seed.LedgerOperations.Add(NewRun(ledger.LedgerId, "test-new"));
            await seed.SaveChangesAsync();
        }

        await new AuditRetentionService(new ServiceDbContextFactory(opts), opts,
            NullLogger<AuditRetentionService>.Instance).PruneAsync(default);

        await using var db = _fixture.NewDbContext();
        var tools = await db.McpToolInvocations.AsNoTracking()
            .Where(r => r.UserId == ledger.UserId).Select(r => r.ToolName).ToListAsync();
        Assert.Contains("new_tool", tools);
        Assert.DoesNotContain("old_tool", tools);

        var runs = await db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledger.LedgerId).Select(r => r.ProviderKey).ToListAsync();
        Assert.Contains("test-new", runs);
        Assert.DoesNotContain("test-old", runs);
    }

    private static LedgerOperationRow NewRun(Guid ledgerId, string providerKey) => new()
    {
        Id = Guid.NewGuid(),
        LedgerId = ledgerId,
        Family = "ingest",
        ProviderKey = providerKey,
        TriggeredVia = "manual",
        Status = "completed",
        StartedAt = DateTime.UtcNow,
    };
}
