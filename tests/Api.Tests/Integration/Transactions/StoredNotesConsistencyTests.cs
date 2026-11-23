using System.Net;
using System.Diagnostics.Tracing;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The stored memo-and-tags presentation is one more projection of the consistency
/// check: a write that bypassed every writer (dynamic SQL, a hand-run statement) shows
/// as a count of drifted headers, the monitor warns and repairs nothing, and the
/// panel's Repair action recomputes exactly those headers, recorded like every repair.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesConsistencyTests : DatabaseTest
{
    public StoredNotesConsistencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int m, int d) => new(2026, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static LedgerConsistencyRepository RepoFor(AppDbContext db, int? storedNotesChunk = null) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System)
        {
            StoredNotesChunkSize = storedNotesChunk ?? LegNotes.BatchSize,
        };

    /// <summary>A ledger of three transactions, one of whose leg memos is then changed past every writer.</summary>
    private async Task<(SyntheticLedger Ledger, Guid Drifted)> DriftedLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, Utc(3, 1), payee: "Payee Drift");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -11m, Utc(3, 2), payee: "Payee Steady");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -12m, Utc(3, 3), payee: "Payee Steady Two");
        await using (var db = Db.NewDbContext())
            await db.TxnLegs.Where(l => l.Id == leg).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "written past the API"));
        return (ledger, await ledger.ResolveHeaderIdAsync(leg));
    }

    [Fact]
    public async Task Drift_is_counted_by_header_named_by_id_and_left_alone()
    {
        var (ledger, drifted) = await DriftedLedgerAsync();
        try
        {
            await using var db = Db.NewDbContext();
            var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

            Assert.False(report.Healthy);
            var notes = Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.StoredNotes);
            Assert.Equal(3, notes.Checked);
            Assert.Equal(1, notes.MismatchedCount);
            var mismatch = Assert.Single(notes.Mismatches);
            Assert.Equal(drifted, mismatch.HeaderId);
            Assert.NotNull(mismatch.AccountId);
            // It names no memo and no tag.
            Assert.DoesNotContain("written past the API", mismatch.Scope + mismatch.Field, StringComparison.Ordinal);

            // Read-only: the drift is still there.
            Assert.NotEmpty(await LegNotes.DifferencesAsync(db, ledger.LedgerId, CancellationToken.None));
        }
        finally
        {
            await ledger.RecomputeAllNotesAsync();
        }
    }

    [Fact]
    public async Task A_header_a_recompute_is_repairing_is_waited_for_and_not_reported()
    {
        // The first read sees the drift; a writer holding the header's lock is just
        // recomputing it. The re-check takes the same ordered lock, so it waits for that
        // writer and then reads the repaired values: nothing is reported.
        var (ledger, drifted) = await DriftedLedgerAsync();
        try
        {
            await using var writer = Db.NewDbContext();
            await using var tx = await writer.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            await LegNotes.RecomputeAsync(writer, [drifted], CancellationToken.None);

            await using var db = Db.NewDbContext();
            var checking = RepoFor(db).CheckAsync(ledger.LedgerId);
            Assert.NotSame(checking, await Task.WhenAny(checking, Task.Delay(TimeSpan.FromSeconds(2))));
            await tx.CommitAsync();

            var report = await checking;
            Assert.True(Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.StoredNotes).Healthy);
        }
        finally
        {
            await ledger.RecomputeAllNotesAsync();
        }
    }

    [Fact]
    public async Task The_monitor_warns_with_the_count_and_repairs_nothing()
    {
        var (ledger, _) = await DriftedLedgerAsync();
        try
        {
            var monitor = new ConsistencyMonitor(
                Db.NewServiceFactory(),
                new NotificationPublisher(
                    Db.NewServiceFactory(), Db.NewLedgerKeyService(),
                    Array.Empty<INotificationSubscriber>(),
                    NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
                NullLogger<ConsistencyMonitor>.Instance, TimeProvider.System);
            await monitor.CheckAllAsync();

            await using var db = Db.NewServiceDbContext();
            var warning = Assert.Single(await db.LedgerEvents.AsNoTracking()
                .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.drift")
                .ToListAsync());
            Assert.Contains($"{ConsistencyProjections.StoredNotes}: 1 of 3", warning.Summary, StringComparison.Ordinal);
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(s => s.LedgerId == ledger.LedgerId);
            Assert.Contains(ConsistencyProjections.StoredNotes, state.FailingProjections);
            // Warns only: the drift is untouched.
            Assert.NotEmpty(await LegNotes.DifferencesAsync(db, ledger.LedgerId, CancellationToken.None));
        }
        finally
        {
            await ledger.RecomputeAllNotesAsync();
        }
    }

    [Fact]
    public async Task The_repair_action_recomputes_the_drifted_headers_and_is_recorded()
    {
        var (ledger, drifted) = await DriftedLedgerAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        // The panel's report, as the SPA reads it.
        var report = await (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency", null)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<LedgerConsistencyReport>();
        var notes = Assert.Single(report!.Projections, p => p.Projection == ConsistencyProjections.StoredNotes);
        Assert.Equal(1, notes.MismatchedCount);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.StoredNotes}/repair", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var repaired = await resp.Content.ReadFromJsonAsync<ProjectionRepairResult>();
        Assert.Equal(drifted, Assert.Single(repaired!.Repaired.Mismatches).HeaderId);
        Assert.True(repaired.ProjectionAfter().Healthy);

        await using var db = Db.NewServiceDbContext();
        Assert.Empty(await LegNotes.DifferencesAsync(db, ledger.LedgerId, CancellationToken.None));
        var ev = Assert.Single(await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.repaired").ToListAsync());
        Assert.Contains(ConsistencyProjections.StoredNotes, ev.Summary, StringComparison.Ordinal);
        Assert.NotNull((await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(s => s.LedgerId == ledger.LedgerId)).LastRepairedAt);

        var after = await (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency", null)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<LedgerConsistencyReport>();
        Assert.True(Assert.Single(after!.Projections, p => p.Projection == ConsistencyProjections.StoredNotes).Healthy);
    }

    /// <summary>
    /// A ledger whose drift spans many chunks of the check, one class of drift each: a
    /// leg's own memo changed past every writer, a leg's tag link deleted past every
    /// writer, a transaction inserted past every writer (no stored presentation at all),
    /// and a posting moved to another transaction past every writer (its stored tag row
    /// still names the header it left).
    /// </summary>
    private async Task<(SyntheticLedger Ledger, IReadOnlySet<Guid> Drifted, int Headers)> DriftedInChunksAsync(int chunk)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var bankLegOf = new Dictionary<Guid, Guid>();
        for (var i = 0; i < 6 * chunk; i++)
        {
            var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -1m - i, Utc(3, 1 + (i % 28)), payee: $"Payee {i}");
            bankLegOf[await ledger.ResolveHeaderIdAsync(leg)] = leg;
        }
        var (splitOrigins, split) = await ledger.AddMultiSplitAsync(
            bank.Id, [(category.Id, -1m), (category.Id, -2m), (category.Id, -3m)], Utc(4, 1), payee: "Payee Split");
        await ledger.AddLegTagAsync(splitOrigins[2], "tag-moved");

        // In the check's own order, so the drifted headers sit in every chunk, the last included.
        await using var db = Db.NewDbContext();
        var pairs = bankLegOf.Keys.ToList();
        var order = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && pairs.Contains(h.Id))
            .OrderBy(h => h.Id).Select(h => h.Id).ToListAsync();
        var memo = order.Where((_, i) => i % 3 == 0).Append(order[^1]).Distinct().ToList();
        var untagged = order.Where((_, i) => i % 3 == 1).ToList();
        var receiver = order[2];

        foreach (var header in untagged)
            await ledger.AddLegTagAsync(bankLegOf[header], "tag-dropped");
        var memoLegs = memo.Select(h => bankLegOf[h]).ToList();
        await db.TxnLegs.Where(l => memoLegs.Contains(l.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "written past the API"));
        var untaggedLegs = untagged.Select(h => bankLegOf[h]).ToList();
        await db.TxnLegTags.Where(t => untaggedLegs.Contains(t.LegId)).ExecuteDeleteAsync();
        await db.TxnLegs.Where(l => l.HeaderId == split && l.PostingIndex == 2)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeaderId, receiver).SetProperty(l => l.PostingIndex, 1));

        var unstored = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var header = Guid.NewGuid();
            unstored.Add(header);
            // Written and recomputed in one transaction, which takes the ledger write lock
            // first (migration 275).
            await db.SeedUnderLedgerLockAsync(ledger.LedgerId, () => db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at, originating_account_id)
                VALUES ({header}, {ledger.LedgerId}, 'manual', {"Payee Unstored " + i}, {Utc(5, 1 + i)}, {Utc(5, 1 + i)}, {Utc(5, 1 + i)}, {bank.Id});
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES ({Guid.NewGuid()}, {header}, {ledger.LedgerId}, {bank.Id}, 0, -5),
                       ({Guid.NewGuid()}, {header}, {ledger.LedgerId}, {category.Id}, 0, 5);
                SELECT fn_recompute_balances_for_account({bank.Id}, '0001-01-01'::timestamptz);
                SELECT fn_recompute_balances_for_account({category.Id}, '0001-01-01'::timestamptz);"));
        }

        var drifted = memo.Concat(untagged).Concat(unstored).Append(split).Append(receiver).ToHashSet();
        var headers = await db.TxnHeaders.CountAsync(h => h.LedgerId == ledger.LedgerId);
        return (ledger, drifted, headers);
    }

    [Fact]
    public async Task Drift_spanning_many_chunks_is_reported_header_by_header_in_every_chunk()
    {
        const int chunk = 4;
        var (ledger, expected, headers) = await DriftedInChunksAsync(chunk);
        try
        {
            await using var db = Db.NewDbContext();
            var chunked = await RepoFor(db, chunk).CheckStoredNotesAsync(ledger.LedgerId, CancellationToken.None);
            Assert.True(headers > 5 * chunk, $"{headers} headers span too few chunks of {chunk}");
            Assert.Equal(headers, chunked.Projection.Checked);
            Assert.Equal(expected.Count, chunked.Projection.MismatchedCount);
            Assert.True(expected.SetEquals(chunked.AllMismatches.Select(m => m.HeaderId!.Value)),
                "reported " + string.Join(", ", chunked.AllMismatches.Select(m => m.HeaderId)) + "; drifted " + string.Join(", ", expected));

            // The same headers as one chunk finds: how the ledger is cut into chunks changes nothing.
            var whole = await RepoFor(db).CheckStoredNotesAsync(ledger.LedgerId, CancellationToken.None);
            Assert.Equal(
                whole.AllMismatches.Select(m => m.HeaderId).ToList(),
                chunked.AllMismatches.Select(m => m.HeaderId).ToList());
        }
        finally
        {
            await ledger.RecomputeAllNotesAsync();
        }
    }

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
