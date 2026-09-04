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
/// Retention — <see cref="AuditRetentionService"/> prunes the high-volume operational
/// logs to their configured windows: the two audit logs (<c>mcp_tool_invocations</c> and
/// <c>ledger_operations</c>, ADR-0081 D3) on one window, and the notification event log
/// (<c>system_events</c> / <c>ledger_events</c>, ADR-0096) on its own longer one.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditRetentionServiceTests
{
    private readonly PostgresFixture _fixture;

    public AuditRetentionServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PruneAsync_prunes_the_notification_event_log_on_its_own_window()
    {
        // The event log (ADR-0096) had NO lifetime at all: nothing anywhere deleted a
        // system_events or ledger_events row, and the per-ledger job signals added roughly
        // one row per enabled job per ledger per day. Growth is small — a few thousand rows
        // a year — so this is not about disk. It is about an append-only table having a
        // stated lifetime rather than growing silently forever.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var opts = Options.Create(new ApiOptions
        {
            ServiceConnectionString = _fixture.ServiceConnectionString,
            AuditRetentionDays = 180,
            EventRetentionDays = 365,
        });

        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var seed = _fixture.NewDbContext())
        {
            seed.SystemEvents.Add(NewSystemEvent($"sys-old-{tag}"));
            seed.SystemEvents.Add(NewSystemEvent($"sys-new-{tag}"));
            seed.LedgerEvents.Add(NewLedgerEvent(ledger.LedgerId, $"led-old-{tag}"));
            seed.LedgerEvents.Add(NewLedgerEvent(ledger.LedgerId, $"led-new-{tag}"));
            await seed.SaveChangesAsync();
        }

        // Age one of each past the EVENT window — deliberately past 365 but well inside
        // nothing else, so this proves the event knob is what did it.
        var aged = DateTime.UtcNow.AddDays(-400);
        await using (var age = _fixture.NewDbContext())
        {
            await age.SystemEvents.Where(e => e.Summary == $"sys-old-{tag}")
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, aged));
            await age.LedgerEvents.Where(e => e.Summary == $"led-old-{tag}")
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, aged));
        }

        await new AuditRetentionService(
            PostgresFixture.ServiceFactoryFor(opts), opts,
            NullLogger<AuditRetentionService>.Instance).PruneAsync(default);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.SystemEvents.AnyAsync(e => e.Summary == $"sys-old-{tag}"));
        Assert.True(await read.SystemEvents.AnyAsync(e => e.Summary == $"sys-new-{tag}"));
        Assert.False(await read.LedgerEvents.AnyAsync(e => e.Summary == $"led-old-{tag}"));
        Assert.True(await read.LedgerEvents.AnyAsync(e => e.Summary == $"led-new-{tag}"));
    }

    [Fact]
    public async Task Disabling_audit_retention_does_not_silently_stop_pruning_events()
    {
        // Two independent knobs. A single guard on AuditRetentionDays would have made
        // "keep my audit log forever" quietly also mean "keep every event forever" —
        // a setting doing something it does not say.
        var opts = Options.Create(new ApiOptions
        {
            ServiceConnectionString = _fixture.ServiceConnectionString,
            AuditRetentionDays = 0,
            EventRetentionDays = 365,
        });

        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var seed = _fixture.NewDbContext())
        {
            seed.SystemEvents.Add(NewSystemEvent($"audit-off-{tag}"));
            await seed.SaveChangesAsync();
        }
        await using (var age = _fixture.NewDbContext())
        {
            await age.SystemEvents.Where(e => e.Summary == $"audit-off-{tag}")
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, DateTime.UtcNow.AddDays(-400)));
        }

        await new AuditRetentionService(
            PostgresFixture.ServiceFactoryFor(opts), opts,
            NullLogger<AuditRetentionService>.Instance).PruneAsync(default);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.SystemEvents.AnyAsync(e => e.Summary == $"audit-off-{tag}"));
    }

    [Fact]
    public async Task Zero_event_retention_keeps_events_indefinitely()
    {
        // 0 means "retain indefinitely", matching AuditRetentionDays. Worth pinning
        // because the arithmetic would otherwise make 0 mean "delete everything older
        // than now", i.e. delete the whole table on the first tick.
        var opts = Options.Create(new ApiOptions
        {
            ServiceConnectionString = _fixture.ServiceConnectionString,
            AuditRetentionDays = 180,
            EventRetentionDays = 0,
        });

        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var seed = _fixture.NewDbContext())
        {
            seed.SystemEvents.Add(NewSystemEvent($"keep-forever-{tag}"));
            await seed.SaveChangesAsync();
        }
        await using (var age = _fixture.NewDbContext())
        {
            await age.SystemEvents.Where(e => e.Summary == $"keep-forever-{tag}")
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, DateTime.UtcNow.AddDays(-4000)));
        }

        await new AuditRetentionService(
            PostgresFixture.ServiceFactoryFor(opts), opts,
            NullLogger<AuditRetentionService>.Instance).PruneAsync(default);

        await using var read = _fixture.NewDbContext();
        Assert.True(await read.SystemEvents.AnyAsync(e => e.Summary == $"keep-forever-{tag}"));
    }

    private static SystemEventRow NewSystemEvent(string summary) => new()
    {
        Severity = "info",
        Topic = "scheduler",
        EventKey = "retention.test",
        Summary = summary,
        DetailJson = "{}",
    };

    private static LedgerEventRow NewLedgerEvent(Guid ledgerId, string summary) => new()
    {
        LedgerId = ledgerId,
        Severity = "info",
        Topic = "scheduler",
        EventKey = "retention.test",
        Summary = summary,
        DetailJson = "{}",
    };

    [Fact]
    public async Task PruneAsync_deletes_aged_rows_and_keeps_recent_in_both_logs()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var opts = Options.Create(new ApiOptions
        {
            ServiceConnectionString = _fixture.ServiceConnectionString,
            AuditRetentionDays = 180,
        });
        var recorder = new McpAuditRecorder(PostgresFixture.ServiceFactoryFor(opts));

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

        await new AuditRetentionService(PostgresFixture.ServiceFactoryFor(opts), opts,
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
