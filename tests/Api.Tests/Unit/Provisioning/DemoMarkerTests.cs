using System.Text.Json;

using Coffer.Api.Provisioning.DemoSeeding;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// The shape of the Demo marker's record, as the ledger list and the Activity summary read
/// it, how the startup sweep closes a marker a restart left running, and the seeder's table
/// of step bodies.
/// </summary>
public sealed class DemoMarkerTests
{
    [Fact]
    public void The_details_hold_every_step_once_with_1_for_each_added_and_0_for_the_rest()
    {
        var details = JsonSerializer.Deserialize<Dictionary<string, int>>(
            DemoMarker.Details(new HashSet<string>(StringComparer.Ordinal) { DemoSeedSteps.Resolve, "folders" }))!;

        Assert.Equal(DemoSeedSteps.All.Select(s => s.Key), details.Keys);
        Assert.Equal(1, details[DemoSeedSteps.Resolve]);
        Assert.Equal(1, details["folders"]);
        Assert.Equal(DemoSeedSteps.Count - 2, details.Values.Count(v => v == 0));
    }

    [Fact]
    public void The_sweep_closes_a_marker_that_records_every_step_as_completed_with_no_error()
    {
        var every = DemoSeedSteps.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Equal((DemoMarker.Completed, (string?)null), DemoMarkerSweep.CloseAs(DemoMarker.Details(every)));
    }

    [Fact]
    public void The_sweep_closes_a_marker_with_steps_not_recorded_as_partial_naming_them()
    {
        var details = DemoMarker.Details(new HashSet<string>(StringComparer.Ordinal)
        {
            DemoSeedSteps.Resolve, "folders", "investments", "budgets", "register", "card-statement",
            "reminders", "reports", "home",
        });

        Assert.Equal(
            (DemoMarker.Partial,
                (string?)"Interrupted: the server restarted before these examples were added: the snapshot, the consistency check."),
            DemoMarkerSweep.CloseAs(details));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void The_sweep_closes_a_marker_with_nothing_readable_recorded_as_partial_naming_every_step(string details)
    {
        var every = DemoSeedSteps.All.Where(s => s.MissingName is not null).Select(s => s.MissingName!);

        Assert.Equal(
            (DemoMarker.Partial,
                (string?)("Interrupted: the server restarted before these examples were added: " + string.Join(", ", every) + ".")),
            DemoMarkerSweep.CloseAs(details));
    }

    [Fact]
    public void The_interrupted_message_names_each_step_not_recorded_as_added()
    {
        var details = DemoMarker.Details(new HashSet<string>(StringComparer.Ordinal)
        {
            DemoSeedSteps.Resolve, "folders", "investments", "budgets", "register", "card-statement",
            "reminders", "reports", "home",
        });

        Assert.Equal(
            "Interrupted: the server restarted before these examples were added: the snapshot, the consistency check.",
            DemoMarker.InterruptedMessage(details));
    }

    [Fact]
    public void Every_step_has_its_own_body_in_the_seeders_table()
    {
        // No two steps run the same body, so a key copied onto another step's body fails
        // here rather than adding one example twice and another never.
        var bodies = DemoSeedSteps.All.Select(s => DemoSeeder.BodyOf(s.Key)).ToList();
        Assert.Equal(DemoSeedSteps.Count, bodies.Distinct().Count());
        Assert.Throws<KeyNotFoundException>(() => DemoSeeder.BodyOf("no-such-step"));
    }
}
