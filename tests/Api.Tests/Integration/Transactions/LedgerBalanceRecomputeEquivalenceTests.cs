using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The stored running balances are exactly what the walk derives.
/// </summary>
/// <remarks>
/// <para>
/// This file used to compare <c>fn_recompute_balances_for_ledger</c> against a loop over
/// <c>fn_recompute_balances_for_account</c>, calling the latter "the reference
/// implementation". Migration 211 made that comparison a TAUTOLOGY on purpose: both are
/// now thin persists over one <c>balance_walk</c>, so there is no second implementation
/// left to disagree.
/// </para>
/// <para>
/// Which is the point. A test that watches two copies of an algorithm agree makes the
/// duplication permanent and only alarms after they drift — and migration 206 rewrote
/// one copy while this test was the sole guard. So the assertion moved to the property
/// that survives refactoring: what the WRITER stored equals what the READ-ONLY walk
/// says, which is also exactly what the consistency checker compares. Both entry points
/// are still exercised, so re-forking them would still be caught.
/// </para>
/// <para>
/// The fixture is unchanged and is the valuable part: a non-zero and per-account
/// opening balance, an override that moves a header EARLIER so the running total must
/// re-order, a hidden header and a merged-away one (both excluded), a multi-split
/// touching one account twice on a single header, and an account with no legs at all.
/// Those are the shapes where a rewrite silently diverges.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerBalanceRecomputeEquivalenceTests
{
    private readonly PostgresFixture _fixture;

    public LedgerBalanceRecomputeEquivalenceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Init-only properties (not positional parameters) so EF's
    /// <c>SqlQuery</c> can materialise it, while the record still gives
    /// structural equality for the list comparison.
    /// </summary>
    private sealed record BalanceRow
    {
        public Guid HeaderId { get; init; }
        public Guid AccountId { get; init; }
        public decimal BalanceAfter { get; init; }
        public decimal NetAmount { get; init; }
    }

    [Fact]
    public async Task Stored_balances_equal_what_the_walk_derives_by_either_route()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var rent = await ledger.AddCategoryAsync("rent");
        // Deliberately unused: no leg ever references it. The old loop iterated it
        // and produced zero rows; the new pass must also produce zero rows.
        await ledger.AddCategoryAsync("never-used");

        // Non-zero, and different per account: the running total starts from
        // opening_balance, so a rewrite that dropped or crossed it would diverge.
        await SetOpeningBalanceAsync(checking.Id, 250.75m);
        await SetOpeningBalanceAsync(savings.Id, 1000m);

        // Plain activity on both accounts.
        var (payLeg, _) = await ledger.AddTransactionPairAsync(
            checking.Id, groceries.Id, -40.25m, new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            checking.Id, rent.Id, -1200m, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            savings.Id, groceries.Id, -15m, new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc));

        // An override moving a header EARLIER than existing ones, so the running
        // total must re-order. If the rewrite ordered by the base posted_at
        // instead of the override, this is the row that would diverge.
        var (movedLeg, _) = await ledger.AddTransactionPairAsync(
            checking.Id, groceries.Id, -99.99m, new DateTime(2024, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        await ledger.EditHeaderAsync(
            movedLeg, postedAt: new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        // Excluded rows: hidden, and merged away into the first payment.
        var (hiddenLeg, _) = await ledger.AddTransactionPairAsync(
            checking.Id, groceries.Id, -500m, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        await ledger.HideTransactionAsync(hiddenLeg);

        var (mergedLeg, _) = await ledger.AddTransactionPairAsync(
            checking.Id, groceries.Id, -777m, new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
        await ledger.MarkTransactionMergedAsync(mergedLeg, payLeg);

        // A multi-split: two legs on one header, so the per-(account, header)
        // grouping has to sum more than one leg for the primary account.
        await ledger.AddMultiSplitAsync(
            checking.Id,
            [(groceries.Id, -30m), (rent.Id, -70m)],
            new DateTime(2024, 2, 14, 0, 0, 0, DateTimeKind.Utc));

        // Both entry points, so re-forking them would still be caught.
        var viaLoop = await RebuildAndReadAsync(ledger.LedgerId, useSetBased: false);
        var viaSetBased = await RebuildAndReadAsync(ledger.LedgerId, useSetBased: true);

        // Guard against a vacuous pass — a comparison over two empty lists succeeds
        // while proving nothing.
        Assert.NotEmpty(viaLoop);
        Assert.Equal(viaLoop.Count, viaSetBased.Count);
        Assert.Equal(viaLoop, viaSetBased);

        // THE PROPERTY THAT MATTERS: what was written equals what the read-only walk
        // derives. This is the same comparison the consistency checker makes, so a
        // writer that stops agreeing with the walk fails here rather than surfacing
        // later as drift a user has to notice.
        var walked = await WalkAsync(ledger.LedgerId);
        Assert.NotEmpty(walked);
        Assert.Equal(walked, viaSetBased);

        // And the shapes built above are genuinely represented, so the predicates
        // were actually exercised.
        Assert.Contains(viaSetBased, r => r.AccountId == checking.Id);
        Assert.Contains(viaSetBased, r => r.AccountId == savings.Id);
        Assert.DoesNotContain(viaSetBased, r => r.NetAmount == -500m);   // hidden
        Assert.DoesNotContain(viaSetBased, r => r.NetAmount == -777m);   // merged away

        // The opening balance is carried, not assumed zero: savings' only visible
        // leg is -15, so its single row must land at 1000 - 15.
        var savingsRows = viaSetBased.Where(r => r.AccountId == savings.Id).ToList();
        Assert.Equal(985m, Assert.Single(savingsRows).BalanceAfter);
    }

    private async Task SetOpeningBalanceAsync(Guid accountId, decimal openingBalance)
    {
        await using var db = _fixture.NewServiceFactory().Create();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE accounts SET opening_balance = {openingBalance} WHERE id = {accountId}");
    }

    /// <summary>
    /// What balance_walk says the balances are, read-only, seeded per account from its
    /// own opening balance at the 0001-01-01 floor — the whole-ledger shape.
    /// </summary>
    private async Task<List<BalanceRow>> WalkAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewServiceFactory().Create();
        return await db.Database
            .SqlQuery<BalanceRow>($@"
                SELECT w.header_id     AS ""HeaderId"",
                       w.account_id    AS ""AccountId"",
                       w.balance_after AS ""BalanceAfter"",
                       w.net_amount    AS ""NetAmount""
                  FROM balance_walk({ledgerId}, NULL, '0001-01-01'::timestamptz, NULL) w
                 ORDER BY w.account_id, w.header_id")
            .ToListAsync();
    }

    /// <summary>
    /// Clear the ledger's balance rows, rebuild them by the chosen route, and
    /// return them in a stable order for comparison.
    /// </summary>
    private async Task<List<BalanceRow>> RebuildAndReadAsync(Guid ledgerId, bool useSetBased)
    {
        await using var db = _fixture.NewServiceFactory().Create();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM txn_header_account_balances WHERE ledger_id = {ledgerId}");

        if (useSetBased)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_recompute_balances_for_ledger({ledgerId})");
        }
        else
        {
            // The pre-188 restore behaviour: one call per account in the ledger,
            // with a 0001-01-01 floor. Driven from C# rather than a DO block
            // because a parameter cannot bind inside a DO body — same account set,
            // same per-account call.
            var accountIds = await db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledgerId)
                .Select(a => a.Id)
                .ToListAsync();

            foreach (var accountId in accountIds)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT fn_recompute_balances_for_account({accountId}, '0001-01-01'::timestamptz)");
            }
        }

        return await db.Database
            .SqlQuery<BalanceRow>($@"
                SELECT header_id     AS ""HeaderId"",
                       account_id    AS ""AccountId"",
                       balance_after AS ""BalanceAfter"",
                       net_amount    AS ""NetAmount""
                  FROM txn_header_account_balances
                 WHERE ledger_id = {ledgerId}
                 ORDER BY account_id, header_id")
            .ToListAsync();
    }
}
