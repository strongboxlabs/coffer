using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Reads and writes the numbers a person typed (mig 225, ADR-0099 D1/D1a).
/// </summary>
/// <remarks>
/// <para>This repository owns the ONE invariant the schema cannot express: a target
/// may sit on a category OR on its descendants, never both (ADR-0099 D1a). "No
/// ancestor or descendant holds a target" is a recursive tree predicate, so no CHECK
/// and no unique index can state it, and ADR-0032 makes a trigger a last resort.</para>
///
/// <para>WHY THE INVARIANT EXISTS AT ALL. A derived normal rolls up arithmetically —
/// a parent's normal already describes its whole subtree, because the aggregation
/// rolled it up. A typed target does not. Allow both and the hero's ceiling, which is
/// the sum over ROOTS, either double-counts the child or silently discards it. The
/// invariant gives every subtree exactly one authoritative mark, so the roots-only sum
/// stays correct with no special case.</para>
///
/// <para>THE TREE IS WALKED IN MEMORY, deliberately. A ledger has hundreds of
/// categories, not millions, and <c>(id, parent_id, name)</c> for all of them is a few
/// tens of kilobytes — the same trade <see cref="ReportingRepository"/> already makes
/// in its rollup. The alternative is a recursive CTE, which in this codebase means a
/// Postgres function and a <c>HasDbFunction</c> binding (ADR-0005 bans raw SQL in
/// <c>src/Api</c>), and that is a lot of machinery to avoid one cheap round trip.</para>
/// </remarks>
public sealed class BudgetTargetsRepository
{
    private readonly AppDbContext _db;

    public BudgetTargetsRepository(AppDbContext db) => _db = db;

    /// <summary>Depth guard on every tree walk. A cycle in <c>parent_id</c> should be
    /// impossible, but an infinite loop inside a request is a worse way to find out.
    /// Matches <see cref="ReportingRepository"/>'s rollup.</summary>
    private const int MaxDepth = 100;

    public enum SetResult
    {
        Ok,
        /// <summary>No such category in this ledger.</summary>
        CategoryNotInLedger,
        /// <summary>The id names a real account, not a category.</summary>
        NotACategory,
        /// <summary>An ancestor already holds a target for this month (ADR-0099 D1a).</summary>
        AncestorHasTarget,
        /// <summary>A descendant already holds a target for this month (ADR-0099 D1a).</summary>
        DescendantHasTarget,
    }

    /// <summary>The outcome, plus the NAME of whichever category caused a refusal.
    /// Naming it is the whole point: "you cannot set this" is not actionable, and the
    /// conflicting category may be several levels away and not on screen.</summary>
    public readonly record struct SetOutcome(SetResult Result, string? ConflictingCategoryName);

    /// <summary>One category's typed target for one month, inserted or updated.</summary>
    /// <param name="month">Any date inside the month; normalised to its first day.</param>
    public async Task<SetOutcome> SetAsync(
        Guid ledgerId,
        Guid categoryId,
        DateOnly month,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var first = FirstOfMonth(month);
        var tree = await LoadTreeAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        if (!tree.IsCategory.TryGetValue(categoryId, out var isCategory))
            return new SetOutcome(SetResult.CategoryNotInLedger, null);
        if (!isCategory)
            return new SetOutcome(SetResult.NotACategory, null);

        var existing = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == first)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var conflict = FindConflict(tree, existing.Select(t => t.CategoryId), categoryId);
        if (conflict is { } c) return c;

        var row = existing.FirstOrDefault(t => t.CategoryId == categoryId);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            _db.BudgetTargets.Add(new BudgetTargetRow
            {
                LedgerId = ledgerId,
                CategoryId = categoryId,
                TargetMonth = first,
                Amount = amount,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            row.Amount = amount;
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SetOutcome(SetResult.Ok, null);
    }

    /// <summary>
    /// Removes a target, returning the category to its derived normal. Returns false
    /// when there was nothing to remove — which is not an error: ADR-0099 D1 makes
    /// PRESENCE the state, so "no target" is a legitimate destination, and a caller
    /// clearing an already-clear cell has got what it asked for.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid ledgerId, Guid categoryId, DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var first = FirstOfMonth(month);
        var removed = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId
                        && t.CategoryId == categoryId
                        && t.TargetMonth == first)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>Every target for one month, for the budget read path.</summary>
    public async Task<IReadOnlyList<BudgetTargetRow>> ForMonthAsync(
        Guid ledgerId, DateOnly month, CancellationToken cancellationToken = default)
    {
        var first = FirstOfMonth(month);
        return await _db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == first)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What one entry of a bulk fill did.</summary>
    public readonly record struct BulkEntryOutcome(
        Guid CategoryId, SetResult Result, string? ConflictingCategoryName);

    /// <summary>
    /// Applies many targets for one month in a single transaction — the "copy last
    /// month" and "fill from average" actions.
    /// </summary>
    /// <remarks>
    /// <para>Entries are validated against the targets ALREADY stored for the month and
    /// against EACH OTHER, because a proposed set can be internally illegal: copying a
    /// month whose hierarchy has since been reparented can put an ancestor and a
    /// descendant in the same batch. Conflicting entries are SKIPPED and reported, never
    /// silently dropped and never written as a violation — one click standing in for
    /// thirty typed decisions is exactly where a silent partial failure would be worst.</para>
    ///
    /// <para>Order matters and is therefore fixed: entries are applied SHALLOWEST FIRST,
    /// so when a batch contains both a parent and its child the parent wins and the
    /// child is the one reported. That matches D1a's reading — a target on a parent
    /// speaks for the whole subtree — and it makes the outcome independent of the order
    /// the caller happened to send.</para>
    /// </remarks>
    public async Task<IReadOnlyList<BulkEntryOutcome>> SetManyAsync(
        Guid ledgerId,
        DateOnly month,
        IReadOnlyCollection<(Guid CategoryId, decimal Amount)> entries,
        CancellationToken cancellationToken = default)
    {
        var first = FirstOfMonth(month);
        var tree = await LoadTreeAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var existing = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == first)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var accepted = existing.Select(t => t.CategoryId).ToHashSet();
        var outcomes = new List<BulkEntryOutcome>(entries.Count);
        var now = DateTime.UtcNow;

        foreach (var entry in entries.OrderBy(e => DepthOf(tree, e.CategoryId)))
        {
            if (!tree.IsCategory.TryGetValue(entry.CategoryId, out var isCategory))
            {
                outcomes.Add(new BulkEntryOutcome(
                    entry.CategoryId, SetResult.CategoryNotInLedger, null));
                continue;
            }
            if (!isCategory)
            {
                outcomes.Add(new BulkEntryOutcome(
                    entry.CategoryId, SetResult.NotACategory, null));
                continue;
            }

            if (FindConflict(tree, accepted, entry.CategoryId) is { } c)
            {
                outcomes.Add(new BulkEntryOutcome(
                    entry.CategoryId, c.Result, c.ConflictingCategoryName));
                continue;
            }

            var row = existing.FirstOrDefault(t => t.CategoryId == entry.CategoryId);
            if (row is null)
            {
                _db.BudgetTargets.Add(new BudgetTargetRow
                {
                    LedgerId = ledgerId,
                    CategoryId = entry.CategoryId,
                    TargetMonth = first,
                    Amount = entry.Amount,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                row.Amount = entry.Amount;
                row.UpdatedAt = now;
            }

            accepted.Add(entry.CategoryId);
            outcomes.Add(new BulkEntryOutcome(entry.CategoryId, SetResult.Ok, null));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return outcomes;
    }

    // ---------------------------------------------------------------------
    // The invariant
    // ---------------------------------------------------------------------

    private sealed record Tree(
        Dictionary<Guid, Guid?> ParentOf,
        Dictionary<Guid, string> NameOf,
        Dictionary<Guid, bool> IsCategory);

    private async Task<Tree> LoadTreeAsync(Guid ledgerId, CancellationToken cancellationToken)
    {
        // Every account, not only categories: the NotACategory check needs to tell
        // "no such id in this ledger" apart from "that id is a bank account", and
        // those are different answers to the caller.
        var rows = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.ParentId, a.Name, a.AccountType })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new Tree(
            rows.ToDictionary(r => r.Id, r => r.ParentId),
            rows.ToDictionary(r => r.Id, r => r.Name),
            rows.ToDictionary(r => r.Id, r => r.AccountType == "category"));
    }

    /// <summary>
    /// The D1a check. Returns the refusal, or null when <paramref name="categoryId"/>
    /// may hold a target alongside everything in <paramref name="targeted"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="categoryId"/> being in <paramref name="targeted"/> is NOT a
    /// conflict with itself — replacing your own number is the ordinary edit.
    /// </remarks>
    private static SetOutcome? FindConflict(
        Tree tree, IEnumerable<Guid> targeted, Guid categoryId)
    {
        var others = targeted.Where(id => id != categoryId).ToHashSet();
        if (others.Count == 0) return null;

        // Upwards: does anything above me already hold a target?
        var cur = tree.ParentOf.TryGetValue(categoryId, out var p) ? p : null;
        for (var guard = 0; cur is { } node && guard < MaxDepth; guard++)
        {
            if (others.Contains(node))
                return new SetOutcome(SetResult.AncestorHasTarget, NameOf(tree, node));
            cur = tree.ParentOf.TryGetValue(node, out var next) ? next : null;
        }

        // Downwards, by walking each targeted node UP to see whether it passes
        // through me. Cheaper than materialising the descendant set, because the
        // targeted set is small (one month's typed numbers) while the subtree
        // underneath a top-level category can be most of the chart of accounts.
        foreach (var other in others)
        {
            var up = tree.ParentOf.TryGetValue(other, out var q) ? q : null;
            for (var guard = 0; up is { } node && guard < MaxDepth; guard++)
            {
                if (node == categoryId)
                    return new SetOutcome(SetResult.DescendantHasTarget, NameOf(tree, other));
                up = tree.ParentOf.TryGetValue(node, out var next) ? next : null;
            }
        }

        return null;
    }

    private static int DepthOf(Tree tree, Guid id)
    {
        var depth = 0;
        var cur = tree.ParentOf.TryGetValue(id, out var p) ? p : null;
        while (cur is { } node && depth < MaxDepth)
        {
            depth++;
            cur = tree.ParentOf.TryGetValue(node, out var next) ? next : null;
        }
        return depth;
    }

    private static string NameOf(Tree tree, Guid id)
        => tree.NameOf.TryGetValue(id, out var n) ? n : "(category)";

    /// <summary>Normalises to the first of the month, matching the database CHECK.</summary>
    private static DateOnly FirstOfMonth(DateOnly month) => new(month.Year, month.Month, 1);
}
