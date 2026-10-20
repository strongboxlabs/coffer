namespace Coffer.Domain.Investment;

/// <summary>
/// The four <c>txn_legs.posting_role</c> values that appear on
/// investment legs, per <see cref="docs/decisions/0027-investment-action-catalog.md">ADR-0027</see>.
/// Non-investment legs carry <c>NULL</c>. The mapping
/// <c>invest.splittype</c> → <c>posting_role</c> is:
/// <list type="bullet">
///   <item><description><c>sec</c> → <see cref="Security"/></description></item>
///   <item><description><c>inc</c> → <see cref="Income"/></description></item>
///   <item><description><c>exp</c> → <see cref="Income"/> (sign on amount discriminates direction)</description></item>
///   <item><description><c>fee</c> → <see cref="Fee"/></description></item>
///   <item><description><c>xfr</c> → <see cref="Transfer"/></description></item>
/// </list>
/// String constants (not an enum) because the DB column is TEXT and
/// the migration-056 CHECK constraint matches by string value.
/// </summary>
public static class PostingRoles
{
    public const string Security = "security";
    public const string Income   = "income";
    public const string Transfer = "transfer";
    public const string Fee      = "fee";
}
