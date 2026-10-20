namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_account_balances</c> (ADR-0034 / mig 089).
/// One row per (header, account) carries the running balance on that
/// account after the header is applied. Maintained by the header-walk
/// trigger family (mig 090); the API treats it as read-only.
/// </summary>
internal sealed class TxnHeaderAccountBalanceRow
{
    public Guid HeaderId { get; init; }
    public Guid AccountId { get; init; }
    public Guid LedgerId { get; init; }
    public decimal BalanceAfter { get; init; }
    /// <summary>
    /// Net cash effect of this header on this account (mig 098). Sum of
    /// leg amounts on this account; the per-step delta that
    /// <see cref="BalanceAfter"/> accumulates over canonical
    /// <c>(posted_at, seq)</c> order.
    /// </summary>
    public decimal NetAmount { get; init; }
}
