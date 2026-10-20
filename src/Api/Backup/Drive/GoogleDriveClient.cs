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
        DriveContentCheck expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var drive = Build(credentials);
        var metadata = new DriveFile { Name = fileName, Parents = new[] { folderId } };
        var upload = drive.Files.Create(metadata, content, "application/octet-stream");
        // size + md5Checksum come back with the file, so the check costs no extra call.
        upload.Fields = "id,size,md5Checksum";
        var progress = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw new DriveOAuthException(
                $"Drive upload failed: {progress.Exception?.Message ?? progress.Status.ToString()}");

        var created = upload.ResponseBody;
        var mismatch = DriveUploadVerification.Mismatch(created.Size, created.Md5Checksum, expected);
        if (mismatch is not null)
        {
            // A file that arrived wrong must not stay in the folder looking like a backup.
            try { await DeleteAsync(credentials, created.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* reported below */ }
            throw new DriveOAuthException($"Drive upload of {fileName} was rejected: {mismatch}");
        }
        return created.Id;
    }

    public async Task<IReadOnlyList<DriveArtifact>> ListAsync(
        DriveCredentials credentials, string folderId, CancellationToken cancellationToken)
    {
        using var drive = Build(credentials);
        return await DrivePaging.ReadAllAsync<DriveArtifact>(async (pageToken, ct) =>
        {
            var list = drive.Files.List();
            list.Q = $"'{folderId}' in parents and trashed = false";
            list.Fields = "nextPageToken,files(id,name,createdTime,size,md5Checksum)";
            list.OrderBy = "createdTime desc";
            list.PageSize = 1000;
            list.PageToken = pageToken;
            var page = await list.ExecuteAsync(ct).ConfigureAwait(false);
            var items = (page.Files ?? new List<DriveFile>())
                .Select(f => new DriveArtifact(
                    f.Id, f.Name, f.CreatedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UnixEpoch,
                    f.Size, f.Md5Checksum?.ToLowerInvariant()))
                .ToList();
            return (items, page.NextPageToken);
        }, cancellationToken).ConfigureAwait(false);
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

/// <summary>Pages of a Drive listing, read to the end.</summary>
internal static class DrivePaging
{
    /// <summary>A listing that repeats a page token would loop forever; no real folder has
    /// anywhere near this many pages of 1000 backups.</summary>
    private const int MaxPages = 1000;

    public static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        Func<string?, CancellationToken, Task<(IReadOnlyList<T> Items, string? NextPageToken)>> fetch,
        CancellationToken cancellationToken)
    {
        var all = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (items, next) = await fetch(token, cancellationToken).ConfigureAwait(false);
            all.AddRange(items);
            if (string.IsNullOrEmpty(next)) return all;
            if (!seen.Add(next))
                throw new DriveOAuthException("Drive returned the same page token twice; the listing is not trustworthy.");
            token = next;
        }
        throw new DriveOAuthException($"Drive listing did not end after {MaxPages} pages.");
    }
}

/// <summary>What an upload must have become on Drive's side.</summary>
internal static class DriveUploadVerification
{
    /// <summary>Why Drive's account of an uploaded file differs from what was sent, or
    /// null when it matches. A missing size or checksum counts as a mismatch: an upload
    /// that cannot be checked has not been verified.</summary>
    public static string? Mismatch(long? gotSize, string? gotMd5, DriveContentCheck expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (gotSize is null || string.IsNullOrEmpty(gotMd5))
            return "Drive did not report the stored size and checksum, so the upload cannot be verified.";
        if (gotSize != expected.SizeBytes)
            return $"Drive holds {gotSize} bytes, {expected.SizeBytes} were sent.";
        if (!string.Equals(gotMd5, expected.Md5Hex, StringComparison.OrdinalIgnoreCase))
            return "Drive's checksum of the stored file differs from the one sent.";
        return null;
    }
}
