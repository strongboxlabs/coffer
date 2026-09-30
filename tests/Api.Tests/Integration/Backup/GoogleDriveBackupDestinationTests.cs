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

    /// <summary>
    /// 1 MB parts, so a test artifact of a few MB genuinely spans several
    /// (ADR-0101) — the split path cannot be exercised at the 49 MB default
    /// without writing 49 MB of fixture.
    /// </summary>
    private const long TestPartSize = 1024 * 1024;

    private BackupStore NewStore() =>
        new(_dir, NullLogger<BackupStore>.Instance, TestPartSize);

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

    /// <summary>
    /// A local artifact of a given size, with deterministic non-repeating
    /// content so a rejoin assertion fails on a wrong ORDER as well as on
    /// wrong bytes — filler of all zeroes would reassemble "correctly" however
    /// the parts were shuffled.
    /// </summary>
    private (string Id, byte[] Bytes) WriteLocalSized(DateTime ts, int size)
    {
        var id = ArtifactId(ts);
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        File.WriteAllBytes(Path.Combine(_dir, id + ".cofferbak"), bytes);
        return (id, bytes);
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

    // --- a backup too big to go up as one file (ADR-0101) -------------------
    //
    // Everything above uses sub-megabyte artifacts, so before these the
    // splitting code had never once executed: the suite was green over a
    // feature that did not run. TestPartSize is 1 MB precisely so a few
    // megabytes of fixture crosses the threshold.

    [Fact]
    public async Task A_backup_over_the_part_size_goes_up_as_named_parts_that_rejoin()
    {
        await ConnectAsync();
        // 2.5 parts, so the last one is SHORT — the case where an off-by-one in
        // the final slice would otherwise go unnoticed.
        var (id, bytes) = WriteLocalSized(DateTime.UtcNow, (int)(TestPartSize * 2.5));
        var drive = new RecordingDriveClient();

        await NewDestination(drive).PushLatestAsync(EmptySet, DateTime.UtcNow);

        Assert.Equal(
            new[]
            {
                $"{id}.cofferbak.001-of-003",
                $"{id}.cofferbak.002-of-003",
                $"{id}.cofferbak.003-of-003",
            },
            drive.Uploads);

        // The count is IN the name because nothing else records how the set was
        // cut; the part size is configurable, so a set written under one value
        // must still read as complete under another.
        Assert.Equal(TestPartSize, drive.Content[$"{id}.cofferbak.001-of-003"].Length);
        Assert.Equal(TestPartSize, drive.Content[$"{id}.cofferbak.002-of-003"].Length);
        Assert.Equal(TestPartSize / 2, drive.Content[$"{id}.cofferbak.003-of-003"].Length);

        // And the bytes are the artifact, in order. Names alone would pass for a
        // split that shipped the wrong content — which shows up much later, as a
        // backup that will not decrypt.
        Assert.Equal(bytes, drive.Uploads.SelectMany(n => drive.Content[n]).ToArray());
    }

    [Fact]
    public async Task A_backup_within_the_part_size_stays_one_file()
    {
        await ConnectAsync();
        var (id, bytes) = WriteLocalSized(DateTime.UtcNow, (int)TestPartSize);
        var drive = new RecordingDriveClient();

        await NewDestination(drive).PushLatestAsync(EmptySet, DateTime.UtcNow);

        // Exactly at the boundary is still ONE file: nothing is gained by
        // splitting something that already fits, and the plain name is what
        // every existing backup in every existing Drive folder carries.
        Assert.Equal(new[] { id + ".cofferbak" }, drive.Uploads);
        Assert.Equal(bytes, drive.Content[id + ".cofferbak"]);
    }

    [Fact]
    public async Task An_interrupted_part_upload_resumes_instead_of_restarting()
    {
        await ConnectAsync();
        var (id, _) = WriteLocalSized(DateTime.UtcNow, (int)(TestPartSize * 2.5));
        var drive = new RecordingDriveClient
        {
            // The push died after two of three parts landed.
            Remote =
            [
                new DriveArtifact("f1", $"{id}.cofferbak.001-of-003", DateTime.UtcNow),
                new DriveArtifact("f2", $"{id}.cofferbak.002-of-003", DateTime.UtcNow),
            ],
        };

        var count = await NewDestination(drive).UploadMissingAsync(EmptySet, DateTime.UtcNow);

        // Only the missing part is sent. Restarting from part 1 is what makes a
        // flaky connection unable to ever finish a large backup.
        Assert.Equal(new[] { $"{id}.cofferbak.003-of-003" }, drive.Uploads);
        Assert.Equal(1, count);   // one ARTIFACT completed, not one part
        Assert.Empty(drive.Deletes);
    }

    [Fact]
    public async Task An_artifact_already_on_drive_whole_is_not_re_cut_into_parts()
    {
        await ConnectAsync();
        var (id, _) = WriteLocalSized(DateTime.UtcNow, (int)(TestPartSize * 2.5));
        var drive = new RecordingDriveClient
        {
            // Uploaded before parts existed, or under a larger part size.
            Remote = [new DriveArtifact("f1", id + ".cofferbak", DateTime.UtcNow)],
        };

        var count = await NewDestination(drive).UploadMissingAsync(EmptySet, DateTime.UtcNow);

        // It is present. Re-cutting would spend the artifact's whole size in
        // bandwidth to arrive at the same bytes in a different shape — and the
        // delete pass must not sweep it either.
        Assert.Empty(drive.Uploads);
        Assert.Equal(0, count);
        Assert.Empty(drive.Deletes);
    }

    private sealed class RecordingDriveClient : IDriveClient
    {
        public List<string> Uploads { get; } = [];
        public List<string> Deletes { get; } = [];
        public IReadOnlyList<DriveArtifact> Remote { get; init; } = [];

        /// <summary>
        /// What was actually sent, per file name. The fake used to drop the
        /// stream on the floor, which meant a test could only ever check that
        /// the NAMES looked right — and a split that named three files
        /// correctly while sending the wrong bytes in them is exactly the
        /// failure that surfaces later as a backup which will not decrypt.
        /// </summary>
        public Dictionary<string, byte[]> Content { get; } = [];

        public Task<string?> GetAccountEmailAsync(DriveCredentials c, CancellationToken ct) =>
            Task.FromResult<string?>("e@x");

        public Task<DriveFolder> EnsureBackupFolderAsync(DriveCredentials c, string name, CancellationToken ct) =>
            Task.FromResult(new DriveFolder("folderX", name));

        public async Task<string> UploadAsync(
            DriveCredentials c, string folderId, string fileName, Stream content, CancellationToken ct)
        {
            Uploads.Add(fileName);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
            Content[fileName] = buffer.ToArray();
            return "file-" + fileName;
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
