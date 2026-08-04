namespace Coffer.Api.Backup.Drive;

/// <summary>
/// The Google Drive operations Coffer needs, behind a seam so the connect +
/// push flows are testable with a fake (the real impl uses
/// <c>Google.Apis.Drive.v3</c>). Every call takes the resolved
/// <see cref="DriveCredentials"/> (client id/secret + refresh token) — the SDK
/// mints access tokens from the refresh token per call. Scope is
/// <c>drive.file</c>, so Coffer only ever sees files it created.
/// </summary>
public interface IDriveClient
{
    /// <summary>The connected Google account's email (display only).</summary>
    Task<string?> GetAccountEmailAsync(DriveCredentials credentials, CancellationToken cancellationToken);

    /// <summary>Find-or-create the Coffer-owned backup folder (folder isolation);
    /// returns its id + name.</summary>
    Task<DriveFolder> EnsureBackupFolderAsync(
        DriveCredentials credentials, string folderName, CancellationToken cancellationToken);

    /// <summary>Resumable-upload a (already-encrypted) artifact into the folder;
    /// returns the new Drive file id.</summary>
    Task<string> UploadAsync(
        DriveCredentials credentials,
        string folderId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>List the artifacts Coffer has in its folder (newest first).
    /// Used by retention reconcile (④b+c).</summary>
    Task<IReadOnlyList<DriveArtifact>> ListAsync(
        DriveCredentials credentials, string folderId, CancellationToken cancellationToken);

    /// <summary>Delete one Drive file by id (retention reconcile, ④b+c).</summary>
    Task DeleteAsync(DriveCredentials credentials, string fileId, CancellationToken cancellationToken);
}

/// <summary>The OAuth material needed to mint Drive access tokens.</summary>
public sealed record DriveCredentials(string ClientId, string ClientSecret, string RefreshToken);

public sealed record DriveFolder(string Id, string Name);

public sealed record DriveArtifact(string FileId, string Name, DateTime CreatedAtUtc);
