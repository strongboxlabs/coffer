using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db;

namespace Coffer.Api.Audit;

/// <summary>
/// Prunes the high-volume operational logs to their configured retention windows: the MCP
/// write audit (<c>mcp_tool_invocations</c>, ADR-0081 D3), the ledger-operation log
/// (<c>ledger_operations</c>, ADR-0055), and the notification event log
/// (<c>system_events</c> / <c>ledger_events</c>, ADR-0096). A hosted <see cref="BackgroundService"/> rather
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
    private readonly int _eventRetentionDays;
    private readonly ILogger<AuditRetentionService> _logger;

    public AuditRetentionService(
        ServiceDbContextFactory serviceFactory,
        IOptions<ApiOptions> options,
        ILogger<AuditRetentionService> logger)
    {
        _serviceFactory = serviceFactory;
        _retentionDays = options.Value.AuditRetentionDays;
        _eventRetentionDays = options.Value.EventRetentionDays;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Both windows off before the loop is even worth starting. Checked separately
        // because they are independent knobs: disabling audit retention must not silently
        // stop pruning events, which is what a single guard on _retentionDays did.
        if (_retentionDays <= 0 && _eventRetentionDays <= 0)
        {
            _logger.LogInformation(
                "Retention disabled (Api:AuditRetentionDays and Api:EventRetentionDays <= 0).");
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
        if (_retentionDays <= 0 && _eventRetentionDays <= 0) return;
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        var eventCutoff = DateTime.UtcNow.AddDays(-_eventRetentionDays);
        try
        {
            await using var db = _serviceFactory.Create();

            // ledger_operations cascades to ledger_operation_errors / _promotions
            // (ON DELETE CASCADE, migration 038), so the parent delete is enough.
            var runs = _retentionDays <= 0 ? 0 : await db.LedgerOperations
                .Where(r => r.StartedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var audits = _retentionDays <= 0 ? 0 : await db.McpToolInvocations
                .Where(r => r.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            // The notification event log (ADR-0096), on its own longer window. Both
            // tables index occurred_at DESC (migs 207 and 208), so the cutoff scan is
            // the index rather than a seq scan.
            //
            // ledger_events also disappears with its ledger (ON DELETE CASCADE, mig 208);
            // this is for the rows that outlive nothing in particular and would otherwise
            // accumulate for the life of the install. system_events has no such parent, so
            // for it this prune is the ONLY thing that ever removes a row.
            var systemEvents = _eventRetentionDays <= 0 ? 0 : await db.SystemEvents
                .Where(r => r.OccurredAt < eventCutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var ledgerEvents = _eventRetentionDays <= 0 ? 0 : await db.LedgerEvents
                .Where(r => r.OccurredAt < eventCutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            // admin_audit_events is deliberately NOT pruned (ADR-0092 D2, migration
            // 191). The two tables above are high-volume operational records; key
            // access is the opposite — a handful of rows per install, whose whole
            // value is that they are old. Don't add it here.

            if (runs > 0 || audits > 0)
                _logger.LogInformation(
                    "Audit retention: pruned {Runs} ledger_operations and {Audits} mcp_tool_invocations older than {Days} days.",
                    runs, audits, _retentionDays);

            if (systemEvents > 0 || ledgerEvents > 0)
                _logger.LogInformation(
                    "Event retention: pruned {SystemEvents} system_events and {LedgerEvents} "
                    + "ledger_events older than {Days} days.",
                    systemEvents, ledgerEvents, _eventRetentionDays);
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
