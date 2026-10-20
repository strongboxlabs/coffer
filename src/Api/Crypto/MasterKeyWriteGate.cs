using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Coffer.Api.Crypto;

/// <summary>
/// Keeps anything from being sealed under the master key while that key is being rotated,
/// or after it has been and before the restart that loads its successor (FU-078).
/// </summary>
/// <remarks>
/// <para>
/// Rotation re-wraps every master-sealed value in one transaction, then the process
/// restarts onto the new key. A value sealed by a concurrent request lands under the OLD
/// key: during the re-wrap it is either overwritten or written over the re-wrap, and after
/// the commit it is written with a key the restarted process no longer has. Either way it
/// is stranded — and because the next rotation's dry run must open everything, it also
/// blocks every rotation after it. Nothing healed it: reconciliation runs only after a restore.
/// </para>
/// <para>
/// So every path that seals under the master key holds a <see cref="Lease"/> from the seal
/// to the save of what it sealed (<see cref="Enter"/>); a source test fails any new sealing
/// path that does not. Rotation closes the gate, waits for the leases in flight to finish,
/// and once it commits keeps it closed for the rest of the process's life. A request that
/// arrives meanwhile gets <see cref="MasterKeyRotatingException"/>, answered as 503 with a
/// Retry-After: the restart is seconds away.
/// </para>
/// </remarks>
public sealed class MasterKeyWriteGate
{
    private readonly object _sync = new();
    private int _inFlight;
    private bool _closed;
    private bool _retired;

    /// <summary>Take a lease for one seal-and-save. Dispose it once the sealed value is
    /// saved (or abandoned).</summary>
    /// <exception cref="MasterKeyRotatingException">A rotation is in progress or has just
    /// committed; the process is about to restart.</exception>
    public Lease Enter()
    {
        lock (_sync)
        {
            if (_closed || _retired) throw new MasterKeyRotatingException();
            _inFlight++;
            return new Lease(this);
        }
    }

    /// <summary>
    /// Refuse new leases and wait for those in flight to finish. Returns false if they had
    /// not finished within <paramref name="timeout"/>; the gate is then reopened and the
    /// rotation should not proceed.
    /// </summary>
    public async Task<bool> CloseAndDrainAsync(TimeSpan timeout, TimeProvider clock, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_retired) return false;
            _closed = true;
        }
        var started = clock.GetTimestamp();
        while (true)
        {
            lock (_sync)
            {
                if (_inFlight == 0) return true;
            }
            if (clock.GetElapsedTime(started) >= timeout)
            {
                Reopen();
                return false;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The rotation did not commit: sealing may resume under the current key.</summary>
    public void Reopen()
    {
        lock (_sync) _closed = false;
    }

    /// <summary>The rotation committed: nothing may be sealed under the key this process
    /// holds, ever again. Only a restart (which loads the new key) clears it.</summary>
    public void Retire()
    {
        lock (_sync)
        {
            _retired = true;
            _closed = true;
        }
    }

    /// <summary>True while leases are refused.</summary>
    public bool IsClosed
    {
        get { lock (_sync) return _closed || _retired; }
    }

    private void Release()
    {
        lock (_sync)
        {
            Debug.Assert(_inFlight > 0);
            _inFlight--;
        }
    }

    /// <summary>One seal-and-save. Idempotent dispose.</summary>
    public sealed class Lease : IDisposable
    {
        private MasterKeyWriteGate? _gate;
        internal Lease(MasterKeyWriteGate gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>A master-key rotation is in progress or has just committed; the server restarts
/// onto the new key in a moment. Answered as 503 with Retry-After.</summary>
public sealed class MasterKeyRotatingException : Exception
{
    public MasterKeyRotatingException()
        : base("The master key is being rotated and the server restarts onto the new one in a "
               + "moment. Nothing was saved; try again shortly.") { }
}

/// <summary>Answers <see cref="MasterKeyRotatingException"/> as 503 Service Unavailable with
/// Retry-After, rather than as the 500 an unhandled exception would be.</summary>
public sealed class MasterKeyRotatingExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not MasterKeyRotatingException) return false;
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "10";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Master key rotation in progress",
                Detail = exception.Message,
                Extensions = { ["code"] = "master-key-rotating" },
            },
            Exception = exception,
        }).ConfigureAwait(false);
    }
}
