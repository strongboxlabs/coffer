using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// A repair leaves a record of itself (FU-012).
/// </summary>
/// <remarks>
/// A repair is invisible by construction: it makes the stored rows agree with the
/// transactions, which is indistinguishable from their having always agreed. So
/// afterwards there is nothing in the data to say it happened.
/// <para>That is the defect FU-012 names. The check is advisory and per-ledger, so
/// an operator with several ledgers cannot tell one that was repaired from one
/// nobody has opened — and a report expected to be unhealthy on some unremembered
/// subset trains people to ignore the one signal that matters.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyRepairIsRecordedTests
{
    private readonly PostgresFixture _fixture;

    public ConsistencyRepairIsRecordedTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Drop the sub-microsecond ticks a <c>timestamptz</c> cannot hold.</summary>
    private static DateTime ToMicroseconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), value.Kind);

    /// <summary>
    /// Block until the monitor's boot pass has recorded a verdict for this ledger.
    /// </summary>
    /// <remarks>
    /// <c>SchedulerService</c> runs its first tick at process start with no initial
    /// delay, so every test that builds an <c>ApiFactory</c> gets one consistency
    /// pass whether it wants one or not, at a moment nothing controls. Asserting
    /// around it is a coin flip; waiting for it makes the state deterministic.
    /// <para>Once it lands the ledger is inside the 12h cadence window, so no later
    /// tick will touch the row — which is what lets the tests below set up a verdict
    /// and trust it to still be there.</para>
    /// </remarks>
    private async Task WaitForBootCheckAsync(SyntheticLedger ledger)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var svc = _fixture.NewServiceDbContext())
            {
                if (await svc.LedgerConsistencyStates.AsNoTracking()
                        .AnyAsync(r => r.LedgerId == ledger.LedgerId && r.LastCheckedAt != null))
                    return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The consistency monitor never recorded a verdict for this ledger.");
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Repairing_a_projection_records_what_was_repaired()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw SQL, so the interceptor that would seed the trade price never runs —
        // this is the drift the repair exists to fix.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Nothing recorded before — otherwise the assertion after the repair could
        // be satisfied by an event from somewhere else entirely.
        await using (var db = _fixture.NewDbContext())
        {
            Assert.False(await db.LedgerEvents.AsNoTracking().AnyAsync(
                e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired"));
        }

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            var ev = Assert.Single(await db.LedgerEvents.AsNoTracking()
                .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired")
                .ToListAsync());

            // WHICH projection, not merely that something happened: the whole point
            // is telling a repaired ledger from an untouched one, and "a repair
            // occurred" without naming the projection cannot do that when the
            // report covers several.
            Assert.Equal(NotificationTopics.Consistency, ev.Topic);
            Assert.Contains(ConsistencyProjections.TradePrices, ev.Summary, StringComparison.Ordinal);
        }

        // And on the ledger's own state row, which is the part an operator with
        // several ledgers reads — the event log answers "what happened", this
        // answers "which of these has been touched".
        await using (var svc = _fixture.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastRepairedAt);
            Assert.InRange(state.LastRepairedAt!.Value,
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }
    }

    [Fact]
    public async Task A_repair_stamps_the_repair_and_leaves_the_verdict_alone()
    {
        // A repair fixes ONE projection. Writing a verdict off that result would
        // claim more than was checked, and healthy is the dangerous direction: it
        // reads as "nothing to do here" while other projections may still be wrong.
        // The monitor's next pass settles the verdict; this endpoint does not.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await WaitForBootCheckAsync(ledger);

        // Pin the verdict, so "unchanged" is a comparison against a known value
        // rather than against whatever the boot pass happened to write.
        //
        // Truncated to the column's microseconds: timestamptz has no room for
        // .NET's 100ns ticks, so a raw UtcNow comes back five digits shorter and
        // the equality below fails on a round-trip rather than on a rewrite.
        var checkedAt = ToMicroseconds(DateTime.UtcNow.AddMinutes(-3));
        await using (var svc = _fixture.NewServiceDbContext())
        {
            var seeded = await svc.LedgerConsistencyStates
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            seeded.LastCheckedAt = checkedAt;
            seeded.Healthy = false;
            seeded.FailingProjections = [ConsistencyProjections.TradePrices];
            await svc.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using (var svc = _fixture.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastRepairedAt);
            Assert.False(state.Healthy);
            Assert.Equal(checkedAt, state.LastCheckedAt);
            Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);
        }
    }

    [Fact]
    public async Task A_repair_before_any_check_records_itself_without_inventing_a_verdict()
    {
        // The branch that exists in production for a ledger the monitor has not
        // reached yet. "Unknown" is a real state and gets a NULL: false would
        // render as UNHEALTHY for a ledger nothing has examined, and a backdated
        // timestamp would be a magic value saying the same thing worse.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddInvestmentAccountAsync("Brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Wait for the boot pass, THEN clear what it wrote. Deleting first would
        // only race it; deleting after is what puts the ledger back into the
        // never-checked state this test is about. The next tick is 15 minutes out.
        await WaitForBootCheckAsync(ledger);
        await using (var svc = _fixture.NewServiceDbContext())
        {
            await svc.LedgerConsistencyStates
                .Where(r => r.LedgerId == ledger.LedgerId)
                .ExecuteDeleteAsync();
        }

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using (var svc = _fixture.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Null(state.Healthy);
            Assert.Null(state.LastCheckedAt);
            Assert.Empty(state.FailingProjections);
            Assert.NotNull(state.LastRepairedAt);
        }
    }

    [Fact]
    public async Task A_repair_that_changed_nothing_is_still_recorded()
    {
        // The no-op case is the one worth keeping. "I ran it and it was already
        // fine" is exactly the state an operator cannot otherwise distinguish from
        // "nobody has looked at this ledger", which is FU-012's complaint.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddInvestmentAccountAsync("Brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var ev = Assert.Single(await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired")
            .ToListAsync());
        Assert.Contains("nothing needed changing", ev.Summary, StringComparison.Ordinal);
    }
}
