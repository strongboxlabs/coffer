using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Coffer.Api.Backup.Drive;

/// <summary>
/// Real <see cref="IDriveClient"/> over <c>Google.Apis.Drive.v3</c> (ADR-0062
/// D6). Builds a <see cref="DriveService"/> per call from the stored refresh
/// token — the SDK mints + refreshes access tokens itself and does resumable
/// upload (a <c>.cofferbak</c> can exceed 100 MB). Scope <c>drive.file</c>, so
/// every list/get only ever sees files Coffer created (folder isolation comes
/// free).
/// </summary>
public sealed class GoogleDriveClient : IDriveClient
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    public async Task<string?> GetAccountEmailAsync(
        DriveCredentials credentials, CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);
        var request = drive.About.Get();
        request.Fields = "user(emailAddress,displayName)";
        var about = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return about.User?.EmailAddress;
    }

    public async Task<DriveFolder> EnsureBackupFolderAsync(
        DriveCredentials credentials, string folderName, CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);

        // drive.file only lists files this app created, so a name match here is
        // unambiguous — it's our folder or nothing.
        var list = drive.Files.List();
        list.Q = $"mimeType = '{FolderMimeType}' and name = '{folderName.Replace("'", "\\'")}' and trashed = false";
        list.Fields = "files(id,name)";
        list.PageSize = 1;
        var existing = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Files is { Count: > 0 })
        {
            var f = existing.Files[0];
            return new DriveFolder(f.Id, f.Name);
        }

        var create = drive.Files.Create(new DriveFile { Name = folderName, MimeType = FolderMimeType });
        create.Fields = "id,name";
        var created = await create.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new DriveFolder(created.Id, created.Name);
    }

    public async Task<string> UploadAsync(
        DriveCredentials credentials,
        string folderId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);
        var metadata = new DriveFile { Name = fileName, Parents = new[] { folderId } };
        var upload = drive.Files.Create(metadata, content, "application/octet-stream");
        upload.Fields = "id";
        var progress = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw new DriveOAuthException(
                $"Drive upload failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        return upload.ResponseBody.Id;
    }

    public async Task<IReadOnlyList<DriveArtifact>> ListAsync(
        DriveCredentials credentials, string folderId, CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);
        var list = drive.Files.List();
        list.Q = $"'{folderId}' in parents and trashed = false";
        list.Fields = "files(id,name,createdTime)";
        list.OrderBy = "createdTime desc";
        list.PageSize = 1000;
        var files = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return (files.Files ?? new List<DriveFile>())
            .Select(f => new DriveArtifact(
                f.Id, f.Name, f.CreatedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UnixEpoch))
            .ToList();
    }

    public async Task DeleteAsync(
        DriveCredentials credentials, string fileId, CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);
        await drive.Files.Delete(fileId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DriveService Build(DriveCredentials credentials)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = credentials.ClientId,
                ClientSecret = credentials.ClientSecret,
            },
            Scopes = new[] { DriveService.ScopeConstants.DriveFile },
        });
        // The SDK refreshes access tokens from this refresh token on demand.
        var token = new TokenResponse { RefreshToken = credentials.RefreshToken };
        var credential = new UserCredential(flow, "coffer", token);
        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Coffer",
        });
    }
}
