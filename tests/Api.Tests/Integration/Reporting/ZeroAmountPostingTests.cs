using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A posting of exactly zero (dimension 11 of the fixture audit).
/// </summary>
/// <remarks>
/// <para>Ordinary in a real ledger — a waived fee entered so the waiver is on
/// the record, a fully-discounted order, a correction zeroed out rather than
/// deleted. The demo did carry zero-amount legs, but every one was an investment
/// security leg, where zero cash is structural rather than a figure the user
/// typed; the ordinary case existed nowhere.</para>
///
/// <para>The point worth pinning is not that the summary tolerates a zero — it
/// is that <see cref="TransactionDirection"/> has THREE values and a zero
/// belongs to only one of them. Inflow is <c>&gt; 0</c> and Outflow is
/// <c>&lt; 0</c>, so a zero-amount line is returned by neither: a user who
/// reaches for either filter cannot see it at all. That is defensible — it
/// moved no money in either direction — but it is a decision, and nothing said
/// so, which is how it gets "fixed" into one side by someone who reads the two
/// predicates as a partition.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ZeroAmountPostingTests
{
    private readonly PostgresFixture _fixture;

    public ZeroAmountPostingTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private async Task<(Guid LedgerId, Guid BankId, Guid FeeId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var fees = await ledger.AddCategoryAsync("Service Charges", "expense");

        // The waived fee, and a real one beside it so a vanished zero is
        // distinguishable from a vanished category.
        await ledger.AddTransactionPairAsync(fees.Id, bank.Id, 0m, May);
        await ledger.AddTransactionPairAsync(fees.Id, bank.Id, 1_200m, May.AddMonths(-1));
        return (ledger.LedgerId, bank.Id, fees.Id);
    }

    [Fact]
    public async Task A_zero_posting_survives_import_and_sums_as_zero()
    {
        var (ledgerId, _, _) = await SeedAsync();

        var result = await new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System).SummarizeAsync(
            new ReportSpec
            {
                LedgerId = ledgerId,
                Measure = ReportMeasure.Spending,
                TimeBucket = ReportTimeBucket.Month,
                GroupBy = ReportGroupBy.Category,
            });

        var byPeriod = result.Rows
            .Where(r => r.GroupName == "Service Charges")
            .ToDictionary(r => r.Period!, r => r.Amount);

        // The month holding only the zero still reports the category, at zero.
        // Dropping the cell would make a month with a waived fee and a month
        // with no fee at all read identically.
        Assert.Equal(0m, byPeriod["2026-05"]);
        Assert.Equal(1_200m, byPeriod["2026-04"]);
        Assert.Equal(1_200m, result.Total);
    }

    [Fact]
    public async Task Neither_direction_filter_returns_a_zero_line()
    {
        var (ledgerId, bankId, _) = await SeedAsync();
        var repo = new ReportingRepository(_fixture.NewDbContext(), TimeProvider.System);

        async Task<int> CountAsync(TransactionDirection direction)
        {
            var page = await repo.ListTransactionsAsync(new TransactionQuery
            {
                LedgerId = ledgerId,
                AccountId = bankId,
                Direction = direction,
                FromUtc = May.AddDays(-1),
                ToUtc = May.AddDays(1),
            });
            return page.Lines.Count;
        }

        // THE ASSERTION THIS FILE EXISTS FOR. Both, not one: checking a single
        // direction would pass if the zero had silently been bundled into the
        // other, which is the exact mistake the two predicates invite.
        Assert.Equal(0, await CountAsync(TransactionDirection.Inflow));
        Assert.Equal(0, await CountAsync(TransactionDirection.Outflow));

        // ... and it is not that the window is empty.
        Assert.Equal(1, await CountAsync(TransactionDirection.All));
    }
}
