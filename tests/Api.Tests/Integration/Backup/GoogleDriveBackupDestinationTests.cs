using System.Globalization;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Backup;
using Coffer.Api.Backup.Drive;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// <see cref="GoogleDriveBackupDestination"/> (ADR-0062 ④b+c): artifact push,
/// upload-existing backfill, and mirror reconcile (Drive = the local set) over
/// a real <see cref="BackupStore"/> (temp dir) + real <see cref="DriveSyncRepository"/>
/// (service DB) + a recording fake <see cref="IDriveClient"/> — no network.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GoogleDriveBackupDestinationTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _dir;
    private readonly LedgerKeyService _keys = new(new MasterKey(new byte[32], "v1"));

    public GoogleDriveBackupDestinationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "coffer-dest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private BackupStore NewStore() =>
        new(_dir, NullLogger<BackupStore>.Instance);

    private GoogleDriveBackupDestination NewDestination(RecordingDriveClient drive) =>
        new(new DriveSyncRepository(_fixture.NewServiceFactory()), _keys, drive, NewStore(),
            NullLogger<GoogleDriveBackupDestination>.Instance);

    private async Task ConnectAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM drive_sync;");
        var actor = (await SyntheticLedger.CreateAsync(_fixture)).UserId;
        var sealed_ = _keys.SealWithMasterKey(
            DriveCredentialCodec.Serialize(new DriveCredentials("cid", "secret", "refresh")));
        await new DriveSyncRepository(_fixture.NewServiceFactory())
            .ConnectAsync(sealed_, "folderX", "Coffer Backups [t]", "e@x", actor, DateTime.UtcNow);
    }

    private string WriteLocal(DateTime ts)
    {
        var id = ArtifactId(ts);
        File.WriteAllText(Path.Combine(_dir, id + ".cofferbak"), "data");
        return id;
    }

    private static string ArtifactId(DateTime ts) =>
        $"coffer-{ts.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}-"
        + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    [Fact]
    public async Task PushLatest_mirrors_uploading_local_backups_missing_from_drive()
    {
        await ConnectAsync();
        var older = WriteLocal(DateTime.UtcNow.AddHours(-2));
        var newest = WriteLocal(DateTime.UtcNow);
        var drive = new RecordingDriveClient();   // empty folder

        await NewDestination(drive).PushLatestAsync(EmptySet, DateTime.UtcNow);

        // The mirror uploads BOTH local backups (both missing from Drive), each
        // with the .cofferbak extension on the Drive file name.
        Assert.Equal(
            new HashSet<string> { older + ".cofferbak", newest + ".cofferbak" },
            drive.Uploads.ToHashSet());
        var status = await new DriveSyncRepository(_fixture.NewServiceFactory()).GetStatusAsync();
        Assert.Equal("ok", status.LastSyncStatus);
    }

    [Fact]
    public async Task UploadMissing_pushes_only_artifacts_not_already_on_drive()
    {
        await ConnectAsync();
        var onDrive = WriteLocal(DateTime.UtcNow.AddHours(-2));
        var missing = WriteLocal(DateTime.UtcNow);
        var drive = new RecordingDriveClient
        {
            // Drive carries the .cofferbak extension; dedup strips it back to the id.
            Remote = [new DriveArtifact("file-1", onDrive + ".cofferbak", DateTime.UtcNow.AddHours(-2))],
        };

        var count = await NewDestination(drive).UploadMissingAsync(EmptySet, DateTime.UtcNow);

        Assert.Equal(1, count);
        Assert.Equal(new[] { missing + ".cofferbak" }, drive.Uploads);   // already-present one is skipped
    }

    [Fact]
    public async Task Mirror_deletes_remote_files_not_in_the_local_set()
    {
        await ConnectAsync();
        var kept = WriteLocal(DateTime.UtcNow);   // the only local backup

        var phantom = ArtifactId(DateTime.UtcNow.AddHours(-1));
        var drive = new RecordingDriveClient
        {
            Remote =
            [
                // Matches a local backup → kept.
                new DriveArtifact("id-kept", kept + ".cofferbak", DateTime.UtcNow),
                // No local counterpart → swept.
                new DriveArtifact("id-phantom", phantom + ".cofferbak", DateTime.UtcNow.AddHours(-1)),
                // Legacy pre-rename extension: StripExtension leaves it unmatched → swept.
                new DriveArtifact("id-legacy", "ledgr-old.ledgrbak", DateTime.UtcNow.AddYears(-1)),
            ],
        };

        await NewDestination(drive).PushLatestAsync(EmptySet, DateTime.UtcNow);

        Assert.Equal(
            new HashSet<string> { "id-phantom", "id-legacy" }, drive.Deletes.ToHashSet());
    }

    [Fact]
    public async Task Mirror_skips_deletes_when_there_are_no_local_backups()
    {
        await ConnectAsync();
        // No local backups written — the safety net must NOT empty the folder.
        var drive = new RecordingDriveClient
        {
            Remote = [new DriveArtifact("id-x", ArtifactId(DateTime.UtcNow) + ".cofferbak", DateTime.UtcNow)],
        };

        await NewDestination(drive).PushLatestAsync(EmptySet, DateTime.UtcNow);

        Assert.Empty(drive.Deletes);
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private sealed class RecordingDriveClient : IDriveClient
    {
        public List<string> Uploads { get; } = [];
        public List<string> Deletes { get; } = [];
        public IReadOnlyList<DriveArtifact> Remote { get; init; } = [];

        public Task<string?> GetAccountEmailAsync(DriveCredentials c, CancellationToken ct) =>
            Task.FromResult<string?>("e@x");

        public Task<DriveFolder> EnsureBackupFolderAsync(DriveCredentials c, string name, CancellationToken ct) =>
            Task.FromResult(new DriveFolder("folderX", name));

        public Task<string> UploadAsync(
            DriveCredentials c, string folderId, string fileName, Stream content, CancellationToken ct)
        {
            Uploads.Add(fileName);
            return Task.FromResult("file-" + fileName);
        }

        public Task<IReadOnlyList<DriveArtifact>> ListAsync(DriveCredentials c, string folderId, CancellationToken ct) =>
            Task.FromResult(Remote);

        public Task DeleteAsync(DriveCredentials c, string fileId, CancellationToken ct)
        {
            Deletes.Add(fileId);
            return Task.CompletedTask;
        }
    }
}
