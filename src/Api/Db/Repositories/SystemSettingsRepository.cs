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

    /// <summary>
    /// Present once first-run setup has created a user (written by setup itself; mig
    /// 248 backfills it on installs that predate it). First-run setup — including its
    /// pre-auth restore — is offered only while it is absent (ADR-0105 D7).
    /// </summary>
    public const string SetupCompletedKey = "setup.completed";

    /// <summary>
    /// Whether an admin can start an upgrade from the app (ADR-0110). On when absent: the host
    /// updater is installed and running by default, and this is the in-app switch over it.
    /// </summary>
    public const string UpgradeInAppEnabledKey = "upgrade.in_app_enabled";

    /// <summary>
    /// Whether the app checks for a new release on its own, every few hours (ADR-0110). Off when
    /// absent: an opt-in. "Check now" is always available either way.
    /// </summary>
    public const string UpgradeAutoCheckKey = "upgrade.auto_check";

    /// <summary>
    /// Whether releases are read from a private fork, with the sealed token
    /// (<c>upgrade_repo_token</c>). Off when absent: the public repository, no token (ADR-0110).
    /// </summary>
    public const string UpgradePrivateRepoKey = "upgrade.private_repo";

    /// <summary>
    /// The release the app last announced as available (a JSON string, ADR-0110), so a newer one is
    /// announced once and an all-clear follows only for something announced. Absent when nothing is
    /// outstanding.
    /// </summary>
    public const string UpgradeAnnouncedKey = "upgrade.announced_version";

    /// <summary>
    /// The last successful release check, as the Upgrade tab shows it (a JSON object, ADR-0110).
    /// Kept so a restart shows what was shown before: the check result lives in memory otherwise.
    /// </summary>
    public const string UpgradeLastCheckKey = "upgrade.last_check";

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

    /// <summary>The raw JSON of <paramref name="key"/>, or null when it is absent.</summary>
    public async Task<string?> GetValueJsonAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.ValueJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Upsert <paramref name="json"/> (valid JSON) for <paramref name="key"/>.</summary>
    public async Task SetValueJsonAsync(
        string key, string json, Guid? updatedBy, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSettingRow
            {
                Key = key, ValueJson = json, UpdatedAt = nowUtc, UpdatedBy = updatedBy,
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

    /// <summary>Remove <paramref name="key"/>; absent already is not an error.</summary>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await db.SystemSettings.Where(s => s.Key == key)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // Raw JSONB text for a boolean setting is "true"/"false"; tolerate stray
    // whitespace and fall back on anything else.
    private static bool ParseBool(string? json, bool fallback) =>
        bool.TryParse(json?.Trim(), out var b) ? b : fallback;
}
