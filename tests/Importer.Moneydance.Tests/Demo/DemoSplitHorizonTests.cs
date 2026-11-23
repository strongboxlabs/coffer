using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The Demo's one stock split is a real one, so it stays in a Demo built today only
/// while today's five-year window still reaches back to it.
/// </summary>
/// <remarks>
/// The only Demo test that reads the clock, on purpose: every other test pins its
/// date, so none of them would notice the day a Demo built from the hub stops
/// carrying a split. The window starts on day 1 of the month five years before
/// AsOf's month, so the split leaves it on <see cref="LastDayWithTheSplit"/>'s
/// next day. When this fails, move the Demo's split to a later real one.
/// </remarks>
public sealed class DemoSplitHorizonTests
{
    /// <summary>The last AsOf whose window still starts on or before the split.</summary>
    private static readonly DateOnly LastDayWithTheSplit = new(2029, 6, 30);

    [Fact]
    public void Demo_split_is_inside_the_window_today()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(today <= LastDayWithTheSplit,
            $"From {LastDayWithTheSplit.AddDays(1):yyyy-MM-dd} a Demo built today no longer carries its stock split "
            + $"(dated {DemoSecurities.SplitDate:yyyy-MM-dd}): its window starts after it. Move the Demo to a later real split.");

        var calendar = new DemoCalendar(today);
        Assert.True(calendar.Contains(DemoSecurities.SplitDate),
            $"the window {calendar.WindowStart:yyyy-MM-dd}..{today:yyyy-MM-dd} misses the split");

        // The horizon constant is the window rule's, not a guess.
        Assert.True(new DemoCalendar(LastDayWithTheSplit).Contains(DemoSecurities.SplitDate));
        Assert.False(new DemoCalendar(LastDayWithTheSplit.AddDays(1)).Contains(DemoSecurities.SplitDate));
    }
}
