using Coffer.Api.Provisioning;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// The next Demo's name is the lowest free one of "Demo", "Demo 2", "Demo 3" and so on
/// among the caller's visible ledgers, compared trimmed and ignoring case; only those
/// exact forms take a number.
/// </summary>
public sealed class DemoLedgerNameTests
{
    [Fact]
    public void With_no_ledgers_the_first_is_Demo() =>
        Assert.Equal("Demo", DemoLedgerName.Next([]));

    [Fact]
    public void After_Demo_comes_Demo_2() =>
        Assert.Equal("Demo 2", DemoLedgerName.Next(["Demo"]));

    [Fact]
    public void A_gap_is_reused() =>
        Assert.Equal("Demo 2", DemoLedgerName.Next(["Demo", "Demo 3"]));

    [Fact]
    public void Demo_itself_is_reused_when_only_later_numbers_remain() =>
        Assert.Equal("Demo", DemoLedgerName.Next(["Demo 2", "Demo 3"]));

    [Fact]
    public void A_run_takes_the_next_number() =>
        Assert.Equal("Demo 4", DemoLedgerName.Next(["Demo 3", "Demo", "Demo 2"]));

    [Fact]
    public void Names_are_compared_trimmed_and_ignoring_case() =>
        Assert.Equal("Demo 3", DemoLedgerName.Next([" demo ", "DEMO 2"]));

    [Theory]
    [InlineData("Demo 02")]
    [InlineData("Demo2")]
    [InlineData("Demo 1")]
    [InlineData("Demo  2")]
    [InlineData("Demo 2a")]
    [InlineData("Demo -2")]
    [InlineData("Demo 99999999999999999999")]
    [InlineData("My Demo")]
    [InlineData("Demos")]
    public void Other_forms_take_no_number(string name) =>
        Assert.Equal("Demo 2", DemoLedgerName.Next(["Demo", name]));

    // That a ledger shared with the caller counts is the endpoint's to pin (it chooses
    // which names to pass): DemoEndpointTests.A_ledger_shared_with_the_caller_counts_for_the_Demos_number.

    [Fact]
    public void Name_of_one_is_the_bare_name() =>
        Assert.Equal(["Demo", "Demo 2", "Demo 10"], new[] { 1, 2, 10 }.Select(DemoLedgerName.NameOf));
}
