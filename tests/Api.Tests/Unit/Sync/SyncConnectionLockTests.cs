using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Tests.Unit.Sync;

/// <summary>
/// API-layer concurrency fast-path (slice 2c.2). Pure in-memory
/// behavior — no DB, no HTTP. The corresponding integration test
/// covers the full DB + API + UI stack.
/// </summary>
public sealed class SyncConnectionLockTests
{
    [Fact]
    public void First_acquire_succeeds_second_returns_null_until_disposed()
    {
        var sut = new SyncConnectionLock();
        var connectionId = Guid.NewGuid();

        var first = sut.TryAcquire(connectionId);
        Assert.NotNull(first);

        var second = sut.TryAcquire(connectionId);
        Assert.Null(second);

        first!.Value.Dispose();

        var third = sut.TryAcquire(connectionId);
        Assert.NotNull(third);
        third!.Value.Dispose();
    }

    [Fact]
    public void Different_connections_lock_independently()
    {
        // Per-connection key — the lock is not a global gate; two
        // syncs against two different connections can run in
        // parallel.
        var sut = new SyncConnectionLock();
        var connA = Guid.NewGuid();
        var connB = Guid.NewGuid();

        using var holdA = sut.TryAcquire(connA);
        using var holdB = sut.TryAcquire(connB);

        Assert.NotNull(holdA);
        Assert.NotNull(holdB);
    }

    [Fact]
    public void Release_via_using_returns_the_slot()
    {
        // RAII pattern: `using var _ = sut.TryAcquire(id)` is the
        // expected caller shape. Disposal on scope exit must
        // release.
        var sut = new SyncConnectionLock();
        var connectionId = Guid.NewGuid();

        using (var first = sut.TryAcquire(connectionId))
        {
            Assert.NotNull(first);
        }
        // After the `using` block, the slot is free.
        using var second = sut.TryAcquire(connectionId);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Concurrent_acquires_yield_exactly_one_winner_under_burst()
    {
        // Burst 20 parallel TryAcquire calls against the same
        // connectionId. Exactly one should win; the other 19
        // must return null. This is the actual race the SPA's
        // rapid-Map-click scenario triggers.
        //
        // History: this test was originally flaky on CI — a
        // TaskCompletionSource start signal plus a short hold
        // (10 ms) wasn't enough to guarantee contention on a
        // constrained 2-core runner. The winner could finish +
        // release its slot before some of the other 19 tasks had
        // even been scheduled, producing 2+ winners. A
        // CountdownEvent barrier solves it: every task signals it
        // has reached the contention point THEN waits for the
        // last sibling. The TryAcquire calls then fire from all
        // 20 threads simultaneously, regardless of scheduler
        // latency, so the lock's atomicity is what's actually
        // exercised — not the test's timing luck.
        var sut = new SyncConnectionLock();
        var connectionId = Guid.NewGuid();

        const int taskCount = 20;
        var ready = new CountdownEvent(taskCount);
        var winners = 0;
        var losers = 0;

        // The hold duration only needs to outlast scheduler jitter
        // for the OTHER 19 tasks to call TryAcquire and see the
        // lock taken. 500 ms is far more than enough on any runner
        // (including the GitHub 2-core box) but stays under any
        // sensible per-test timeout.
        var holdMs = TimeSpan.FromMilliseconds(500);

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            // Synchronise: every task is INSIDE this method, past
            // any thread-pool queue latency, before any TryAcquire
            // call fires.
            ready.Signal();
            ready.Wait();

            using var slot = sut.TryAcquire(connectionId);
            if (slot is null) Interlocked.Increment(ref losers);
            else
            {
                Interlocked.Increment(ref winners);
                // Hold the lock long enough that every other task
                // calls TryAcquire while the lock is still taken.
                // Without this, the winner can release before some
                // of its siblings have been scheduled to run, and
                // they then "win" on a now-free lock — not what
                // this test is exercising.
                Thread.Sleep(holdMs);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
        Assert.Equal(taskCount - 1, losers);
    }
}
