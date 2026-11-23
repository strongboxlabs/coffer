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
public sealed class StarterCategoriesSeederTests : DatabaseTest
{
    public StarterCategoriesSeederTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Seeds_the_starter_tree_as_category_accounts_with_kinds_and_hierarchy()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeder = new StarterCategoriesSeeder(TimeProvider.System);

        // The catalogue is actually loaded from the embedded resource, at every
        // depth: ADR-0091's tree as amended on 2026-11-18.
        Assert.Equal(108, seeder.CategoryCount);

        int created;
        await using (var db = Db.NewServiceFactory().Create())
        {
            var before = await db.Accounts.CountAsync(
                a => a.LedgerId == ledger.LedgerId && a.AccountType == "category");
            Assert.Equal(0, before);

            created = await seeder.SeedAsync(db, ledger.LedgerId);
            Assert.Equal(seeder.CategoryCount, created);
        }

        await using var assertDb = Db.NewServiceFactory().Create();
        var cats = await assertDb.Accounts
            .Where(a => a.LedgerId == ledger.LedgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.Name, a.CategoryKind, a.ParentId, a.OpeningBalance })
            .ToListAsync();

        Assert.Equal(108, cats.Count);
        Assert.Contains(cats, c => c.CategoryKind == "income");
        Assert.Contains(cats, c => c.CategoryKind == "expense");
        Assert.Contains(cats, c => c.CategoryKind == "adjustment");
        Assert.Contains(cats, c => c.ParentId != null);                 // hierarchy present
        Assert.All(cats, c => Assert.True(c.CategoryKind is "income" or "expense" or "adjustment"));
        Assert.All(cats, c => Assert.Equal(0m, c.OpeningBalance));      // categories carry no balance

        // A third level: a row whose parent has a parent, each a child of the
        // one above and of the same kind.
        var byId = cats.ToDictionary(c => c.Id);
        var deep = cats.Where(c => c.ParentId is { } p && byId[p].ParentId is not null).ToList();
        Assert.NotEmpty(deep);
        var airfare = Assert.Single(deep, c => c.Name == "Airfare");
        var travel = byId[airfare.ParentId!.Value];
        var vacation = byId[travel.ParentId!.Value];
        Assert.Equal(("Travel", "Vacation"), (travel.Name, vacation.Name));
        Assert.Null(vacation.ParentId);
        Assert.All(deep, c => Assert.Equal(byId[c.ParentId!.Value].CategoryKind, c.CategoryKind));
    }
}
