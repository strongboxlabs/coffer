using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The consistency check remembers what it found, per ledger (FU-012, mig 242).
/// </summary>
/// <remarks>
/// Before this the check was computed and thrown away, so three situations looked
/// identical from outside: a ledger nobody had checked, one checked and healthy,
/// and one checked months ago. That is what made a report "expected to be
/// unhealthy on some unremembered subset" train people to ignore it.
/// <para>The same row also gates the cadence. The monitor is INVOKED every
/// scheduler tick because it must not be a switchable job, but it now WORKS twice
/// a day — and the gate is the recorded timestamp rather than an in-memory timer,
/// so a restarted process does not start over.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyStateTests
{
    private readonly PostgresFixture _fixture;

    public ConsistencyStateTests(PostgresFixture fixture) => _fixture = fixture;

    private ConsistencyMonitor NewMonitor() => new(
        _fixture.NewServiceFactory(),
        new NotificationPublisher(
            _fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            Array.Empty<INotificationSubscriber>(),
            NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
        NullLogger<ConsistencyMonitor>.Instance, TimeProvider.System);

    [Fact]
    public async Task A_check_records_its_verdict_and_when_it_ran()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using (var db = _fixture.NewServiceDbContext())
        {
            // No row at all is the third state — "nobody has looked at this" —
            // and it must be distinguishable from a healthy verdict, not merely
            // absent-and-assumed-fine.
            Assert.Null(await db.LedgerConsistencyStates.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LedgerId == ledger.LedgerId));
        }

        await NewMonitor().CheckAllAsync();

        await using (var db = _fixture.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state);
            Assert.True(state!.Healthy);
            Assert.Empty(state.FailingProjections);
            // The timestamp is the point: a verdict with no date cannot tell
            // "fine an hour ago" from "fine last August".
            Assert.NotNull(state.LastCheckedAt);
            Assert.InRange(state.LastCheckedAt!.Value,
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
            Assert.Null(state.LastRepairedAt);
        }
    }

    [Fact]
    public async Task A_ledger_checked_recently_is_skipped_on_the_next_pass()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var monitor = NewMonitor();

        await monitor.CheckAllAsync();

        DateTime? first;
        await using (var db = _fixture.NewServiceDbContext())
        {
            first = (await db.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId)).LastCheckedAt;
            Assert.NotNull(first);
        }

        // The monitor is invoked on every 15-minute tick; it must not re-verify
        // every balance row of a real-sized ledger each time to announce at most
        // twice a day (FU-045).
        await monitor.CheckAllAsync();

        await using (var db = _fixture.NewServiceDbContext())
        {
            var again = (await db.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId)).LastCheckedAt;
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public async Task A_ledger_last_checked_beyond_the_cadence_is_checked_again()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var monitor = NewMonitor();
        await monitor.CheckAllAsync();

        // Age the recorded check past the cadence. Asserting the skip alone would
        // pass against a monitor that had simply stopped working.
        await using (var db = _fixture.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            state.LastCheckedAt = DateTime.UtcNow - ConsistencyMonitor.CheckEvery
                                  - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        await monitor.CheckAllAsync();

        await using (var db = _fixture.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastCheckedAt);
            Assert.InRange(state.LastCheckedAt!.Value,
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }
    }

    [Fact]
    public async Task A_ledger_whose_verdict_was_invalidated_is_checked_again()
    {
        // Nulling last_checked_at is how a restore says "the rows this verdict was
        // about are gone" without having to know what the cadence is. The gate is a
        // `>` comparison, and NULL > cutoff is NULL rather than true — so this test
        // is the one that proves the three-valued logic lands on DUE and not on the
        // silent opposite. Ageing the timestamp (the test above) would pass either
        // way.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var monitor = NewMonitor();
        await monitor.CheckAllAsync();

        await using (var db = _fixture.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            state.LastCheckedAt = null;
            state.Healthy = null;
            await db.SaveChangesAsync();
        }

        await monitor.CheckAllAsync();

        await using (var db = _fixture.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastCheckedAt);
            Assert.True(state.Healthy);
        }
    }

    [Fact]
    public async Task Half_a_verdict_cannot_be_stored()
    {
        // The two columns are one fact in two places, so the database refuses to
        // hold them apart. Without this an invalidation that nulled the timestamp
        // and forgot the verdict would leave `healthy = true` standing as a claim
        // about data nothing had checked — and it would READ as a green light,
        // which is the failure direction that matters.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await NewMonitor().CheckAllAsync();

        await using var db = _fixture.NewServiceDbContext();
        var state = await db.LedgerConsistencyStates
            .SingleAsync(r => r.LedgerId == ledger.LedgerId);
        state.LastCheckedAt = null;   // verdict deliberately left behind
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
