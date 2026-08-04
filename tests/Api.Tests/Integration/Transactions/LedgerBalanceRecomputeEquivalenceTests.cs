using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Migration 188 replaced snapshot restore's per-account balance loop —
/// <c>fn_recompute_balances_for_account(a, '0001-01-01')</c> once per account —
/// with a single set-based pass, <c>fn_recompute_balances_for_ledger</c>.
/// </summary>
/// <remarks>
/// <para>The two must agree exactly, so this compares them against each other
/// rather than against hand-computed numbers: the per-account function is the
/// reference implementation and remains the incremental path used by the write
/// triggers. A rewrite of a money-balance rebuild is not something to take on
/// trust, and "the totals look right" would not catch an ordering or partitioning
/// error.</para>
/// <para>The ledger is built to exercise every predicate the function carries,
/// because those are where a set-based rewrite can silently diverge: a non-zero
/// opening balance (the running total's starting point), an override-shifted
/// <c>posted_at</c> that reorders the running total, a hidden header and a
/// merged-away header (both excluded), a multi-split touching one account twice on
/// a single header, and an account with no legs at all — the case the old loop
/// paid for on every ledger and produced nothing for.</para>
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
    public async Task Set_based_ledger_rebuild_matches_the_per_account_loop()
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
        await ledger.SetHeaderOverrideAsync(
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

        // Reference: clear, then run the per-account loop exactly as pre-188
        // restore did.
        var viaLoop = await RebuildAndReadAsync(ledger.LedgerId, useSetBased: false);

        // Candidate: clear, then one set-based pass.
        var viaSetBased = await RebuildAndReadAsync(ledger.LedgerId, useSetBased: true);

        // Guard against a vacuous pass — if both rebuilds produced nothing, the
        // comparison would succeed while proving nothing.
        Assert.NotEmpty(viaLoop);
        Assert.Equal(viaLoop.Count, viaSetBased.Count);
        Assert.Equal(viaLoop, viaSetBased);

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
