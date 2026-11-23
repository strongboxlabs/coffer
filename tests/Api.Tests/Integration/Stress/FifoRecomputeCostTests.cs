using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Measures <c>recompute_holdings_cost_basis</c> against a DEEP ledger — few
/// holdings, hundreds of events each — which is the shape its cost actually
/// depends on.
/// </summary>
/// <remarks>
/// <para>The walk re-queries the open-lot set once per event, so its cost grows
/// with events × open-lots WITHIN a holding rather than with holding count. On
/// <c>StressLedger.Scale.Default</c> (200 holdings, 22 events each) the whole-ledger
/// walk took <b>0.3s</b>; on <c>Scale.Deep</c> (20 holdings, 500 events each,
/// 8000 lots) it took ~2.3s, 7.7× the breadth figure for a comparable transaction
/// count, which is what makes events-per-holding the axis. Those figures predate
/// the seed's five extra event shapes per holding (27 and 505 events, 8060 lots on
/// <c>Scale.Deep</c>), a change of about 1% in the walk's input on Deep.</para>
/// <para>The number that decides whether optimising the walk is worth doing is the
/// narrowed one, because the write interceptors call it per touched position on
/// every transaction write: on <c>Scale.Deep</c> that is <b>~0.1s</b>, which does not justify the risk
/// of hoisting the open-lot query out of the per-event loop. No performance bound
/// is asserted; the numbers are the output.</para>
/// <para>Kept as a regression tripwire: if a future change makes the walk
/// superlinear, the printed figures move sharply and the reconciliation assertion
/// below catches any correctness slip that came with it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FifoRecomputeCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public FifoRecomputeCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    [Fact]
    public async Task Fifo_recompute_cost_scales_with_events_per_holding_not_holding_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Deep;

        var seedElapsed = await StressLedger.SeedAsync(Db, ledger, scale);
        // The seed itself runs one whole-ledger recompute, so this already includes
        // a full walk at depth — worth printing alongside.
        _output.WriteLine(
            $"seed:    {seedElapsed.TotalSeconds,7:F1}s  " +
            $"({scale.Holdings} holdings x " +
            $"{scale.BuysPerHolding + scale.SellsPerHolding + StressLedger.Scale.ShapeEventsPerHolding} events)");

        await using var db = Db.NewServiceFactory().Create();
        db.Database.SetCommandTimeout(600);

        var lots = await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM lots WHERE ledger_id = {ledger.LedgerId}").SingleAsync();
        var gains = await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM realized_gains WHERE ledger_id = {ledger.LedgerId}").SingleAsync();
        _output.WriteLine($"seeded:  {lots:N0} lots, {gains:N0} realized gains");

        Assert.Equal(scale.TotalLots, lots);
        Assert.True(gains > 0, "no realized_gains — the disposals closed no lots, so the walk was not exercised");

        // Whole-ledger: what a bulk import runs.
        var wholeLedger = Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_cost_basis({ledger.LedgerId})");
        wholeLedger.Stop();
        _output.WriteLine($"fifo (whole ledger):    {wholeLedger.Elapsed.TotalSeconds,7:F1}s");

        // Single (account, security): what EVERY transaction write pays, since the
        // interceptors narrow the recompute to the touched position. This is the
        // number that decides whether optimising the walk is worth doing.
        var one = await db.Database.SqlQuery<Guid>($"""
            SELECT h.security_id AS "Value"
              FROM holdings h
             WHERE h.ledger_id = {ledger.LedgerId}
             ORDER BY h.security_id
             LIMIT 1
            """).SingleAsync();
        var accountId = await db.Database.SqlQuery<Guid>($"""
            SELECT h.account_id AS "Value"
              FROM holdings h
             WHERE h.ledger_id = {ledger.LedgerId} AND h.security_id = {one}
             LIMIT 1
            """).SingleAsync();

        var narrowed = Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_cost_basis({ledger.LedgerId}, {accountId}, {one})");
        narrowed.Stop();
        _output.WriteLine(
            $"fifo (one position):    {narrowed.Elapsed.TotalSeconds,7:F1}s  " +
            $"<- paid on every write touching this position");

        // Consistency must survive the walk, or the timing is meaningless.
        var drifted = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT h.id
                  FROM holdings h
                  LEFT JOIN lots l ON l.holding_id = h.id AND l.is_closed = FALSE
                 WHERE h.ledger_id = {ledger.LedgerId}
                 GROUP BY h.id, h.quantity
                HAVING COALESCE(SUM(l.quantity), 0) <> h.quantity
            ) d
            """).SingleAsync();
        Assert.Equal(0, drifted);
    }
}
