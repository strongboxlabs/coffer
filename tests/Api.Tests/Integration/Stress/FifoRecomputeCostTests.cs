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
/// <para>This test exists because a measurement corrected a wrong belief.
/// Migration 188 was written on the reading that the FIFO walk's nested loops were
/// the ~27s in a real restore. Measured on the 200-holding fixture it came out at
/// <b>0.3s</b> — because that fixture gives each holding only 22 events, and the
/// walk re-queries the open-lot set once per event, so its cost grows with
/// events × open-lots WITHIN a holding rather than with holding count. Migration
/// 188's decision still stands on its own (don't re-derive what the payload
/// carries, and don't do provably dead work), but its attribution of the time did
/// not survive contact with a number.</para>
/// <para>At depth the whole-ledger walk measures ~2.3s (20 holdings, 500 events
/// each, 8,000 lots) — 7.7× the breadth figure for a comparable transaction count,
/// which confirms events-per-holding is the axis. But the number that decides
/// whether optimising the walk is worth doing is the narrowed one, because the
/// write interceptors call it per touched position on every transaction write:
/// that is <b>~0.1s</b>. So hoisting the open-lot query out of the per-event loop
/// was demoted from "the fix" to "not currently worth the risk" by these numbers.
/// No performance bound is asserted; the numbers are the output.</para>
/// <para>Kept as a regression tripwire: if a future change makes the walk
/// superlinear, the printed figures move sharply and the reconciliation assertion
/// below catches any correctness slip that came with it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FifoRecomputeCostTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public FifoRecomputeCostTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Fifo_recompute_cost_scales_with_events_per_holding_not_holding_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var scale = StressLedger.Scale.Deep;

        var seedElapsed = await StressLedger.SeedAsync(_fixture, ledger, scale);
        // The seed itself runs one whole-ledger recompute, so this already includes
        // a full walk at depth — worth printing alongside.
        _output.WriteLine(
            $"seed:    {seedElapsed.TotalSeconds,7:F1}s  " +
            $"({scale.Holdings} holdings x {scale.BuysPerHolding + scale.SellsPerHolding} events)");

        await using var db = _fixture.NewServiceFactory().Create();
        db.Database.SetCommandTimeout(600);

        var lots = await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM lots WHERE ledger_id = {ledger.LedgerId}").SingleAsync();
        var gains = await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM realized_gains WHERE ledger_id = {ledger.LedgerId}").SingleAsync();
        _output.WriteLine($"seeded:  {lots:N0} lots, {gains:N0} realized gains");

        Assert.Equal(scale.Holdings * scale.BuysPerHolding, lots);
        Assert.True(gains > 0, "no realized_gains — the disposals closed no lots, so the walk was not exercised");

        // Whole-ledger: what restore used to run, and what a bulk import runs.
        var wholeLedger = Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_cost_basis({ledger.LedgerId})");
        wholeLedger.Stop();
        _output.WriteLine($"fifo (whole ledger):    {wholeLedger.Elapsed.TotalSeconds,7:F1}s");

        // Single (account, security): what EVERY transaction write pays, since the
        // interceptors narrow the recompute to the touched position. This is the
        // number that decides whether the follow-up is worth doing.
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
