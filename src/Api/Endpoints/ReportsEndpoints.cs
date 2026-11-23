using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The transaction-aggregation report over HTTP (FU-008, ADR-0104).
/// </summary>
/// <remarks>
/// <para>The SPA's general report surface over <see cref="ReportingRepository"/>
/// (ADR-0063), deliberately the SAME repository the MCP server and budget progress
/// use rather than a parallel query path, which is the thing FU-008 asks for in as
/// many words.</para>
///
/// <para>A GET with query parameters, not a POST with a spec body. Every
/// <see cref="ReportSpec"/> field is a scalar, so the whole spec fits in a
/// query string — and that keeps a report a URL you can link, reload and send,
/// which a POST would take away for no gain. It also means the browser and any
/// cache in front of the API can treat a report as what it is: a read.</para>
/// </remarks>
public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder routes)
    {
        // Bound to a local because MapGet returns a RouteHandlerBuilder, not
        // the group — chaining a second one off the first compiles against the
        // wrong receiver.
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/reports")
                          .RequireAuthorization()
                          .RequireLedgerMembership();

        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/net-worth-history", NetWorthHistoryAsync);
        group.MapGet("/allocation", AllocationAsync);
        group.MapGet("/returns", ReturnsAsync);
        group.MapGet("/returns-series", ReturnsSeriesAsync);

        return routes;
    }

    private static async Task<IResult> SummaryAsync(
        Guid ledgerId,
        HttpRequest http,
        ReportingRepository reporting,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (ReadSummarySpec(ledgerId, name => q[name], out var spec) is { } problem) return problem;
        // The caller's local date: a sliding window then ends where the register's
        // Scheduled begins. A request parameter, never part of the spec a saved
        // report persists.
        if (!TryDay(q["today"], out var today)) return Bad("today", "a yyyy-MM-dd date");

        try
        {
            return Results.Ok(await reporting.SummaryAsync(spec!, today, cancellationToken).ConfigureAwait(false));
        }
        catch (ReportRequestException ex)
        {
            // The repository refuses a daily bucket on anything but Category,
            // because honouring it would silently return monthly buckets
            // labelled as days, and a sliding window with no count. Caller
            // errors, so a 400 here rather than the 500 an unhandled throw would
            // produce. Only these: any other exception is not the caller's to
            // read (FU-095).
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// <c>GET /reports/net-worth-history</c> — net worth at the end of each
    /// period in a window (FU-008). The REST surface over
    /// <see cref="AccountsReportingRepository"/>'s series, which the MCP tool
    /// also reads; without it the catalog cannot list net worth.
    /// </summary>
    /// <remarks>
    /// <para>`from` and `to` are REQUIRED here, unlike on <c>/summary</c>. A
    /// summary with no window is "everything", which is a sensible report; a
    /// net-worth series with no window would have to invent its own bounds and
    /// would then silently disagree with whatever the caller assumed. The
    /// repository also caps the number of points, and a cap over an unbounded
    /// range is a coin toss.</para>
    /// </remarks>
    private static async Task<IResult> NetWorthHistoryAsync(
        Guid ledgerId,
        HttpRequest http,
        AccountsReportingRepository accounts,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryEnum<ReportTimeBucket>(q["interval"], ReportTimeBucket.Month, out var interval))
            return Bad("interval", "Month, Quarter or Year");
        if (!TryDate(q["from"], out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(q["to"], out var toUtc)) return Bad("to", "an ISO-8601 instant");
        if (fromUtc is null) return Bad("from", "supplied — a net-worth series needs a window");
        if (toUtc is null) return Bad("to", "supplied — a net-worth series needs a window");
        // The caller's local date: the series then ends where the overview's "now" does.
        if (!TryDay(q["today"], out var today)) return Bad("today", "a yyyy-MM-dd date");

        try
        {
            return Results.Ok(await accounts
                .NetWorthHistoryAsync(ledgerId, fromUtc.Value, toUtc.Value, interval, today, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ReportRequestException ex)
        {
            // Two caller errors share this arm: too many points for the range
            // and interval, and a daily interval, which the repository refuses
            // rather than quietly answering in months.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// <c>GET /reports/allocation</c> — portfolio composition at an instant
    /// (FU-008). The one report shape where a proportional chart is honest,
    /// because shares of a whole cannot go negative.
    /// </summary>
    /// <remarks>
    /// <para>The response is passed through WHOLE rather than reduced to
    /// buckets, and that is the point. Two fields decide whether the buckets
    /// may be drawn as a complete picture at all:</para>
    /// <list type="bullet">
    ///   <item><c>undecomposedMultiAssets</c> — a multi-asset fund with no
    ///   component weights sits entirely in one opaque bucket and understates
    ///   every other one: the more of a portfolio such a fund holds, the
    ///   further below its true share equity reads. A pie drawn over that is confidently wrong, so the caller gets the flag.</item>
    ///   <item><c>excludedBrokerageCash</c> — <c>totalMarketValue</c> is
    ///   SECURITIES ONLY. Uninvested cash has no asset class, so a chart that
    ///   treats the buckets as the whole portfolio is short by this.</item>
    /// </list>
    ///
    /// <para>Dimension spellings match the <c>allocation</c> MCP tool
    /// (<c>asset_class</c>, <c>vehicle</c>, …) rather than the C# enum names,
    /// so there is one vocabulary for the two callers instead of a snake_case
    /// one for the LLM and a PascalCase one for the SPA.</para>
    /// </remarks>
    private static async Task<IResult> AllocationAsync(
        Guid ledgerId,
        HttpRequest http,
        InvestmentReportingRepository investments,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryAllocationDimension(q["dimension"], out var dimension))
            return Bad("dimension", "asset_class, region, vehicle, security or account");
        if (!TryDate(q["asOf"], out var asOfUtc)) return Bad("asOf", "an ISO-8601 instant");
        // The caller's local date places "now" when no instant is asked for, as it
        // does for returns, so the two value the same instant.
        if (!TryDay(q["today"], out var today)) return Bad("today", "a yyyy-MM-dd date");

        return Results.Ok(await investments
            .AllocationAsync(ledgerId, dimension, asOfUtc, today, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// The MCP tool's vocabulary, minus its throw. An unknown value is a 400
    /// here rather than an exception, but it is still REFUSED — defaulting a
    /// misspelling to asset_class would answer a question nobody asked and
    /// label it as the one they did (ADR-0063 D4).
    /// </summary>
    private static bool TryAllocationDimension(string? raw, out AllocationDimension value)
    {
        value = AllocationDimension.AssetClass;
        if (string.IsNullOrEmpty(raw)) return true;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "asset_class" or "assetclass": value = AllocationDimension.AssetClass; return true;
            case "region":                      value = AllocationDimension.Region;     return true;
            case "vehicle" or "vehicle_type" or "vehicletype":
                                                value = AllocationDimension.VehicleType; return true;
            case "security":                    value = AllocationDimension.Security;   return true;
            case "account":                     value = AllocationDimension.Account;    return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>GET /reports/returns</c> — time- and money-weighted return over a
    /// window (FU-008). The last of the three surfaces.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a number, not a curve.</b> The result is TWR and MWR
    /// for ONE window, plus start value, end value, net contributions and gain:
    /// the window's change in value that contributions and withdrawals do not
    /// explain (price movement, realized or not, plus dividends and interest, less
    /// fees and investment expenses; not annualized; for the whole scope). A
    /// performance line over time is a different calculation with its own
    /// endpoint, <c>/reports/returns-series</c>, which chains the same
    /// time-weighted return and reads each period end as a mark on it; this
    /// surface does not pretend otherwise by returning a two-point line.</para>
    ///
    /// <para>Passed through whole, for the same reason allocation is. Either
    /// rate can be null with a reason beside it (too short a window, a gap in
    /// valuations), and a caller that read only the rate would plot a zero
    /// where the honest answer is "not computable". <c>accounts</c> carries
    /// every brokerage the window covered at ledger scope, which is the only
    /// safe way to pick accounts: choosing by CURRENT balance silently drops
    /// any account that held money when the window opened and was emptied
    /// before it closed, and a rollover source is exactly that.</para>
    /// </remarks>
    private static async Task<IResult> ReturnsAsync(
        Guid ledgerId,
        HttpRequest http,
        InvestmentReportingRepository investments,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryGuid(q["accountId"], out var accountId)) return Bad("accountId", "a GUID");
        if (!TryDate(q["from"], out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(q["to"], out var toUtc)) return Bad("to", "an ISO-8601 instant");
        // The caller's local date: "now" is then the end of their today as the
        // register counts it, the instant the overview values investments at.
        if (!TryDay(q["today"], out var today)) return Bad("today", "a yyyy-MM-dd date");

        // Both optional, unlike net-worth history: omitted means
        // since-inception to now, which is exact here rather than invented —
        // the scope resolver derives the start from the ledger's own first
        // flow.
        try
        {
            return Results.Ok(await investments
                .ReturnsAsync(ledgerId, accountId, fromUtc, toUtc, today, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ReportRequestException ex)
        {
            // An accountId that is not one of the ledger's brokerages.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// <c>GET /reports/returns-series</c> — the time-weighted index, cumulative gain and
    /// money-weighted return of the 12 months ending there, at the end of each period
    /// (ADR-0104 D6).
    /// </summary>
    /// <remarks>
    /// <para>The window is optional. With no <c>from</c> (and no sliding window) the series
    /// starts at the scope's own inception, and with no <c>to</c> it ends at the caller's
    /// today. <c>dateMode=Sliding</c> with <c>windowCount</c>, <c>windowUnit</c> and
    /// <c>windowAlign</c> resolves exactly as on <c>/summary</c>, so a saved report slides.</para>
    /// <para>The result is passed through whole: a point's rate is null with its reason beside
    /// it. The ledger series is converted into the reporting currency, each brokerage's own
    /// stays in its currency, and a point that needs a missing rate is null with the
    /// <c>rate_unavailable</c> reason and names it (ADR-0114 D7, D9).</para>
    /// <para><c>benchmark</c> names one of the ledger's benchmark symbols, and every point then
    /// carries its value (ADR-0114 D3). A value that is not a symbol is a 400. A symbol that
    /// is not one of the ledger's is answered, not refused: a saved report keeps its pick
    /// after the symbol is removed, and the result says <c>benchmark_not_in_ledger</c>
    /// (ADR-0114 D1).</para>
    /// </remarks>
    private static async Task<IResult> ReturnsSeriesAsync(
        Guid ledgerId,
        HttpRequest http,
        InvestmentReportingRepository investments,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryGuid(q["accountId"], out var accountId)) return Bad("accountId", "a GUID");
        if (!TryEnum<ReportTimeBucket>(q["interval"], ReportTimeBucket.Month, out var interval)
            || interval == ReportTimeBucket.None)
            return Bad("interval", "Month, Quarter or Year");
        if (!TryEnum<ReportDateMode>(q["dateMode"], ReportDateMode.Static, out var dateMode))
            return Bad("dateMode", "Static or Sliding");
        if (!TryEnum<ReportWindowUnit>(q["windowUnit"], ReportWindowUnit.Month, out var windowUnit))
            return Bad("windowUnit", "Day, Month, Quarter or Year");
        if (!TryInt(q["windowCount"], out var windowCount)) return Bad("windowCount", "a whole number");
        if (!TryDate(q["from"], out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(q["to"], out var toUtc)) return Bad("to", "an ISO-8601 instant");
        if (!TryDay(q["today"], out var today)) return Bad("today", "a yyyy-MM-dd date");
        string? benchmark = q["benchmark"];
        if (string.IsNullOrEmpty(benchmark)) benchmark = null;
        else if (!BenchmarkSymbol.IsValid(benchmark)) return Bad("benchmark", "a benchmark symbol, such as VT");

        var spec = new ReportSpec
        {
            LedgerId = ledgerId,
            DateMode = dateMode,
            WindowCount = windowCount,
            WindowUnit = windowUnit,
            WindowAlignToPeriod = IsTrue(q["windowAlign"]),
            FromUtc = fromUtc,
            ToUtc = toUtc,
        };

        try
        {
            // Resolved here, against the caller's day, as /summary does.
            var (from, to) = today is null
                ? spec.ResolveWindow(clock.GetUtcNow().UtcDateTime)
                : spec.ResolveWindow(
                    ScheduledBoundary.LastCountedInstant(today, clock),
                    ScheduledBoundary.ScheduledFrom(today, clock));

            return Results.Ok(await investments
                .ReturnsSeriesAsync(
                    ledgerId, accountId, IsTrue(q["includeAccounts"]), from, to, interval, today,
                    cancellationToken, benchmark)
                .ConfigureAwait(false));
        }
        catch (ReportRequestException ex)
        {
            // A daily interval, a range past the period cap, a sliding window with no count.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IResult Bad(string field, string expected) =>
        Results.BadRequest(new { error = $"{field} must be {expected}." });

    /// <summary>
    /// The aggregation report's question, read from the parameters a report spec is sent
    /// as: <paramref name="parameter"/> gives each one's value by name, or null when it is
    /// absent. A saved report stores its spec under these same names, so this is also how
    /// a stored spec reads as a <see cref="ReportSpec"/>.
    /// </summary>
    /// <returns>The 400 a malformed parameter gets, with <paramref name="spec"/> null; or
    /// null, with the spec.</returns>
    /// <remarks>Shared with the Demo's examples phase, which reads each saved report it
    /// writes back through it before writing (ADR-0115).</remarks>
    internal static IResult? ReadSummarySpec(Guid ledgerId, Func<string, string?> parameter, out ReportSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        spec = null;
        if (!TryEnum<ReportMeasure>(parameter("measure"), ReportMeasure.Spending, out var measure))
            return Bad("measure", "Spending, Income or Net");
        if (!TryEnum<ReportGroupBy>(parameter("groupBy"), ReportGroupBy.Category, out var groupBy))
            return Bad("groupBy", "Category, Account, Payee, Tag or CategoryKind");
        if (!TryEnum<ReportTimeBucket>(parameter("bucket"), ReportTimeBucket.None, out var bucket))
            return Bad("bucket", "None, Day, Month, Quarter or Year");
        if (!TryEnum<ReportDateMode>(parameter("dateMode"), ReportDateMode.Static, out var dateMode))
            return Bad("dateMode", "Static or Sliding");
        if (!TryEnum<ReportWindowUnit>(parameter("windowUnit"), ReportWindowUnit.Month, out var windowUnit))
            return Bad("windowUnit", "Day, Month, Quarter or Year");

        if (!TryInt(parameter("windowCount"), out var windowCount)) return Bad("windowCount", "a whole number");
        if (!TryInt(parameter("topN"), out var topN)) return Bad("topN", "a whole number");
        if (!TryGuid(parameter("tagId"), out var tagId)) return Bad("tagId", "a GUID");
        if (!TryDate(parameter("from"), out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(parameter("to"), out var toUtc)) return Bad("to", "an ISO-8601 instant");

        spec = new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = measure,
            GroupBy = groupBy,
            TimeBucket = bucket,
            DateMode = dateMode,
            WindowCount = windowCount,
            WindowUnit = windowUnit,
            WindowAlignToPeriod = IsTrue(parameter("windowAlign")),
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TagId = tagId,
            Rollup = IsTrue(parameter("rollup")),
            TopN = topN,
        };
        return null;
    }

    private static bool TryEnum<T>(string? raw, T fallback, out T value) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw)) { value = fallback; return true; }
        return Enum.TryParse(raw, ignoreCase: true, out value);
    }

    private static bool TryInt(string? raw, out int? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!int.TryParse(raw, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryGuid(string? raw, out Guid? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!Guid.TryParse(raw, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryDay(string? raw, out DateOnly? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryDate(string? raw, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// A flag is true when present and not an explicit "false" — so
    /// <c>?rollup</c> and <c>?rollup=true</c> both mean on, which is how a URL
    /// anyone hand-edits is expected to behave.
    /// </summary>
    private static bool IsTrue(string? raw) =>
        raw is not null && !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
}
