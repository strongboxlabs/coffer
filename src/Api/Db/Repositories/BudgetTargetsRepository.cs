using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Domain.Money;

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
    private readonly TimeProvider _clock;

    public BudgetTargetsRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The currency a target typed now is in: the ledger's reporting currency
    /// (ADR-0114 D12). Stamped on an insert and on a retype alike, so retyping a target
    /// stores it in the current reporting currency; nothing else rewrites it.
    /// </summary>
    private Task<string> ReportingCurrencyAsync(Guid ledgerId, CancellationToken cancellationToken) =>
        _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .SingleAsync(cancellationToken);

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
        /// <summary>The typed amount carries more places than the currency it would be
        /// stamped with allows (ADR-0114 D15); the outcome names that currency. Nothing is
        /// written.</summary>
        AmountPlacesExceedCurrency,
    }

    /// <summary>The outcome, plus the NAME of whichever category caused a refusal.
    /// Naming it is the whole point: "you cannot set this" is not actionable, and the
    /// conflicting category may be several levels away and not on screen.</summary>
    /// <param name="CurrencyCode">For <see cref="SetResult.AmountPlacesExceedCurrency"/>, the
    /// currency the amount does not fit.</param>
    public readonly record struct SetOutcome(SetResult Result, string? ConflictingCategoryName, string? CurrencyCode = null);

    /// <summary>
    /// True when a typed <paramref name="amount"/>, stamped with <paramref name="currency"/>,
    /// may be written over <paramref name="stored"/> (ADR-0114 D15): it fits the currency's
    /// places, or it and the currency equal the stored row's, because a stored target that
    /// does not fit is kept and an unchanged retype is not refused for it.
    /// </summary>
    private static bool TypedAmountFits(decimal amount, string currency, BudgetTargetRow? stored) =>
        CurrencyMinorUnits.Fits(amount, currency)
        || (stored is not null && stored.Amount == amount
            && string.Equals(stored.CurrencyCode, currency, StringComparison.Ordinal));

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

        // The ledger write lock (migration 275) first, before the reporting currency is read
        // and stamped (ADR-0114 D12). A reporting-currency change holds it while it writes
        // the new code (LedgersRepository.SetReportingCurrencyAsync), so this target either
        // commits before that change, in the code then current, or waits for it and is
        // stamped with the new one.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

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
        var currency = await ReportingCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        // Checked at the currency read under the lock, which is the one stamped below.
        if (!TypedAmountFits(amount, currency, row))
            return new SetOutcome(SetResult.AmountPlacesExceedCurrency, null, currency);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            _db.BudgetTargets.Add(new BudgetTargetRow
            {
                LedgerId = ledgerId,
                CategoryId = categoryId,
                TargetMonth = first,
                Amount = amount,
                CurrencyCode = currency,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            row.Amount = amount;
            row.CurrencyCode = currency;
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
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
    /// <param name="CurrencyCode">For <see cref="SetResult.AmountPlacesExceedCurrency"/>, the
    /// currency the entry's amount does not fit.</param>
    public readonly record struct BulkEntryOutcome(
        Guid CategoryId, SetResult Result, string? ConflictingCategoryName, string? CurrencyCode = null);

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
    /// <remarks>
    /// Every entry is stamped with the ledger's reporting currency (ADR-0114 D12): the
    /// fill-from-average amounts are figures in it. A copy keeps the currency each source
    /// target was typed in, and goes through <see cref="CopyMonthAsync"/>.
    /// <para>Each amount is a typed figure, held to the reporting currency's places
    /// (ADR-0114 D15). Unlike a conflict, an amount that does not fit refuses the whole
    /// fill: nothing is written, and the outcomes list only the entries that do not fit,
    /// each as <see cref="SetResult.AmountPlacesExceedCurrency"/>. An amount equal to the
    /// stored target, in the same currency, passes.</para>
    /// </remarks>
    public Task<IReadOnlyList<BulkEntryOutcome>> SetManyAsync(
        Guid ledgerId,
        DateOnly month,
        IReadOnlyCollection<(Guid CategoryId, decimal Amount)> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return ApplyManyAsync(
            ledgerId, month,
            _ => Task.FromResult<IReadOnlyCollection<(Guid CategoryId, decimal Amount, string? CurrencyCode)>>(
                entries.Select(e => (e.CategoryId, e.Amount, (string?)null)).ToList()),
            cancellationToken);
    }

    /// <summary>
    /// "Copy last month": every target stored for <paramref name="sourceMonth"/> written to
    /// <paramref name="month"/>, each with the amount AND the currency it was typed in
    /// (ADR-0114 D12), under the same D1a rules and outcome list as
    /// <see cref="SetManyAsync"/>. A source month with no targets applies nothing.
    /// </summary>
    /// <remarks>
    /// The server reads the source rows itself, under the write lock, rather than taking
    /// the amounts from the client: the client's copy of last month is a progress read,
    /// whose figures may be converted into the reporting currency, and a converted amount
    /// stamped with the source's currency would be a number nobody typed.
    /// <para>The copied figures are not checked against their currency's places
    /// (ADR-0114 D15): copying is not typing, and a stored target that does not fit is
    /// kept, in its copy as in itself.</para>
    /// </remarks>
    public Task<IReadOnlyList<BulkEntryOutcome>> CopyMonthAsync(
        Guid ledgerId,
        DateOnly sourceMonth,
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var source = FirstOfMonth(sourceMonth);
        return ApplyManyAsync(ledgerId, month, async ct =>
            (await _db.BudgetTargets.AsNoTracking()
                .Where(t => t.LedgerId == ledgerId && t.TargetMonth == source)
                .OrderBy(t => t.CategoryId)
                .Select(t => new { t.CategoryId, t.Amount, t.CurrencyCode })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(t => (t.CategoryId, t.Amount, (string?)t.CurrencyCode))
            .ToList(),
            cancellationToken);
    }

    /// <summary>The bulk write both fills share. <paramref name="loadEntries"/> runs under
    /// the lock; an entry with no currency takes the reporting currency.</summary>
    private async Task<IReadOnlyList<BulkEntryOutcome>> ApplyManyAsync(
        Guid ledgerId,
        DateOnly month,
        Func<CancellationToken, Task<IReadOnlyCollection<(Guid CategoryId, decimal Amount, string? CurrencyCode)>>> loadEntries,
        CancellationToken cancellationToken)
    {
        var first = FirstOfMonth(month);

        // The ledger write lock (migration 275) first, before the reads the batch is
        // validated against. A category merge holds it while it locks its source and moves
        // the source's targets onto the destination's; a fill that updated the destination's
        // target and then inserted one naming the source would wait for the merge's lock on
        // the source while the merge waited for that target. Taken first, the two take turns,
        // and the fill reads the tree and the targets the merge committed. A reporting-
        // currency change takes it too, so the code stamped below is the one current at
        // commit (ADR-0114 D12).
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var entries = await loadEntries(cancellationToken).ConfigureAwait(false);
        var tree = await LoadTreeAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var existing = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == first)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var accepted = existing.Select(t => t.CategoryId).ToHashSet();
        var outcomes = new List<BulkEntryOutcome>(entries.Count);
        var reporting = await ReportingCurrencyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow().UtcDateTime;

        // A typed entry (one with no currency of its own) is held to the reporting
        // currency's places, read under the lock (ADR-0114 D15). One that does not fit
        // refuses the whole fill before anything is written. A copied entry carries its
        // source's currency and is not typed, so it is not checked.
        var unfit = entries
            .Where(e => e.CurrencyCode is null
                        && !TypedAmountFits(e.Amount, reporting, existing.FirstOrDefault(t => t.CategoryId == e.CategoryId)))
            .Select(e => new BulkEntryOutcome(e.CategoryId, SetResult.AmountPlacesExceedCurrency, null, reporting))
            .ToList();
        if (unfit.Count > 0) return unfit;

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

            var currency = entry.CurrencyCode ?? reporting;
            var row = existing.FirstOrDefault(t => t.CategoryId == entry.CategoryId);
            if (row is null)
            {
                _db.BudgetTargets.Add(new BudgetTargetRow
                {
                    LedgerId = ledgerId,
                    CategoryId = entry.CategoryId,
                    TargetMonth = first,
                    Amount = entry.Amount,
                    CurrencyCode = currency,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                row.Amount = entry.Amount;
                row.CurrencyCode = currency;
                row.UpdatedAt = now;
            }

            accepted.Add(entry.CategoryId);
            outcomes.Add(new BulkEntryOutcome(entry.CategoryId, SetResult.Ok, null));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
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
