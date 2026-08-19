using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Checking balances reports drift without repairing it; repairing is separate.
/// </summary>
/// <remarks>
/// The check used to heal as a side effect, because the only implementation of
/// the running-sum rules lived inside the recompute's DELETE + INSERT — the only
/// way to learn whether a balance was right was to overwrite it. On a real ledger
/// that meant a diagnostic silently rewriting 2,741 rows. Migration 206 split the
/// pure walk from the persist, so these two tests pin the property that was
/// impossible before: <b>a question that does not change the answer</b>.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BalanceHealthCheckTests
{
    private readonly PostgresFixture _fixture;

    public BalanceHealthCheckTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Corrupt one stored balance the way an out-of-band write would.</summary>
    private async Task<(Guid AccountId, Guid HeaderId, decimal Corrupted)> CorruptOneAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.HeaderId)
            .FirstAsync();
        var corrupted = row.BalanceAfter + 1_234.56m;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_header_account_balances
               SET balance_after = {corrupted}
             WHERE header_id = {row.HeaderId} AND account_id = {row.AccountId};");
        return (row.AccountId, row.HeaderId, corrupted);
    }

    private async Task<decimal> StoredBalanceAsync(Guid accountId, Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        return (await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(b => b.AccountId == accountId && b.HeaderId == headerId))
            .BalanceAfter;
    }

    private async Task<SyntheticLedger> SeededLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        for (var i = 0; i < 4; i++)
            await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 10 + i));
        return ledger;
    }

    [Fact]
    public async Task Check_reports_drift_and_leaves_the_stored_value_alone()
    {
        var ledger = await SeededLedgerAsync();
        var (accountId, headerId, corrupted) = await CorruptOneAsync(ledger.LedgerId);

        await using var db = _fixture.NewDbContext();
        var report = await new RegisterRepository(db).CheckBalancesAsync(ledger.LedgerId);

        // It must SEE the drift ...
        Assert.False(report.Healthy);
        var hit = Assert.Single(report.Drifted, d => d.HeaderId == headerId);
        Assert.Equal(corrupted, hit.StoredBefore);
        Assert.NotEqual(corrupted, hit.RecomputedAfter);

        // ... and must NOT have fixed it. This is the whole point: the old
        // implementation could not report drift without erasing it.
        Assert.Equal(corrupted, await StoredBalanceAsync(accountId, headerId));
    }

    [Fact]
    public async Task Repair_fixes_the_drift_and_a_second_check_is_clean()
    {
        var ledger = await SeededLedgerAsync();
        var (accountId, headerId, corrupted) = await CorruptOneAsync(ledger.LedgerId);

        await using var db = _fixture.NewDbContext();
        var repo = new RegisterRepository(db);

        var repaired = await repo.VerifyAndHealBalancesAsync(ledger.LedgerId);
        Assert.Contains(repaired.Drifted, d => d.HeaderId == headerId);
        Assert.NotEqual(corrupted, await StoredBalanceAsync(accountId, headerId));

        // The check and the repair share one implementation of the rules, so
        // after a repair the check must agree there is nothing left.
        var after = await repo.CheckBalancesAsync(ledger.LedgerId);
        Assert.True(after.Healthy, $"{after.DriftedCount} rows still drifted after repair");
    }

    [Fact]
    public async Task Check_is_clean_on_an_untouched_ledger()
    {
        var ledger = await SeededLedgerAsync();

        await using var db = _fixture.NewDbContext();
        var report = await new RegisterRepository(db).CheckBalancesAsync(ledger.LedgerId);

        // Guards against the opposite failure: a check that always reports drift
        // (a seed mismatch, say) would be just as useless as one that heals.
        Assert.True(report.Healthy, $"unexpected drift: {report.DriftedCount}");
        Assert.True(report.RowsChecked > 0, "checked nothing — the walk found no rows");
    }
}
