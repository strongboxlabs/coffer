using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// The shared current-balance source (ADR-0056 slice 1), backed by the
/// <c>account_current_balances</c> view: a single account, all accounts, and
/// active-only filtering — the one definition every consumer reuses.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountBalancesRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public AccountBalancesRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reads_single_all_and_active_only()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var activeId = await AddAccountAsync(ledger, "Active", 100m, isActive: true);
        var inactiveId = await AddAccountAsync(ledger, "Inactive", 50m, isActive: false);

        await using var db = _fixture.NewDbContext();
        var repo = new AccountBalancesRepository(db);

        // Single account — the view's opening-balance fallback (no txns).
        Assert.Equal(100m, await repo.GetCurrentBalanceAsync(ledger.LedgerId, activeId));
        // Unknown account → null (not zero).
        Assert.Null(await repo.GetCurrentBalanceAsync(ledger.LedgerId, Guid.NewGuid()));

        // Active-only (default) excludes the archived account.
        var active = await repo.GetCurrentBalancesAsync(ledger.LedgerId, activeOnly: true);
        Assert.True(active.ContainsKey(activeId));
        Assert.False(active.ContainsKey(inactiveId));

        // activeOnly:false includes both.
        var all = await repo.GetCurrentBalancesAsync(ledger.LedgerId, activeOnly: false);
        Assert.Equal(100m, all[activeId]);
        Assert.Equal(50m, all[inactiveId]);
    }

    private static async Task<Guid> AddAccountAsync(
        SyntheticLedger ledger, string name, decimal opening, bool isActive)
    {
        var id = Guid.NewGuid();
        await using var db = ledger.NewDbContext();
        db.Accounts.Add(new AccountRow
        {
            Id = id,
            LedgerId = ledger.LedgerId,
            Name = name,
            AccountType = "bank",
            CurrencyCode = "USD",
            OpeningBalance = opening,
            IsActive = isActive,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
