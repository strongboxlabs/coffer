namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>recompute_posting_counts_for_header</c>
/// TVF wrapper (migration 120). The wrapper calls
/// <c>fn_recompute_posting_counts_for_header</c> (void) for the supplied
/// header and returns the input header id so EF Core's
/// <c>HasDbFunction</c> binding has a typed shape to project; callers
/// discard the value.
/// </summary>
/// <remarks>
/// The point of the call is the side effect on <c>txn_legs</c> — every
/// leg of the header gets its denormalized
/// <c>account_postings_on_header</c> + <c>header_total_postings</c>
/// re-derived from the current legs. ADR-0034 / ADR-0032: recompute is
/// invoked at API write call sites (folded into the same interceptor as
/// balances), never via triggers.
/// </remarks>
internal sealed class RecomputePostingCountsForHeaderRow
{
    public Guid HeaderId { get; init; }
}
