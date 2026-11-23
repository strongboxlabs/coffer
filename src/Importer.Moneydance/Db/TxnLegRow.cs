namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of one row in <c>txn_legs</c> (ADR-0022). Two legs
/// per posting (one on each account), N postings per multi-split
/// header. <see cref="PostingIndex"/> pairs the two legs of one posting
/// (same value within the header, different account_id). <see cref="Amount"/>
/// is the impact on <see cref="AccountId"/>, in that account's currency;
/// the two legs of a posting in one currency, or with a category side, sum
/// to zero. A posting between two real accounts in different currencies is a
/// two-amount pair: each leg its own account's figure, nonzero and opposite
/// in sign (ADR-0114 D13).
/// </summary>
/// <remarks>
/// Investment metadata (<see cref="SecurityId"/>, <see cref="Quantity"/>,
/// <see cref="UnitPrice"/>) is per-leg — the holdings-side leg carries
/// shares, the cash-side leg carries dollars. NULL on legs where it
/// doesn't apply.
///
/// Two redundancies have been removed from this row over time:
///   - Commission column (ADR-0018 Rule 3) dropped in migration 046 —
///     the fee leg under ADR-0019 Rule 5 is the source of truth.
///   - InvestmentAction column dropped in migration 047 — action is a
///     header-level property (one action per event); see TxnHeaderRow.
/// </remarks>
public sealed record TxnLegRow(
    Guid Id,
    Guid HeaderId,
    Guid LedgerId,
    Guid AccountId,
    int PostingIndex,
    string? LegMemo,
    decimal Amount,
    Guid? SecurityId,
    decimal? Quantity,
    decimal? UnitPrice,
    // Migration 056: investment posting role marker. One of
    // 'security', 'income', 'transfer', 'fee'; NULL on non-investment
    // legs (bank/credit-card transactions, apart from the brokerage cash
    // move below). Set
    // by the investment mapper from MD's `invest.splittype`, and to
    // 'transfer' by the bank route for a one-posting brokerage cash move
    // (BrokerageCashTransfer); both legs of a posting share the same value.
    string? PostingRole = null);
