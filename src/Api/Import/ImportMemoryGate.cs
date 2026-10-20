namespace Coffer.Api.Import;

/// <summary>
/// One parsed Moneydance export in memory at a time, across every user (FU-092). Singleton.
/// </summary>
/// <remarks>
/// A parsed export holds about 6.5 times its file size (measured on a large real export) until
/// its preview returns or its import finishes. Nothing bounded how many were held at once:
/// the one-import-per-user rule ran only after the parse, so two users, or two previews,
/// each parsed in full, and any user could take the process out of memory for everyone.
/// The slot is taken before the parse and released when the document is: at the end of a
/// preview, or by the background import that took ownership of it. A second upload while
/// the slot is taken is refused at once, before it is read, and can be retried.
/// </remarks>
public sealed class ImportMemoryGate
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The slot, or null when another export holds it. Dispose to release.</summary>
    public IDisposable? TryEnter() => _one.Wait(0) ? new Slot(_one) : null;

    /// <summary>True while an export holds the slot.</summary>
    public bool IsHeld => _one.CurrentCount == 0;

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Slot(SemaphoreSlim semaphore) => _semaphore = semaphore;

        // Idempotent: a slot passed to the runner may be disposed by more than one path.
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
