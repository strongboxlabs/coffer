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
    private readonly TimeProvider _clock;

    public SessionsRepository(ServiceDbContextFactory serviceFactory, TimeProvider clock)
    {
        _serviceFactory = serviceFactory;
        _clock = clock;
    }

    /// <summary>
    /// Insert a new session row, stamped with the app clock. The caller
    /// supplies a pre-computed hash + expiry so the cookie value is set
    /// once and never re-derived. Returns the persisted row.
    /// </summary>
    public async Task<SessionRow> InsertAsync(
        Guid userId,
        byte[] sessionHash,
        string? userAgent,
        DateTime expiresAt,
        string authMethod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHash);
        if (sessionHash.Length != 32)
            throw new ArgumentException("Session hash must be SHA-256 (32 bytes).", nameof(sessionHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethod);

        // Stamped here rather than left to the column default: the session's start
        // and last-seen are compared against the app clock (idle timeout, step-up
        // eligibility), so they come from it too. Truncated to the microseconds
        // timestamptz keeps, so the row returned is the row stored.
        var clockNow = _clock.GetUtcNow().UtcDateTime;
        var now = new DateTime(clockNow.Ticks - clockNow.Ticks % 10, DateTimeKind.Utc);
        var row = new SessionRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionHash = sessionHash,
            UserAgent = userAgent,
            ExpiresAt = expiresAt,
            AuthMethod = authMethod,
            CreatedAt = now,
            LastSeenAt = now,
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
    /// hash matches no row, the row is revoked, the row has expired, or its
    /// user is disabled — the auth handler treats all four the same way (401).
    /// </summary>
    /// <remarks>
    /// The disabled check is not redundant with disabling revoking every session
    /// (2026-10-12 audit, H2). Revocation handles the sessions that exist when the
    /// admin clicks; this handles a session minted in the same instant, and any
    /// future path that flips <c>is_disabled</c> without going through the revoke.
    /// </remarks>
    public async Task<ActiveSession?> GetActiveByHashAsync(
        byte[] sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionHash);
        if (sessionHash.Length != 32)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.SessionHash == sessionHash
                     && s.RevokedAt == null
                     && s.ExpiresAt > now)
            .Join(
                db.Users.Where(u => !u.IsDisabled),
                s => s.UserId,
                u => u.Id,
                (s, u) => new ActiveSession(
                    s.Id, s.UserId, s.ExpiresAt, s.LastSeenAt, u.IsAdmin, s.CreatedAt, s.AuthMethod))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bump <c>last_seen_at</c> on a session row. Called on every
    /// authenticated request so the idle-timeout check has fresh data.
    /// Single-statement <c>UPDATE</c> via EF's <c>ExecuteUpdate</c>.
    /// </summary>
    /// <remarks>
    /// The app clock, read once and sent as a parameter, like the insert's
    /// stamp and the idle-timeout check that reads it. It used to be the
    /// database's <c>now()</c> (a bare clock read inside the lambda is
    /// translated to SQL), which the idle check then compared against the
    /// process clock: two clocks in one comparison.
    /// </remarks>
    public async Task BumpLastSeenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        await db.AuthSessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastSeenAt, _ => now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke a single session by id. Idempotent — calling on an
    /// already-revoked row leaves the original <c>revoked_at</c> alone.
    /// </summary>
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        await db.AuthSessions
            .Where(s => s.Id == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke every active session for a user: disabling them, and an admin's
    /// "sign out everywhere".
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A user's live sessions, most recently used first (FU-087): not revoked, not past
    /// their lifetime, and seen since <paramref name="seenSince"/>, the idle cutoff the
    /// caller owns. ADR-0013 promised this list; until now there was only a button.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListActiveForUserAsync(
        Guid userId, DateTime seenSince, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now && s.LastSeenAt >= seenSince)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new SessionSummary(s.Id, s.CreatedAt, s.LastSeenAt, s.UserAgent, s.AuthMethod))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revoke one of <paramref name="userId"/>'s own sessions (FU-087). False when there is
    /// no live session with that id for that user, someone else's included.
    /// </summary>
    public async Task<bool> RevokeOwnAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Revoke every active session for a user except <paramref name="keepSessionId"/>:
    /// "sign out of other sessions", which keeps the one asking.
    /// </summary>
    public async Task<int> RevokeOthersForUserAsync(
        Guid userId, Guid keepSessionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = _serviceFactory.Create();
        return await db.AuthSessions
            .Where(s => s.UserId == userId && s.Id != keepSessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A live session joined to the owning user's admin flag, as returned
    /// by <see cref="GetActiveByHashAsync"/>. <see cref="LastSeenAt"/> drives
    /// the idle-timeout check; <see cref="IsAdmin"/> rides into the
    /// authenticated principal as a claim. <see cref="CreatedAt"/> and
    /// <see cref="AuthMethod"/> feed the step-up rules: a tap must come from a
    /// passkey older than the session, and a recovery-code session may add one.
    /// </summary>
    public sealed record ActiveSession(
        Guid Id, Guid UserId, DateTime ExpiresAt, DateTime LastSeenAt, bool IsAdmin,
        DateTime CreatedAt, string AuthMethod);

    /// <summary>One of a user's live sessions, as their session list shows it.</summary>
    public sealed record SessionSummary(
        Guid Id, DateTime CreatedAt, DateTime LastSeenAt, string? UserAgent, string AuthMethod);
}
