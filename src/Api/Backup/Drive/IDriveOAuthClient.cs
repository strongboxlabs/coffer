namespace Coffer.Api.Backup.Drive;

/// <summary>
/// Google OAuth 2.0 authorization-code flow (ADR-0062 D2) — the seam over
/// Google's auth + token endpoints so the connect flow is testable with a fake
/// while the real impl talks to <c>accounts.google.com</c> /
/// <c>oauth2.googleapis.com</c>. Coffer is web-exposed over HTTPS, so it uses the
/// standard redirect flow with a callback to its own origin (not the device-code
/// flow): this lets an operator reuse an existing <b>Web application</b> OAuth
/// client by just adding one authorized redirect URI.
/// </summary>
public interface IDriveOAuthClient
{
    /// <summary>Build the Google consent URL to send the admin's browser to.
    /// Requests offline access (so a refresh token comes back) for the
    /// <c>drive.file</c> scope, carrying an opaque CSRF <paramref name="state"/>.</summary>
    string BuildAuthorizationUrl(string clientId, string redirectUri, string state);

    /// <summary>Exchange the authorization code Google redirected back with for a
    /// refresh token. <paramref name="redirectUri"/> must match the one used in
    /// <see cref="BuildAuthorizationUrl"/>.</summary>
    Task<DriveTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri,
        CancellationToken cancellationToken);
}

/// <summary>Outcome of the code exchange. <see cref="RefreshToken"/> is set only
/// when <see cref="Success"/> is true.</summary>
public sealed record DriveTokenResult(bool Success, string? RefreshToken = null, string? ErrorDetail = null);
