using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The reusable reporting aggregation layer (ADR-0063). Aggregates over the
/// override-aware <c>resolved_transactions</c> view (effective amount / posted_at
/// / is_hidden, merged rows excluded) joined to <c>accounts</c> for the category
/// kind — LINQ/EF throughout, no raw SQL (the OverviewRepository precedent).
///
/// The measure (spending/income/net) always selects category-leg postings by
/// kind; <see cref="ReportSpec.GroupBy"/> chooses the grouping key:
/// <list type="bullet">
///   <item><b>Category</b> — the category account itself (nests via parent_id, so
///   <see cref="ReportSpec.Rollup"/> applies).</item>
///   <item><b>Account</b> — the real account paired with the categorized posting
///   (the view's counterparty for that posting_index — the cash/credit account
///   that "paid"). Split-safe: each posting_index pairs one category leg with one
///   cash leg.</item>
///   <item><b>Payee</b> — the transaction payee text.</item>
/// </list>
///
/// Sign: per the symmetric-posting model (per-posting sum-to-zero, ADR-0019/0025)
/// the cash leg and the category leg are opposite-signed, so expense-category
/// legs are positive and income-category legs negative. We normalize to positive
/// magnitudes for Spending/Income; Net = income − spending (income +, expense −).
/// </summary>
public sealed class ReportingRepository
{
    private readonly AppDbContext _db;

    public ReportingRepository(AppDbContext db) => _db = db;

    // Pre-rollup intermediate: one row per (month, group, kind).
    private sealed record Cell(
        int Year, int Month, Guid? GroupId, string GroupName, Guid? ParentId, string Kind, decimal Sum);

    public async Task<ReportResult> SummarizeAsync(
        ReportSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var kinds = spec.Measure switch
        {
            ReportMeasure.Spending => new[] { "expense" },
            ReportMeasure.Income => new[] { "income" },
            _ => new[] { "income", "expense" },   // Net
        };

        // Category-account postings only — which inherently excludes account↔account
        // transfers (those have no category leg), per the agreed "spending =
        // expense-category postings, transfers excluded" definition.
        var q =
            from r in _db.ResolvedTransactions.AsNoTracking()
            join a in _db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == spec.LedgerId
                  && a.AccountType == "category"
                  && a.CategoryKind != null
                  && kinds.Contains(a.CategoryKind)
                  && !r.IsHidden
                  && r.IsMergedInto == null
            select new { r, a };

        if (spec.FromUtc is { } from) q = q.Where(x => x.r.PostedAt >= from);
        if (spec.ToUtc is { } to) q = q.Where(x => x.r.PostedAt < to);

        if (spec.TagId is { } tag)
            q = q.Where(x => _db.TxnHeaderTags.Any(t => t.HeaderId == x.r.HeaderId && t.TagId == tag));

        // Aggregate at month granularity in SQL (Year/Month translate cleanly),
        // keyed by the chosen dimension; coarser buckets + rollup are rolled up in
        // memory. The grouped result is small (#groups × #months).
        var grouped = spec.GroupBy switch
        {
            ReportGroupBy.Account => await q
                .Where(x => x.r.CounterpartyAccountId != null)
                .GroupBy(x => new
                {
                    x.r.PostedAt.Year,
                    x.r.PostedAt.Month,
                    GroupId = x.r.CounterpartyAccountId,
                    GroupName = x.r.CounterpartyAccountName,
                    Kind = x.a.CategoryKind!,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.GroupId,
                    g.Key.GroupName ?? "(account)", null, g.Key.Kind, g.Sum(x => x.r.Amount)))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            ReportGroupBy.Payee => await q
                .GroupBy(x => new
                {
                    x.r.PostedAt.Year,
                    x.r.PostedAt.Month,
                    x.r.Payee,
                    Kind = x.a.CategoryKind!,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, null,
                    g.Key.Payee ?? "(no payee)", null, g.Key.Kind, g.Sum(x => x.r.Amount)))
                .ToListAsync(cancellationToken).ConfigureAwait(false),

            _ => await q
                .GroupBy(x => new
                {
                    x.r.PostedAt.Year,
                    x.r.PostedAt.Month,
                    CategoryId = x.a.Id,
                    x.a.Name,
                    x.a.ParentId,
                    Kind = x.a.CategoryKind!,
                })
                .Select(g => new Cell(
                    g.Key.Year, g.Key.Month, g.Key.CategoryId,
                    g.Key.Name, g.Key.ParentId, g.Key.Kind, g.Sum(x => x.r.Amount)))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
        };

        // Normalize each cell to the measure's convention.
        static decimal Value(ReportMeasure m, string kind, decimal sum) => m switch
        {
            ReportMeasure.Spending => sum,            // expense legs already positive
            ReportMeasure.Income => -sum,             // income legs negative → flip
            _ => -sum,                                 // Net: income +, expense −
        };

        // Collapse months into the requested bucket + (period, group) key.
        var cells = grouped
            .GroupBy(g => new
            {
                Period = PeriodLabel(spec.TimeBucket, g.Year, g.Month),
                g.GroupId,
                g.GroupName,
                g.ParentId,
            })
            .Select(g => new ReportRow(
                g.Key.Period,
                g.Key.GroupId,
                g.Key.GroupName,
                g.Key.ParentId,
                g.Sum(x => Value(spec.Measure, x.Kind, x.Sum))))
            .ToList();

        // Grand total = the true sum of underlying postings (pre-rollup), so it
        // stays correct whether or not parent rows are materialized as subtotals.
        var total = cells.Sum(c => c.Amount);

        // Category-tree rollup: parent amounts include descendants. Build the full
        // ancestor set per (period, category) so the model gets the whole tree.
        if (spec.Rollup && spec.GroupBy == ReportGroupBy.Category)
            cells = await RollUpCategoryTreeAsync(spec.LedgerId, cells, cancellationToken)
                .ConfigureAwait(false);

        // Top-N by absolute total over the whole range, then keep only those
        // groups' cells (so a time series is limited to the top movers). Keyed by
        // a stable group key (id, or name for the payee dimension).
        if (spec.TopN is { } topN && topN > 0)
        {
            var keep = cells
                .GroupBy(GroupKey)
                .Select(g => new { g.Key, Total = g.Sum(c => c.Amount) })
                .OrderByDescending(x => Math.Abs(x.Total))
                .Take(topN)
                .Select(x => x.Key)
                .ToHashSet();
            cells = cells.Where(c => keep.Contains(GroupKey(c))).ToList();
        }

        var rows = cells
            .OrderBy(c => c.Period, StringComparer.Ordinal)
            .ThenByDescending(c => Math.Abs(c.Amount))
            .ToList();

        return new ReportResult(rows, total);
    }

    // Stable per-group key for top-N / dedup: the id when present, else the name
    // (the payee dimension has no id).
    private static string GroupKey(ReportRow r) =>
        r.GroupId?.ToString() ?? ("name:" + r.GroupName);

    // Propagate each leaf category's amount onto all its ancestors, per period,
    // so the result carries the whole tree with parent subtotals. Cycles are
    // guarded; the grand total is computed pre-rollup by the caller.
    private async Task<List<ReportRow>> RollUpCategoryTreeAsync(
        Guid ledgerId, List<ReportRow> leaves, CancellationToken cancellationToken)
    {
        var cats = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.ParentId, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var parentOf = cats.ToDictionary(c => c.Id, c => c.ParentId);
        var nameOf = cats.ToDictionary(c => c.Id, c => c.Name);

        var rolled = new Dictionary<(string? Period, Guid Id), decimal>();
        foreach (var leaf in leaves)
        {
            if (leaf.GroupId is not { } id) continue;
            var cur = (Guid?)id;
            var guard = 0;
            while (cur is { } node && guard++ < 100)
            {
                var key = (leaf.Period, node);
                rolled[key] = rolled.TryGetValue(key, out var acc) ? acc + leaf.Amount : leaf.Amount;
                cur = parentOf.TryGetValue(node, out var p) ? p : null;
            }
        }

        return rolled
            .Select(kv => new ReportRow(
                kv.Key.Period,
                kv.Key.Id,
                nameOf.TryGetValue(kv.Key.Id, out var n) ? n : "(category)",
                parentOf.TryGetValue(kv.Key.Id, out var par) ? par : null,
                kv.Value))
            .ToList();
    }

    private static string? PeriodLabel(ReportTimeBucket bucket, int year, int month) => bucket switch
    {
        ReportTimeBucket.None => null,
        ReportTimeBucket.Year => year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
        ReportTimeBucket.Quarter => $"{year:D4}-Q{(month - 1) / 3 + 1}",
        _ => $"{year:D4}-{month:D2}",   // Month
    };

    /// <summary>
    /// The transaction line-drill (ADR-0063 §D5 v2): filtered/sorted/paged rows
    /// from the override-aware view. Offset paging with a deterministic sort
    /// (the chosen key, then leg id) so a page is reproducible while the data is
    /// unchanged; <see cref="TransactionLinesResult.HasMore"/> peeks one row past
    /// the limit to avoid a count query. Min/Max bound the absolute amount.
    /// </summary>
    public async Task<TransactionLinesResult> ListTransactionsAsync(
        TransactionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);

        var q =
            from r in _db.ResolvedTransactions.AsNoTracking()
            join a in _db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == query.LedgerId && !r.IsHidden && r.IsMergedInto == null
            select new { r, a };

        if (query.AccountId is { } acct)
            q = q.Where(x => x.r.AccountId == acct);
        else
            // Money side only when not scoped to a specific account, so each
            // transaction appears once (not once per leg).
            q = q.Where(x => x.a.AccountType != "category");

        if (query.CategoryId is { } cat)
            q = q.Where(x => x.r.CounterpartyAccountId == cat);

        if (query.TagId is { } tag)
            q = q.Where(x => _db.TxnHeaderTags.Any(t => t.HeaderId == x.r.HeaderId && t.TagId == tag));

        if (!string.IsNullOrWhiteSpace(query.Payee))
            q = q.Where(x => x.r.Payee == query.Payee);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = "%" + query.Text.Trim() + "%";
            q = q.Where(x =>
                (x.r.Payee != null && EF.Functions.ILike(x.r.Payee, pattern)) ||
                (x.r.Memo != null && EF.Functions.ILike(x.r.Memo, pattern)));
        }

        q = query.Direction switch
        {
            TransactionDirection.Inflow => q.Where(x => x.r.Amount > 0m),
            TransactionDirection.Outflow => q.Where(x => x.r.Amount < 0m),
            _ => q,
        };

        if (query.MinAmount is { } min)
            q = q.Where(x => x.r.Amount >= min || x.r.Amount <= -min);
        if (query.MaxAmount is { } max)
            q = q.Where(x => x.r.Amount <= max && x.r.Amount >= -max);

        if (query.FromUtc is { } from) q = q.Where(x => x.r.PostedAt >= from);
        if (query.ToUtc is { } to) q = q.Where(x => x.r.PostedAt < to);

        var desc = query.Descending;
        var ordered = query.Sort switch
        {
            TransactionSort.Amount => desc
                ? q.OrderByDescending(x => x.r.Amount)
                : q.OrderBy(x => x.r.Amount),
            TransactionSort.AbsAmount => desc
                ? q.OrderByDescending(x => x.r.Amount < 0m ? -x.r.Amount : x.r.Amount)
                : q.OrderBy(x => x.r.Amount < 0m ? -x.r.Amount : x.r.Amount),
            _ => desc
                ? q.OrderByDescending(x => x.r.PostedAt).ThenByDescending(x => x.r.HeaderSeq)
                : q.OrderBy(x => x.r.PostedAt).ThenBy(x => x.r.HeaderSeq),
        };
        // Deterministic tiebreak so offset paging is reproducible.
        ordered = desc ? ordered.ThenByDescending(x => x.r.Id) : ordered.ThenBy(x => x.r.Id);

        var page = await ordered
            .Skip(offset)
            .Take(limit + 1)   // peek one past for HasMore
            .Select(x => new TransactionLine(
                x.r.HeaderId,
                x.r.PostedAt,
                x.r.Payee,
                x.r.AccountId,
                x.a.Name,
                x.r.CounterpartyAccountId,
                x.r.CounterpartyAccountName,
                x.r.Amount,
                x.r.LegMemo,
                x.r.Status))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        return new TransactionLinesResult(page, limit, offset, hasMore);
    }
}
