using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// EF Core-backed gateway to <c>auth_sessions</c>. Connects via the
/// service-role factory (<see cref="ServiceDbContextFactory"/>): the
/// cookie auth handler validates sessions BEFORE <c>app.user_id</c>
/// is set on the request, so RLS would deny the read on coffer_app.
/// Session insert/revoke also runs as the auth subsystem rather than
/// as the user, so the service role applies there too.
/// </summary>
public sealed class SessionsRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public SessionsRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// Insert a new session row. The schema generates id + timestamps;
    /// the caller supplies a pre-computed hash + expiry so the cookie
    /// value is set once and never re-derived. Returns the persisted row
    /// (so callers can read back the assigned id and DB-defaulted
    /// timestamps via the RETURNING clause).
    /// </summary>
    public async Task<SessionRow> InsertAsync(
        Guid userId,
        byte[] sessionHash,
        string? userAgent,
        DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHash);
        if (sessionHash.Length != 32)
            throw new ArgumentException("Session hash must be SHA-256 (32 bytes).", nameof(sessionHash));

        var row = new SessionRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionHash = sessionHash,
            UserAgent = userAgent,
            ExpiresAt = expiresAt,
        };
        await using var db = _serviceFactory.Create();
        db.AuthSessions.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Look up a session by its hashed cookie value, joined to the owning
    /// user so the caller gets <c>is_admin</c> in the same round-trip (the
    /// cookie auth handler stamps it as a claim on every request — keeping
    /// it one query avoids a second hot-path read). Returns null when the
    /// hash matches no row, the row is revoked, or the row has expired —
    /// the auth handler treats all three the same way (401).
    /// </summary>
    public async Task<ActiveSession?> GetActiveByHashAsync(
        byte[] sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHash);
        if (sessionHash.Length != 32)
            return null;

        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.SessionHash == sessionHash
                     && s.RevokedAt == null
                     && s.ExpiresAt > now)
            .Join(
                db.Users,
                s => s.UserId,
                u => u.Id,
                (s, u) => new ActiveSession(
                    s.Id, s.UserId, s.ExpiresAt, s.LastSeenAt, u.IsAdmin))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bump <c>last_seen_at</c> on a session row. Called on every
    /// authenticated request so the idle-timeout check has fresh data.
    /// Single-statement <c>UPDATE</c> via EF's <c>ExecuteUpdate</c>.
    /// </summary>
    /// <remarks>
    /// The new value is <c>DateTime.UtcNow</c> evaluated inside the
    /// lambda, which the Npgsql provider translates to server-side
    /// <c>NOW() AT TIME ZONE 'UTC'</c>. Using the Postgres clock here
    /// matches <c>auth_sessions.last_seen_at</c>'s INSERT default
    /// (<c>DEFAULT now()</c>) so successive insert→bump comparisons
    /// can't tip over because of process-to-server clock drift or
    /// Windows' ~15 ms <c>DateTime.UtcNow</c> tick granularity.
    /// </remarks>
    public async Task BumpLastSeenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await db.AuthSessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastSeenAt, _ => DateTime.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke a single session by id. Idempotent — calling on an
    /// already-revoked row leaves the original <c>revoked_at</c> alone.
    /// </summary>
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        await db.AuthSessions
            .Where(s => s.Id == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke every active session for a user. Powers a future
    /// "sign out everywhere" UI; not exposed via an endpoint in PR 3.3.
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A live session joined to the owning user's admin flag, as returned
    /// by <see cref="GetActiveByHashAsync"/>. <see cref="LastSeenAt"/> drives
    /// the idle-timeout check; <see cref="IsAdmin"/> rides into the
    /// authenticated principal as a claim.
    /// </summary>
    public sealed record ActiveSession(
        Guid Id, Guid UserId, DateTime ExpiresAt, DateTime LastSeenAt, bool IsAdmin);
}
