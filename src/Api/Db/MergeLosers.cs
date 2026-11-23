using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db;

/// <summary>
/// The rows merged into transactions a save is about to remove outright.
/// </summary>
/// <remarks>
/// <para>A merged row points at the row it lost to (<c>is_merged_into</c>), and the
/// database clears that pointer when the winner is deleted (<c>ON DELETE SET NULL</c>,
/// migration 121). The row is then merged into nothing, so it is back in the register and
/// in every walk that leaves a merged row out: the balances of each account it has a line
/// on, and the position of each investment line. The database does that past the change
/// tracker, so a save that deletes a winner recomputes none of it, and the loser's other
/// accounts, such as a feed row's category, keep balances that leave it out.</para>
/// <para>Loaded tracked before the winner is removed, each loser is un-merged by EF's own
/// fixup of the relationship (<c>DeleteBehavior.SetNull</c> on <c>IsMergedInto</c>) as a
/// change to its <c>is_merged_into</c>, which every recompute interceptor counts as a change
/// to every line of the header (ADR-0032, 2026-11-03 amendment). Every tracked removal of a
/// header calls this first; a guard test holds them to it. A set-based delete bypasses the
/// tracker and reads the losers itself (<c>BulkTransactionsRepository.BulkDeleteAsync</c>).
/// An un-merged loser is a trade again (migration 273), so the same change re-derives the
/// trade price of each day it has an execution on.</para>
/// </remarks>
internal static class MergeLosers
{
    /// <summary>
    /// Track every row merged into one of <paramref name="winnerIds"/>, so removing a winner
    /// un-merges them in the change tracker as the database does.
    /// </summary>
    internal static async Task TrackAsync(
        AppDbContext db, IReadOnlyCollection<Guid> winnerIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(winnerIds);
        if (winnerIds.Count == 0) return;
        await db.TxnHeaders
            .Where(h => h.IsMergedInto != null && winnerIds.Contains(h.IsMergedInto.Value))
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
