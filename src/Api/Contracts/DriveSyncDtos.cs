namespace Coffer.Api.Contracts;

/// <summary>
/// Public status of Google Drive backup sync (ADR-0062). Never carries the
/// sealed OAuth material — only whether an account is connected + display
/// metadata. Returned by <c>GET /api/admin/drive-sync</c>.
/// </summary>
public sealed record DriveSyncStatus(
    bool Enabled,
    bool Connected,
    string? ConnectedEmail,
    string? FolderName,
    string? InstallId,
    DateTime? LastSyncAt,
    string? LastSyncStatus,
    string? LastSyncError);

/// <summary>
/// Request body for <c>POST /api/admin/drive-sync/connect/start</c> — the
/// admin's own Google Cloud <b>Web application</b> OAuth client. Kicks off the
/// authorization-code redirect flow.
/// </summary>
public sealed class DriveConnectStartRequest
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
}

/// <summary>
/// Response from <c>connect/start</c>: the Google consent URL to redirect the
/// admin's browser to. The client secret rides server-side state keyed by the
/// CSRF <c>state</c> embedded in the URL, never the wire.
/// </summary>
public sealed record DriveConnectStartResponse(string AuthorizationUrl);

/// <summary>Request body for <c>PUT /api/admin/drive-sync/enabled</c> — toggle
/// auto-push-with-each-backup (④b+c).</summary>
public sealed class DriveEnabledRequest
{
    public bool Enabled { get; init; }
}
