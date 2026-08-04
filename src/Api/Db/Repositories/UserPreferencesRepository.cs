using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The general per-(user, ledger) preference store (ADR-0057). One row per
/// (user, ledger, namespace); the value is a namespace-typed JSON document.
/// Typed accessors per namespace serialize/deserialize at this boundary (the
/// <c>ledger_operations.details</c> pattern); a GET always returns a fully-populated
/// value (server defaults when absent) so callers never special-case "unset".
/// </summary>
public sealed class UserPreferencesRepository
{
    /// <summary>Namespace keys (ADR-0057). New preference area = new entry.</summary>
    public static class Namespaces
    {
        public const string Quotes = "quotes";
        public const string Dashboard = "dashboard";
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;

    public UserPreferencesRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The <c>quotes</c> preference for (user, ledger) — the external providers
    /// this user enabled for this ledger. Returns an empty (opt-out) value when
    /// no row exists.
    /// </summary>
    public async Task<QuotesPrefs> GetQuotesAsync(
        Guid userId, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var json = await GetRawAsync(userId, ledgerId, Namespaces.Quotes, cancellationToken)
            .ConfigureAwait(false);
        if (json is null)
            return new QuotesPrefs();
        return JsonSerializer.Deserialize<QuotesPrefs>(json, JsonOpts) ?? new QuotesPrefs();
    }

    /// <summary>Upsert the <c>quotes</c> preference for (user, ledger).</summary>
    public Task SetQuotesAsync(
        Guid userId, Guid ledgerId, QuotesPrefs prefs, CancellationToken cancellationToken = default) =>
        UpsertRawAsync(
            userId, ledgerId, Namespaces.Quotes,
            JsonSerializer.Serialize(prefs, JsonOpts), cancellationToken);

    /// <summary>
    /// The <c>dashboard</c> layout preference for (user, ledger). Empty (no
    /// widgets) when unset — the SPA resolves that to the canonical default.
    /// </summary>
    public async Task<DashboardPrefs> GetDashboardAsync(
        Guid userId, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var json = await GetRawAsync(userId, ledgerId, Namespaces.Dashboard, cancellationToken)
            .ConfigureAwait(false);
        if (json is null)
            return new DashboardPrefs();
        return JsonSerializer.Deserialize<DashboardPrefs>(json, JsonOpts) ?? new DashboardPrefs();
    }

    /// <summary>Upsert the <c>dashboard</c> layout preference for (user, ledger).</summary>
    public Task SetDashboardAsync(
        Guid userId, Guid ledgerId, DashboardPrefs prefs, CancellationToken cancellationToken = default) =>
        UpsertRawAsync(
            userId, ledgerId, Namespaces.Dashboard,
            JsonSerializer.Serialize(prefs, JsonOpts), cancellationToken);

    private async Task<string?> GetRawAsync(
        Guid userId, Guid ledgerId, string ns, CancellationToken cancellationToken)
    {
        var rows = await _db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.LedgerId == ledgerId && p.Namespace == ns)
            .Select(p => p.ValueJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task UpsertRawAsync(
        Guid userId, Guid ledgerId, string ns, string json, CancellationToken cancellationToken)
    {
        var existing = await _db.UserPreferences
            .FirstOrDefaultAsync(
                p => p.UserId == userId && p.LedgerId == ledgerId && p.Namespace == ns,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.UserPreferences.Add(new UserPreferenceRow
            {
                UserId = userId,
                LedgerId = ledgerId,
                Namespace = ns,
                ValueJson = json,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.ValueJson = json;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
