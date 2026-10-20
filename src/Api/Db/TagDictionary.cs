using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// The ledger's tag dictionary: names in, ids out, created on first use.
/// </summary>
/// <remarks>
/// <para>Lifted out of <see cref="HeaderTags"/> when per-leg tags landed
/// (ADR-0103). Resolving a name against the ledger's vocabulary has nothing to
/// do with headers — it is the same operation whether the tag is about to be
/// attached to a transaction or to one posting — and leaving it on the header
/// helper would have meant the leg path reaching into a type named for the other
/// level to do it.</para>
///
/// <para>The alternative was a second copy, which is what <see cref="HeaderTags"/>
/// was itself extracted to avoid: two copies of a rule are two places for it to
/// drift, and the drift here would be silent, since case-folding and
/// first-casing-wins decide whether two writes land on one dictionary row or
/// two.</para>
/// </remarks>
internal static class TagDictionary
{
    /// <summary>
    /// Resolve tag NAMES to their ids within the ledger's dictionary, creating a row
    /// on first use (tracked; INSERTed on the next SaveChanges). Normalizes first:
    /// trim, drop empties, case-insensitive dedupe with the first user casing winning
    /// on insert.
    ///
    /// <para>Resolve ONCE per unit of work. The bulk path calls this a single time for
    /// the whole batch, so if two headers both introduce the same brand-new name it
    /// maps to ONE dictionary row — a per-header re-query would miss the prior
    /// iteration's tracker-pending insert and create a duplicate. The same applies
    /// across LEVELS: a request that tags a header and one of its legs with the same
    /// new name must resolve both from one call, or it creates the tag twice.</para>
    /// </summary>
    public static async Task<HashSet<Guid>> ResolveIdsAsync(
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

        // Tracker-pending rows from an earlier call in the same unit of work are not
        // in the query above — that is the duplicate the remarks warn about, and the
        // leg path makes it reachable from a single request rather than only from the
        // bulk loop.
        foreach (var pending in db.ChangeTracker.Entries<TagRow>()
                     .Where(e => e.State == EntityState.Added)
                     .Select(e => e.Entity)
                     .Where(t => t.LedgerId == ledgerId))
        {
            existingByLower.TryAdd(pending.Name.ToLowerInvariant(), pending);
        }

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
                existingByLower[lower] = fresh;
                targetTagIds.Add(fresh.Id);
            }
        }
        return targetTagIds;
    }
}
