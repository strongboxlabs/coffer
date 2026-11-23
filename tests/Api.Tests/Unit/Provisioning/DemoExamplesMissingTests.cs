using Coffer.Api.Provisioning.DemoSeeding;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// What the ledger list says is missing from a Demo, read from its marker: nothing for a
/// ledger with no marker, a completed one, or one still being built; otherwise the names
/// of the examples not recorded as added, and all of them when the first step failed.
/// </summary>
public sealed class DemoExamplesMissingTests
{
    private static readonly IReadOnlyList<string> AllNames =
        DemoSeedSteps.All.Where(s => s.MissingName is not null).Select(s => s.MissingName!).ToList();

    private static string Details(params string[] addedKeys) =>
        "{" + string.Join(",", DemoSeedSteps.All.Select(s =>
            $"\"{s.Key}\":{(addedKeys.Contains(s.Key) ? 1 : 0)}")) + "}";

    private static string[] AllKeysExcept(params string[] keys) =>
        DemoSeedSteps.All.Select(s => s.Key).Where(k => !keys.Contains(k)).ToArray();

    [Fact]
    public void A_ledger_with_no_marker_is_not_a_Demo() =>
        Assert.Null(DemoExamplesMissing.From(null, null, jobIsLive: false));

    [Fact]
    public void A_completed_Demo_misses_nothing() =>
        Assert.Null(DemoExamplesMissing.From("completed", Details(AllKeysExcept()), jobIsLive: false));

    [Fact]
    public void A_Demo_still_being_built_says_nothing_yet() =>
        Assert.Null(DemoExamplesMissing.From("running", Details(DemoSeedSteps.Resolve), jobIsLive: true));

    [Fact]
    public void A_partial_Demo_names_the_steps_not_added()
    {
        var missing = DemoExamplesMissing.From("partial", Details(AllKeysExcept("budgets", "home")), jobIsLive: false);
        Assert.Equal(["budget targets", "the Home layout"], missing);
    }

    [Fact]
    public void A_step_left_out_of_the_details_counts_as_not_added()
    {
        var missing = DemoExamplesMissing.From("partial", """{"resolve":1,"folders":1}""", jobIsLive: false);
        Assert.Equal(AllNames.Skip(1).ToList(), missing);
    }

    [Fact]
    public void When_the_first_step_failed_every_later_step_is_named()
    {
        // Even an entry recorded as added counts for nothing without the first step.
        var missing = DemoExamplesMissing.From("failed", """{"resolve":0,"folders":1}""", jobIsLive: false);
        Assert.Equal(AllNames, missing);
    }

    [Fact]
    public void A_running_marker_with_no_live_job_names_what_it_had_not_reached()
    {
        var missing = DemoExamplesMissing.From(
            "running", Details(DemoSeedSteps.Resolve, "folders", "investments"), jobIsLive: false);
        Assert.Equal(AllNames.Skip(2).ToList(), missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"resolve":"1"}""")]
    public void Unreadable_details_name_every_step(string details) =>
        Assert.Equal(AllNames, DemoExamplesMissing.From("partial", details, jobIsLive: false));

    [Fact]
    public void A_partial_marker_with_every_step_added_says_nothing() =>
        Assert.Null(DemoExamplesMissing.From("partial", Details(AllKeysExcept()), jobIsLive: false));

    [Fact]
    public void The_names_are_the_ones_the_hub_lists() =>
        Assert.Equal(
            [
                "folders", "investment settings", "budget targets", "register examples",
                "the card statement import", "reminders", "saved reports", "the Home layout",
                "the snapshot", "the consistency check",
            ],
            AllNames);
}
