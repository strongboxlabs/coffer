namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type of the row-lock functions of migrations 264 and 274
/// (<c>fn_lock_txn_headers</c>, <c>fn_lock_tags</c>, <c>fn_lock_accounts</c>,
/// <c>fn_lock_trade_price_accounts</c>, <c>fn_lock_securities</c>): how many of the given
/// rows it locked.
/// </summary>
/// <remarks>The point of the call is the side effect, row locks taken in id order and held
/// until the caller's transaction ends. The lock is taken by enumerating the query, not by
/// building it.</remarks>
internal sealed class TxnHeaderLockRow
{
    public int Locked { get; init; }
}
