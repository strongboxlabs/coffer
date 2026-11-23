namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>fn_ledger_write_lock</c> and <c>fn_ledger_write_session_lock</c>
/// (migration 275): always true once the ledger write lock is held.
/// </summary>
/// <remarks>The call's point is its side effect, as with
/// <see cref="ReminderOccurrenceLockRow"/>: the lock is taken by enumerating the query,
/// and a query that is composed and never awaited has locked nothing. Use
/// <see cref="LedgerWriteLock"/>, which takes it the way its rules require.</remarks>
internal sealed class LedgerWriteLockRow
{
    public bool Locked { get; init; }
}

/// <summary>Keyless result of <c>fn_ledger_write_session_unlock</c> (migration 275):
/// whether this session held the lock it released.</summary>
internal sealed class LedgerWriteUnlockRow
{
    public bool Unlocked { get; init; }
}
