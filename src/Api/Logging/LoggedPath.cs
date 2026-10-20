using Microsoft.AspNetCore.Http;

namespace Coffer.Api.Logging;

/// <summary>
/// The request path as the access log may write it: the secret a path carries is
/// replaced, everything else is kept (FU-083).
/// </summary>
/// <remarks>
/// An invite link is a seven-day credential, and some grant admin; the setup token makes
/// whoever holds it the first admin; a consent ticket names the authorization it approves.
/// Each travels as one path segment, and the access line is written at Information, so the
/// raw path put them in every log a deployment keeps and ships.
///
/// Redacting by prefix rather than by the matched route keeps every other path intact,
/// identifiers included, and covers what no API route matches: the SPA pages those links
/// open, served by the fallback. <c>AccessLogRedactionTests</c> holds the catalog: every
/// route parameter in the API's table and every one in the SPA's router must be an
/// identifier, a declared non-secret, or sit under one of these prefixes.
/// </remarks>
public static class LoggedPath
{
    /// <summary>Each is followed by exactly one secret segment.</summary>
    public static readonly IReadOnlyList<string> SecretSegmentPrefixes =
    [
        "/api/auth/invite/",
        "/api/auth/setup/",
        "/api/oauth/consent/",
        "/invite/",
        "/setup/",
    ];

    public const string Redacted = "{redacted}";

    public static string? Render(PathString path)
    {
        var value = path.Value;
        if (value is null) return null;

        foreach (var prefix in SecretSegmentPrefixes)
        {
            // Routing matches without regard to case, so the redaction must too.
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var end = value.IndexOf('/', prefix.Length);
            return value[..prefix.Length] + Redacted + (end < 0 ? "" : value[end..]);
        }
        return value;
    }
}
