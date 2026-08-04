using System.Security.Cryptography;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Coffer.Api.Contracts;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Backup.Drive;

/// <summary>
/// Business-layer orchestration for the Google Drive <b>connection</b> lifecycle
/// (ADR-0062): the authorization-code connect flow, sealing the OAuth material
/// under the master KEK, provisioning the per-install Coffer folder, status, and
/// disconnect. The actual artifact push + remote retention live in
/// <see cref="GoogleDriveBackupDestination"/> (the <see cref="IBackupDestination"/>
/// impl). The plaintext client secret / refresh token only ever live on the
/// stack here — the DB holds the KEK-sealed blob, never cleartext.
/// </summary>
public sealed class DriveSyncService
{
    /// <summary>Base name of the Drive folder Coffer creates + manages. The
    /// per-install id is appended (<c>"Coffer Backups [a1b2c3]"</c>) so two installs
    /// sharing one OAuth client + account land in distinct folders (ADR-0062 D5).</summary>
    public const string FolderBaseName = "Coffer Backups";

    /// <summary>Compose the per-install folder name from the base + install id.</summary>
    public static string FolderNameFor(string installId) => $"{FolderBaseName} [{installId}]";

    private readonly IDriveOAuthClient _oauth;
    private readonly IDriveClient _drive;
    private readonly DriveSyncRepository _repo;
    private readonly LedgerKeyService _keys;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DriveSyncService> _logger;

    public DriveSyncService(
        IDriveOAuthClient oauth,
        IDriveClient drive,
        DriveSyncRepository repo,
        LedgerKeyService keys,
        IMemoryCache cache,
        ILogger<DriveSyncService> logger)
    {
        _oauth = oauth;
        _drive = drive;
        _repo = repo;
        _keys = keys;
        _cache = cache;
        _logger = logger;
    }

    public Task<DriveSyncStatus> GetStatusAsync(CancellationToken ct = default) =>
        _repo.GetStatusAsync(ct);

    /// <summary>Begin the authorization-code flow: mint a CSRF state, stash the
    /// client secret + redirect uri + actor server-side keyed by it (never echoed
    /// to the browser), and return the Google consent URL to redirect to.</summary>
    public string StartConnect(
        string clientId, string clientSecret, string redirectUri, Guid actorUserId)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _cache.Set(
            CacheKey(state),
            new PendingConnect(clientId, clientSecret, redirectUri, actorUserId),
            TimeSpan.FromMinutes(15));
        return _oauth.BuildAuthorizationUrl(clientId, redirectUri, state);
    }

    /// <summary>Complete the flow from Google's redirect: validate the state
    /// (single-use), exchange the code for a refresh token, seal it, provision the
    /// folder, and persist. The state — created only by an admin-authenticated
    /// <see cref="StartConnect"/> and delivered solely to that admin's browser via
    /// Google — is the CSRF guard, so the callback needs no separate auth.</summary>
    public async Task<DriveSyncStatus> CompleteConnectAsync(
        string code, string state, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(CacheKey(state), out PendingConnect? pending) || pending is null)
            throw new DriveOAuthException(
                "The Google sign-in link expired or was already used. Start the connection again.");
        _cache.Remove(CacheKey(state));

        var result = await _oauth.ExchangeCodeAsync(
            pending.ClientId, pending.ClientSecret, code, pending.RedirectUri, ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrEmpty(result.RefreshToken))
            throw new DriveOAuthException(result.ErrorDetail ?? "Authorization failed.");

        var creds = new DriveCredentials(pending.ClientId, pending.ClientSecret, result.RefreshToken);
        var email = await _drive.GetAccountEmailAsync(creds, ct).ConfigureAwait(false);
        // Stable per-install id → distinct folder per install even when two
        // installs reuse one OAuth client + account (ADR-0062 D5). Generated once,
        // kept across disconnect so a reconnect resolves the same folder.
        var installId = await _repo.EnsureInstallIdAsync(NewInstallId(), ct).ConfigureAwait(false);
        var folder = await _drive.EnsureBackupFolderAsync(
            creds, FolderNameFor(installId), ct).ConfigureAwait(false);
        var sealedBlob = _keys.SealWithMasterKey(DriveCredentialCodec.Serialize(creds));
        var status = await _repo.ConnectAsync(
            sealedBlob, folder.Id, folder.Name, email, pending.ActorUserId, nowUtc, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Google Drive connected by user {UserId} (folder '{Folder}').", pending.ActorUserId, folder.Name);
        return status;
    }

    /// <summary>Enable/disable auto-push (push runs with each backup). Enabling
    /// requires a connected account.</summary>
    public async Task<DriveSyncStatus> SetEnabledAsync(
        bool enabled, Guid actorUserId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (enabled)
        {
            var current = await _repo.GetStatusAsync(ct).ConfigureAwait(false);
            if (!current.Connected)
                throw new DriveOAuthException("Connect a Google account before enabling sync.");
        }
        return await _repo.SetEnabledAsync(enabled, actorUserId, nowUtc, ct).ConfigureAwait(false);
    }

    public Task DisconnectAsync(DateTime nowUtc, CancellationToken ct = default) =>
        _repo.DisconnectAsync(nowUtc, ct);

    private static string CacheKey(string state) => "drive-oauth-state:" + state;

    /// <summary>A short opaque install id: 3 random bytes as 6 lowercase hex
    /// chars (e.g. <c>a1b2c3</c>) — enough to disambiguate one operator's own
    /// installs without being unwieldy in a folder name.</summary>
    private static string NewInstallId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private sealed record PendingConnect(
        string ClientId, string ClientSecret, string RedirectUri, Guid ActorUserId);
}
