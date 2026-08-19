using Dapper;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>account_balance_as_of_instants</c> is the only as-of balance feeder, and it
/// backs the register, the overview, net worth, snapshots and returns. These assert
/// what it must answer, at the boundaries where an ordering rule decides it.
/// </summary>
/// <remarks>
/// These replace an equivalence suite that compared migration 198's rewrite against
/// the pre-198 formulation inlined as a reference. Migration 203 dropped that
/// function, and keeping a dead one purely so a test could diff against it would be
/// two implementations of one rule with a test as the glue — exactly what the
/// collapse removed. The SCENARIOS were the valuable part and they are all here:
/// the ordering rule (<c>COALESCE(override, posted_at) DESC, seq DESC</c>) exercised
/// by an override that moves a date BACKWARDS past other rows, two headers sharing
/// an instant so only <c>seq</c> separates them, the opening-balance fallback, and
/// dates landing exactly on and either side of a transaction so an off-by-one in the
/// <c>&lt;=</c> boundary shows.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AccountBalanceAsOfValueTests
{
    private readonly PostgresFixture _fixture;

    public AccountBalanceAsOfValueTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d, int h = 0) =>
        new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    private async Task<decimal> BalanceAsync(Guid ledgerId, Guid accountId, DateTime asOf)
    {
        await using var conn = _fixture.OpenServiceConnection();
        return await conn.ExecuteScalarAsync<decimal>(
            """
            SELECT balance
            FROM account_balance_as_of_instants(
                @ledgerId, ARRAY[@asOf]::timestamptz[], ARRAY[@accountId]::uuid[])
            """,
            new { ledgerId, asOf, accountId });
    }

    [Fact]
    public async Task Reflects_only_headers_up_to_the_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var other = await ledger.AddBankAccountAsync("Savings");

        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 100m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 250m, Utc(2024, 3, 5));

        // Before anything: the opening balance, which is 0 here.
        Assert.Equal(0m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 1, 1)));
        // The boundary is inclusive — landing exactly on the instant counts it.
        Assert.Equal(100m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 1, 10)));
        Assert.Equal(100m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 3, 4)));
        Assert.Equal(350m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 3, 5)));
        Assert.Equal(350m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 12, 31)));

        // The counterparty mirrors it — scoping to one account must not leak.
        Assert.Equal(-350m, await BalanceAsync(ledger.LedgerId, other.Id, Utc(2024, 12, 31)));
    }

    /// <summary>
    /// Two headers on one instant, separated only by <c>seq</c>. The feeder must land
    /// on the LAST of them; taking the first would report a balance that existed for
    /// no length of time.
    /// </summary>
    [Fact]
    public async Task Takes_the_last_header_when_two_share_an_instant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var other = await ledger.AddBankAccountAsync("Savings");

        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 7m, Utc(2024, 6, 1));
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 11m, Utc(2024, 6, 1));

        Assert.Equal(18m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 6, 1)));
        Assert.Equal(18m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 6, 2)));
        Assert.Equal(0m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 5, 31)));
    }

    /// <summary>
    /// A posted-at override that moves a header BACKWARDS. The ordering must run on
    /// the effective date, not the raw <c>posted_at</c> — and this is the case that
    /// disproved an attempt to denormalise that ordering, because the override moves
    /// the date without moving the row's <c>seq</c>.
    /// </summary>
    [Fact]
    public async Task Honors_a_posted_at_override_that_moves_a_header_earlier()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var other = await ledger.AddBankAccountAsync("Savings");

        await ledger.AddTransactionPairAsync(bank.Id, other.Id, 100m, Utc(2024, 1, 10));
        var (moved, _) = await ledger.AddTransactionPairAsync(
            bank.Id, other.Id, 900m, Utc(2024, 9, 1));
        await ledger.SetHeaderOverrideAsync(moved, postedAt: Utc(2024, 2, 1));
        await ledger.RecomputeBalancesAsync(new[] { bank.Id, other.Id });

        // At 2024-03-01 the moved header's EFFECTIVE date (Feb 1) has passed, so its
        // 900 counts. Reading the raw September date would give 100.
        Assert.Equal(1_000m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 3, 1)));
        // And before the override date it does not count.
        Assert.Equal(100m, await BalanceAsync(ledger.LedgerId, bank.Id, Utc(2024, 1, 15)));
    }

    [Fact]
    public async Task Falls_back_to_the_opening_balance_for_an_untouched_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var untouched = await ledger.AddBankAccountAsync("Nothing Here", openingBalance: 250m);

        // No headers at all: every instant reports the opening balance, and the
        // account still appears rather than being dropped.
        Assert.Equal(250m, await BalanceAsync(ledger.LedgerId, untouched.Id, Utc(2024, 6, 1)));
        Assert.Equal(250m, await BalanceAsync(ledger.LedgerId, untouched.Id, Utc(2030, 1, 1)));
    }
}
