using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>A ledger of <paramref name="pairs"/> bank-to-category transactions, two legs each.</summary>
    private async Task<SyntheticLedger> PairsLedgerAsync(int pairs)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        for (var i = 0; i < pairs; i++)
            await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m - i, Utc(3, 1 + (i % 28)), payee: "Payee " + i);
        return ledger;
    }

    [Fact]
    public async Task The_check_reads_the_ledger_a_chunk_at_a_time()
    {
        // What the check holds at its peak must not grow with the ledger (CheckStoredNotesAsync),
        // and what it can hold is what it reads. So this counts the rows every query of the check
        // reads, on the check's own connection: no query reads more than one chunk's headers
        // bring, however large the ledger, and a larger ledger is read in more round trips.
        // Drifted, one query more reads the report itself (each drifted header's owning
        // account), one row per header reported. Counts, not memory or time: the outcome is the
        // same on every machine and under any load. The heap itself is measured, at stress
        // size, in the stress lane (StoredNotesCheckPeakMemoryTests).
        const int chunk = 4;
        var small = await PairsLedgerAsync(12);
        var large = await PairsLedgerAsync(48);
        try
        {
            var smallBound = chunk * await RowsPerHeaderAsync(small.LedgerId);
            var largeBound = chunk * await RowsPerHeaderAsync(large.LedgerId);
            var smallAgreeing = await ReadsAsync(small.LedgerId, chunk);
            var largeAgreeing = await ReadsAsync(large.LedgerId, chunk);

            Assert.Equal(0, smallAgreeing.Result.Projection.MismatchedCount);
            Assert.Equal(0, largeAgreeing.Result.Projection.MismatchedCount);
            var smallHeaders = smallAgreeing.Result.Projection.Checked;
            var largeHeaders = largeAgreeing.Result.Projection.Checked;
            Assert.True(smallHeaders >= 3 * chunk, $"{smallHeaders} headers span too few chunks of {chunk}");
            Assert.True(largeHeaders >= 3 * smallHeaders, $"{largeHeaders} headers against {smallHeaders}");
            // The bound has teeth: the large ledger's legs alone are several times one chunk's rows.
            await using (var db = Db.NewServiceDbContext())
            {
                var legs = await db.TxnLegs.CountAsync(l => l.LedgerId == large.LedgerId);
                Assert.True(legs > 4 * largeBound, $"{legs} legs against a bound of {largeBound} rows a query");
            }

            AssertWithin(smallAgreeing.Reads, smallBound, "small agreeing");
            AssertWithin(largeAgreeing.Reads, largeBound, "large agreeing");
            Assert.True(largeAgreeing.Reads.Count >= 3 * smallAgreeing.Reads.Count,
                $"{largeAgreeing.Reads.Count} queries for {largeHeaders} headers against {smallAgreeing.Reads.Count} "
                + $"for {smallHeaders}: the ledger is not read a chunk at a time");

            await using (var db = Db.NewServiceDbContext())
            {
                await db.TxnLegs.Where(l => l.LedgerId == large.LedgerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, l => (l.LegMemo ?? "") + " written past the API"));
            }
            var drifted = await ReadsAsync(large.LedgerId, chunk);
            var reported = drifted.Result.Projection.MismatchedCount;
            Assert.True(reported > largeHeaders / 2, $"{reported} of {largeHeaders} headers drifted");
            // The recheck reads the drifted headers a chunk at a time too; only the report's
            // own query reads more, and it reads the report.
            var over = drifted.Reads.Where(r => r.Reads > largeBound + 1).ToList();
            Assert.True(over.Count == 1 && over[0].Reads == reported + 1,
                $"drifted, {over.Count} queries read more than {largeBound} rows ("
                + string.Join(", ", over.Select(r => r.Reads - 1)) + $"); only the report's, of {reported}, may");
        }
        finally
        {
            await small.RecomputeAllNotesAsync();
            await large.RecomputeAllNotesAsync();
        }
    }

    /// <summary>The most rows any one header of the ledger owns in a table the check reads
    /// per chunk: its legs, its tags, its legs' tags and its stored note-tag rows, together.</summary>
    private async Task<int> RowsPerHeaderAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.TxnHeaders.Where(h => h.LedgerId == ledgerId)
            .Select(h =>
                db.TxnLegs.Count(l => l.HeaderId == h.Id)
                + db.TxnHeaderTags.Count(t => t.HeaderId == h.Id)
                + db.TxnLegTags.Count(t => db.TxnLegs.Any(l => l.Id == t.LegId && l.HeaderId == h.Id))
                + db.TxnLegNoteTags.Count(t => t.HeaderId == h.Id))
            .MaxAsync();
    }

    private static void AssertWithin(IReadOnlyList<Read> reads, int bound, string run)
    {
        Assert.NotEmpty(reads);
        var over = reads.Where(r => r.Reads > bound + 1).ToList();
        Assert.True(over.Count == 0,
            $"{run}: {over.Count} queries read more than one chunk's {bound} rows: "
            + string.Join(", ", over.Select(r => $"{r.Reads - 1} rows")));
    }

    /// <summary>One query the check ran: how many reads its reader made, which is a reader
    /// that ran to its end its rows and one more, the read that found the end.</summary>
    private sealed record Read(int Reads);

    /// <summary>The stored-notes check over <paramref name="ledgerId"/>, and every query it ran
    /// on its connection, the inner store's included.</summary>
    private async Task<(LedgerConsistencyRepository.CheckResult Result, IReadOnlyList<Read> Reads)> ReadsAsync(
        Guid ledgerId, int chunk)
    {
        await using var db = Db.NewServiceDbContext();
        await db.Database.OpenConnectionAsync();
        using var recorder = new ReaderRecorder(db.Database.GetDbConnection());
        var result = await RepoFor(db, chunk).CheckStoredNotesAsync(ledgerId, CancellationToken.None);
        return (result, recorder.Reads);
    }

    /// <summary>
    /// The rows every data reader on one connection read, from EF Core's own diagnostic events
    /// (<see cref="RelationalEventId.DataReaderDisposing"/>). The check reads through two
    /// contexts on the same connection, the second with options of its own, so an interceptor
    /// on one context would miss the other's queries; the events carry the command, and so its
    /// connection, for both. Queries on any other connection, another test's, are not counted.
    /// </summary>
    private sealed class ReaderRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly System.Data.Common.DbConnection _connection;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly List<Read> _reads = [];
        private readonly object _gate = new();

        public ReaderRecorder(System.Data.Common.DbConnection connection)
        {
            _connection = connection;
            var all = DiagnosticListener.AllListeners.Subscribe(this);
            lock (_gate) _subscriptions.Add(all);
        }

        public IReadOnlyList<Read> Reads
        {
            get { lock (_gate) return [.. _reads]; }
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name) return;
            var subscription = listener.Subscribe(this, name => name == RelationalEventId.DataReaderDisposing.Name);
            lock (_gate) _subscriptions.Add(subscription);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is DataReaderDisposingEventData data && ReferenceEquals(data.Command.Connection, _connection))
                lock (_gate) _reads.Add(new Read(data.ReadCount));
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var s in _subscriptions) s.Dispose();
                _subscriptions.Clear();
            }
        }
    }
}
