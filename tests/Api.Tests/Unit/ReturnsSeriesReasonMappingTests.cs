using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Domain.Investment;

using State = Coffer.Domain.Investment.ReturnsCalculator.TwrReadState;
using Outcome = Coffer.Domain.Investment.ReturnsCalculator.XirrOutcome;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The reason a returns-series point gives for a missing figure, for each state of the
/// time-weighted chain and each outcome of the money-weighted solver. Some outcomes (a solve
/// whose flows all share one instant, or offset exactly) cannot be built from a ledger at a
/// year's coverage, so the mapping is held here for every value of both enums.
/// </summary>
public sealed class ReturnsSeriesReasonMappingTests
{
    /// <summary>Mutation caught: any state mapped to another's reason, or the negative-growth
    /// state falling to a different one.</summary>
    [Theory]
    [InlineData(State.Value, null)]
    [InlineData(State.BeforeFirstFunding, ReturnsSeriesReasons.BeforeFirstFunding)]
    [InlineData(State.NoInvestedBase, ReturnsSeriesReasons.NoInvestedBase)]
    [InlineData(State.NegativeCumulativeGrowth, ReturnsSeriesReasons.NegativeCumulativeGrowth)]
    public void Each_chain_state_names_its_own_reason(State state, string? expected) =>
        Assert.Equal(expected, InvestmentReportingRepository.SeriesIndexReason(state));

    /// <summary>Mutation caught: any outcome mapped to another's reason, a solved outcome given
    /// a reason, or the catch-all hiding an outcome that has its own.</summary>
    [Theory]
    [InlineData(Outcome.Solved, null)]
    [InlineData(Outcome.TooFewFlows, ReturnsSeriesReasons.TooFewFlows)]
    [InlineData(Outcome.SingleSignedFlows, ReturnsSeriesReasons.SingleSignedFlows)]
    [InlineData(Outcome.ZeroLengthWindow, ReturnsSeriesReasons.ZeroLengthWindow)]
    [InlineData(Outcome.Indeterminate, ReturnsSeriesReasons.Indeterminate)]
    [InlineData(Outcome.NoRootInRange, ReturnsSeriesReasons.NoRootInRange)]
    public void Each_solver_outcome_names_its_own_reason(Outcome outcome, string? expected) =>
        Assert.Equal(expected, InvestmentReportingRepository.SeriesMwrReason(outcome));

    /// <summary>Mutation caught: an enum value added with no mapping (it would fall to the
    /// catch-all and share a reason), or two values sharing one.</summary>
    [Fact]
    public void Every_unsolved_outcome_and_every_unvalued_state_has_a_distinct_reason()
    {
        var mwr = Enum.GetValues<Outcome>()
            .Where(o => o != Outcome.Solved)
            .Select(InvestmentReportingRepository.SeriesMwrReason)
            .ToList();
        Assert.All(mwr, r => Assert.False(string.IsNullOrEmpty(r)));
        Assert.Equal(mwr.Count, mwr.Distinct().Count());

        var index = Enum.GetValues<State>()
            .Where(s => s != State.Value)
            .Select(InvestmentReportingRepository.SeriesIndexReason)
            .ToList();
        Assert.All(index, r => Assert.False(string.IsNullOrEmpty(r)));
        Assert.Equal(index.Count, index.Distinct().Count());
    }
}
