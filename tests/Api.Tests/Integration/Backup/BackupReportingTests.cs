using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Backup;
using Coffer.Api.Backup.Drive;
using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// What a backup says about itself, and what it must not say (FU-063, FU-065, FU-067):
/// a failed Drive push is announced, the scheduled job reports what actually happened to the
/// backup, and a Drive that has stopped taking copies is noticed. Over the real notification
/// publisher and the real <c>system_events</c> table: an event that is not read back from
/// where it lands has not been shown to exist.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BackupReportingTests : IDisposable
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly PostgresFixture _fixture;
    private readonly string _dir;
    private readonly LedgerKeyService _keys = new(new MasterKey(new byte[32], "v1"));

    public BackupReportingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "coffer-report-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // --- plumbing -------------------------------------------------------------

    private NotificationPublisher Publisher() => new(
        _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService(), [],
        NullLogger<NotificationPublisher>.Instance, TimeProvider.System);

    /// <summary>A publisher whose database is unreachable: every write throws.</summary>
    private NotificationPublisher BrokenPublisher() => new(
        PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1;Command Timeout=1",
            ServiceConnectionString = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1;Command Timeout=1",
        })),
        _fixture.NewLedgerKeyService(), [],
        NullLogger<NotificationPublisher>.Instance, TimeProvider.System);

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewServiceDbContext();
        await db.SystemEvents.ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM drive_sync; DELETE FROM drive_mirrored_backups;");
    }

    private async Task<List<(string Key, string Severity)>> EventsAsync()
    {
        await using var db = _fixture.NewServiceDbContext();
        return (await db.SystemEvents.AsNoTracking().OrderBy(e => e.OccurredAt)
                .Select(e => new { e.EventKey, e.Severity }).ToListAsync())
            .Select(e => (e.EventKey, e.Severity)).ToList();
    }

    private static readonly NotificationEvent AnyEvent = new(
        NotificationSeverity.Info, NotificationTopics.Backup, "test.event", "x");

    private sealed class FakeRunner(Func<Task<BackupFileInfo>> create) : IBackupRunner
    {
        public Task<BackupFileInfo> CreateBackupAsync(CancellationToken ct = default) => create();
    }

    private static BackupFileInfo Info() => new("coffer-20261015T120000000Z-abcd1234", 4321, DateTime.UtcNow);

    private sealed class FakeDestination(Func<Task> push, bool enabled = true) : IBackupDestination
    {
        public string Name => "google-drive";
        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(enabled);
        public Task PushLatestAsync(IReadOnlySet<string> pinned, DateTime now, CancellationToken ct = default) => push();
        public Task<int> UploadMissingAsync(IReadOnlySet<string> pinned, DateTime now, CancellationToken ct = default) => Task.FromResult(0);
    }

    // --- FU-063: a failed push is announced ----------------------------------

    private BackupDestinationPusher Pusher(IBackupDestination dest, NotificationPublisher? publisher = null) => new(
        [dest], publisher ?? Publisher(), TimeProvider.System, NullLogger<BackupDestinationPusher>.Instance);

    [Fact]
    public async Task A_failing_push_raises_drive_push_failed_and_does_not_throw()
    {
        await ResetAsync();
        var dest = new FakeDestination(() => throw new DriveOAuthException("invalid_grant at accounts.google.com"));

        await Pusher(dest).PushAsync(new HashSet<string>(), default);   // must not throw: the backup is safe

        var events = await EventsAsync();
        var raised = Assert.Single(events);
        Assert.Equal("drive.push-failed", raised.Key);
        Assert.Equal(NotificationSeverity.Warning, raised.Severity);
    }

    [Fact]
    public async Task The_push_failure_event_does_not_carry_the_exception_text()
    {
        await ResetAsync();
        var dest = new FakeDestination(() => throw new DriveOAuthException("token refused by accounts.google.com for key-id-9876"));

        await Pusher(dest).PushAsync(new HashSet<string>(), default);

        await using var db = _fixture.NewServiceDbContext();
        var e = await db.SystemEvents.AsNoTracking().SingleAsync(x => x.EventKey == "drive.push-failed");
        // This goes to whatever webhook an operator configured; a Drive failure can name hosts.
        Assert.DoesNotContain("accounts.google.com", e.Summary);
        Assert.DoesNotContain("key-id-9876", e.Summary);
    }

    [Fact]
    public async Task A_push_that_works_raises_nothing()
    {
        await ResetAsync();

        await Pusher(new FakeDestination(() => Task.CompletedTask)).PushAsync(new HashSet<string>(), default);

        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_disabled_destination_is_not_pushed_to_and_raises_nothing()
    {
        await ResetAsync();
        var pushed = false;

        await Pusher(new FakeDestination(() => { pushed = true; return Task.CompletedTask; }, enabled: false))
            .PushAsync(new HashSet<string>(), default);

        Assert.False(pushed);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_push_failure_that_cannot_be_announced_still_does_not_fail_the_backup()
    {
        // Precondition, so this cannot pass vacuously: the broken publisher really does throw.
        await Assert.ThrowsAnyAsync<Exception>(() => BrokenPublisher().PublishAsync(AnyEvent, default));

        var dest = new FakeDestination(() => throw new DriveOAuthException("boom"));

        await Pusher(dest, BrokenPublisher()).PushAsync(new HashSet<string>(), default);
    }

    // --- FU-067: the scheduled job reports what happened to the backup --------

    private DailyBackupJobHandler Handler(IBackupRunner runner, NotificationPublisher publisher) =>
        new(runner, publisher, NullLogger<DailyBackupJobHandler>.Instance, TimeProvider.System);

    [Fact]
    public async Task A_written_backup_publishes_the_success_heartbeat()
    {
        await ResetAsync();

        await Handler(new FakeRunner(() => Task.FromResult(Info())), Publisher()).RunAsync(null!, default);

        Assert.Equal(["backup.succeeded"], (await EventsAsync()).Select(e => e.Key));
    }

    [Fact]
    public async Task A_success_event_that_cannot_be_written_does_not_turn_a_good_backup_into_a_failure()
    {
        // FU-067. The publish sat inside the try that announces and rethrows a failed backup,
        // so a failed event write announced a backup that was written, verified and listed as
        // FAILED, and counted toward the scheduler's five-failure auto-disable.
        await Assert.ThrowsAnyAsync<Exception>(() => BrokenPublisher().PublishAsync(AnyEvent, default));   // precondition

        await Handler(new FakeRunner(() => Task.FromResult(Info())), BrokenPublisher()).RunAsync(null!, default);

        // No exception reached the scheduler, so it records a success.
    }

    [Fact]
    public async Task A_failed_backup_publishes_backup_failed_and_rethrows_for_the_scheduler()
    {
        await ResetAsync();
        var handler = Handler(
            new FakeRunner(() => throw new BackupException("pg_dump failed against db.internal")), Publisher());

        await Assert.ThrowsAsync<BackupException>(() => handler.RunAsync(null!, default));

        await using var db = _fixture.NewServiceDbContext();
        var e = await db.SystemEvents.AsNoTracking().SingleAsync();
        Assert.Equal("backup.failed", e.EventKey);
        Assert.Equal(NotificationSeverity.Critical, e.Severity);
        Assert.DoesNotContain("db.internal", e.Summary);
    }

    [Fact]
    public async Task A_failure_that_cannot_be_announced_still_reaches_the_scheduler_as_the_original_failure()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => BrokenPublisher().PublishAsync(AnyEvent, default));   // precondition
        var handler = Handler(new FakeRunner(() => throw new BackupException("disk full")), BrokenPublisher());

        // The announce must not replace the exception the scheduler counts.
        await Assert.ThrowsAsync<BackupException>(() => handler.RunAsync(null!, default));
    }

    // --- FU-065: a backup cut short by a stop leaves a trace -----------------

    [Fact]
    public async Task A_backup_interrupted_by_a_stop_publishes_backup_failed_and_rethrows()
    {
        await ResetAsync();
        using var stopping = new CancellationTokenSource();
        var handler = Handler(new FakeRunner(() =>
        {
            stopping.Cancel();
            return Task.FromCanceled<BackupFileInfo>(stopping.Token);
        }), Publisher());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.RunAsync(null!, stopping.Token));

        // Published on a token of its own: the one that fired is why this is running at all.
        var e = Assert.Single(await EventsAsync());
        Assert.Equal("backup.failed", e.Key);
        await using var db = _fixture.NewServiceDbContext();
        Assert.Contains("interrupted", (await db.SystemEvents.AsNoTracking().SingleAsync()).Summary);
    }

    // --- FU-063: a Drive that has stopped taking copies is noticed -------------

    private DriveSyncMonitor Monitor(NotificationPublisher? publisher = null) => new(
        new BackupStore(_dir, NullLogger<BackupStore>.Instance, 1024 * 1024, TimeProvider.System),
        new DriveSyncRepository(_fixture.NewServiceFactory()),
        publisher ?? Publisher(), TimeProvider.System);

    private void PlaceBackup(double ageHours, string suffix = "abcd1234")
    {
        var stamp = DateTime.UtcNow.AddHours(-ageHours).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, $"coffer-{stamp}-{suffix}.cofferbak"), "not a real archive");
    }

    private async Task ConnectDriveAsync(bool enabled = true)
    {
        var actor = (await SyntheticLedger.CreateAsync(_fixture)).UserId;
        var sealed_ = _keys.SealWithMasterKey(
            DriveCredentialCodec.Serialize(new DriveCredentials("cid", "secret", "refresh")));
        var repo = new DriveSyncRepository(_fixture.NewServiceFactory());
        await repo.ConnectAsync(sealed_, "folderX", "Coffer Backups [t]", "e@x", actor, DateTime.UtcNow);
        if (!enabled) await repo.SetEnabledAsync(false, actor, DateTime.UtcNow);
    }

    private async Task SetMirroredHoursAgoAsync(string id, double hours)
    {
        await new DriveSyncRepository(_fixture.NewServiceFactory()).RecordMirroredAsync([id]);
        await using var db = _fixture.NewServiceDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE drive_mirrored_backups SET mirrored_at = now() - make_interval(secs => {0}) WHERE backup_id = {1}",
            hours * 3600, id);
    }

    [Fact]
    public async Task Nothing_is_said_about_a_Drive_that_is_not_enabled()
    {
        await ResetAsync();
        await ConnectDriveAsync(enabled: false);
        PlaceBackup(ageHours: 200);   // very stale, but nothing was promised

        Assert.Null(await Monitor().CheckAsync());
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Nothing_is_said_when_there_is_no_backup_to_copy()
    {
        await ResetAsync();
        await ConnectDriveAsync();

        Assert.Null(await Monitor().CheckAsync());   // BackupAgeMonitor says that one
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_recent_backup_not_yet_on_Drive_raises_nothing()
    {
        await ResetAsync();
        await ConnectDriveAsync();
        PlaceBackup(ageHours: DriveSyncMonitor.CriticalAfterHours - 1);

        var age = await Monitor().CheckAsync();

        Assert.NotNull(age);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Backups_that_never_reached_Drive_raise_drive_stale_once_one_is_old_enough_to_expect_it()
    {
        await ResetAsync();
        await ConnectDriveAsync();
        PlaceBackup(ageHours: DriveSyncMonitor.CriticalAfterHours + 1);

        await Monitor().CheckAsync();
        await Monitor().CheckAsync();   // standing condition: announced once per window, not per tick

        var e = Assert.Single(await EventsAsync());
        Assert.Equal("drive.stale", e.Key);
        Assert.Equal(NotificationSeverity.Critical, e.Severity);
    }

    [Fact]
    public async Task A_Drive_that_took_a_copy_recently_is_fine_whatever_its_last_attempt_said()
    {
        // last_sync_at moves on EVERY attempt, failures included, so a Drive that has failed
        // all week looks freshly synced by it. What counts is when a backup last landed.
        await ResetAsync();
        await ConnectDriveAsync();
        PlaceBackup(ageHours: 100);
        await SetMirroredHoursAgoAsync("coffer-20261015T120000000Z-abcd1234", hours: 5);

        Assert.NotNull(await Monitor().CheckAsync());
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_Drive_that_stopped_taking_copies_raises_drive_stale_even_though_attempts_continue()
    {
        await ResetAsync();
        await ConnectDriveAsync();
        PlaceBackup(ageHours: 1);   // backups keep arriving locally...
        await SetMirroredHoursAgoAsync("coffer-20261012T120000000Z-abcd1234", hours: 100);   // ...Drive stopped
        // ...and every attempt since has failed, which stamps last_sync_at fresh:
        await new DriveSyncRepository(_fixture.NewServiceFactory())
            .RecordSyncOutcomeAsync("error", "token revoked", DateTime.UtcNow);

        var age = await Monitor().CheckAsync();

        Assert.True(age > TimeSpan.FromHours(DriveSyncMonitor.CriticalAfterHours));
        Assert.Equal("drive.stale", Assert.Single(await EventsAsync()).Key);
    }

    [Fact]
    public async Task The_stale_event_does_not_repeat_the_last_push_error()
    {
        await ResetAsync();
        await ConnectDriveAsync();
        PlaceBackup(ageHours: DriveSyncMonitor.CriticalAfterHours + 5);
        await new DriveSyncRepository(_fixture.NewServiceFactory())
            .RecordSyncOutcomeAsync("error", "quota exceeded for project 424242 at oauth2.googleapis.com", DateTime.UtcNow);

        await Monitor().CheckAsync();

        await using var db = _fixture.NewServiceDbContext();
        var e = await db.SystemEvents.AsNoTracking().SingleAsync(x => x.EventKey == "drive.stale");
        Assert.DoesNotContain("424242", e.Summary + e.DetailJson);
        Assert.DoesNotContain("googleapis", e.Summary + e.DetailJson);
    }
}
