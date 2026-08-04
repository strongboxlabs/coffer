using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Backup.Drive;

/// <summary>
/// Real <see cref="IDriveOAuthClient"/> — the OAuth 2.0 authorization-code flow
/// against Google's endpoints (ADR-0062 D2). Coffer serves the redirect callback
/// on its own HTTPS origin, so an operator can reuse an existing Web-application
/// OAuth client by adding one redirect URI. Scope is <c>drive.file</c> only;
/// <c>access_type=offline</c> + <c>prompt=consent</c> guarantee a refresh token.
/// </summary>
public sealed class GoogleDriveOAuthClient : IDriveOAuthClient
{
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _http;
    private readonly ILogger<GoogleDriveOAuthClient> _logger;

    public GoogleDriveOAuthClient(HttpClient http, ILogger<GoogleDriveOAuthClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string BuildAuthorizationUrl(string clientId, string redirectUri, string state)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = clientId;
        q["redirect_uri"] = redirectUri;
        q["response_type"] = "code";
        q["scope"] = DriveFileScope;
        // offline → issue a refresh token; consent → force it even if the user
        // already granted this client before (otherwise Google omits it).
        q["access_type"] = "offline";
        q["prompt"] = "consent";
        q["include_granted_scopes"] = "true";
        q["state"] = state;
        return $"{AuthEndpoint}?{q}";
    }

    public async Task<DriveTokenResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri,
        CancellationToken cancellationToken)
    {
        using var resp = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }), cancellationToken).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return new DriveTokenResult(false,
                ErrorDetail: Field(json, "error_description") ?? Field(json, "error") ?? $"HTTP {(int)resp.StatusCode}");

        var refresh = Field(json, "refresh_token");
        if (string.IsNullOrEmpty(refresh))
            return new DriveTokenResult(false,
                ErrorDetail: "Google returned no refresh token. Revoke any prior Coffer grant at "
                    + "myaccount.google.com/permissions and connect again so offline access is requested.");

        return new DriveTokenResult(true, RefreshToken: refresh);
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // non-JSON body (rare); callers fall back to status code
            _logger.LogWarning(ex, "Google OAuth token endpoint returned a non-JSON body ({Status})", resp.StatusCode);
            return default;
        }
    }

    private static string? Field(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>Raised on a non-recoverable OAuth failure.</summary>
public sealed class DriveOAuthException : Exception
{
    public DriveOAuthException(string message) : base(message) { }
}
