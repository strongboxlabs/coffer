namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>recompute_balances_for_account</c>
/// TVF wrapper (migration 102). The wrapper calls
/// <c>fn_recompute_balances_for_account</c> (void) for the supplied
/// account + anchor and returns the input account id so EF Core's
/// <c>HasDbFunction</c> binding has a typed shape to project; callers
/// discard the value.
/// </summary>
/// <remarks>
/// The point of the call is the side effect on
/// <c>txn_header_account_balances</c> — every per-(header, account)
/// balance row from the anchor's posted_at forward is wiped and
/// rebuilt from current <c>txn_legs</c> in canonical
/// <c>(posted_at, seq)</c> order. ADR-0034 / ADR-0032: recompute is
/// invoked at API write call sites, never via triggers.
/// </remarks>
internal sealed class RecomputeBalancesForAccountRow
{
    public Guid AccountId { get; init; }
}
