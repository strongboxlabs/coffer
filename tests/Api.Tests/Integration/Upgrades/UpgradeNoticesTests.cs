using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Api.Upgrades;

namespace Coffer.Api.Tests.Integration.Upgrades;

/// <summary>
/// An available release as an ordinary deployment-scope notification (ADR-0110): announced once per
/// version, cleared when the install catches up, and never by a check that merely failed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UpgradeNoticesTests : DatabaseTest
{
    private const string Installed = "0.130.2";

    private readonly TestClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
    private readonly ReleaseCheckState _state = new();

    public UpgradeNoticesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private UpgradeNotices Notices() => new(
        new NotificationPublisher(
            Db.NewServiceFactory(), Db.NewLedgerKeyService(), [],
            NullLogger<NotificationPublisher>.Instance, _clock),
        new SystemSettingsRepository(Db.NewServiceFactory()),
        _state, _clock, NullLogger<UpgradeNotices>.Instance);

    private Task AnnounceAsync(UpgradeNotices notices, string? latest, string installed = Installed) =>
        notices.ReconcileLockedAsync(latest, installed, CancellationToken.None);

    private async Task<int> CountAsync(string eventKey)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.SystemEvents.AsNoTracking()
            .CountAsync(e => e.Topic == NotificationTopics.Upgrade && e.EventKey == eventKey);
    }

    [Fact]
    public async Task A_newer_release_is_announced_once_per_version()
    {
        var notices = Notices();

        await AnnounceAsync(notices, "0.131.0");
        await AnnounceAsync(notices, "0.131.0");     // another check, same release
        Assert.Equal(1, await CountAsync(UpgradeNotices.AvailableKey));
        Assert.Equal("0.131.0", await notices.AnnouncedAsync());

        await AnnounceAsync(notices, "0.132.0");     // a later release is news again
        Assert.Equal(2, await CountAsync(UpgradeNotices.AvailableKey));
        Assert.Equal("0.132.0", await notices.AnnouncedAsync());
    }

    [Fact]
    public async Task The_announcement_is_a_warning_on_the_upgrade_topic_naming_the_version()
    {
        await AnnounceAsync(Notices(), "0.131.0");

        await using var db = Db.NewServiceDbContext();
        var row = await db.SystemEvents.AsNoTracking()
            .SingleAsync(e => e.EventKey == UpgradeNotices.AvailableKey);
        // Warning, because the badge takes warning and above and a third state is ruled out
        // (ADR-0102 D5); the topic is the one a destination must name to hear it (ADR-0110).
        Assert.Equal(NotificationSeverity.Warning, row.Severity);
        Assert.Equal(NotificationTopics.Upgrade, row.Topic);
        Assert.Equal("Update available: 0.131.0", row.Summary);
        Assert.Contains("0.131.0", row.DetailJson);
    }

    [Fact]
    public async Task Nothing_is_announced_when_the_install_is_current()
    {
        var notices = Notices();

        await AnnounceAsync(notices, Installed);          // equal
        await AnnounceAsync(notices, "0.130.1");          // older

        Assert.Equal(0, await CountAsync(UpgradeNotices.AvailableKey));
        Assert.Null(await notices.AnnouncedAsync());
    }

    [Fact]
    public async Task The_all_clear_waits_for_the_install_to_reach_the_announced_release()
    {
        var notices = Notices();
        await AnnounceAsync(notices, "0.131.0");

        await notices.ClearIfInstalledAsync(Installed);          // still behind
        Assert.Equal(0, await CountAsync(UpgradeNotices.CurrentKey));
        Assert.Equal("0.131.0", await notices.AnnouncedAsync());

        await notices.ClearIfInstalledAsync("0.131.0");          // caught up: no check needed
        Assert.Equal(1, await CountAsync(UpgradeNotices.CurrentKey));
        Assert.Null(await notices.AnnouncedAsync());

        await notices.ClearIfInstalledAsync("0.131.0");          // and only once
        Assert.Equal(1, await CountAsync(UpgradeNotices.CurrentKey));
    }

    [Fact]
    public async Task A_failed_check_does_not_clear_a_standing_notice()
    {
        var notices = Notices();
        await AnnounceAsync(notices, "0.131.0");

        // GitHub unreachable says nothing about the release, so what was announced stands.
        await AnnounceAsync(notices, latest: null);

        Assert.Equal(0, await CountAsync(UpgradeNotices.CurrentKey));
        Assert.Equal("0.131.0", await notices.AnnouncedAsync());
    }

    [Fact]
    public async Task A_withdrawn_release_clears_the_notice()
    {
        var notices = Notices();
        await AnnounceAsync(notices, "0.131.0");

        // The newest release a check can now see is no longer ahead of the install: the announced
        // one is gone, and must not pin a dot for something that cannot be installed.
        await AnnounceAsync(notices, Installed);

        Assert.Equal(1, await CountAsync(UpgradeNotices.CurrentKey));
        Assert.Null(await notices.AnnouncedAsync());
    }

    [Fact]
    public async Task The_subject_is_outstanding_until_its_all_clear()
    {
        var notices = Notices();
        await AnnounceAsync(notices, "0.131.0");

        await using (var db = Db.NewServiceDbContext())
        {
            var standing = await SystemOutstandingSubjects.ListAsync(db, CancellationToken.None);
            var upgrade = Assert.Single(standing, s => s.Subject == "upgrade.");
            Assert.Equal(NotificationSeverity.Warning, upgrade.Severity);
        }

        await notices.ClearIfInstalledAsync("0.131.0");

        await using (var db = Db.NewServiceDbContext())
        {
            var standing = await SystemOutstandingSubjects.ListAsync(db, CancellationToken.None);
            Assert.DoesNotContain(standing, s => s.Subject == "upgrade.");
        }
    }

    [Fact]
    public async Task A_new_release_after_an_all_clear_is_outstanding_again()
    {
        var notices = Notices();
        await AnnounceAsync(notices, "0.131.0");
        await notices.ClearIfInstalledAsync("0.131.0");

        await AnnounceAsync(notices, "0.132.0", installed: "0.131.0");

        await using var db = Db.NewServiceDbContext();
        var standing = await SystemOutstandingSubjects.ListAsync(db, CancellationToken.None);
        Assert.Contains(standing, s => s.Subject == "upgrade.");
    }
}
