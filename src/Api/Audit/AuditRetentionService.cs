using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db;

namespace Coffer.Api.Audit;

/// <summary>
/// Prunes the audit logs to the configured retention window (ADR-0081 D3): the MCP
/// write audit (<c>mcp_tool_invocations</c>) and the ledger-operation log
/// (<c>ledger_operations</c>, ADR-0055). A hosted <see cref="BackgroundService"/> rather
/// than an admin-scheduled global job (the backup pattern) because retention is an
/// always-on system invariant, not something the operator opts into scheduling — so
/// it runs unconditionally on a daily cadence (plus once shortly after startup).
/// Deletes via the service role (BYPASSRLS) so it spans every user, and via
/// <c>ExecuteDeleteAsync</c> (set-based, no change-tracking).
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly int _retentionDays;
    private readonly ILogger<AuditRetentionService> _logger;

    public AuditRetentionService(
        ServiceDbContextFactory serviceFactory,
        IOptions<ApiOptions> options,
        ILogger<AuditRetentionService> logger)
    {
        _serviceFactory = serviceFactory;
        _retentionDays = options.Value.AuditRetentionDays;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionDays <= 0)
        {
            _logger.LogInformation("Audit retention disabled (Api:AuditRetentionDays <= 0).");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PruneAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Delete audit rows older than the retention window. Public so the integration
    /// test can drive one pass directly. A failure is logged and swallowed — the next
    /// cycle retries; retention must not crash the host.
    /// </summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        if (_retentionDays <= 0) return;
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        try
        {
            await using var db = _serviceFactory.Create();

            // ledger_operations cascades to ledger_operation_errors / _promotions
            // (ON DELETE CASCADE, migration 038), so the parent delete is enough.
            var runs = await db.LedgerOperations
                .Where(r => r.StartedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var audits = await db.McpToolInvocations
                .Where(r => r.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            if (runs > 0 || audits > 0)
                _logger.LogInformation(
                    "Audit retention: pruned {Runs} ledger_operations and {Audits} mcp_tool_invocations older than {Days} days.",
                    runs, audits, _retentionDays);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit retention prune failed; will retry next cycle.");
        }
    }
}
