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
    private readonly TimeProvider _clock;

    public UserPreferencesRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
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
    /// The stored <c>dashboard</c> document for (user, ledger) as stored, whatever its
    /// version, and the row's <c>updated_at</c>; both null when there is no row. The
    /// SPA resolves the document, so it is passed through rather than typed here.
    /// </summary>
    public async Task<(string? Json, DateTime? UpdatedAt)> GetDashboardAsync(
        Guid userId, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var row = await _db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.LedgerId == ledgerId && p.Namespace == Namespaces.Dashboard)
            .Select(p => new { p.ValueJson, p.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? (null, null) : (row.ValueJson, row.UpdatedAt);
    }

    /// <summary>
    /// Write the <c>dashboard</c> document if the row is still as the caller last read
    /// it: <paramref name="expectedUpdatedAt"/> null means no row may exist, a value
    /// means the row's <c>updated_at</c> must equal it. Returns the row's new
    /// <c>updated_at</c>, or null when the write was stale and nothing was written.
    /// </summary>
    /// <remarks>
    /// A compare-and-swap of its own rather than the shared upsert, which writes
    /// whatever it is given. Two tabs each saving a layout would otherwise each erase
    /// the other's without either knowing. The new <c>updated_at</c> is cut to
    /// microseconds, the column's precision, so the value returned is the value stored
    /// and the caller's next write can match it.
    /// </remarks>
    public async Task<DateTime?> SaveDashboardAsync(
        Guid userId, Guid ledgerId, string json, DateTime? expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);

        if (expectedUpdatedAt is { } expected)
        {
            var at = expected.Kind == DateTimeKind.Utc ? expected : expected.ToUniversalTime();
            var written = await _db.UserPreferences
                .Where(p => p.UserId == userId && p.LedgerId == ledgerId
                            && p.Namespace == Namespaces.Dashboard && p.UpdatedAt == at)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.ValueJson, json)
                    .SetProperty(p => p.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            return written == 1 ? now : null;
        }

        var row = new UserPreferenceRow
        {
            UserId = userId,
            LedgerId = ledgerId,
            Namespace = Namespaces.Dashboard,
            ValueJson = json,
            UpdatedAt = now,
        };
        _db.UserPreferences.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return now;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
                                               { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A row exists after all: the caller's read is out of date.
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>Remove the <c>dashboard</c> document for (user, ledger), if any. The
    /// SPA's default layout applies again.</summary>
    public Task DeleteDashboardAsync(
        Guid userId, Guid ledgerId, CancellationToken cancellationToken = default) =>
        _db.UserPreferences
            .Where(p => p.UserId == userId && p.LedgerId == ledgerId && p.Namespace == Namespaces.Dashboard)
            .ExecuteDeleteAsync(cancellationToken);

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
                UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            });
        }
        else
        {
            existing.ValueJson = json;
            existing.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
