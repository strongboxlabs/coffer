using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The scale lane: snapshot create + restore against a ~50k-transaction,
/// 200-holding ledger. Deliberately OUTSIDE the default test run — the
/// <c>Integration.Stress</c> namespace is excluded from the sharded suite
/// (<c>scripts/ci-dotnet-shards.sh</c>), because building the fixture takes minutes
/// and would dominate every ordinary run. Invoke it explicitly by filtering on this
/// namespace:
/// <code>dotnet test --filter "FullyQualifiedName~Integration.Stress"</code>
/// </summary>
/// <remarks>
/// <para>This exists because restore latency was completely unmeasured. The ~27s
/// figure that motivated mig 188 came from real prod-shaped data, and the
/// representative fixture is far too small to reproduce it (19 headers, 1
/// holding) — so the payoff of that change was reasoned and unit-tested but never
/// timed. Nothing here asserts a speedup: it asserts restore stays well inside its
/// timeout at scale, and prints the timings so a regression is visible.</para>
/// <para>The thresholds are deliberately loose. This runs on whatever hardware the
/// lane is invoked on, so a tight bound would be a flake generator. The value is
/// the printed numbers plus a guard against the failure that actually happened in
/// prod — a restore exceeding its command timeout.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SnapshotRestoreLatencyTests
{
    /// <summary>
    /// The prod failure was a 30s command timeout; the cap is now 600s
    /// (SnapshotOpTimeoutSeconds). Fail well before that so there is headroom to
    /// diagnose rather than a hard stop at the cap.
    /// </summary>
    private static readonly TimeSpan RestoreBudget = TimeSpan.FromSeconds(120);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SnapshotRestoreLatencyTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Snapshot_create_and_restore_stay_within_budget_at_scale()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var scale = StressLedger.Scale.Default;

        var seedElapsed = await StressLedger.SeedAsync(_fixture, ledger, scale);
        _output.WriteLine($"seed:    {seedElapsed.TotalSeconds,7:F1}s  (target {scale.TotalTxns:N0} txns)");

        // Prove the scale actually materialised — a silently-small ledger would
        // make every timing below meaningless.
        var counts = await ReadCountsAsync(ledger.LedgerId);
        _output.WriteLine(
            $"seeded:  {counts.Headers:N0} headers, {counts.Legs:N0} legs, " +
            $"{counts.Holdings:N0} holdings, {counts.Lots:N0} lots, {counts.Gains:N0} realized gains");

        Assert.Equal(scale.TotalTxns, counts.Headers);
        Assert.Equal(scale.Holdings, counts.Holdings);
        Assert.Equal(scale.Holdings * scale.BuysPerHolding, counts.Lots);
        // Sells must have produced realized gains, or the FIFO path went unexercised.
        Assert.True(counts.Gains > 0, "no realized_gains rows — the disposals did not close any lots");

        // Systemic invariants must hold at scale, not just in the small fixture.
        await AssertLedgerIsConsistentAsync(ledger.LedgerId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createWatch = Stopwatch.StartNew();
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("stress"));
        createWatch.Stop();
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;
        _output.WriteLine(
            $"create:  {createWatch.Elapsed.TotalSeconds,7:F1}s  " +
            $"({snap.ContentSizeUncompressed / (1024.0 * 1024.0):F1} MB payload)");

        var restoreWatch = Stopwatch.StartNew();
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        restoreWatch.Stop();
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);
        _output.WriteLine($"restore: {restoreWatch.Elapsed.TotalSeconds,7:F1}s  (budget {RestoreBudget.TotalSeconds:F0}s)");

        // The ledger must still be consistent after a full round trip, and the
        // graph must be intact rather than partially restored.
        var afterCounts = await ReadCountsAsync(ledger.LedgerId);
        Assert.Equal(counts, afterCounts);
        await AssertLedgerIsConsistentAsync(ledger.LedgerId);

        // Measure, don't assert: how long the FIFO recompute that mig 188 removed
        // from restore actually takes at this scale. Two reasons to keep this here
        // rather than reason about it — it quantifies what mig 188 saved (restore
        // used to pay this on top of the number above), and it is the baseline for
        // the follow-up that optimises the walk itself, which still runs on EVERY
        // transaction write via the interceptors.
        var fifoElapsed = await TimeFifoRecomputeAsync(ledger.LedgerId);
        _output.WriteLine(
            $"fifo:    {fifoElapsed.TotalSeconds,7:F1}s  " +
            $"(removed from restore by mig 188; still runs on every txn write)");

        Assert.True(
            restoreWatch.Elapsed < RestoreBudget,
            $"restore took {restoreWatch.Elapsed.TotalSeconds:F1}s, over the {RestoreBudget.TotalSeconds:F0}s budget");
    }

    private async Task<TimeSpan> TimeFifoRecomputeAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewServiceFactory().Create();
        // This is the operation whose cost is in question — it must not be cut off
        // by the 30s Npgsql default while we are trying to measure it.
        db.Database.SetCommandTimeout(600);

        var watch = Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_cost_basis({ledgerId})");
        watch.Stop();
        return watch.Elapsed;
    }

    private readonly record struct Counts(int Headers, int Legs, int Holdings, int Lots, int Gains);

    private async Task<Counts> ReadCountsAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewServiceFactory().Create();
        return new Counts(
            await db.TxnHeaders.CountAsync(h => h.LedgerId == ledgerId),
            await db.TxnLegs.CountAsync(l => l.LedgerId == ledgerId),
            await db.Holdings.CountAsync(h => h.LedgerId == ledgerId),
            await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM lots WHERE ledger_id = {ledgerId}").SingleAsync(),
            await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM realized_gains WHERE ledger_id = {ledgerId}").SingleAsync());
    }

    /// <summary>
    /// The same systemic invariants the representative fixture asserts, applied at
    /// scale: every header balances, and holdings reconcile with their open lots.
    /// Magnitude bugs surface here and nowhere smaller.
    /// </summary>
    private async Task AssertLedgerIsConsistentAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewServiceFactory().Create();

        var unbalanced = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT l.header_id
                  FROM txn_legs l
                  JOIN live_txn_headers h ON h.id = l.header_id
                 WHERE l.ledger_id = {ledgerId}
                 GROUP BY l.header_id
                HAVING SUM(l.amount) <> 0
            ) d
            """).SingleAsync();
        Assert.Equal(0, unbalanced);

        var drifted = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT h.id
                  FROM holdings h
                  LEFT JOIN lots l ON l.holding_id = h.id AND l.is_closed = FALSE
                 WHERE h.ledger_id = {ledgerId}
                 GROUP BY h.id, h.quantity
                HAVING COALESCE(SUM(l.quantity), 0) <> h.quantity
            ) d
            """).SingleAsync();
        Assert.Equal(0, drifted);

        var negative = await db.Holdings
            .CountAsync(h => h.LedgerId == ledgerId && h.Quantity < 0);
        Assert.Equal(0, negative);
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        // A 50k-transaction snapshot + restore runs far past HttpClient's 100s default.
        client.Timeout = TimeSpan.FromMinutes(15);
        return client;
    }
}
