using Dapper;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Migration 201 added <c>account_balance_as_of_instants</c>: the mig-199 balances
/// for many instants in one pass. It is the second half of what let the TWR
/// boundary cap be deleted, and every balance a returns report uses now comes
/// through it, so it is only acceptable if it is output-identical.
/// </summary>
/// <remarks>
/// Compared against mig 199 rather than against restated numbers. The forward-fill
/// hinges entirely on ordering — effective date, then real-rows-before-instants,
/// then <c>seq</c> — and the posted-at OVERRIDE (mig 173) is the case that makes
/// ordering non-obvious: an override can move a header's effective date without
/// changing its position in <c>seq</c>. That is what disproved an earlier attempt
/// to denormalise this ordering, so it gets its own case here.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AccountBalanceAsOfInstantsBatchingTests
{
    private readonly PostgresFixture _fixture;

    public AccountBalanceAsOfInstantsBatchingTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d, int h = 0) =>
        new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    private sealed record BalanceRow(Guid AccountId, decimal Balance);

    /// <summary>
    /// Every instant, both ways, with the batched form asked for ALL of them at
    /// once — the condition that matters, since a single-instant request would
    /// never exercise the island numbering.
    /// </summary>
    private async Task AssertAgreeAtAllAsync(Guid ledgerId, Guid[] accountIds, params DateTime[] instants)
    {
        await using var conn = _fixture.OpenServiceConnection();
        foreach (var t in instants)
        {
            var expected = (await conn.QueryAsync<BalanceRow>(
                """
                SELECT account_id AS "AccountId", balance AS "Balance"
                FROM account_balance_as_of_instants(@ledgerId, ARRAY[@t]::timestamptz[], @accountIds)
                """,
                new { ledgerId, t, accountIds }))
                .OrderBy(r => r.AccountId).ToList();

            var actual = (await conn.QueryAsync<BalanceRow>(
                """
                SELECT account_id AS "AccountId", balance AS "Balance"
                FROM account_balance_as_of_instants(@ledgerId, @instants, @accountIds)
                WHERE as_of = @t
                """,
                new { ledgerId, instants, accountIds, t }))
                .OrderBy(r => r.AccountId).ToList();

            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].AccountId, actual[i].AccountId);
                Assert.Equal(expected[i].Balance, actual[i].Balance);
            }
        }
    }

    [Fact]
    public async Task Batching_is_invariant_across_a_transaction_history()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var other = await ledger.AddBankAccountAsync("Savings");

        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 100m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 250m, Utc(2024, 3, 5));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, -75m, Utc(2024, 6, 20));
        // Two headers on ONE instant, so the tie-break by seq decides the answer.
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 40m, Utc(2024, 8, 1));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 60m, Utc(2024, 8, 1));

        await AssertAgreeAtAllAsync(
            ledger.LedgerId, [bank.Id, other.Id],
            Utc(2024, 1, 1),      // before anything → opening balance
            Utc(2024, 1, 10),     // exactly ON a header
            Utc(2024, 2, 1), Utc(2024, 3, 5), Utc(2024, 6, 20),
            Utc(2024, 8, 1),      // exactly ON the tied pair
            Utc(2024, 12, 31));
    }

    /// <summary>
    /// The case that has already broken one attempt at this: an override moves a
    /// header's effective date without moving it in seq order, so a fill keyed on
    /// anything but the effective date lands on the wrong balance.
    /// </summary>
    [Fact]
    public async Task Batched_honors_a_posted_at_override()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var other = await ledger.AddBankAccountAsync("Savings");

        var (firstLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, other.Id, 100m, Utc(2024, 2, 1));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 250m, Utc(2024, 4, 1));

        // Move the FIRST header far later — after the second — without touching the
        // second. Its seq still sorts before, its effective date now sorts after.
        await ledger.SetHeaderOverrideAsync(firstLeg, postedAt: Utc(2024, 9, 1));
        await ledger.RecomputeBalancesAsync([bank.Id, other.Id]);

        await AssertAgreeAtAllAsync(
            ledger.LedgerId, [bank.Id, other.Id],
            Utc(2024, 1, 1), Utc(2024, 2, 1), Utc(2024, 3, 1),
            Utc(2024, 4, 1), Utc(2024, 9, 1), Utc(2024, 12, 31));
    }

    [Fact]
    public async Task Batched_falls_back_to_opening_balance_and_covers_untouched_accounts()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var funded = await ledger.AddBankAccountAsync("Opened With Money");
        var untouched = await ledger.AddBankAccountAsync("Never Used");
        var other = await ledger.AddBankAccountAsync("Savings");

        await ledger.AddTransactionPairAsync(funded.Id, other.Id, 100m, Utc(2024, 5, 1));

        // An account with no header before the instant reports its opening balance,
        // and one with no headers AT ALL must still appear — island 0 has no balance
        // row to fill from, which is exactly where a missing COALESCE surfaces as a
        // null instead of a number.
        await AssertAgreeAtAllAsync(
            ledger.LedgerId, [funded.Id, untouched.Id, other.Id],
            Utc(2024, 1, 1), Utc(2024, 5, 1), Utc(2024, 12, 31));
    }
}
