using System.Globalization;
using System.Text.Json.Nodes;

using Coffer.Api.Contracts;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// What the examples steps write that the manifest does not carry: values that exist only
/// in Coffer, built here in C# so the compiler checks their shapes.
/// </summary>
internal static class DemoSeedContent
{
    /// <summary>
    /// The look-through sleeve each of the target-date fund's components stands for: its
    /// economic class and region (ADR-0067). The manifest weights the fund by its
    /// components' tickers; a look-through is stated in sleeves. One sleeve per component,
    /// the region each index fund is mostly invested in.
    /// </summary>
    internal static IReadOnlyDictionary<string, (string AssetClass, string Region)> LookThroughSleeves { get; } =
        new Dictionary<string, (string AssetClass, string Region)>(StringComparer.Ordinal)
        {
            [DemoSecurities.Vti] = ("equity", "us"),
            [DemoSecurities.Vxus] = ("equity", "developed_ex_us"),
            [DemoSecurities.Bnd] = ("fixed_income", "us"),
        };

    /// <summary>
    /// The target-date fund's look-through, from the manifest's weights by component
    /// ticker: one component per sleeve, heaviest first, weights as given.
    /// </summary>
    /// <exception cref="DemoSeedStepException">The weights do not sum to one, one is not
    /// positive, or a ticker has no sleeve here.</exception>
    internal static IReadOnlyList<SecurityComponentDto> LookThrough(IReadOnlyDictionary<string, decimal> weightsByTicker)
    {
        ArgumentNullException.ThrowIfNull(weightsByTicker);
        if (weightsByTicker.Count == 0)
            throw new DemoSeedStepException("the manifest gives the target-date fund no look-through");
        var sum = weightsByTicker.Values.Sum();
        if (sum != 1m)
            throw new DemoSeedStepException($"the target-date fund's look-through weights sum to {sum}, not 1");

        var components = new List<SecurityComponentDto>();
        foreach (var (ticker, weight) in weightsByTicker.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (weight <= 0m)
                throw new DemoSeedStepException($"the look-through weight of {ticker} is not positive");
            if (!LookThroughSleeves.TryGetValue(ticker, out var sleeve))
                throw new DemoSeedStepException($"the look-through component {ticker} has no sleeve");
            components.Add(new SecurityComponentDto(sleeve.AssetClass, sleeve.Region, weight));
        }

        return components
            .GroupBy(c => (c.AssetClass, c.Region))
            .Select(g => new SecurityComponentDto(g.Key.AssetClass, g.Key.Region, g.Sum(c => c.Weight)))
            .OrderByDescending(c => c.Weight)
            .ThenBy(c => c.AssetClass, StringComparer.Ordinal)
            .ThenBy(c => c.Region, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The stable-value fund's hand-entered prices: its month-end unit value at each
    /// quarter's end, from the quarter its first buy falls in through
    /// <paramref name="asOf"/>, oldest first. The fund has no ticker and is never priced
    /// from a quote, so these are its prices, as a person would type them from a quarterly
    /// statement.
    /// </summary>
    /// <param name="monthEnds">The manifest's month-end unit values.</param>
    /// <param name="firstBuy">The day the ledger first bought the fund.</param>
    /// <param name="asOf">The Demo's last day.</param>
    internal static IReadOnlyList<DemoUnitValue> QuarterEndPrices(
        IReadOnlyList<DemoUnitValue> monthEnds, DateOnly firstBuy, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(monthEnds);
        return monthEnds
            .Where(m => m.Date.Month % 3 == 0 && m.Date.AddDays(1).Day == 1)
            .Where(m => m.Date >= firstBuy && m.Date <= asOf)
            .OrderBy(m => m.Date)
            .ToList();
    }

    /// <summary>The name the card statement's CSV layout is saved under.</summary>
    internal const string CardStatementLayoutName = "Rewards Card statement";

    /// <summary>
    /// The card statement's CSV layout, as the layout editor writes one (the current
    /// version): comma-delimited, one header row, then the ISO date, the bank's description
    /// as the payee, and the amount signed as it moved the card, decimals after a point
    /// (<see cref="CardStatementCsv"/>).
    /// </summary>
    internal const string CardStatementLayoutYaml = """
        version: 2
        delimiter: comma
        header_rows: 1
        date:
          column: 1
          format: yyyy-MM-dd
        payee:
          column: 2
        amount:
          shape: signed
          column: 3
          invert: false
          decimal_separator: point
        """;

    /// <summary>The sliding window the monthly saved reports cover: this many whole
    /// months, the current one to date among them, as Reports' own preset has it.</summary>
    internal const int SavedReportMonths = 12;

    /// <summary>The most category rows the spending report draws; the rest are one
    /// series.</summary>
    internal const int SpendingReportTopN = 6;

    /// <summary>
    /// The Demo's saved reports, in the order they are saved, pinned and shown on Home:
    /// spending by category, month by month; income against spending, month by month (the
    /// cash-flow question, grouped by category kind); and income by payee, year by year,
    /// over the whole history. A trade or a dividend is described by its security's name,
    /// so the last lists each security's income beside the other sources.
    /// </summary>
    /// <remarks>
    /// Each question is a <see cref="ReportSpec"/>, so the compiler checks its types; the
    /// step writes it in the shape Reports saves (<see cref="SavedReportSpecJson"/>) and
    /// reads it back before writing. A report spec has no category filter, so the spending
    /// report covers every expense category, rolled up, with its largest drawn.
    /// </remarks>
    internal static IReadOnlyList<DemoSavedReport> SavedReports(Guid ledgerId) =>
    [
        new("Spending by category, monthly",
            new ReportSpec
            {
                LedgerId = ledgerId,
                Measure = ReportMeasure.Spending,
                GroupBy = ReportGroupBy.Category,
                TimeBucket = ReportTimeBucket.Month,
                DateMode = ReportDateMode.Sliding,
                WindowCount = SavedReportMonths,
                WindowUnit = ReportWindowUnit.Month,
                WindowAlignToPeriod = true,
                Rollup = true,
                TopN = SpendingReportTopN,
            },
            View: "chart", ChartStack: "stacked"),
        new("Income versus spending, monthly",
            new ReportSpec
            {
                LedgerId = ledgerId,
                Measure = ReportMeasure.Net,
                GroupBy = ReportGroupBy.CategoryKind,
                TimeBucket = ReportTimeBucket.Month,
                DateMode = ReportDateMode.Sliding,
                WindowCount = SavedReportMonths,
                WindowUnit = ReportWindowUnit.Month,
                WindowAlignToPeriod = true,
            },
            View: "chart", ChartStack: "grouped"),
        new("Income by payee, yearly",
            new ReportSpec
            {
                LedgerId = ledgerId,
                Measure = ReportMeasure.Income,
                GroupBy = ReportGroupBy.Payee,
                TimeBucket = ReportTimeBucket.Year,
                DateMode = ReportDateMode.Static,
            },
            View: "table", ChartStack: null),
    ];

    /// <summary>
    /// A saved report's spec in the shape Reports saves it, the SPA's <c>ReportSpec</c>:
    /// the summary parameters under their query names, which the summary endpoint reads
    /// (<c>ReportsEndpoints.ReadSummarySpec</c>), each written only when Reports would
    /// send it, plus the drawing choices, which only the SPA reads.
    /// </summary>
    internal static string SavedReportSpecJson(DemoSavedReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var spec = report.Spec;
        var json = new JsonObject
        {
            ["measure"] = spec.Measure.ToString(),
            ["groupBy"] = spec.GroupBy.ToString(),
            ["bucket"] = spec.TimeBucket.ToString(),
            ["dateMode"] = spec.DateMode.ToString(),
        };
        if (spec.DateMode == ReportDateMode.Sliding)
        {
            if (spec.WindowCount is { } count) json["windowCount"] = count;
            json["windowUnit"] = spec.WindowUnit.ToString();
            if (spec.WindowAlignToPeriod) json["windowAlign"] = true;
        }
        else
        {
            if (spec.FromUtc is { } from) json["from"] = Instant(from);
            if (spec.ToUtc is { } to) json["to"] = Instant(to);
        }
        if (spec.TagId is { } tag) json["tagId"] = tag.ToString();
        if (spec.Rollup) json["rollup"] = true;
        if (spec.TopN is { } topN) json["topN"] = topN;
        json["view"] = report.View;
        if (report.ChartStack is { } stack) json["chartStack"] = stack;
        return json.ToJsonString();
    }

    private static string Instant(DateTime at) =>
        DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The description of the snapshot the examples phase takes, as the snapshots
    /// panel lists it: what a restore of it brings back. Within the endpoint's description
    /// limit, and already trimmed as the endpoint trims one.</summary>
    internal const string SnapshotDescription = "The Demo as it was added, with its examples";
}

/// <summary>A saved report the examples phase writes: its name, its question, and how
/// Reports draws it (<c>table</c> or <c>chart</c>, and for a chart over time,
/// <c>stacked</c> or <c>grouped</c>).</summary>
internal sealed record DemoSavedReport(string Name, ReportSpec Spec, string View, string? ChartStack);
