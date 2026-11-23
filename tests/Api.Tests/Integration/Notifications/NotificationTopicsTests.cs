using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The topic list, and what migration 272 did with it: <c>upgrade</c> is a deployment-scope topic
/// that a destination receives only by naming it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationTopicsTests : DatabaseTest
{
    public NotificationTopicsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public void Opt_in_topics_are_part_of_the_full_list_and_upgrade_is_the_only_one()
    {
        Assert.All(NotificationTopics.OptIn, t => Assert.Contains(t, NotificationTopics.All));
        Assert.Equal(new[] { NotificationTopics.Upgrade }, NotificationTopics.OptIn);
    }

    [Fact]
    public async Task Every_topic_the_code_names_is_accepted_at_system_scope()
    {
        // A topic added to NotificationTopics.All without widening ck_system_events_topic is an event
        // that cannot be written: a write error at the one moment something needed saying.
        await using var db = Db.NewServiceDbContext();
        foreach (var topic in NotificationTopics.All)
        {
            db.SystemEvents.Add(new SystemEventRow
            {
                Severity = NotificationSeverity.Info, Topic = topic, EventKey = $"{topic}.probe",
                Summary = topic, DetailJson = "{}",
            });
        }

        await db.SaveChangesAsync();
        Assert.Equal(NotificationTopics.All.Count,
            await db.SystemEvents.CountAsync(e => e.EventKey.EndsWith(".probe")));
    }

    [Fact]
    public async Task A_ledger_event_still_refuses_the_upgrade_topic()
    {
        // Only system_events was widened: an upgrade is a fact about the installation, never a ledger.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewServiceDbContext();
        db.LedgerEvents.Add(new LedgerEventRow
        {
            LedgerId = ledger.LedgerId, Severity = NotificationSeverity.Warning,
            Topic = NotificationTopics.Upgrade, EventKey = "upgrade.available",
            Summary = "not a ledger fact", DetailJson = "{}",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
