using System.Collections.Concurrent;

namespace Coffer.Api.Sync.SimpleFin;

/// <summary>
/// In-process API-layer concurrency guard for per-connection sync
/// requests (slice 2c.2). Pairs with the DB-level UNIQUE partial
/// index from migration 040 — both enforce "at most one running
/// sync per connection," but at different layers:
///
/// <list type="bullet">
///   <item><description><b>DB</b> — the load-bearing correctness
///   layer. UNIQUE partial index raises a constraint violation;
///   survives process restarts and multi-instance deployments.</description></item>
///   <item><description><b>API</b> (this class) — fast-path layer.
///   Rejects duplicates before any DB round-trip; saves the
///   wasted INSERT attempt and gives a cleaner stack trace on
///   the second-click case.</description></item>
///   <item><description><b>UI</b> — per-row "Mapping…" state on
///   the SPA. UX clarity, not a guarantee.</description></item>
/// </list>
///
/// <para>Each layer is independent: the DB layer is sufficient for
/// correctness on its own; this layer optimizes single-process
/// behavior. Project memory: feedback_server_side_concurrency.</para>
/// </summary>
/// <remarks>
/// <para>Singleton. Keyed <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of <see cref="SemaphoreSlim"/> per connectionId. Semaphores are
/// never evicted — at personal-use scale (≤25 connections per
/// ledger × ≤handful of ledgers per deployment) the memory cost
/// is negligible and eviction would introduce a separate
/// race ("the semaphore I just released got removed before I
/// could use it"). Revisit if connection counts ever go into the
/// thousands.</para>
///
/// <para>The lock is non-blocking. <see cref="TryAcquire"/> returns
/// <c>null</c> when another caller already holds the lock for that
/// connection — the caller should map that to
/// <see cref="SimpleFinSyncService.FailureReason.SyncInProgress"/>
/// just like the DB-layer unique-violation. Acquired locks
/// release on dispose; the typical caller pattern is
/// <c>using var _ = _lock.TryAcquire(id);</c>.</para>
/// </remarks>
public sealed class SyncConnectionLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Try to acquire the per-connection lock without waiting.
    /// Returns a disposable handle on success; <c>null</c> when
    /// another caller currently holds the lock. Never blocks.
    /// </summary>
    public Handle? TryAcquire(Guid connectionId)
    {
        var sem = _locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        if (!sem.Wait(0)) return null;
        return new Handle(sem);
    }

    /// <summary>
    /// RAII-style release. Dispose-time is when the lock is
    /// returned to the pool — same single semaphore is reused
    /// across all future acquires for the same connection.
    /// </summary>
    public readonly struct Handle : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        internal Handle(SemaphoreSlim sem) { _sem = sem; }
        public void Dispose() => _sem.Release();
    }
}
