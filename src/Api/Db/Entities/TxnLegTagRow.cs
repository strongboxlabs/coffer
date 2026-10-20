namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_leg_tags</c> (ADR-0103, migration 243). A tag on ONE
/// posting.
/// </summary>
/// <remarks>
/// <para>Distinct from <see cref="TxnHeaderTagRow"/>, and neither is derived
/// from the other. A header tag is a claim about the transaction — <i>Holiday</i>,
/// <i>Tax 2026</i>; a leg tag is a claim about one posting — <i>Property B</i>.
/// A rental payment split across three units is the case that reopened
/// ADR-0009, which had rejected split-level tags "unless real use surfaces a
/// split-level tag pattern that matters".</para>
///
/// <para>The union model ADR-0009 named as its alternative was not taken: it
/// would make a transaction tag a derived artifact that changes silently when a
/// leg is added later, and would require rewriting every header tag that is
/// correct today.</para>
/// </remarks>
internal sealed class TxnLegTagRow
{
    public Guid LegId { get; init; }
    public Guid TagId { get; init; }

    /// <summary>
    /// Denormalized from <c>txn_legs.ledger_id</c> + <c>tags.ledger_id</c>,
    /// following migration 072. Composite FKs lock both parents to this ledger,
    /// which is what makes the flat RLS predicate on this column a consequence
    /// rather than an assertion — and it is required outright for snapshot
    /// capture, which chunks on <c>ledger_id</c>.
    /// </summary>
    public Guid LedgerId { get; init; }

    public DateTime CreatedAt { get; init; }
}
