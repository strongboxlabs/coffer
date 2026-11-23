using Coffer.Api.Reminders;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The order the auto-post job judges a series in, which every reader shares
/// (<see cref="AutoPostExclusion"/>): each reason wins over the ones after it.
/// </summary>
/// <remarks>
/// A managed loan payment across currencies is refused before its split is priced
/// (ADR-0114 D13), so it is named even when a figure of that split would not fit its
/// currency either (ADR-0114 D15): editing the figure could never make it post.
/// </remarks>
public sealed class AutoPostExclusionTests
{
    [Theory]
    // hasNoLines, investment, inactive, loanAcross, doesNotFit -> reason
    [InlineData(true, true, true, true, true, AutoPostExclusion.NoLines)]
    [InlineData(false, true, true, true, true, AutoPostExclusion.Investment)]
    [InlineData(false, false, true, true, true, AutoPostExclusion.InactiveAccount)]
    [InlineData(false, false, false, true, true, AutoPostExclusion.LoanAcrossCurrencies)]
    [InlineData(false, false, false, true, false, AutoPostExclusion.LoanAcrossCurrencies)]
    [InlineData(false, false, false, false, true, AutoPostExclusion.TemplateAmountDoesNotFit)]
    [InlineData(false, false, false, false, false, null)]
    public void Each_reason_wins_over_the_ones_after_it(
        bool hasNoLines, bool isInvestmentShape, bool usesInactiveAccount, bool loanAcrossCurrencies,
        bool templateAmountDoesNotFit, string? expected)
    {
        Assert.Equal(expected, AutoPostExclusion.ReasonFor(
            isInvestmentShape, hasNoLines, usesInactiveAccount, templateAmountDoesNotFit, loanAcrossCurrencies));
    }

    [Fact]
    public void A_candidate_reads_its_reason_through_the_same_order()
    {
        var candidate = new AutoPostCandidate(
            Guid.NewGuid(), new DateOnly(2026, 1, 31), AutoCommitDaysBefore: 3,
            IsInvestmentShape: false, IsLoanReminder: true, HasNoLines: false, UsesInactiveAccount: false,
            Payee: "Payee A", TemplateAmountDoesNotFit: true, LoanAcrossCurrencies: true);

        Assert.Equal(AutoPostExclusion.LoanAcrossCurrencies, candidate.ExcludedReason);
    }
}
