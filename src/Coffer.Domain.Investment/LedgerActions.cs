namespace Coffer.Domain.Investment;

/// <summary>
/// The Ledger investment-header actions, per
/// <see href="../../docs/decisions/0027-investment-action-catalog.md">ADR-0027</see>
/// (the original 9) plus <c>transfer_shares</c>
/// (<see href="../../docs/decisions/0065-transfer-shares-in-kind.md">ADR-0065</see>).
/// String constants (not an enum) because the DB CHECK on
/// <c>txn_headers.action</c> matches by string value.
/// </summary>
public static class LedgerActions
{
    public const string Buy              = "buy";
    public const string BuyXfr           = "buyx";
    public const string Sell             = "sell";
    public const string SellXfr          = "sellx";
    public const string DividendCash     = "dividend_cash";
    public const string DividendReinvest = "dividend_reinvest";
    public const string DivXfr           = "divx";
    public const string Transfer         = "transfer";
    public const string Misc             = "misc";

    /// <summary>
    /// In-kind share transfer between two holdings accounts (ADR-0065).
    /// Moves FIFO lots + cost basis source → destination with zero
    /// realized gain. Handled on a dedicated path (not the generic
    /// single-lot acquire/dispose flow): it is therefore deliberately
    /// excluded from <see cref="TouchesHoldings"/> /
    /// <see cref="AcquiresShares"/> / <see cref="DisposesShares"/>.
    /// </summary>
    public const string TransferShares   = "transfer_shares";

    /// <summary>
    /// All actions that may carry a holdings-impact (acquire or
    /// dispose shares). Convenience for callers that need to gate
    /// holdings-delta + lot creation.
    /// </summary>
    public static bool TouchesHoldings(string action) => action is
        Buy or BuyXfr or Sell or SellXfr or DividendReinvest;

    /// <summary>
    /// Actions that acquire shares (positive quantity delta + lot
    /// creation). Cost basis includes apportioned commission per IRS
    /// convention; the brokerage's <c>is_trade_commission</c> flag
    /// drives whether the recompute function preserves that.
    /// </summary>
    public static bool AcquiresShares(string action) => action is
        Buy or BuyXfr or DividendReinvest;

    /// <summary>
    /// Actions that dispose shares (negative quantity delta). Lot
    /// consumption (FIFO) is run by the recompute function on save,
    /// not by the editor.
    /// </summary>
    public static bool DisposesShares(string action) => action is
        Sell or SellXfr;
}
