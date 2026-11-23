using System.Text.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The examples steps that set up what the Demo's owner sees first: the saved reports and
/// their pins, and the Home layout that shows them (ADR-0115 D3).
/// </summary>
/// <remarks>
/// Each writes through the repository the app's own endpoint writes through, after that
/// endpoint's checks, which it shares. Every report and the layout are checked whole
/// before the first write, so content the app would refuse writes nothing.
/// </remarks>
internal static class DemoReportSteps
{
    /// <summary>
    /// Step 18: the saved reports (<see cref="DemoSeedContent.SavedReports"/>), private to
    /// the Demo's owner, each pinned to their rail in order, then the catalog reports the
    /// Home layout file pins. Each spec passes the create endpoint's checks and reads back
    /// as the report it was built as before anything is written. Hands the reports' ids to
    /// the Home step.
    /// </summary>
    internal static async Task ReportsAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var ledgerId = run.LedgerId;
        var owner = run.Context.OwnerUserId;
        var reports = CheckReports(DemoSeedContent.SavedReports(ledgerId), ledgerId, run.AsOf);
        var catalogPins = CheckCatalogPins(DemoHomeLayout.Embedded.CatalogPins);

        var saved = new SavedReportsRepository(db, run.Clock);
        var ids = new List<Guid>(reports.Count);
        foreach (var (name, spec) in reports)
        {
            var row = await saved
                .CreateAsync(ledgerId, owner, name, spec, SavedReportVisibility.Private, cancellationToken)
                .ConfigureAwait(false);
            ids.Add(row.Id);
        }
        run.SavedReportIds = ids;

        var pins = new ReportPinsRepository(db, run.Clock);
        foreach (var id in ids)
        {
            if (!await pins.PinSavedAsync(ledgerId, id, owner, cancellationToken).ConfigureAwait(false))
                throw new DemoSeedStepException($"the saved report {id} could not be pinned: its owner cannot see it");
        }
        foreach (var key in catalogPins)
            await pins.PinCatalogAsync(ledgerId, key, owner, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Step 19: the Home layout file's layout, its references resolved to this Demo's
    /// rows and the saved reports' tiles, saved for the owner as a first save is: only
    /// when no layout is stored yet. One the owner saved while the examples were being
    /// added is kept, and the step fails saying so.
    /// </summary>
    internal static Task HomeAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken) =>
        SaveHomeAsync(run, db, DemoHomeLayout.Embedded, cancellationToken);

    /// <summary><see cref="HomeAsync"/> for a given layout file.</summary>
    internal static async Task SaveHomeAsync(
        DemoSeedRun run, AppDbContext db, DemoHomeLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(layout);
        var document = layout.Resolve(run.Ids, run.SavedReportIds);
        var check = PreferencesEndpoints.CheckDashboard(document);
        if (check.Problem is { } problem)
            throw new DemoSeedStepException("the Home layout was refused: " + BusinessError.Describe(problem));

        var savedAt = await new UserPreferencesRepository(db, run.Clock)
            .SaveDashboardAsync(run.Context.OwnerUserId, run.LedgerId, check.Stored!, expectedUpdatedAt: null, cancellationToken)
            .ConfigureAwait(false);
        if (savedAt is null)
            throw new DemoSeedStepException(
                "a Home layout was saved on this Demo before the examples reached it, and it is kept");
    }

    /// <summary>
    /// Each report as it is stored, by its trimmed name, once every one has passed the
    /// create endpoint's checks and read back, through the summary endpoint's own reading,
    /// as the question it was built as, with a window the summary can resolve.
    /// </summary>
    /// <exception cref="DemoSeedStepException">A report would be refused, or would read
    /// as another question.</exception>
    internal static IReadOnlyList<(string Name, string Spec)> CheckReports(
        IEnumerable<DemoSavedReport> reports, Guid ledgerId, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var dayAfter = asOf.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var checkedReports = new List<(string Name, string Spec)>();
        foreach (var report in reports)
        {
            var json = DemoSeedContent.SavedReportSpecJson(report);
            using var spec = JsonDocument.Parse(json);
            if (SavedReportsEndpoints.Validate(report.Name, spec.RootElement, SavedReportVisibility.Private) is { } refused)
                throw new DemoSeedStepException(
                    $"the saved report '{report.Name}' was refused: {BusinessError.Describe(refused)}");

            var root = spec.RootElement;
            if (ReportsEndpoints.ReadSummarySpec(ledgerId, name => Parameter(root, name), out var read) is { } unread)
                throw new DemoSeedStepException(
                    $"the saved report '{report.Name}' does not read as a report: {BusinessError.Describe(unread)}");
            if (!SameQuestion(report.Spec, read!))
                throw new DemoSeedStepException(
                    $"the saved report '{report.Name}' reads back as another report than it was built as");
            try
            {
                read!.ResolveWindow(dayAfter);
            }
            catch (ReportRequestException ex)
            {
                throw new DemoSeedStepException($"the saved report '{report.Name}' has no window: {ex.Message}", ex);
            }

            checkedReports.Add((report.Name.Trim(), json));
        }
        return checkedReports;
    }

    /// <summary>The catalog keys to pin, trimmed, as the pin endpoint takes them, once
    /// none is blank.</summary>
    internal static IReadOnlyList<string> CheckCatalogPins(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var trimmed = keys.Select(k => k.Trim()).ToList();
        if (trimmed.Any(k => k.Length == 0))
            throw new DemoSeedStepException("the Home layout file pins a catalog report with no key");
        return trimmed;
    }

    /// <summary>A stored spec's value for one summary parameter, as Reports sends it: a
    /// string as itself, a number as written, <c>true</c> as "true", and anything absent,
    /// null or false as not sent.</summary>
    private static string? Parameter(JsonElement spec, string name) =>
        !spec.TryGetProperty(name, out var value) ? null : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };

    private static bool SameQuestion(ReportSpec built, ReportSpec read) =>
        built.Measure == read.Measure
        && built.GroupBy == read.GroupBy
        && built.TimeBucket == read.TimeBucket
        && built.DateMode == read.DateMode
        && built.WindowCount == read.WindowCount
        && built.WindowUnit == read.WindowUnit
        && built.WindowAlignToPeriod == read.WindowAlignToPeriod
        && built.FromUtc == read.FromUtc
        && built.ToUtc == read.ToUtc
        && built.TagId == read.TagId
        && built.Rollup == read.Rollup
        && built.TopN == read.TopN;
}
