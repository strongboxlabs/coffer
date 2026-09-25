using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// Apply a header's tag set — the one implementation both write paths call.
/// </summary>
/// <remarks>
/// <para>Tags are a property of the EVENT (ADR-0009), not of a leg or of an
/// account, so they apply to any header regardless of shape. This lived as
/// three private methods on <c>TransactionsRepository</c>, which is why only
/// bank-shape transactions could carry them through HTTP: the investment
/// contracts had no tag field at all, and the only reachable write path was the
/// MCP bulk tool — which had no shape gate, so it could already tag an
/// investment header that the register then refused to display.</para>
///
/// <para>Extracted rather than duplicated for the same reason
/// <see cref="HeaderOriginals"/> was: two copies of a rule are two places for it
/// to drift, and the drift here would be silent — a tag written by one path and
/// not found by the other looks like data loss.</para>
/// </remarks>
internal static class HeaderTags
{
    /// <summary>
    /// Replace <paramref name="headerId"/>'s tag set with <paramref name="tags"/>.
    /// Only pushes changes into the change tracker; the caller owns SaveChanges
    /// and the transaction boundary.
    /// </summary>
    public static async Task ApplyAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid headerId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var targetTagIds = await ResolveTagIdsAsync(db, ledgerId, tags, cancellationToken)
            .ConfigureAwait(false);
        await DiffHeaderTagPairingsAsync(db, ledgerId, headerId, targetTagIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve tag NAMES to their ids within the ledger's dictionary, creating a row
    /// on first use (tracked; INSERTed on the next SaveChanges). Normalizes first:
    /// trim, drop empties, case-insensitive dedupe with the first user casing winning
    /// on insert.
    ///
    /// <para>Resolve ONCE per unit of work. The bulk path calls this a single time for
    /// the whole batch, so if two headers both introduce the same brand-new name it
    /// maps to ONE dictionary row — a per-header re-query would miss the prior
    /// iteration's tracker-pending insert and create a duplicate.</para>
    /// </summary>
    public static async Task<HashSet<Guid>> ResolveTagIdsAsync(
        AppDbContext db,
        Guid ledgerId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var distinct = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .GroupBy(t => t.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        var distinctLower = distinct
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        // Resolve every requested tag against the ledger's dictionary (one round
        // trip) — anything that doesn't come back is inserted on first use.
        var existing = await db.Tags
            .Where(t => t.LedgerId == ledgerId
                        && distinctLower.Contains(t.Name.ToLower()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingByLower = existing.ToDictionary(
            t => t.Name.ToLowerInvariant(),
            t => t);

        var targetTagIds = new HashSet<Guid>();
        foreach (var name in distinct)
        {
            var lower = name.ToLowerInvariant();
            if (existingByLower.TryGetValue(lower, out var match))
            {
                targetTagIds.Add(match.Id);
            }
            else
            {
                var fresh = new TagRow
                {
                    Id = Guid.NewGuid(),
                    LedgerId = ledgerId,
                    Name = name, // preserve user casing on first use
                };
                db.Tags.Add(fresh);
                targetTagIds.Add(fresh.Id);
            }
        }
        return targetTagIds;
    }

    /// <summary>
    /// Replace ONE header's tag pairings with <paramref name="targetTagIds"/>
    /// (diff-against-current: drop pairings not in the target, add the missing ones).
    /// Idempotent; only pushes changes into the change tracker — the caller owns the
    /// SaveChanges / commit boundary.
    /// </summary>
    public static async Task DiffHeaderTagPairingsAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid headerId,
        HashSet<Guid> targetTagIds,
        CancellationToken cancellationToken)
    {
        var current = await db.TxnHeaderTags
            .Where(t => t.HeaderId == headerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentTagIds = current.Select(t => t.TagId).ToHashSet();

        foreach (var existingPair in current)
        {
            if (!targetTagIds.Contains(existingPair.TagId))
                db.TxnHeaderTags.Remove(existingPair);
        }
        foreach (var targetTagId in targetTagIds)
        {
            if (!currentTagIds.Contains(targetTagId))
            {
                db.TxnHeaderTags.Add(new TxnHeaderTagRow
                {
                    HeaderId = headerId,
                    TagId = targetTagId,
                    LedgerId = ledgerId,
                });
            }
        }
    }
}
