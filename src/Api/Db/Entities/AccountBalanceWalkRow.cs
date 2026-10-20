namespace Coffer.Api.Db.Entities;

/// <summary>
/// One header's running balance as the PURE walk computes it (migration 206) —
/// what <c>txn_header_account_balances</c> would hold if it were rebuilt now.
/// </summary>
/// <remarks>
/// Keyless: the function writes nothing, so there is no row identity to track.
/// This exists so a consistency check can ASK what a balance should be without
/// overwriting it, which was impossible while the only implementation of the
/// rules lived inside the recompute's DELETE + INSERT.
/// </remarks>
internal sealed class AccountBalanceWalkRow
{
    public Guid HeaderId { get; init; }
    public DateTime PostedAt { get; init; }
    public long Seq { get; init; }
    public decimal NetAmount { get; init; }
    public decimal BalanceAfter { get; init; }
}
