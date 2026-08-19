using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Xunit.Abstractions;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Measures what a TWR boundary actually costs, because
/// <c>MaxReturnsBoundaries</c> is a wall-clock budget and a budget set without a
/// measurement is a guess — as this test's own first version proved.
/// </summary>
/// <remarks>
/// <para>A boundary valuation reads every scoped account's balance and holdings
/// as of that instant. The first version of this test seeded a NEAR-EMPTY ledger
/// — one holding, no history — measured 3.4 ms per boundary, and the cap was
/// raised from 400 to 2000 on that basis. Against a realistic ledger the figure
/// was 80-100 ms, so that reasoning was ~60x optimistic and would have licensed
/// a seven-minute request. The ledger is seeded through <c>StressLedger</c> now
/// for exactly that reason: the cost is O(ledger), so measuring it without one
/// measures nothing.</para>
/// <para>Measured on 50,000 transactions, per boundary: <b>~165 ms</b> before
/// migration 198 and <b>~90 ms</b> after it. The cap is a wall-clock budget
/// derived from that: 700 boundaries is roughly 60 s.</para>
/// <para>No timing bound is asserted — the numbers are the output, and hardware
/// differs. The assertion is a correctness one: every measured scale must still
/// PRODUCE a time-weighted return rather than silently degrade to IRR, which is
/// the regression a too-low cap causes.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsBoundaryCostTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ReturnsBoundaryCostTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(390)]   // just inside the cap
    public async Task Twr_still_computes_as_boundary_count_grows(int flowDates)
    {
        // A REALISTIC ledger underneath the flows. A boundary valuation costs
        // O(ledger), not O(1): measuring against a near-empty ledger reported
        // 3.4 ms per boundary and a cap sized from that was ~60x optimistic.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await StressLedger.SeedAsync(_fixture, ledger, StressLedger.Scale.Default);

        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Flow Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // One flow every other day — the shape a ledger with regular payroll
        // contributions across several accounts takes.
        var start = Utc(2016, 1, 1);
        for (var i = 0; i < flowDates; i++)
            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 100m, start.AddDays(i * 2));

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, start);
        await ledger.AddSecurityPriceAsync(sec, 12m, start.AddDays((flowDates * 2) + 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var sw = Stopwatch.StartNew();
        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: start.AddDays((flowDates * 2) + 30));
        sw.Stop();

        var perBoundary = sw.Elapsed.TotalMilliseconds / flowDates;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"boundaries={flowDates,5}  elapsed={sw.ElapsedMilliseconds,6}ms  per-boundary={perBoundary:F2}ms"));

        // The regression that bit: a cap set too low silently drops TWR entirely.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.Null(result.TimeWeightedUnavailableReason);
    }

    /// <summary>
    /// Prints what the batched feeder costs at two sizes, and asserts the thing that
    /// can actually be asserted: batching does not change the ANSWER.
    /// </summary>
    /// <remarks>
    /// An earlier version asserted per-instant cost must not grow with instant count.
    /// That was wrong twice over. It is not a property the design promises — the
    /// merged stream is positions x instants rows through a sort, so mild
    /// superlinearity is expected and measured (1.35ms each at 100 instants,
    /// 1.63ms at 420). And it would not have caught the regression it existed for:
    /// going back to one replay per instant ALSO gives a roughly flat per-instant
    /// cost, just a much larger one, so a ratio cannot tell the two apart. Only the
    /// absolute figure distinguishes them, and this lane deliberately asserts no
    /// absolute timings because it runs on whatever hardware invokes it — the
    /// printed numbers are the output.
    /// </remarks>
    [Fact]
    public async Task Batched_holdings_feeder_prints_cost_and_agrees_with_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await StressLedger.SeedAsync(_fixture, ledger, StressLedger.Scale.Default);

        var start = Utc(2016, 1, 1);
        DateTime[] Instants(int n) =>
            Enumerable.Range(0, n).Select(i => start.AddDays(i * 4)).ToArray();

        await using var db = _fixture.NewDbContext();

        // NULL account set = every holdings account. An EMPTY array means none, and
        // passing one measured a query that returned nothing while reporting a 56x
        // speedup — the reason this note is here.
        async Task<(long Ms, List<(DateTime AsOf, decimal Mv)> Rows)> TimeIt(int n)
        {
            var sw = Stopwatch.StartNew();
            var rows = await db.HoldingsMarketValueAsOfSet(ledger.LedgerId, Instants(n), null)
                .Select(r => new { r.AsOf, r.MarketValue })
                .ToListAsync();
            sw.Stop();
            Assert.NotEmpty(rows);
            return (sw.ElapsedMilliseconds, rows.Select(r => (r.AsOf, r.MarketValue)).ToList());
        }

        _ = await TimeIt(1);                    // warm
        var small = await TimeIt(100);
        var large = await TimeIt(420);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"100 instants={small.Ms}ms ({small.Ms / 100.0:F2}ms each)  " +
            $"420 instants={large.Ms}ms ({large.Ms / 420.0:F2}ms each)"));

        // The assertion: every instant the small call covered must come back
        // identically from the large one. Batching changes cost, never the answer —
        // and unlike a timing bound this holds on any hardware.
        var largeByInstant = large.Rows
            .GroupBy(r => r.AsOf)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Mv));
        var smallByInstant = small.Rows
            .GroupBy(r => r.AsOf)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Mv));

        Assert.NotEmpty(smallByInstant);
        foreach (var (asOf, mv) in smallByInstant)
        {
            Assert.True(largeByInstant.ContainsKey(asOf), $"instant {asOf:o} missing from the 420-instant call");
            Assert.Equal(mv, largeByInstant[asOf]);
        }
    }
}
