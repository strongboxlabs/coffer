using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Db;

/// <summary>
/// Startup-time reader for a single <c>system_settings</c> boolean, used to gate
/// MCP registration before the DI container exists (ADR-0063 §D8). It opens a
/// short-lived <see cref="AppDbContext"/> on the service-role connection — the
/// same construction <see cref="ServiceDbContextFactory"/> uses — because the
/// gate decision happens during <c>builder.Services</c> configuration, before
/// any scoped services or the migration runner have run.
/// </summary>
/// <remarks>
/// The read is deliberately defensive: on a brand-new install the
/// <c>system_settings</c> table doesn't exist until DbUp runs (which is after
/// <c>builder.Build()</c>), so the first start reads the fallback. Since the
/// fallback is <c>false</c> (MCP off), default-off always holds; the real value
/// is read on the next restart, which is exactly the "takes effect after
/// restart" semantics the toggle promises.
/// </remarks>
public static class SystemSettingsBootstrap
{
    public static bool TryReadBool(
        string serviceConnectionString,
        string key,
        bool fallback,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(serviceConnectionString))
            return fallback;

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(serviceConnectionString)
                .Options;
            using var db = new AppDbContext(options);
            var row = db.SystemSettings.AsNoTracking()
                .FirstOrDefault(s => s.Key == key);
            if (row is null)
                return fallback;
            return bool.TryParse(row.ValueJson?.Trim(), out var b) ? b : fallback;
        }
        catch (Exception ex)
        {
            // Table absent (pre-migration) or DB unreachable at this instant —
            // fall back rather than fail startup. Debug, not warning: this is the
            // expected path on a fresh install's first start.
            logger?.LogDebug(ex,
                "system_settings read for '{Key}' failed (likely pre-migration); using fallback {Fallback}.",
                key, fallback);
            return fallback;
        }
    }
}
