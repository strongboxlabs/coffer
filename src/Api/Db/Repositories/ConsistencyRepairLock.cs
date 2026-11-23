using System.Collections.Concurrent;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// One consistency repair per ledger at a time: a repair sent while another repair of the
/// same ledger runs is refused before it rewrites anything.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A repair that has begun runs to the end whatever happens to its
/// client, so a second can arrive while the first runs: from another tab, or pressed after
/// an answer that never arrived. Every piece of a rewrite takes the ledger's write lock
/// before it reads (migration 275), so a second would take turns with the first piece by
/// piece and rewrite what the first had just rewritten. The rule is per ledger rather than
/// per writer, one rule for every projection: refusing an overlap that would only have
/// waited costs a second press once the first repair has answered.</para>
/// <para><b>How.</b> The mechanism of <see cref="Coffer.Api.Sync.SimpleFin.SyncConnectionLock"/>,
/// which refuses a second sync of a connection: a singleton holding one semaphore per
/// ledger, taken without waiting and never evicted (a deployment holds a handful of
/// ledgers). One API process serves an install, as the scheduler also assumes, so every
/// repair of a ledger passes through it.</para>
/// </remarks>
public sealed class ConsistencyRepairLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Take the ledger's repair lock without waiting. A handle that releases it on dispose,
    /// or null when another repair of the ledger holds it.
    /// </summary>
    public IDisposable? TryAcquire(Guid ledgerId)
    {
        var semaphore = _locks.GetOrAdd(ledgerId, _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0) ? new Handle(semaphore) : null;
    }

    /// <summary>Releases once, however often it is disposed.</summary>
    private sealed class Handle(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
