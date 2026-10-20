using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// Encapsulates the cookie-session contract: mints a 32-byte random cookie,
/// stores its SHA-256 in <c>auth_sessions</c>, validates a presented cookie
/// against the table, and revokes on logout. The plaintext only ever
/// exists in transit between client and this service — the DB sees only
/// the hash.
/// </summary>
public sealed class SessionService
{
    private readonly SessionsRepository _sessions;
    private readonly CookieSessionOptions _cookieOptions;
    private readonly TimeSpan _maxLifetime;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _clock;

    public SessionService(SessionsRepository sessions, IOptions<ApiOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions;
        _cookieOptions = options.Value.Cookie;
        _maxLifetime = TimeSpan.FromDays(Math.Max(1, _cookieOptions.MaxLifetimeDays));
        _idleTimeout = TimeSpan.FromDays(Math.Max(1, _cookieOptions.IdleTimeoutDays));
        _clock = clock;
    }

    /// <summary>
    /// The cookie name, read by everything that sets, clears or validates it.
    /// </summary>
    /// <remarks>
    /// <c>__Host-</c>-prefixed whenever the cookie is Secure (2026-10-12 audit, M1).
    /// The browser then refuses the cookie unless it is Secure, host-only and
    /// <c>Path=/</c> — so a sibling subdomain can no longer plant a
    /// <c>Domain=.example.com</c> cookie of the same name to swap the operator into
    /// a session of its choosing. Plain-HTTP development can't carry the prefix (the
    /// browser would drop the cookie), so it keeps the bare configured name.
    /// Renaming signed everyone out once.
    /// </remarks>
    public string CookieName =>
        _cookieOptions.RequireHttps && !_cookieOptions.Name.StartsWith(HostPrefix, StringComparison.Ordinal)
            ? HostPrefix + _cookieOptions.Name
            : _cookieOptions.Name;

    private const string HostPrefix = "__Host-";

    /// <summary>
    /// Mint a session for <paramref name="userId"/>: 32 random bytes →
    /// SHA-256 stored in <c>auth_sessions</c>, base64url plaintext returned
    /// to the caller for the cookie. <paramref name="authMethod"/> records how the
    /// user proved who they are (<see cref="Db.Entities.SessionAuthMethods"/>).
    /// </summary>
    public async Task<IssuedSession> IssueAsync(
        Guid userId,
        string? userAgent,
        string authMethod,
        CancellationToken cancellationToken = default)
    {
        var (plaintext, hash) = GenerateCookieValue();
        var expiresAt = _clock.GetUtcNow().UtcDateTime.Add(_maxLifetime);

        var row = await _sessions.InsertAsync(userId, hash, userAgent, expiresAt, authMethod, cancellationToken)
                                 .ConfigureAwait(false);
        return new IssuedSession(row.Id, plaintext, row.ExpiresAt);
    }

    /// <summary>
    /// Validate a presented cookie value: hash, look up, check the idle
    /// timeout, bump <c>last_seen_at</c> on success. Returns the
    /// authenticated user id when valid, null otherwise (revoked /
    /// expired / unknown / idle too long).
    /// </summary>
    public async Task<ValidatedSession?> ValidateAsync(
        string presentedCookieValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presentedCookieValue))
            return null;

        byte[] hash;
        try
        {
            hash = HashCookieValue(presentedCookieValue);
        }
        catch (FormatException)
        {
            // Garbage cookie — treat as anonymous, never throw past here.
            return null;
        }

        var row = await _sessions.GetActiveByHashAsync(hash, cancellationToken)
                                 .ConfigureAwait(false);
        if (row is null) return null;

        if (_clock.GetUtcNow().UtcDateTime - row.LastSeenAt > _idleTimeout)
        {
            // Idle past the threshold → revoke and treat as expired so a
            // subsequent attempt with the same cookie also fails.
            await _sessions.RevokeAsync(row.Id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _sessions.BumpLastSeenAsync(row.Id, cancellationToken).ConfigureAwait(false);
        return new ValidatedSession(row.Id, row.UserId, row.ExpiresAt, row.IsAdmin, row.CreatedAt, row.AuthMethod);
    }

    /// <summary>
    /// Revoke a session by its presented cookie value. Returns true when
    /// a matching session was found and flipped, false when the cookie
    /// was unknown / already revoked (logout-of-anonymous-session is a
    /// no-op).
    /// </summary>
    public async Task<bool> RevokeAsync(
        string presentedCookieValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presentedCookieValue))
            return false;

        byte[] hash;
        try { hash = HashCookieValue(presentedCookieValue); }
        catch (FormatException) { return false; }

        var row = await _sessions.GetActiveByHashAsync(hash, cancellationToken)
                                 .ConfigureAwait(false);
        if (row is null) return false;

        await _sessions.RevokeAsync(row.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The user's live sessions (FU-087): the rows <see cref="ValidateAsync"/> would still
    /// accept, so a session idle past the timeout, which no longer signs anyone in, is
    /// not listed as if it did.
    /// </summary>
    public Task<IReadOnlyList<SessionsRepository.SessionSummary>> ListLiveAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _sessions.ListActiveForUserAsync(userId, _clock.GetUtcNow().UtcDateTime - _idleTimeout, cancellationToken);

    /// <summary>
    /// Build the <see cref="CookieOptions"/> the application uses when
    /// writing the cookie back to the response. Centralised so endpoints
    /// that issue or clear the cookie can't accidentally drift from the
    /// validator's expectations (cookie name, path, security flags).
    /// </summary>
    public Microsoft.AspNetCore.Http.CookieOptions BuildCookieOptions(DateTime expiresAt) =>
        new()
        {
            HttpOnly = true,
            Secure = _cookieOptions.RequireHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true,
        };

    /// <summary>
    /// Generate a 32-byte random cookie value, return its base64url
    /// plaintext (URL-safe so it can ride in a cookie header) and its
    /// SHA-256 hash for storage. Static so unit tests can exercise the
    /// encoder without instantiating the service.
    /// </summary>
    internal static (string Plaintext, byte[] Hash) GenerateCookieValue()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var plaintext = Base64UrlEncode(bytes);
        var hash = SHA256.HashData(bytes);
        return (plaintext, hash);
    }

    /// <summary>
    /// Hash a presented plaintext cookie value back to the SHA-256 stored
    /// at issue time so verification is a byte-array equality check.
    /// </summary>
    internal static byte[] HashCookieValue(string plaintext) =>
        SHA256.HashData(Base64UrlDecode(plaintext));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public sealed record IssuedSession(Guid SessionId, string CookieValue, DateTime ExpiresAt);
    public sealed record ValidatedSession(
        Guid SessionId, Guid UserId, DateTime ExpiresAt, bool IsAdmin, DateTime CreatedAt, string AuthMethod);
}
