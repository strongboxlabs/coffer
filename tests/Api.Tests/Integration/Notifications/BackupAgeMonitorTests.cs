using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Backup;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The backup dead-man's switch: its threshold, its "no backup at all" case, and its
/// repeat suppression.
/// </summary>
/// <remarks>
/// <para>
/// This monitor had NO tests, which is how three separate delivery blockers reached
/// 0.65.0. Untested time arithmetic in an alerting path is the specific way you end up
/// with an alert that never fires: nothing about a threshold comparison is
/// self-evidently right, and the failure mode is silence — indistinguishable from
/// "nothing was wrong" until the day something is.
/// </para>
/// <para>
/// Ages come from the backup FILENAME, which is where BackupStore reads them
/// (<c>coffer-{yyyyMMddTHHmmssfffZ}-{rand}</c>, parsed by Describe with the file's
/// mtime only as a fallback). So a test can place a backup at an exact age without
/// touching the clock, and the boundary can be probed from both sides.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BackupAgeMonitorTests : IDisposable
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly PostgresFixture _fixture;
    private readonly string _dir;

    public BackupAgeMonitorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "coffer-agemon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Place a backup artifact whose encoded timestamp is <paramref name="ageHours"/> old.</summary>
    private void PlaceBackup(double ageHours)
    {
        var stamp = DateTime.UtcNow.AddHours(-ageHours).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, $"coffer-{stamp}-abcd1234.cofferbak"), "not a real archive");
    }

    private BackupAgeMonitor Monitor() => new(
        new BackupStore(_dir, NullLogger<BackupStore>.Instance),
        new NotificationPublisher(
            _fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            [],
            NullLogger<NotificationPublisher>.Instance));

    private async Task ResetEventsAsync()
    {
        await using var db = _fixture.NewServiceDbContext();
        await db.SystemEvents.ExecuteDeleteAsync();
    }

    private async Task<List<string>> EventKeysAsync()
    {
        await using var db = _fixture.NewServiceDbContext();
        return await db.SystemEvents.AsNoTracking()
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.EventKey)
            .ToListAsync();
    }

    [Fact]
    public async Task A_backup_just_inside_the_threshold_raises_nothing()
    {
        // One hour inside 48. The boundary is probed from both sides because an
        // off-by-one in the comparison is invisible from either side alone.
        await ResetEventsAsync();
        PlaceBackup(BackupAgeMonitor.CriticalAfterHours - 1);

        var age = await Monitor().CheckAsync();

        Assert.NotNull(age);
        Assert.Empty(await EventKeysAsync());
    }

    [Fact]
    public async Task A_backup_past_the_threshold_raises_backup_stale()
    {
        await ResetEventsAsync();
        PlaceBackup(BackupAgeMonitor.CriticalAfterHours + 1);

        var age = await Monitor().CheckAsync();

        Assert.NotNull(age);
        Assert.Equal(["backup.stale"], await EventKeysAsync());
    }

    [Fact]
    public async Task No_backup_at_all_raises_backup_none_not_stale()
    {
        // A distinct key, because they are distinct situations: "the schedule stopped
        // running" and "backups were never configured" need different responses, and a
        // single key would make them indistinguishable in the event log.
        await ResetEventsAsync();

        var age = await Monitor().CheckAsync();

        Assert.Null(age);
        Assert.Equal(["backup.none"], await EventKeysAsync());
    }

    [Fact]
    public async Task A_standing_problem_is_announced_once_per_repeat_window()
    {
        // Drift and staleness are STANDING conditions: still true on the next tick, and
        // every tick after, until someone acts. Announcing each time would mute the
        // channel within a day — the cry-wolf failure ADR-0096 D4 warns about, arriving
        // through repetition rather than severity. The scheduler ticks far more often
        // than RepeatAfter, so this is the common path, not an edge case.
        await ResetEventsAsync();
        PlaceBackup(BackupAgeMonitor.CriticalAfterHours + 1);

        var monitor = Monitor();
        await monitor.CheckAsync();
        await monitor.CheckAsync();
        await monitor.CheckAsync();

        Assert.Equal(["backup.stale"], await EventKeysAsync());
    }

    [Fact]
    public async Task The_window_is_measured_from_the_last_announcement()
    {
        // Suppression must expire, or a problem announced once at hour zero is never
        // mentioned again however long it persists. Simulated by ageing the recorded
        // event past RepeatAfter rather than by waiting, which is the only way to test
        // a 12-hour window in a test suite.
        await ResetEventsAsync();
        PlaceBackup(BackupAgeMonitor.CriticalAfterHours + 1);

        var monitor = Monitor();
        await monitor.CheckAsync();
        Assert.Single(await EventKeysAsync());

        await using (var db = _fixture.NewServiceDbContext())
        {
            await db.SystemEvents
                .Where(e => e.EventKey == "backup.stale")
                .ExecuteUpdateAsync(s => s.SetProperty(
                    e => e.OccurredAt,
                    DateTime.UtcNow - BackupAgeMonitor.RepeatAfter - TimeSpan.FromMinutes(1)));
        }

        await monitor.CheckAsync();
        Assert.Equal(["backup.stale", "backup.stale"], await EventKeysAsync());
    }
}
