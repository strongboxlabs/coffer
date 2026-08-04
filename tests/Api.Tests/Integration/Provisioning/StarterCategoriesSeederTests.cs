using Microsoft.EntityFrameworkCore;

using Coffer.Api.Provisioning;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// ADR-0071 D5: a new ledger seeds a starter category tree. Verifies the
/// embedded catalogue loads + inserts as valid category accounts (kinds +
/// hierarchy).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StarterCategoriesSeederTests
{
    private readonly PostgresFixture _fixture;

    public StarterCategoriesSeederTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Seeds_the_starter_tree_as_category_accounts_with_kinds_and_hierarchy()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var seeder = new StarterCategoriesSeeder();

        // The catalogue is non-trivial + actually loaded from the embedded resource.
        Assert.True(seeder.CategoryCount >= 40);

        int created;
        await using (var db = _fixture.NewServiceFactory().Create())
        {
            var before = await db.Accounts.CountAsync(
                a => a.LedgerId == ledger.LedgerId && a.AccountType == "category");
            Assert.Equal(0, before);

            created = await seeder.SeedAsync(db, ledger.LedgerId);
            Assert.Equal(seeder.CategoryCount, created);
        }

        await using var assertDb = _fixture.NewServiceFactory().Create();
        var cats = await assertDb.Accounts
            .Where(a => a.LedgerId == ledger.LedgerId && a.AccountType == "category")
            .Select(a => new { a.CategoryKind, a.ParentId, a.OpeningBalance })
            .ToListAsync();

        Assert.Equal(created, cats.Count);
        Assert.Contains(cats, c => c.CategoryKind == "income");
        Assert.Contains(cats, c => c.CategoryKind == "expense");
        Assert.Contains(cats, c => c.ParentId != null);                 // hierarchy present
        Assert.All(cats, c => Assert.True(c.CategoryKind is "income" or "expense"));
        Assert.All(cats, c => Assert.Equal(0m, c.OpeningBalance));      // categories carry no balance
    }
}
