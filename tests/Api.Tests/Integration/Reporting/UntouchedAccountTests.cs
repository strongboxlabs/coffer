using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// An account with an opening balance and NO transactions (dimension 4).
/// </summary>
/// <remarks>
/// <para>Utterly ordinary — a house, a CD, an account opened and not yet used,
/// one closed years ago — and the shape where every balance path has no rows to
/// walk and has to fall back on the opening balance alone. If net worth took
/// its figures from postings it would value such an account at zero, and a
/// ledger's largest single asset would silently not count.</para>
///
/// <para>The Demo carries this case (a dormant certificate of deposit: an
/// opening balance and no transactions), and <see cref="Provisioning.FullDemoTests"/> pins
/// that it keeps it. These tests pin what the balance paths do with it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class UntouchedAccountTests : DatabaseTest
{
    public UntouchedAccountTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // Net worth reads through the Overview, which needs the balance and
    // investment repositories; real instances over the same context suffice.
    private static AccountsReportingRepository NewRepository(AppDbContext db)
    {
        var balances = new AccountBalancesRepository(db, TimeProvider.System);
        var investment = new InvestmentReportingRepository(db, TimeProvider.System);
        var overview = new OverviewRepository(db, balances, investment);
        return new AccountsReportingRepository(db, balances, investment, overview);
    }

    [Fact]
    public async Task Net_worth_counts_an_account_that_has_never_been_transacted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var touched = await ledger.AddBankAccountAsync("Checking", openingBalance: 1_000m);
        await ledger.AddBankAccountAsync("Rainy Day", openingBalance: 25_000m);

        // One posting on the touched account only, so the two accounts differ
        // in the thing under test and in nothing else.
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        await ledger.AddTransactionPairAsync(
            groceries.Id, touched.Id, 250m, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var netWorth = await NewRepository(Db.NewDbContext())
            .NetWorthAsync(ledger.LedgerId, today: null);

        var rainy = netWorth.Breakdown.SingleOrDefault(a => a.Name == "Rainy Day");
        Assert.NotNull(rainy);

        // The assertion. An account with no legs has nothing to sum, so a
        // balance derived from postings alone reads 0 — and the account still
        // APPEARS in the breakdown, which is what makes that failure quiet.
        Assert.Equal(25_000m, rainy!.Balance);

        // And it is actually in the total, not merely listed beside it.
        Assert.Equal(1_000m - 250m + 25_000m, netWorth.NetWorth);
    }

    [Fact]
    public async Task A_liability_never_transacted_still_subtracts()
    {
        // The sign half. An untouched ASSET that read zero would understate
        // net worth; an untouched liability that read zero would OVERSTATE it,
        // and an error in that direction is the one nobody goes looking for.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Checking", openingBalance: 5_000m);
        await ledger.AddAccountAsync("Old Card Balance", "credit_card", openingBalance: -800m);

        var netWorth = await NewRepository(Db.NewDbContext())
            .NetWorthAsync(ledger.LedgerId, today: null);

        Assert.Equal(4_200m, netWorth.NetWorth);
        // TotalLiabilities is SIGNED, not a magnitude — net worth is
        // assets + liabilities, not assets - liabilities.
        Assert.Equal(-800m, netWorth.TotalLiabilities);
        Assert.Equal(5_000m, netWorth.TotalAssets);
    }
}
