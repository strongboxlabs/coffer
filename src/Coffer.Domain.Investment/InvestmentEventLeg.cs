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
/// account being read): a solo Buy is one leg; a Buy+Fee is two; a 3-leg DividendXfr+Fee
/// is three. Per ADR-0028 the projector collapses them to one event row.
/// <see cref="SecurityId"/>/<see cref="Quantity"/>/<see cref="UnitPrice"/> are on
/// the holdings-side leg of a <c>security</c> posting; for Dividend/DividendXfr/Misc the
/// security id rides on the qty=0 cash leg (<see cref="Quantity"/> null there).
/// <para><see cref="CounterpartyAmount"/> and <see cref="CounterpartyCurrencyCode"/> are
/// the posting's other leg as stored, on <see cref="CounterpartyAccountId"/>, in that
/// account's currency: on a transfer between currencies a figure of its own, not this
/// leg's negated (ADR-0114 D13). The projector carries them to the slot they fill; the
/// caller, which knows this account's currency, decides whether they show.</para>
/// </remarks>
public sealed record InvestmentEventLeg(
    Guid Id,
    int LegIndex,
    decimal Amount,
    decimal? BalanceAfter,
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
    string? CounterpartyAccountType,
    decimal? CounterpartyAmount = null,
    string? CounterpartyCurrencyCode = null);

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
    /// Every reinvestment is in that class, so on any brokerage that
    /// reinvests it is routine, not an edge case.
    ///
    /// Both numbers are real and neither replaces the other, so both are
    /// projected and the surface decides. Named "settled" rather than "gross"
    /// because ADR-0073 already calls it that; a third word for one number is
    /// how the two ADRs came to disagree in the first place.
    /// </remarks>
    decimal? SettledAmount,
    /// <summary>The other leg of the posting that fills the counterparty chip, in its
    /// account's currency (<see cref="InvestmentEventLeg.CounterpartyAmount"/>).</summary>
    decimal? CounterpartyAmount = null,
    /// <summary>The currency <see cref="CounterpartyAmount"/> is in.</summary>
    string? CounterpartyCurrencyCode = null,
    /// <summary>The transfer slot's other leg, on the transfer account, in its currency
    /// (ADR-0114 D14, ADR-0028 Slot 6).</summary>
    decimal? TransferAmount = null,
    /// <summary>The currency <see cref="TransferAmount"/> is in.</summary>
    string? TransferCurrencyCode = null);
