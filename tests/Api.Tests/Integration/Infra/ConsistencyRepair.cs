using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// A projection's repair as the repair endpoint answers it, without the endpoint's record:
/// the rewrite, then the whole ledger checked once it has committed.
/// </summary>
internal static class ConsistencyRepair
{
    /// <summary>
    /// Rewrite <paramref name="projection"/>, then check every projection again, for a test of
    /// what a repair rewrites and what it leaves.
    /// </summary>
    /// <param name="repairedByUserId">Who repairs. Only the reminder auto-post repair records
    /// anyone, as the job's configuring user, so a test of it passes a real user; every other
    /// repair ignores it.</param>
    public static async Task<ProjectionRepairResult> RepairAsync(
        this LedgerConsistencyRepository consistency,
        Guid ledgerId,
        string projection,
        DateOnly? today = null,
        Guid repairedByUserId = default,
        CancellationToken cancellationToken = default)
    {
        var repaired = await consistency.RewriteAsync(ledgerId, projection, repairedByUserId, cancellationToken)
            .ConfigureAwait(false);
        var after = await consistency.CheckAsync(ledgerId, today, cancellationToken).ConfigureAwait(false);
        return new ProjectionRepairResult(projection, repaired, after);
    }
}
