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
public sealed class ConsistencyRepairIsRecordedTests : DatabaseTest
{
    public ConsistencyRepairIsRecordedTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

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
            await using (var svc = Db.NewServiceDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw SQL, so the interceptor that would seed the trade price never runs —
        // this is the drift the repair exists to fix.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Nothing recorded before — otherwise the assertion after the repair could
        // be satisfied by an event from somewhere else entirely.
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.LedgerEvents.AsNoTracking().AnyAsync(
                e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired"));
        }

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using (var db = Db.NewDbContext())
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
        await using (var svc = Db.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastRepairedAt);
            Assert.InRange(state.LastRepairedAt!.Value,
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }
    }

    [Fact]
    public async Task A_repair_records_the_verdict_of_the_full_check_after_it()
    {
        // A repair fixes ONE projection, so its verdict is not read off that
        // projection: it is the full check the repair runs once it has committed,
        // which claims no more than was checked. Healthy is the dangerous direction
        // ("nothing to do here"), and it is written only when every projection
        // agrees.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await WaitForBootCheckAsync(ledger);

        // Pin the verdict, so "replaced" is a comparison against a known value
        // rather than against whatever the boot pass happened to write.
        //
        // Truncated to the column's microseconds: timestamptz has no room for
        // .NET's 100ns ticks, so a raw UtcNow comes back five digits shorter.
        var checkedAt = ToMicroseconds(DateTime.UtcNow.AddMinutes(-3));
        await using (var svc = Db.NewServiceDbContext())
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

        // The stored row is the repair's full check, not the repaired projection alone:
        // the raw purchase left its position unstored too, which the trade-price repair
        // does not touch, so the verdict stays unhealthy and names what is left.
        var repair = (await resp.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.True(repair.ProjectionAfter().Healthy);
        Assert.NotNull(repair.After);
        Assert.False(repair.After.Healthy);
        var left = repair.After.Projections.Where(p => !p.Healthy).Select(p => p.Projection)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Contains(ConsistencyProjections.Holdings, left);
        await using (var svc = Db.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.NotNull(state.LastRepairedAt);
            Assert.False(state.Healthy);
            Assert.True(state.LastCheckedAt > checkedAt);
            Assert.Equal(state.LastRepairedAt, state.LastCheckedAt);
            Assert.Equal(left, state.FailingProjections);
        }
    }

    [Fact]
    public async Task The_stored_verdict_reads_back_without_running_a_check()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddInvestmentAccountAsync("Brokerage");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await WaitForBootCheckAsync(ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/balances/consistency/state";

        // A verdict the check would not reach on its own, so reading it back proves
        // the read returns the stored row rather than checking again.
        var checkedAt = ToMicroseconds(DateTime.UtcNow.AddMinutes(-3));
        var repairedAt = ToMicroseconds(DateTime.UtcNow.AddMinutes(-9));
        await using (var svc = Db.NewServiceDbContext())
        {
            var seeded = await svc.LedgerConsistencyStates.SingleAsync(r => r.LedgerId == ledger.LedgerId);
            seeded.LastCheckedAt = checkedAt;
            seeded.Healthy = false;
            seeded.FailingProjections = [ConsistencyProjections.TradePrices];
            seeded.LastRepairedAt = repairedAt;
            await svc.SaveChangesAsync();
        }

        var state = (await client.GetFromJsonAsync<LedgerConsistencyStateDto>(path))!;
        Assert.Equal(checkedAt, state.LastCheckedAt);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);
        Assert.Equal(repairedAt, state.LastRepairedAt);

        // Never checked: no verdict, which reads as "not checked yet", never unhealthy.
        await using (var svc = Db.NewServiceDbContext())
        {
            await svc.LedgerConsistencyStates.Where(r => r.LedgerId == ledger.LedgerId).ExecuteDeleteAsync();
        }
        var unknown = (await client.GetFromJsonAsync<LedgerConsistencyStateDto>(path))!;
        Assert.Null(unknown.Healthy);
        Assert.Null(unknown.LastCheckedAt);
        Assert.Null(unknown.LastRepairedAt);
        Assert.Empty(unknown.FailingProjections);
    }

    [Fact]
    public async Task A_repair_before_any_check_records_the_check_it_ran()
    {
        // The branch that exists in production for a ledger the monitor has not
        // reached yet. The repair's own full check is a verdict, taken now, so the
        // row it writes holds it rather than an unknown.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddInvestmentAccountAsync("Brokerage");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Wait for the boot pass, THEN clear what it wrote. Deleting first would
        // only race it; deleting after is what puts the ledger back into the
        // never-checked state this test is about. The next tick is 15 minutes out.
        await WaitForBootCheckAsync(ledger);
        await using (var svc = Db.NewServiceDbContext())
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

        await using (var svc = Db.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.True(state.Healthy);
            Assert.NotNull(state.LastCheckedAt);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddInvestmentAccountAsync("Brokerage");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/"
            + $"{ConsistencyProjections.TradePrices}/repair",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var ev = Assert.Single(await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired")
            .ToListAsync());
        Assert.Contains("nothing needed changing", ev.Summary, StringComparison.Ordinal);
    }
}
