using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Backup.Drive;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin-only Google Drive backup-sync surface (ADR-0062). The admin routes are
/// gated by <see cref="AuthPolicies.RequireAdmin"/> — Drive sync is a
/// deployment-global destination, not per-ledger. The OAuth callback is the lone
/// anonymous route: it's reached by Google redirecting the browser back, and is
/// guarded instead by the single-use CSRF <c>state</c> minted by the (admin-only)
/// connect/start.
///
///   * GET  /api/admin/drive-sync                — status (never the token)
///   * POST /api/admin/drive-sync/connect/start  — begin auth; returns Google URL
///   * GET  /api/admin/drive-sync/oauth/callback — Google's redirect target (anon)
///   * POST /api/admin/drive-sync/disconnect     — forget the token + folder
///   * PUT  /api/admin/drive-sync/enabled        — toggle auto-upload (④b+c)
///   * POST /api/admin/drive-sync/upload-all     — mirror local backups to the Drive folder (④b+c)
/// </summary>
public static class AdminDriveSyncEndpoints
{
    /// <summary>The OAuth redirect path. Must match the authorized redirect URI
    /// registered on the operator's Web OAuth client.</summary>
    public const string CallbackPath = "/api/admin/drive-sync/oauth/callback";

    public static IEndpointRouteBuilder MapAdminDriveSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/drive-sync")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/", GetStatusAsync);
        group.MapPost("/connect/start", ConnectStartAsync);
        group.MapPost("/disconnect", DisconnectAsync);
        group.MapPut("/enabled", SetEnabledAsync);
        group.MapPost("/upload-all", UploadAllAsync);

        // Anonymous: Google redirects the browser here; the state is the guard.
        routes.MapGet(CallbackPath, OAuthCallbackAsync).AllowAnonymous();
        return routes;
    }

    private static async Task<IResult> GetStatusAsync(
        DriveSyncService drive, CancellationToken cancellationToken) =>
        Results.Ok(await drive.GetStatusAsync(cancellationToken).ConfigureAwait(false));

    private static IResult ConnectStartAsync(
        DriveConnectStartRequest? request,
        ICurrentUserAccessor currentUser,
        DriveSyncService drive,
        IOptions<ApiOptions> options)
    {
        if (string.IsNullOrWhiteSpace(request?.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
            return BusinessError.Problem(BusinessError.Codes.DriveClientRequired,
                "Provide the OAuth client id and secret from your Google Cloud project.");

        var redirectUri = WebOrigin(options.Value) + CallbackPath;
        var authUrl = drive.StartConnect(
            request.ClientId.Trim(), request.ClientSecret.Trim(), redirectUri, currentUser.UserId);
        return Results.Ok(new DriveConnectStartResponse(authUrl));
    }

    private static async Task<IResult> OAuthCallbackAsync(
        string? code,
        string? state,
        string? error,
        DriveSyncService drive,
        IOptions<ApiOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var backTo = WebOrigin(options.Value) + "/system?tab=backups&drive=";

        // The user declined consent at Google, or a required param is missing.
        if (!string.IsNullOrEmpty(error))
            return Results.Redirect(backTo + "denied");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Results.Redirect(backTo + "error");

        try
        {
            await drive.CompleteConnectAsync(code, state, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return Results.Redirect(backTo + "connected");
        }
        catch (Exception ex)
        {
            // Expired state, code-exchange failure, or folder provisioning error.
            // Detail stays in the logs; the browser just learns it failed.
            loggerFactory.CreateLogger("AdminDriveSync").LogWarning(ex, "Drive OAuth callback failed.");
            return Results.Redirect(backTo + "error");
        }
    }

    private static async Task<IResult> DisconnectAsync(
        DriveSyncService drive, CancellationToken cancellationToken)
    {
        await drive.DisconnectAsync(DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> SetEnabledAsync(
        DriveEnabledRequest? request,
        ICurrentUserAccessor currentUser,
        DriveSyncService drive,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BusinessError.Problem(BusinessError.Codes.DriveNotConnected, "Missing request body.");
        try
        {
            var status = await drive.SetEnabledAsync(
                request.Enabled, currentUser.UserId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        }
        catch (DriveOAuthException ex)
        {
            return BusinessError.Problem(BusinessError.Codes.DriveNotConnected, ex.Message);
        }
    }

    private static async Task<IResult> UploadAllAsync(
        GoogleDriveBackupDestination destination,
        BackupPinsRepository pins,
        DriveSyncService drive,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var pinned = await pins.GetPinnedIdsAsync(cancellationToken).ConfigureAwait(false);
            await destination.UploadMissingAsync(pinned, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await drive.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DriveOAuthException ex)
        {
            // Not connected — a business rejection.
            return BusinessError.Problem(BusinessError.Codes.DriveNotConnected, ex.Message);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("AdminDriveSync").LogError(ex, "Drive upload-all failed.");
            return Results.Problem("Drive upload failed. Check the server logs.", statusCode: 500);
        }
    }

    /// <summary>The browser-facing HTTPS origin (same canonical URL the WebAuthn
    /// RP uses): the first configured Fido2 origin, else the dev default.</summary>
    private static string WebOrigin(ApiOptions options) =>
        (options.Fido2.Origins.Count > 0 ? options.Fido2.Origins[0] : "http://localhost:8080").TrimEnd('/');
}
