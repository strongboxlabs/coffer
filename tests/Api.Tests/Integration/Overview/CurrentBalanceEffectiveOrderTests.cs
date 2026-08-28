using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// The current balance is the LAST balance in the order the balances were
/// accumulated, which is the override-aware effective date.
/// </summary>
/// <remarks>
/// <para>
/// <c>account_current_balances</c> (mig 133) took the latest
/// <c>txn_header_account_balances</c> row ordered by RAW <c>h.posted_at</c>, joining
/// <c>txn_headers</c> with no override join. Every writer of those values accumulates
/// them in <c>COALESCE(o.posted_at, h.posted_at)</c> order (mig 124, mig 206), so once
/// an override moved a header past the raw-latest one, the view returned an
/// INTERMEDIATE cumulative value — a real balance from the middle of the sequence,
/// which is why it looked plausible instead of obviously wrong.
/// </para>
/// <para>
/// Mig 173 found and fixed this same bug in <c>account_balance_as_of</c>: "Bound +
/// order by the SAME COALESCE the recompute's running sum uses". The mig-133 view never
/// got that treatment, so the dashboard overview, the brokerage-cash read and the
/// loan-payoff figure all kept the raw ordering. Mig 210 applies it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrentBalanceEffectiveOrderTests
{
    private readonly PostgresFixture _fixture;

    public CurrentBalanceEffectiveOrderTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_override_that_moves_a_header_last_makes_its_balance_the_current_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking", openingBalance: 1000m);
        var expense = await ledger.AddCategoryAsync("Groceries", "expense");

        // Raw order: Jan (-100) then Feb (-10). Running balance 900, then 890.
        var jan = await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -100m, new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -10m, new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(890m, await CurrentBalanceAsync(checking.Id));

        // Now move the JANUARY transaction to March by override, so effective order
        // becomes Feb (-10) then Mar (-100): 990, then 890. The final balance is the
        // same 890 — the point is WHICH ROW the view calls latest.
        await ledger.SetHeaderOverrideAsync(
            jan.FromTxnId, postedAt: new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc));

        // The override changes the effective ORDER, so the balances must be rebuilt
        // for the account before "current" means anything. This mirrors what the
        // override write path does.
        await RecomputeAsync(checking.Id);

        // In effective order the last row is the moved January header, whose
        // balance_after is 890. Ordering by RAW posted_at instead makes the February
        // header latest, whose balance_after after the rebuild is 990 — a real
        // mid-sequence value, and the wrong answer.
        Assert.Equal(890m, await CurrentBalanceAsync(checking.Id));
    }

    private async Task<decimal> CurrentBalanceAsync(Guid accountId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.AccountCurrentBalances.AsNoTracking()
            .Where(b => b.AccountId == accountId)
            .Select(b => b.Balance)
            .SingleAsync();
    }

    private async Task RecomputeAsync(Guid accountId)
    {
        await using var db = _fixture.NewDbContext();
        _ = await db.RecomputeBalancesForAccount(
                accountId, new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Select(r => r.AccountId)
            .FirstAsync();
    }
}
