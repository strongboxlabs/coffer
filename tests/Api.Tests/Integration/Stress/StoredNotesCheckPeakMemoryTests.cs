using System.Diagnostics.Tracing;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the stored-notes check holds at its peak, read from the managed heap over a
/// stress-sized ledger. A measurement, not a functional test: its reading follows the
/// machine (how often the collector runs, what else the process does), so it runs in the
/// stress lane, on demand. The every-build test of the same promise is
/// <c>StoredNotesConsistencyTests.The_check_reads_the_ledger_a_chunk_at_a_time</c>, which
/// counts the rows each query reads.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesCheckPeakMemoryTests : DatabaseTest
{
    public StoredNotesCheckPeakMemoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static LedgerConsistencyRepository RepoFor(AppDbContext db, int storedNotesChunk) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System)
        {
            StoredNotesChunkSize = storedNotesChunk,
        };

    /// <summary>What a run of the check held live: at its peak, and once it returned (its report).</summary>
    private sealed record Footprint(long Peak, long Held);

    [Fact]
    public async Task The_checks_peak_does_not_grow_with_the_ledger()
    {
        // Two ledgers, one four times the other, compared a chunk at a time. Agreeing, the
        // check holds one chunk at its peak, so the larger ledger must not double it. Drifted
        // throughout, it returns a report naming every header; at its peak it must hold less
        // than twice that report, so nothing is kept per leg on the way to it.
        const int chunk = 500;
        using var gc = new GcPeakListener();
        var small = await SyntheticLedger.CreateAsync(Db);
        var large = await SyntheticLedger.CreateAsync(Db);
        try
        {
            await StressLedger.SeedLedgerAsync(Db, small, new StressLedger.Scale(BankTxns: 2_000, Holdings: 2, BuysPerHolding: 2, SellsPerHolding: 1));
            await StressLedger.SeedLedgerAsync(Db, large, new StressLedger.Scale(BankTxns: 8_000, Holdings: 2, BuysPerHolding: 2, SellsPerHolding: 1));

            var (smallAgreeing, smallHeaders) = await FootprintsAsync(gc, small.LedgerId, chunk, drifted: false);
            var (largeAgreeing, largeHeaders) = await FootprintsAsync(gc, large.LedgerId, chunk, drifted: false);
            await using (var db = Db.NewServiceDbContext())
            {
                var ledgers = new[] { small.LedgerId, large.LedgerId };
                await db.TxnLegs.Where(l => ledgers.Contains(l.LedgerId))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, l => (l.LegMemo ?? "") + " written past the API"));
            }
            var (smallDrifted, _) = await FootprintsAsync(gc, small.LedgerId, chunk, drifted: true);
            var (largeDrifted, _) = await FootprintsAsync(gc, large.LedgerId, chunk, drifted: true);

            foreach (var (name, runs) in new[]
                     {
                         ("small agreeing", smallAgreeing), ("large agreeing", largeAgreeing),
                         ("small drifted", smallDrifted), ("large drifted", largeDrifted),
                     })
                foreach (var run in runs)
                    Output.WriteLine($"notes-check-footprint {name}: peak={run.Peak / 1024}kB held={run.Held / 1024}kB");
            Assert.True(largeHeaders >= 3 * smallHeaders, $"{largeHeaders} headers against {smallHeaders}");
            Assert.True(smallHeaders >= 3 * chunk, $"{smallHeaders} headers span too few chunks of {chunk}");

            var smallPeak = Median(smallAgreeing.Select(r => r.Peak));
            var largePeak = Median(largeAgreeing.Select(r => r.Peak));
            // A reading over-estimates the peak (garbage a young collection left alone) and
            // never misses it, so a check that held the larger ledger shows it in EVERY run's
            // reading: its lowest is the one to judge growth by. A busy machine inflates one
            // run or two, and the median of three then reads that noise as growth.
            var largeLeast = largeAgreeing.Min(r => r.Peak);
            Assert.True(smallPeak > 0, "the collector reported nothing during the small ledger's check");
            Assert.True(largeLeast < 2 * smallPeak,
                $"agreeing, the check peaked at least at {largeLeast / 1024}kB over {largeHeaders} headers "
                + $"against {smallPeak / 1024}kB over {smallHeaders}: it grows with the ledger");

            var driftedPeak = Median(largeDrifted.Select(r => r.Peak));
            var report = Median(largeDrifted.Select(r => r.Held));
            // What it holds on the way to a report is that report plus the chunk it is
            // working on, which is what the same check peaks at when nothing drifted.
            Assert.True(driftedPeak < report + 3 * largePeak / 2,
                $"drifted, the check peaked at {driftedPeak / 1024}kB for a report of {report / 1024}kB "
                + $"and a working chunk of {largePeak / 1024}kB: it holds more than its report on the way to it");
        }
        finally
        {
            await small.RecomputeAllNotesAsync();
            await large.RecomputeAllNotesAsync();
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>The check over <paramref name="ledgerId"/>, once to warm up and then measured three times.</summary>
    private async Task<(IReadOnlyList<Footprint> Runs, int Headers)> FootprintsAsync(
        GcPeakListener gc, Guid ledgerId, int chunk, bool drifted)
    {
        var runs = new List<Footprint>();
        var headers = 0;
        for (var round = 0; round < 4; round++)
        {
            await using var db = Db.NewServiceDbContext();
            db.Database.SetCommandTimeout(600);
            var repo = RepoFor(db, chunk);
            // Only figures leave the measurement: a report kept here would still be live
            // when the next round reads its baseline.
            var (footprint, (checkedCount, mismatched)) = await FootprintAsync(
                gc,
                () => repo.CheckStoredNotesAsync(ledgerId, CancellationToken.None),
                r => (r.Projection.Checked, r.Projection.MismatchedCount));
            Assert.True(drifted ? mismatched > checkedCount / 2 : mismatched == 0,
                $"{mismatched} of {checkedCount} headers drifted");
            headers = checkedCount;
            if (round > 0) runs.Add(footprint);
        }
        return (runs, headers);
    }

    /// <summary>
    /// The managed heap <paramref name="work"/> holds: the largest the collector reported
    /// after any collection it ran by itself while the work ran, and what is left once it
    /// returns, with its result still held. In-process counters, not time.
    /// </summary>
    /// <remarks>
    /// Nothing here forces a collection while the work runs: the check allocates enough
    /// that the runtime collects on its own, many times over a ledger this size, and each
    /// collection reports the size of every generation after it. Forced collections in a
    /// loop would slow the work being measured several times over. The reading can include
    /// garbage a generation-0 or -1 collection left alone, which makes it an over-estimate,
    /// never a miss: a check that held the ledger at its peak shows it in the generations
    /// the collector reports.
    /// </remarks>
    private static async Task<(Footprint Footprint, TFacts Facts)> FootprintAsync<T, TFacts>(
        GcPeakListener gc, Func<Task<T>> work, Func<T, TFacts> facts)
    {
        static long Live()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            return GC.GetTotalMemory(forceFullCollection: false);
        }

        // The caller holds the previous measurement's task, and through it that run's
        // result, until it awaits this one: yielding first makes it let go.
        await Task.Yield();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var baseline = Live();
        gc.Reset();
        var result = await work();
        var rise = gc.Rise;
        var held = Live();
        var figures = facts(result);
        return (new Footprint(Math.Max(rise, held - baseline), held - baseline), figures);
    }

    /// <summary>
    /// The size of the managed heap, every generation together, after each collection the
    /// runtime runs, read from its own events. The largest since <see cref="Reset"/> is
    /// <see cref="Peak"/>. Process-wide, like the counters the footprints are taken from.
    /// </summary>
    private sealed class GcPeakListener : EventListener
    {
        private readonly object _gate = new();
        private long _peak;
        private long _least = long.MaxValue;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x1); // GC
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is null
                || !eventData.EventName.StartsWith("GCHeapStats", StringComparison.Ordinal)
                || eventData.PayloadNames is null
                || eventData.Payload is null)
                return;
            long size = 0;
            // Gen 1, 2, the large object heap and the pinned object heap: gen 0 is the
            // allocation budget, whose size follows the machine's cache and not the check.
            foreach (var name in new[] { "GenerationSize1", "GenerationSize2", "GenerationSize3", "GenerationSize4" })
            {
                var at = eventData.PayloadNames.IndexOf(name);
                if (at >= 0) size += Convert.ToInt64(eventData.Payload[at], System.Globalization.CultureInfo.InvariantCulture);
            }
            lock (_gate)
            {
                if (size > _peak) _peak = size;
                if (size < _least) _least = size;
            }
        }

        /// <summary>
        /// How far the reported size rose above the lowest it reported since <see cref="Reset"/>.
        /// The level the heap sits at between collections differs from one run of the test
        /// process to the next, by several times what the check holds; the rise within a
        /// run does not carry it.
        /// </summary>
        public long Rise
        {
            get { lock (_gate) return _peak == 0 ? 0 : _peak - _least; }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _peak = 0;
                _least = long.MaxValue;
            }
        }
    }
}
