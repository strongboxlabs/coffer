using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db;

/// <summary>
/// The one walk down a ledger's category tree.
/// </summary>
/// <remarks>
/// <para>Three repositories needed the same answer within one release — the
/// budget's expanded row (a rollup figure's transactions), the register's
/// subtree scope (a parent category holds no postings of its own), and the
/// merge/reparent target guard — and each grew its own copy of the same
/// breadth-first walk. They agreed, which is the dangerous kind of duplication:
/// nothing would have failed if one of them had later stopped agreeing.</para>
///
/// <para>IN MEMORY, deliberately. A chart of accounts is hundreds of rows, so
/// the walk is trivial, while the SQL alternative is a recursive CTE — which
/// under ADR-0005 (no raw SQL in <c>src/Api</c>) means a Postgres function plus
/// a <c>HasDbFunction</c> binding, migrated and versioned, for one cheap round
/// trip. The round trip only happens when a caller actually asks.</para>
/// </remarks>
internal static class CategoryTree
{
    /// <summary>
    /// Every category beneath <paramref name="rootId"/> in this ledger.
    /// </summary>
    /// <param name="includeRoot">
    /// <c>true</c> for a SCOPE (the root and everything under it — what a
    /// rollup figure covers); <c>false</c> for the strict descendants (what a
    /// reparent would drag along with a node).
    /// </param>
    /// <remarks>
    /// The visited set gates the enqueue, so it doubles as the cycle guard: a
    /// corrupted parent chain terminates instead of spinning. Accounts cannot
    /// cycle per the schema, but an import could always produce one.
    /// </remarks>
    internal static async Task<HashSet<Guid>> DescendantsAsync(
        AppDbContext db,
        Guid ledgerId,
        Guid rootId,
        bool includeRoot,
        CancellationToken cancellationToken = default)
    {
        var tree = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.ParentId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var childrenOf = tree
            .Where(t => t.ParentId != null)
            .GroupBy(t => t.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var found = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!childrenOf.TryGetValue(id, out var kids)) continue;
            foreach (var k in kids) if (found.Add(k)) queue.Enqueue(k);
        }

        if (!includeRoot) found.Remove(rootId);
        return found;
    }
}
