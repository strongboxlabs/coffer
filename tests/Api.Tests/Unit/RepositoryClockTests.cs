using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The repositories that read a rate for "today" take the injected clock as a required
/// argument and refuse a null one at construction (ADR-0114 D13, the one rate rule).
/// </summary>
/// <remarks>
/// The compiler holds every call site to passing a clock; this holds the constructor to
/// refusing a null one, so the failure is at construction and not deep inside the first
/// write that needs a rate.
/// </remarks>
public sealed class RepositoryClockTests
{
    [Fact]
    public void The_investment_repository_refuses_a_null_clock()
    {
        var thrown = Assert.Throws<ArgumentNullException>(
            () => new InvestmentTransactionsRepository(null!, null!));
        Assert.Equal("clock", thrown.ParamName);
    }

    [Fact]
    public void The_reminders_repository_refuses_a_null_clock()
    {
        var thrown = Assert.Throws<ArgumentNullException>(
            () => new RemindersRepository(null!, null!, null!, null!, null!));
        Assert.Equal("clock", thrown.ParamName);
    }
}
