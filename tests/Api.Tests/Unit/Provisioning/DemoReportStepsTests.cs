using System.Text.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Endpoints;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// What the saved-reports and Home steps check before they write: each saved report passes
/// the create endpoint's checks and reads back, through the summary endpoint's own
/// reading, as the question it was built as; and the Home layout file's references
/// resolve to the Demo's rows into a layout the PUT endpoint's checks accept, or fail
/// naming what they could not find.
/// </summary>
public sealed class DemoReportStepsTests
{
    private static readonly Guid LedgerId = Guid.NewGuid();

    [Fact]
    public void The_saved_reports_pass_the_create_checks_and_are_stored_as_Reports_saves_them()
    {
        var reports = DemoReportSteps.CheckReports(DemoSeedContent.SavedReports(LedgerId), LedgerId, DemoTestExports.AsOf);

        Assert.Equal(
            ["Spending by category, monthly", "Income versus spending, monthly", "Income by payee, yearly"],
            reports.Select(r => r.Name));
        var stored = reports.Select(r => JsonDocument.Parse(r.Spec).RootElement).ToList();
        Assert.Equal(
            new (string?, string?, string?, string?)[]
            {
                ("Spending", "Category", "Month", "Sliding"),
                ("Net", "CategoryKind", "Month", "Sliding"),
                ("Income", "Payee", "Year", "Static"),
            },
            stored.Select(s => (Text(s, "measure"), Text(s, "groupBy"), Text(s, "bucket"), Text(s, "dateMode"))));

        // The monthly two slide over whole months, as Reports' own year preset does.
        foreach (var monthly in stored.Take(2))
        {
            Assert.Equal(DemoSeedContent.SavedReportMonths, monthly.GetProperty("windowCount").GetInt32());
            Assert.Equal("Month", Text(monthly, "windowUnit"));
            Assert.True(monthly.GetProperty("windowAlign").GetBoolean());
        }
        // The yearly one is the whole history: no bounds.
        Assert.False(stored[2].TryGetProperty("from", out _));
        Assert.False(stored[2].TryGetProperty("to", out _));
        // Every key is one the SPA's ReportSpec has.
        string[] spaKeys =
        [
            "measure", "groupBy", "bucket", "dateMode", "windowCount", "windowUnit", "windowAlign",
            "from", "to", "tagId", "rollup", "topN", "view", "chartStack",
        ];
        Assert.All(stored.SelectMany(s => s.EnumerateObject()), p => Assert.Contains(p.Name, spaKeys));
    }

    [Fact]
    public void A_report_the_create_endpoint_refuses_fails_the_check()
    {
        var blank = Report(" ", new ReportSpec { LedgerId = LedgerId });

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReportSteps.CheckReports([blank], LedgerId, DemoTestExports.AsOf));

        Assert.Equal("the saved report ' ' was refused: name is required.", failure.Message);
    }

    [Fact]
    public void A_sliding_report_with_no_window_fails_the_check()
    {
        var report = Report("No window", new ReportSpec { LedgerId = LedgerId, DateMode = ReportDateMode.Sliding });

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReportSteps.CheckReports([report], LedgerId, DemoTestExports.AsOf));

        Assert.StartsWith("the saved report 'No window' has no window: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_whose_stored_shape_loses_part_of_its_question_fails_the_check()
    {
        // Reports never sends a window count for fixed dates, so the stored spec cannot
        // carry this one, and would read back as another question.
        var report = Report("Lost count", new ReportSpec { LedgerId = LedgerId, WindowCount = 3 });

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReportSteps.CheckReports([report], LedgerId, DemoTestExports.AsOf));

        Assert.Equal("the saved report 'Lost count' reads back as another report than it was built as", failure.Message);
    }

    [Fact]
    public void A_blank_catalog_pin_fails_and_keys_are_trimmed_as_the_endpoint_trims_them()
    {
        Assert.Equal(["returns"], DemoReportSteps.CheckCatalogPins([" returns "]));
        Assert.Throws<DemoSeedStepException>(() => DemoReportSteps.CheckCatalogPins(["returns", "  "]));
    }

    [Fact]
    public void The_Home_layout_resolves_to_the_Demo_s_rows_and_passes_the_endpoint_s_checks()
    {
        var ids = StandInIds();
        var saved = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var json = DemoHomeLayout.Embedded.Resolve(ids, saved);

        var check = PreferencesEndpoints.CheckDashboard(json);
        Assert.Null(check.Problem);
        Assert.Null(check.UpdatedAt);
        using var stored = JsonDocument.Parse(check.Stored!);
        var widgets = stored.RootElement.GetProperty("widgets").EnumerateArray().ToList();
        Assert.Equal(2, stored.RootElement.GetProperty("version").GetInt32());

        var watchlist = Assert.Single(widgets, w => Text(w, "key") == "watchlist");
        Assert.Equal(
            new[] { ("Personal", "Groceries"), ("Personal", "Dining"), ("Personal", "Entertainment"), ("Personal", "Clothing"), ("Bills", "Electric") }
                .Select(p => ids.Category(new DemoCategoryRef(MdCategoryType.Expense, [p.Item1, p.Item2])).ToString()),
            watchlist.GetProperty("settings").GetProperty("categoryIds").EnumerateArray().Select(e => e.GetString()));

        var forecast = Assert.Single(widgets, w => Text(w, "key") == "cash-forecast");
        Assert.Equal(
            ids.Accounts[DemoAccountRoles.JointChecking].ToString(),
            Text(forecast.GetProperty("settings"), "focusAccountId"));

        Assert.Equal(
            saved.Select(id => "report:saved:" + id.ToString().ToLowerInvariant()),
            widgets.Where(w => Text(w, "key") == "report").Select(w => Text(w, "id")));
        Assert.Equal(["income-and-expenses", "returns"], DemoHomeLayout.Embedded.CatalogPins);
    }

    [Fact]
    public void A_reference_that_finds_nothing_fails_naming_each_one()
    {
        var ids = StandInIds();
        var withoutDining = ids with
        {
            CategoriesByPath = ids.CategoriesByPath
                .Where(p => p.Key != new DemoCategoryRef(MdCategoryType.Expense, ["Personal", "Dining"]).ToString())
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };

        var failure = Assert.Throws<DemoSeedStepException>(
            () => DemoHomeLayout.Embedded.Resolve(withoutDining, [Guid.NewGuid(), Guid.NewGuid()]));

        Assert.Equal(
            "the Home layout names what this Demo does not have: no saved report 3 of the 2 saved; "
            + "no category e Personal > Dining",
            failure.Message);
    }

    [Fact]
    public void A_reference_the_layout_may_not_use_fails()
    {
        var layout = DemoHomeLayout.Parse("""
            {"catalogPins": [], "layout": {"version": 2, "columns": "single", "widgets": [
              {"id": "cash-forecast", "key": "cash-forecast", "visible": true, "column": "side",
               "settings": {"focusAccountId": {"securityRef": "fund"}}}]}}
            """);

        var failure = Assert.Throws<DemoSeedStepException>(() => layout.Resolve(StandInIds(), []));

        Assert.Equal(
            "the Home layout names what this Demo does not have: securityRef \"fund\" is not a reference this layout may use",
            failure.Message);
    }

    private static DemoSavedReport Report(string name, ReportSpec spec) => new(name, spec, "table", null);

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    /// <summary>A stand-in id for every account and category the Tiny manifest names.</summary>
    private static DemoSeedIds StandInIds()
    {
        var manifest = DemoTestExports.Tiny.Value.Manifest;
        var categories = manifest.Categories.Values.Concat(manifest.BudgetTargets.Select(t => t.Category))
            .Select(c => c.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, _ => Guid.NewGuid(), StringComparer.Ordinal);
        return new DemoSeedIds
        {
            Accounts = manifest.AccountIds.ToDictionary(p => p.Key, _ => Guid.NewGuid(), StringComparer.Ordinal),
            AccountsByMdId = new Dictionary<string, Guid>(StringComparer.Ordinal),
            Categories = manifest.Categories.ToDictionary(
                p => p.Key, p => categories[p.Value.ToString()], StringComparer.Ordinal),
            CategoriesByPath = categories,
            Transactions = new Dictionary<string, Guid>(StringComparer.Ordinal),
            Securities = new Dictionary<string, Guid>(StringComparer.Ordinal),
            InternetReminder = Guid.NewGuid(),
        };
    }
}
