using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Coffer.Api.Db;

/// <summary>
/// The ledger write lock (migration 275): one lock per ledger, which every transaction that
/// writes to a ledger and can recompute its balances or holdings takes before it writes or
/// locks anything, so two of them over one ledger take turns.
/// </summary>
/// <remarks>
/// <para><see cref="BeginLedgerWriteAsync"/> is the transaction form: it opens a READ
/// COMMITTED transaction when none is open and takes the lock as that transaction's first
/// statement. <see cref="HoldLedgerWriteSessionAsync"/> is the session form, for a writer
/// whose own transaction is REPEATABLE READ (snapshot restore): taken before that
/// transaction begins and released after it ends.</para>
/// <para>The database refuses the lock in a transaction that has already written or
/// row-locked, at any isolation level but READ COMMITTED, and while the session holds
/// another advisory lock (the reminder slot lock among them) other than the ledger's
/// snapshot-create lock; those refusals are faults in the caller and surface as 500s. It
/// does nothing when the transaction or the session already holds it.</para>
/// <para>A wait longer than half the context's command timeout is refused with SQLSTATE
/// <see cref="BusySqlState"/>, raised here as <see cref="LedgerWriteBusyException"/> and
/// answered as a 422 with <see cref="Errors.BusinessError.Codes.LedgerWriteBusy"/>
/// (<see cref="Errors.LedgerWriteBusyExceptionHandler"/>): half, so the wait is refused
/// before the command timeout cancels the statement, and a statement that takes the lock
/// and then rewrites keeps the other half for the rewrite.</para>
/// </remarks>
public static class LedgerWriteLock
{
    /// <summary>The SQLSTATE <c>fn_ledger_write_lock</c> and
    /// <c>fn_ledger_write_session_lock</c> raise when the wait runs out.</summary>
    public const string BusySqlState = "LW001";

    /// <summary>What a person reads when a write is refused because another write to the
    /// same ledger held the lock past the wait.</summary>
    public const string BusyDetail =
        "Another change to this ledger is still being saved. Nothing was changed; try again in a moment.";

    /// <summary>What a person reads when a consistency repair is refused this way. A repair
    /// takes the lock one piece at a time and keeps every piece it committed before the
    /// refusal, so it cannot say that nothing was changed.</summary>
    public const string RepairBusyDetail =
        "Another change to this ledger is still being saved, so the repair stopped. "
        + "Anything it had already repaired stays repaired; repair again to finish.";

    /// <summary>The functions' own default wait, used when the context's command timeout is
    /// unbounded: half of Npgsql's default 30-second command timeout.</summary>
    internal const int DefaultWaitMilliseconds = 15_000;

    /// <summary>
    /// Take <paramref name="ledgerId"/>'s write lock as the first statement of a transaction
    /// on <paramref name="db"/>, opening a READ COMMITTED one when none is open.
    /// </summary>
    /// <returns>A scope whose <see cref="LedgerWriteScope.CommitAsync"/> commits, and whose
    /// disposal rolls back, only the transaction this call opened; inside a caller's
    /// transaction both leave that transaction to the caller.</returns>
    /// <exception cref="LedgerWriteBusyException">Another transaction held the lock past
    /// the wait. Nothing was written; a transaction this call opened is rolled back.</exception>
    public static async Task<LedgerWriteScope> BeginLedgerWriteAsync(
        this AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IDbContextTransaction? owned = null;
        if (db.Database.CurrentTransaction is null)
        {
            owned = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
        }
        try
        {
            _ = await db.LedgerWriteLockFn(ledgerId, WaitMilliseconds(db))
                .Select(r => r.Locked)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            if (IsBusy(e)) throw new LedgerWriteBusyException(ledgerId, e);
            throw;
        }
        return new LedgerWriteScope(owned);
    }

    /// <summary>
    /// Hold <paramref name="ledgerId"/>'s write lock on <paramref name="db"/>'s session
    /// until the returned handle is disposed, keeping the connection open across both.
    /// Call it before the transaction it guards begins, and dispose it after that
    /// transaction ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">A transaction is already open on
    /// <paramref name="db"/>.</exception>
    /// <exception cref="LedgerWriteBusyException">Another transaction held the lock past
    /// the wait. Nothing is held.</exception>
    public static async Task<LedgerWriteSession> HoldLedgerWriteSessionAsync(
        this AppDbContext db, Guid ledgerId, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "The session form of the ledger write lock is taken before the transaction it guards begins.");

        var opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await db.LedgerWriteSessionLockFn(ledgerId, WaitMilliseconds(db))
                .Select(r => r.Locked)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (opened) await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            if (IsBusy(e)) throw new LedgerWriteBusyException(ledgerId, e);
            throw;
        }
        return new LedgerWriteSession(db, ledgerId, opened, logger);
    }

    /// <summary>Whether <paramref name="exception"/>, or one it wraps, is the ledger write
    /// lock's busy refusal: <see cref="LedgerWriteBusyException"/>, or SQLSTATE
    /// <see cref="BusySqlState"/> from a statement that took the lock itself.</summary>
    public static bool IsBusy(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is LedgerWriteBusyException or PostgresException { SqlState: BusySqlState })
                return true;
        }
        return false;
    }

    /// <summary>How long the lock waits on <paramref name="db"/>: half of its command
    /// timeout, or <see cref="DefaultWaitMilliseconds"/> when that is unbounded.</summary>
    internal static int WaitMilliseconds(AppDbContext db)
    {
        var seconds = db.Database.GetCommandTimeout()
            ?? (db.Database.GetDbConnection() as NpgsqlConnection)?.CommandTimeout
            ?? 0;
        return seconds > 0 ? seconds * 500 : DefaultWaitMilliseconds;
    }
}

/// <summary>The transaction form of the ledger write lock, held
/// (<see cref="LedgerWriteLock.BeginLedgerWriteAsync"/>).</summary>
public sealed class LedgerWriteScope : IAsyncDisposable
{
    private IDbContextTransaction? _owned;
    private readonly bool _ownsTransaction;

    internal LedgerWriteScope(IDbContextTransaction? owned)
    {
        _owned = owned;
        _ownsTransaction = owned is not null;
    }

    /// <summary>True when the lock opened the transaction it was taken in.</summary>
    public bool OwnsTransaction => _ownsTransaction;

    /// <summary>Commit the transaction the lock opened, which releases the lock; inside a
    /// caller's transaction, nothing (the lock ends with that transaction).</summary>
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Dispose the transaction the lock opened, rolling it back unless it was
    /// committed; inside a caller's transaction, nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref _owned, null);
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The session form of the ledger write lock, held
/// (<see cref="LedgerWriteLock.HoldLedgerWriteSessionAsync"/>). Disposal releases it and
/// closes the connection the call opened.</summary>
public sealed class LedgerWriteSession : IAsyncDisposable
{
    private AppDbContext? _db;
    private readonly Guid _ledgerId;
    private readonly bool _closeConnection;
    private readonly ILogger _logger;

    internal LedgerWriteSession(AppDbContext db, Guid ledgerId, bool closeConnection, ILogger logger)
    {
        _db = db;
        _ledgerId = ledgerId;
        _closeConnection = closeConnection;
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        var db = Interlocked.Exchange(ref _db, null);
        if (db is null) return;
        try
        {
            var held = await db.LedgerWriteSessionUnlockFn(_ledgerId)
                .Select(r => r.Unlocked)
                .FirstAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (!held)
                _logger.LogWarning("The ledger write lock of ledger {LedgerId} was not held by this session at release.", _ledgerId);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The session is gone, and its lock with it.
            _logger.LogWarning(ex, "Could not release the ledger write lock of ledger {LedgerId}.", _ledgerId);
        }
        finally
        {
            if (_closeConnection) await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Another transaction held a ledger's write lock past the wait
/// (<see cref="LedgerWriteLock"/>). Nothing was written; a retry once that write has
/// committed succeeds. Answered as a 422 with
/// <see cref="Errors.BusinessError.Codes.LedgerWriteBusy"/>.
/// </summary>
public sealed class LedgerWriteBusyException : Exception
{
    public LedgerWriteBusyException(Guid ledgerId, Exception innerException)
        : base(LedgerWriteLock.BusyDetail, innerException)
    {
        LedgerId = ledgerId;
    }

    /// <summary>The ledger whose write lock was held.</summary>
    public Guid LedgerId { get; }
}
