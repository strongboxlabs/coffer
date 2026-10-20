namespace Coffer.Domain.Investment;

/// <summary>
/// Persistence-agnostic shape of one leg of an investment posting.
/// Callers (importer, API) translate to their own row/entity type
/// (Dapper DTO vs EF entity) — this record carries only the fields
/// the domain rules produce.
/// </summary>
/// <remarks>
/// Each posting is two legs (cash side + counterparty side) sharing
/// a <see cref="PostingIndex"/> within a header. <see cref="Amount"/>
/// is the cash impact on <see cref="AccountId"/>; the two legs of one
/// posting sum to zero (or both are zero on the legitimate
/// one-sided shapes MD emits — see ADR-0019).
/// <para>
/// <see cref="SecurityId"/> + <see cref="Quantity"/> + <see cref="UnitPrice"/>
/// are populated on the holdings-side leg of a <c>sec</c> posting,
/// and on the cash-side leg of an <c>inc</c>/<c>exp</c>/<c>fee</c>
/// pair when MD pinned a security_id link (the per-security register
/// query joins through these).
/// </para>
/// <para>
/// <see cref="HeaderId"/> + <see cref="PostingIndex"/> are assigned
/// by the caller after the full pair list is built — the builders
/// here return specs without those values; callers attach when they
/// have the resolved header id and the final posting-index sequence.
/// </para>
/// </remarks>
public sealed record InvestmentLegSpec(
    Guid AccountId,
    decimal Amount,
    string PostingRole,
    Guid? SecurityId = null,
    decimal? Quantity = null,
    decimal? UnitPrice = null,
    string? LegMemo = null);

/// <summary>
/// A posting — two paired legs (brokerage cash side + counterparty
/// side) — emitted by one of the <c>Build*Pair</c> helpers. Both
/// legs share the same <c>PostingRole</c> and (post-assignment) the
/// same <c>PostingIndex</c>.
/// </summary>
public sealed record InvestmentPosting(
    InvestmentLegSpec Cash,
    InvestmentLegSpec Counterparty);
