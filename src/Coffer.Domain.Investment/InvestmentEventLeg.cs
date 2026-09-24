namespace Coffer.Domain.Investment;

/// <summary>
/// One resolved leg of an investment header as a read path sees it (post-override,
/// from <c>resolved_transactions</c>). The input shape for
/// <see cref="InvestmentEventProjector.ProjectEvent"/>; callers (register read,
/// MCP) map their own row/DTO type onto this persistence-agnostic record so the
/// projector stays free of API-contract and EF dependencies (ADR-0080).
/// </summary>
/// <remarks>
/// A single investment event is 1..N of these (one per leg of the header on the
/// account being read): a solo Buy is one leg; a Buy+Fee is two; a 3-leg DivXfr+Fee
/// is three. Per ADR-0028 the projector collapses them to one event row.
/// <see cref="SecurityId"/>/<see cref="Quantity"/>/<see cref="UnitPrice"/> are on
/// the holdings-side leg of a <c>security</c> posting; for Div/DivXfr/Misc the
/// security id rides on the qty=0 cash leg (<see cref="Quantity"/> null there).
/// </remarks>
public sealed record InvestmentEventLeg(
    Guid Id,
    int LegIndex,
    decimal Amount,
    decimal? BalanceAfter,
    bool HasOverrides,
    string? PostingRole,
    string? DerivedAction,
    Guid CounterpartyId,
    Guid? SecurityId,
    string? SecurityTicker,
    string? SecurityName,
    decimal? Quantity,
    decimal? UnitPrice,
    Guid? CounterpartyAccountId,
    string? CounterpartyAccountName,
    string? CounterpartyAccountType);

/// <summary>
/// The derived fields of one aggregated investment event (ADR-0028 §2): the cash
/// sum, running balance, security identity + qty@price, and the category /
/// transfer / fee slots. <see cref="InvestmentEventProjector.ProjectEvent"/>
/// computes ONLY these; the caller overlays them on the canonical leg's row
/// (register) or maps them to its own event DTO (MCP), so this record never
/// carries the ~40 universal register columns.
/// </summary>
public sealed record InvestmentEventProjection(
    decimal Amount,
    decimal? BalanceAfter,
    bool HasOverrides,
    Guid CounterpartyId,
    Guid? SecurityId,
    string? SecurityTicker,
    string? SecurityName,
    decimal? Quantity,
    decimal? UnitPrice,
    Guid? CounterpartyAccountId,
    string? CounterpartyAccountName,
    string? CounterpartyAccountType,
    Guid? CategoryAccountId,
    string? CategoryAccountName,
    string? CategoryAccountType,
    Guid? TransferAccountId,
    string? TransferAccountName,
    string? TransferAccountType,
    decimal? FeeAmount,
    Guid? FeeCategoryId,
    string? FeeCategoryName,
    /// <summary>
    /// The event's SETTLED money — ADR-0073 D1's "Amount", which is the trade
    /// value the caller sent and the legs store directly. Null when the event
    /// has no leg that carries one.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Amount"/>, and the distinction is the point.
    /// <see cref="Amount"/> is ADR-0028's register figure: the SUM of this
    /// account's legs, i.e. the net cash the event moved in or out of the
    /// sleeve. For a cash-neutral event — a reinvestment, an in-kind transfer,
    /// a sale whose proceeds a fee consumes — that sum is structurally ZERO,
    /// and a column of zeroes is a true answer to a question nobody asked.
    /// 38.8% of this repo's dev brokerage entries are in that class.
    ///
    /// Both numbers are real and neither replaces the other, so both are
    /// projected and the surface decides. Named "settled" rather than "gross"
    /// because ADR-0073 already calls it that; a third word for one number is
    /// how the two ADRs came to disagree in the first place.
    /// </remarks>
    decimal? SettledAmount);
