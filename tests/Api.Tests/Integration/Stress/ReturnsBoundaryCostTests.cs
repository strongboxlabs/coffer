using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Xunit.Abstractions;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Measures what a time-weighted return costs as its boundary count grows, against
/// a realistically sized ledger.
/// </summary>
/// <remarks>
/// <para>A boundary valuation reads every scoped account's balance and holdings
/// as of that instant, so its cost is O(ledger). Against a NEAR-EMPTY ledger —
/// one holding, no history — it reads ~60x cheaper than against a realistic one,
/// so a figure taken there says nothing about a real request. The ledger is
/// seeded through <c>StressLedger</c> for that reason.</para>
/// <para><c>ReturnsAsync</c> values every interior boundary in two set-based
/// queries — holdings through <c>holdings_market_value_as_of_set</c> and cash
/// through <c>account_balance_as_of_instants</c> — rather than two per boundary,
/// so no boundary-count limit applies. The printed per-boundary figure is how a
/// return to per-boundary valuation (cost boundaries × history) shows up.</para>
/// <para>No timing bound is asserted — the numbers are the output, and hardware
/// differs. The assertion is a correctness one: every measured scale must still
/// PRODUCE a time-weighted return, not just the money-weighted one, which is what
/// a boundary-count limit or a per-boundary timeout would cause.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsBoundaryCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public ReturnsBoundaryCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(390)]
    public async Task Twr_still_computes_as_boundary_count_grows(int flowDates)
    {
        // A REALISTIC ledger underneath the flows. A boundary valuation costs
        // O(ledger), not O(1): a near-empty ledger underestimates it ~60x.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedAsync(Db, ledger, StressLedger.Scale.Default);

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

        await using var db = Db.NewDbContext();
        var repository = new InvestmentReportingRepository(db, TimeProvider.System);

        var sw = Stopwatch.StartNew();
        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: start.AddDays((flowDates * 2) + 30));
        sw.Stop();

        var perBoundary = sw.Elapsed.TotalMilliseconds / flowDates;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"boundaries={flowDates,5}  elapsed={sw.ElapsedMilliseconds,6}ms  per-boundary={perBoundary:F2}ms"));

        // The regression this guards: TWR silently dropped at a boundary count the
        // report has to serve.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.Null(result.TimeWeightedUnavailableReason);
    }

    /// <summary>
    /// Prints what the batched feeder costs at two sizes, and asserts the thing that
    /// can actually be asserted: batching does not change the ANSWER.
    /// </summary>
    /// <remarks>
    /// Per-instant cost is not asserted flat. It is not a property the design
    /// promises — the merged stream is positions x instants rows through a sort, so
    /// mild superlinearity is expected (on the <c>StressLedger</c> default, 1.35ms
    /// each at 100 instants and 1.63ms at 420). Nor would a ratio catch the regression that matters: one
    /// replay per instant ALSO gives a roughly flat per-instant cost, just a much
    /// larger one, so a ratio cannot tell the two apart. Only the
    /// absolute figure distinguishes them, and this lane deliberately asserts no
    /// absolute timings because it runs on whatever hardware invokes it — the
    /// printed numbers are the output.
    /// </remarks>
    [Fact]
    public async Task Batched_holdings_feeder_prints_cost_and_agrees_with_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedAsync(Db, ledger, StressLedger.Scale.Default);

        var start = Utc(2016, 1, 1);
        DateTime[] Instants(int n) =>
            Enumerable.Range(0, n).Select(i => start.AddDays(i * 4)).ToArray();

        await using var db = Db.NewDbContext();

        // NULL account set = every holdings account. An EMPTY array means none, and
        // passing one would time a query that returns nothing and report it as a
        // large speedup.
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
