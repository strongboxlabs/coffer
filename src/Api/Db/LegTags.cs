using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// Apply one posting's tag set (ADR-0103, migration 243).
/// </summary>
/// <remarks>
/// <para>The leg-level counterpart of <see cref="HeaderTags"/>, and deliberately
/// a separate operation rather than a mode on it: a header tag and a leg tag are
/// different claims. <i>Holiday</i> is about the transaction; <i>Property B</i>
/// is about one posting, and neither is derived from the other.</para>
///
/// <para>Both share <see cref="TagDictionary"/> for name resolution, so a header
/// and a leg tagged with the same new name in one request land on ONE dictionary
/// row.</para>
/// </remarks>
internal static class LegTags
{
    /// <summary>
    /// Replace <paramref name="legId"/>'s tag set with <paramref name="tags"/>.
    /// Only pushes changes into the change tracker; the caller owns SaveChanges
    /// and the transaction boundary.
    /// </summary>
    public static async Task ApplyAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid legId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var targetTagIds = await TagDictionary
            .ResolveIdsAsync(db, ledgerId, tags, cancellationToken)
            .ConfigureAwait(false);
        await DiffLegTagPairingsAsync(db, ledgerId, legId, targetTagIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replace ONE leg's tag pairings with <paramref name="targetTagIds"/>
    /// (diff-against-current: drop pairings not in the target, add the missing ones).
    /// Idempotent; only pushes changes into the change tracker.
    /// </summary>
    public static async Task DiffLegTagPairingsAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid legId,
        HashSet<Guid> targetTagIds,
        CancellationToken cancellationToken)
    {
        var current = await db.TxnLegTags
            .Where(t => t.LegId == legId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentTagIds = current.Select(t => t.TagId).ToHashSet();

        foreach (var existingPair in current)
        {
            if (!targetTagIds.Contains(existingPair.TagId))
                db.TxnLegTags.Remove(existingPair);
        }
        foreach (var targetTagId in targetTagIds)
        {
            if (!currentTagIds.Contains(targetTagId))
            {
                db.TxnLegTags.Add(new TxnLegTagRow
                {
                    LegId = legId,
                    TagId = targetTagId,
                    LedgerId = ledgerId,
                });
            }
        }
    }
}
