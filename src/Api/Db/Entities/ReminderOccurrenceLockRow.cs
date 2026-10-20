namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>reminder_occurrence_lock</c> function (migration 218).
/// </summary>
/// <remarks>
/// <para>
/// The returned value carries no information — it is always <c>true</c> — and callers
/// discard it. The POINT OF THE CALL IS THE SIDE EFFECT: a transaction-scoped advisory
/// lock on one (reminder series, occurrence date) slot, held until the caller's
/// transaction ends. The row exists only because EF binds set-returning functions, the
/// same shape <see cref="RecomputeBalancesForAccountRow"/> uses for the void recompute
/// wrappers.
/// </para>
/// <para>
/// So the lock is taken by ENUMERATING the query, not by building it. A caller that
/// composes the <c>IQueryable</c> and never awaits it has locked nothing.
/// </para>
/// </remarks>
internal sealed class ReminderOccurrenceLockRow
{
    public bool Locked { get; init; }
}
