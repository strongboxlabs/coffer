using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over the budget (ADR-0099). Read-only.
/// </summary>
/// <remarks>
/// <para>The budget shipped in 0.88.0 with a REST surface and a SPA screen and
/// no MCP tool at all, which made it the one subsystem an agent could not see:
/// it could read every account and every transaction, and still not answer
/// "what is the target for this category" or "how is the month tracking".</para>
///
/// <para>The arithmetic runs HERE, never in the model (ADR-0063 §D4). `mark` in
/// particular must not be re-derived from `target` and `typical` by a caller:
/// per ADR-0099 D1a a target and its ancestors are mutually exclusive, so an
/// untargeted row's mark is its own normal with each targeted DESCENDANT's
/// normal swapped for that descendant's number — a tree walk the server has
/// already done. A model that tried to reconstruct it from the two visible
/// numbers would be wrong on exactly the rows that matter.</para>
///
/// <para>RLS scopes every read to the bearer's user, so an out-of-grant
/// <c>ledgerId</c> yields empty rows rather than someone else's budget.</para>
/// </remarks>
[McpServerToolType]
public static class BudgetTools
{
    [McpServerTool(Name = "budget_progress"), Description(
        "How a ledger's spending is tracking against its budget for one month. " +
        "Returns one row per expense category with: actual (spent this month), " +
        "typical (the mean over the trailing window — history, not a decision), " +
        "target (what a person typed, or null — most rows have none), and mark " +
        "(THE number to judge against: the target where there is one, else the " +
        "rolled normal). Judge a row by actual-vs-mark; do NOT recompute mark " +
        "from target and typical, because a parent's mark already accounts for " +
        "targets set on its descendants. Rows carry parentId and form a tree; " +
        "parent rows are ROLLUPS, so summing every row double-counts — use " +
        "actualTotal/markTotal for the ledger-wide figures. Amounts are positive " +
        "magnitudes in the ledger's currency. Use list_ledgers first to resolve " +
        "ledgerId.")]
    public static async Task<BudgetProgressSummary> BudgetProgress(
        BudgetProgressRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Month as 'yyyy-MM' (e.g. 2026-04). Omit for the current month.")]
        string? month = null,
        [Description(
            "How many trailing complete months 'typical' averages over. Default 3 — "
            + "long enough to absorb a fortnightly shop landing twice in one month, "
            + "short enough that a rent rise stops looking anomalous within a quarter.")]
        int? windowMonths = null,
        CancellationToken cancellationToken = default)
    {
        var first = ParseMonth(month);
        var monthStartUtc = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var window = windowMonths ?? BudgetProgressRepository.DefaultWindowMonths;
        if (window < 1)
            throw new ArgumentException("windowMonths must be at least 1.", nameof(windowMonths));

        var dto = await repository
            .GetAsync(ledgerId, monthStartUtc, window, cancellationToken)
            .ConfigureAwait(false);

        // The DTO carries two per-day series that exist to draw the SPA's chart.
        // They are the bulk of the payload and answer nothing a model asks, so
        // they are dropped rather than spent: a year of them would crowd out the
        // rows, which are the part that carries meaning.
        return new BudgetProgressSummary(
            dto.Month,
            dto.WindowMonths,
            dto.CurrencyCode,
            dto.MixedCurrency,
            dto.Rows
                .Select(r => new BudgetProgressRow(
                    r.CategoryId, r.Name, r.ParentId,
                    r.Actual, r.Typical, r.Target, r.Mark))
                .ToList(),
            dto.ActualTotal,
            dto.TypicalTotal,
            dto.LatestMonthWithSpending);
    }

    /// <summary>
    /// A MONTH, not a date. Accepting a full date here would let a caller smuggle
    /// in a day that silently lands the window a month off; the REST surface takes
    /// the same format for the same reason.
    /// </summary>
    private static DateOnly ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = DateTime.UtcNow;
            return new DateOnly(today.Year, today.Month, 1);
        }

        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ArgumentException(
                $"month must be formatted yyyy-MM (got '{month}').", nameof(month));

        return new DateOnly(parsed.Year, parsed.Month, 1);
    }
}

/// <summary>One category's month, as an agent sees it.</summary>
/// <param name="Mark">
/// What to judge <paramref name="Actual"/> against. Never recompute it — see the
/// remarks on <see cref="BudgetTools"/>.
/// </param>
/// <param name="Typical">
/// The trailing mean. Null when the category has no history in the window: null
/// rather than 0 on purpose, because a brand-new category showing "typically 0"
/// reads as infinitely over.
/// </param>
/// <param name="Target">What a person typed for this category and month, or null.</param>
public sealed record BudgetProgressRow(
    Guid CategoryId,
    string Name,
    Guid? ParentId,
    decimal Actual,
    decimal? Typical,
    decimal? Target,
    decimal? Mark);

/// <summary>
/// One month of budget progress, trimmed for a model: the rows and the totals,
/// without the per-day chart series the SPA needs.
/// </summary>
/// <param name="MixedCurrency">
/// True when the ledger's accounts do not agree on a currency, in which case the
/// totals are summed WITHOUT conversion and must be reported as such rather than
/// stamped with one symbol.
/// </param>
/// <param name="LatestMonthWithSpending">
/// Populated only when the requested month has no spending at all — the month
/// where the data actually is, so an answer of "nothing" can say where to look
/// instead of reading as an empty ledger.
/// </param>
public sealed record BudgetProgressSummary(
    string Month,
    int WindowMonths,
    string CurrencyCode,
    bool MixedCurrency,
    IReadOnlyList<BudgetProgressRow> Rows,
    decimal ActualTotal,
    decimal? TypicalTotal,
    string? LatestMonthWithSpending);
