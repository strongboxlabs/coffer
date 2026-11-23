using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over the budget (ADR-0099). Read-only.
/// </summary>
/// <remarks>
/// <para>Without these tools the budget is the one subsystem an agent cannot see:
/// it can read every account and every transaction, and still not answer
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
        "typical (the mean over the trailing window — history, not a decision; for a " +
        "ledger younger than the window, over only the months since its first " +
        "transaction, which historyFrom then gives), " +
        "target (what a person typed, or null — most rows have none), " +
        "targetCurrencyCode (the currency a stored target was typed in, null when " +
        "none is stored: a null target with a targetCurrencyCode is a stored target " +
        "that needs a missing exchange rate), and mark " +
        "(THE number to judge against: the target where there is one, else the " +
        "rolled normal). Judge a row by actual-vs-mark; do NOT recompute mark " +
        "from target and typical, because a parent's mark already accounts for " +
        "targets set on its descendants. Rows carry parentId and form a tree; " +
        "parent rows are ROLLUPS, so summing every row double-counts — use " +
        "actualTotal for the ledger-wide spend and typicalTotal for the ledger-wide " +
        "usual (averaged over the same months as typical, the first-transaction rule " +
        "included; it ignores targets and is not the sum of the marks, so there is no " +
        "ledger-wide mark). Amounts are positive " +
        "magnitudes in the ledger's reporting currency (currencyCode): spending in " +
        "another currency converts at each transaction's date, and a target typed in " +
        "another currency converts at the month's last day, or at the latest rate for " +
        "the current month. A figure that needs a missing exchange rate is null and " +
        "rateUnavailable names the currency pair and day; it is never a partial sum. " +
        "Use list_ledgers first to resolve ledgerId.")]
    public static async Task<BudgetProgressSummary> BudgetProgress(
        BudgetProgressRepository repository,
        TimeProvider clock,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Month as 'yyyy-MM' (e.g. 2026-04). Omit for the current month.")]
        string? month = null,
        [Description(
            "How many trailing complete months 'typical' averages over. Default 3 — "
            + "long enough to absorb a fortnightly shop landing twice in one month, "
            + "short enough that a rent rise stops looking anomalous within a quarter. "
            + "A ledger whose first transaction is inside the window averages only the "
            + "months since it.")]
        int? windowMonths = null,
        CancellationToken cancellationToken = default)
    {
        var first = ParseMonth(month, clock);
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
            dto.Rows
                .Select(r => new BudgetProgressRow(
                    r.CategoryId, r.Name, r.ParentId,
                    r.Actual, r.Typical, r.Target, r.TargetCurrencyCode, r.Mark, r.RateUnavailable))
                .ToList(),
            dto.ActualTotal,
            dto.TypicalTotal,
            dto.LatestMonthWithSpending,
            dto.HistoryFrom,
            dto.RateUnavailable);
    }

    /// <summary>
    /// A MONTH, not a date. Accepting a full date here would let a caller smuggle
    /// in a day that silently lands the window a month off; the REST surface takes
    /// the same format for the same reason.
    /// </summary>
    private static DateOnly ParseMonth(string? month, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = clock.GetUtcNow().UtcDateTime;
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
/// The trailing mean, over only the months since the ledger's first transaction when
/// it is younger than the window (see <see cref="BudgetProgressSummary.HistoryFrom"/>).
/// Null when the category has no history in the window: null
/// rather than 0 on purpose, because a brand-new category showing "typically 0"
/// reads as infinitely over.
/// </param>
/// <param name="Target">What a person typed for this category and month, converted into
/// the reporting currency, or null: none is typed, or one is but needs a missing rate
/// (then <paramref name="RateUnavailable"/> names it).</param>
/// <param name="TargetCurrencyCode">The currency a stored target was typed in, null when
/// none is stored (ADR-0114 D12). It tells a stored target that could not convert from
/// no target at all.</param>
public sealed record BudgetProgressRow(
    Guid CategoryId,
    string Name,
    Guid? ParentId,
    decimal? Actual,
    decimal? Typical,
    decimal? Target,
    string? TargetCurrencyCode,
    decimal? Mark,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// One month of budget progress, trimmed for a model: the rows and the totals,
/// without the per-day chart series the SPA needs.
/// </summary>
/// <param name="WindowMonths">
/// The requested window's length in complete months; it does not shrink for a ledger
/// younger than the window. When <paramref name="HistoryFrom"/> is set, an answer that
/// names what was averaged names that day instead of the month count.
/// </param>
/// <param name="RateUnavailable">
/// Every missing exchange rate a total needed, which is then null (ADR-0114 D9).
/// </param>
/// <param name="LatestMonthWithSpending">
/// Populated only when the requested month has no spending at all — the month
/// where the data actually is, so an answer of "nothing" can say where to look
/// instead of reading as an empty ledger.
/// </param>
/// <param name="HistoryFrom">
/// The day of the ledger's first transaction when it falls after the window's first
/// day, or null when the whole window is averaged. The window's months before it are
/// not averaged, so an answer naming the window names this day instead.
/// </param>
public sealed record BudgetProgressSummary(
    string Month,
    int WindowMonths,
    string CurrencyCode,
    IReadOnlyList<BudgetProgressRow> Rows,
    decimal? ActualTotal,
    decimal? TypicalTotal,
    string? LatestMonthWithSpending,
    DateOnly? HistoryFrom = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);
