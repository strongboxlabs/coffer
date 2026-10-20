namespace Coffer.Domain.Investment;

/// <summary>
/// Side-effect of an investment txn on the per-security position
/// (<c>holdings</c>) and lot ledger (<c>lots</c>). Returned by
/// <see cref="InvestmentPostings.BuildHoldingsImpact"/> for actions
/// that touch holdings (<see cref="LedgerActions.TouchesHoldings"/>);
/// <c>null</c> otherwise.
/// </summary>
/// <remarks>
/// Cost-basis policy at import / save time: include commission per
/// IRS convention on every share-acquiring action (buy / buyx /
/// dividend_reinvest). The recompute function
/// (<c>fn_recompute_holdings_cost_basis</c>, migration 056) ALWAYS
/// re-derives lot <c>unit_cost</c> based on the brokerage's
/// <c>is_trade_commission</c> flag — so the value written here is
/// a placeholder that recompute either preserves (flag=TRUE) or
/// strips commission from (flag=FALSE).
/// </remarks>
public sealed record InvestmentHoldingsImpact(
    Guid HoldingsAccountId,
    Guid SecurityId,
    decimal QuantityDelta,
    decimal CostBasisDelta,
    DateTimeOffset AsOf,
    InvestmentLotSpec? NewLot);

/// <summary>
/// New lot proposed by a share-acquiring action. The caller resolves
/// <c>HoldingId</c> after the parent <c>holdings</c> row is upserted,
/// and rebinds <c>LegSpecKey</c> to the persisted leg id.
/// </summary>
/// <remarks>
/// <see cref="LegSpecKey"/> is a stable identifier the caller assigns
/// to the holdings-side leg of the acquiring sec pair before insert
/// — typically a fresh GUID that the caller also stamps onto the
/// resulting leg row. The shared layer doesn't materialize legs, so
/// it doesn't allocate this; the caller passes it in and gets it
/// back in this lot spec for later rebinding.
/// </remarks>
public sealed record InvestmentLotSpec(
    decimal Quantity,
    decimal UnitCost,
    DateTimeOffset AcquiredAt);
