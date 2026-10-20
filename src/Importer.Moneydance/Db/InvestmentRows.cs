namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>holdings</c>. Each
/// <c>(account_id, security_id)</c> pair has at most one row; the importer
/// aggregates all per-(account, security) deltas across investment
/// transactions and upserts the totals.
/// </summary>
/// <remarks>
/// Under the symmetric-posting model (ADR-0019), <see cref="AccountId"/>
/// is the brokerage's user-visible cash account — not its system
/// Holdings sibling. The Holdings sibling exists to host the holdings-
/// side transaction rows; the rolled-up holdings record stays attached
/// to the account the user thinks of as "their brokerage."
/// </remarks>
public sealed record HoldingRow(
    Guid Id,
    Guid LedgerId,
    Guid AccountId,
    Guid SecurityId,
    decimal Quantity,
    decimal CostBasis,
    DateTimeOffset AsOf);

/// <summary>
/// Persistable shape of a row in <c>lots</c>. One lot per buy and per
/// dividend-reinvest (each represents a discrete acquisition at a known
/// unit cost on a known date). Lot-closing on sells is deferred per
/// ADR-0018 rule 4; <see cref="IsClosed"/> stays <c>false</c> in the
/// initial import. <see cref="LegId"/> points at the holdings-side
/// <c>txn_legs</c> row of the buy/divr pair (ADR-0022 Phase 2 retarget
/// from <c>transactions</c>).
/// </summary>
public sealed record LotRow(
    Guid Id,
    Guid LedgerId,
    Guid HoldingId,
    Guid LegId,
    decimal Quantity,
    decimal UnitCost,
    DateTimeOffset AcquiredAt,
    bool IsClosed);
