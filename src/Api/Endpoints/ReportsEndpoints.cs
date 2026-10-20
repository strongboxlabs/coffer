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
/// <para><see cref="ReportingRepository"/> shipped with the MCP server
/// (ADR-0063) and had exactly one REST caller — budget progress — so the SPA
/// had nothing general to call. This is that surface, and deliberately the SAME
/// repository rather than a parallel query path, which is the thing FU-008 asks
/// for in as many words.</para>
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

        return routes;
    }

    private static async Task<IResult> SummaryAsync(
        Guid ledgerId,
        HttpRequest http,
        ReportingRepository reporting,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryEnum<ReportMeasure>(q["measure"], ReportMeasure.Spending, out var measure))
            return Bad("measure", "Spending, Income or Net");
        if (!TryEnum<ReportGroupBy>(q["groupBy"], ReportGroupBy.Category, out var groupBy))
            return Bad("groupBy", "Category, Account, Payee, Tag or CategoryKind");
        if (!TryEnum<ReportTimeBucket>(q["bucket"], ReportTimeBucket.None, out var bucket))
            return Bad("bucket", "None, Day, Month, Quarter or Year");
        if (!TryEnum<ReportDateMode>(q["dateMode"], ReportDateMode.Static, out var dateMode))
            return Bad("dateMode", "Static or Sliding");
        if (!TryEnum<ReportWindowUnit>(q["windowUnit"], ReportWindowUnit.Month, out var windowUnit))
            return Bad("windowUnit", "Day, Month, Quarter or Year");

        if (!TryInt(q["windowCount"], out var windowCount)) return Bad("windowCount", "a whole number");
        if (!TryInt(q["topN"], out var topN)) return Bad("topN", "a whole number");
        if (!TryGuid(q["tagId"], out var tagId)) return Bad("tagId", "a GUID");
        if (!TryDate(q["from"], out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(q["to"], out var toUtc)) return Bad("to", "an ISO-8601 instant");

        var spec = new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = measure,
            GroupBy = groupBy,
            TimeBucket = bucket,
            DateMode = dateMode,
            WindowCount = windowCount,
            WindowUnit = windowUnit,
            WindowAlignToPeriod = IsTrue(q["windowAlign"]),
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TagId = tagId,
            Rollup = IsTrue(q["rollup"]),
            TopN = topN,
        };

        try
        {
            return Results.Ok(await reporting.SummarizeAsync(spec, cancellationToken).ConfigureAwait(false));
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
    /// period in a window (FU-008). The first of the three surfaces that
    /// <see cref="AccountsReportingRepository"/> and
    /// <see cref="InvestmentReportingRepository"/> were missing, which is why
    /// the catalog could not list net worth at all.
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

        try
        {
            return Results.Ok(await accounts
                .NetWorthHistoryAsync(ledgerId, fromUtc.Value, toUtc.Value, interval, cancellationToken)
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
    ///   every other one. The DTO records the measured case: one such fund
    ///   holding most of a portfolio made equity read as a small fraction of
    ///   its true share. A pie drawn over that is confidently wrong, so the caller gets the flag.</item>
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

        return Results.Ok(await investments
            .AllocationAsync(ledgerId, dimension, asOfUtc, cancellationToken)
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
    /// for ONE window, plus start value, end value and net contributions. An
    /// indexed performance line of the kind a dashboard draws needs a SERIES
    /// of returns, which nothing in the reporting layer computes — so it is a
    /// missing calculation, not a missing endpoint, and this surface does not
    /// pretend otherwise by returning a two-point line.</para>
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
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var q = http.Query;

        if (!TryGuid(q["accountId"], out var accountId)) return Bad("accountId", "a GUID");
        if (!TryDate(q["from"], out var fromUtc)) return Bad("from", "an ISO-8601 instant");
        if (!TryDate(q["to"], out var toUtc)) return Bad("to", "an ISO-8601 instant");

        // Both optional, unlike net-worth history: omitted means
        // since-inception to now, which is exact here rather than invented —
        // the scope resolver derives the start from the ledger's own first
        // flow.
        return Results.Ok(await investments
            .ReturnsAsync(ledgerId, accountId, fromUtc, toUtc, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false));
    }

    private static IResult Bad(string field, string expected) =>
        Results.BadRequest(new { error = $"{field} must be {expected}." });

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
