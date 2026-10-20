using System.Globalization;
using System.Security.Claims;

namespace Coffer.Api.Auth;

/// <summary>
/// Claims the cookie scheme stamps about the session itself, as opposed to the user.
/// </summary>
/// <remarks>
/// Only a cookie session carries them. Step-up reads <see cref="CreatedAt"/> to refuse a
/// passkey enrolled after the session began, and <see cref="AuthMethod"/> to let a
/// recovery-code session add one passkey (2026-10-12 audit, M2). A principal without them
/// — an MCP bearer token, dev auth — is not a session and gets neither rule's benefit.
/// </remarks>
public static class SessionClaims
{
    public const string SessionId = "sid";
    public const string CreatedAt = "coffer:session_created_at";
    public const string AuthMethod = "coffer:session_auth_method";

    /// <summary>The session's id, or null when the principal is not a cookie session.</summary>
    public static Guid? SessionIdOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(SessionId), out var id) ? id : null;

    /// <summary>When the session began (UTC), or null when the principal is not a cookie session.</summary>
    public static DateTime? CreatedAtOf(ClaimsPrincipal principal) =>
        DateTime.TryParse(
            principal.FindFirstValue(CreatedAt), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUniversalTime()
            : null;

    /// <summary>How the session began, or null when the principal is not a cookie session.</summary>
    public static string? AuthMethodOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue(AuthMethod);
}
