using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>system_settings</c> (mig 147, ADR-0063 §D8) — the
/// deployment-global key/value settings store. Connects via the
/// <b>service-role</b> factory: the table has no per-ledger RLS predicate and is
/// reserved to <c>coffer_service</c> (same posture as
/// <see cref="GlobalSchedulesRepository"/> / backup_pins). The admin HTTP
/// surface gates writes with the RequireAdmin policy; this repository is the
/// data seam. Values are stored as JSONB; this v2 slice only needs booleans, so
/// the typed surface is intentionally minimal.
/// </summary>
public sealed class SystemSettingsRepository
{
    /// <summary>Key for the MCP server runtime toggle (ADR-0063 §D8). Read at
    /// startup as the effective gate alongside <c>Api:Mcp:Enabled</c>.</summary>
    public const string McpEnabledKey = "mcp.enabled";

    /// <summary>Key for the MCP <b>write</b> toggle (ADR-0068). Off by default;
    /// when on (AND <see cref="McpEnabledKey"/> is on), the MCP write tools are
    /// registered. Read at startup alongside <c>Api:Mcp:WritesEnabled</c>. Absent
    /// key ⇒ false, so no migration/seed is required.</summary>
    public const string McpWritesEnabledKey = "mcp.writes_enabled";

    private readonly ServiceDbContextFactory _serviceFactory;

    public SystemSettingsRepository(ServiceDbContextFactory serviceFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        _serviceFactory = serviceFactory;
    }

    /// <summary>The boolean value of <paramref name="key"/>, or
    /// <paramref name="fallback"/> when the key is absent or unparseable.</summary>
    public async Task<bool> GetBoolAsync(
        string key, bool fallback, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? fallback : ParseBool(row.ValueJson, fallback);
    }

    /// <summary>Upsert a boolean value for <paramref name="key"/>, stamping the
    /// editing admin + time.</summary>
    public async Task SetBoolAsync(
        string key,
        bool value,
        Guid? updatedBy,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);
        var json = value ? "true" : "false";
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSettingRow
            {
                Key = key,
                ValueJson = json,
                UpdatedAt = nowUtc,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = nowUtc;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Raw JSONB text for a boolean setting is "true"/"false"; tolerate stray
    // whitespace and fall back on anything else.
    private static bool ParseBool(string? json, bool fallback) =>
        bool.TryParse(json?.Trim(), out var b) ? b : fallback;
}
