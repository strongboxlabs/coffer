using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The stress seeder writes straight into Postgres, so its consistency is a claim
/// until something checks it. This runs the seed at <see cref="StressLedger.Scale.Smoke"/>
/// in the ordinary suite, where it costs seconds, so a seed change that breaks the
/// posting structure, the placement rules or a promised shape fails a PR rather than
/// quietly skewing the next on-demand stress run.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StressLedgerTests : DatabaseTest
{
    public StressLedgerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Smoke_seed_is_a_ledger_the_write_paths_could_have_produced()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Smoke;

        await StressLedger.SeedAsync(Db, ledger, scale);

        await StressLedger.AssertInvariantsAsync(Db, ledger.LedgerId, scale);
    }

    /// <summary>
    /// The long-lived shape on top of the smoke seed: every shape it promises is there, and
    /// the product's own consistency check, which reads what the writers derive, finds the
    /// ledger healthy. A timing taken on it measures a ledger a user could have.
    /// </summary>
    [Fact]
    public async Task Long_lived_shape_holds_every_shape_it_promises_and_is_consistent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedAsync(Db, ledger, StressLedger.Scale.Smoke);
        var dormant = await LongLivedAccounts.AddAsync(ledger);
        var added = await LongLivedLedger.AddAsync(Db, ledger);

        Assert.True(added.StarterCategories > 50, "a real ledger gets the starter tree (100+)");
        Assert.True(added.NestedCategoriesWithRows > 0, "rows on nested categories");
        Assert.True(added.HiddenHeaders > 0);
        Assert.True(added.MergedHeaders > 0);
        Assert.Equal(LongLivedLedger.ReminderSeries + 1, added.Reminders);
        Assert.Equal(2, added.EuroAccounts.Count);

        await using (var db = Db.NewDbContext())
        {
            var id = ledger.LedgerId;
            Assert.Equal(added.HiddenHeaders,
                await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == id && h.IsHidden));
            Assert.Equal(added.MergedHeaders,
                await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == id && h.IsMergedInto != null));
            Assert.True(await db.Accounts.AsNoTracking()
                .AnyAsync(a => a.LedgerId == id && a.CurrencyCode == "EUR"));
            Assert.True(await db.Accounts.AsNoTracking()
                .AnyAsync(a => a.LedgerId == id && !a.IsActive), "a closed account");
            Assert.True(await db.Accounts.AsNoTracking().AnyAsync(a => a.LedgerId == id && a.AccountType == "loan"));
            Assert.True(LongLivedLedger.PricedMonths <=
                await db.SecurityPrices.AsNoTracking()
                    .CountAsync(p => p.LedgerId == id && p.SecurityId == added.PricedSecurity));
            var soldOutPrices = await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == id && p.SecurityId == added.SoldOutSecurity)
                .Select(p => p.PriceDate).ToListAsync();
            Assert.NotEmpty(soldOutPrices);
            Assert.True(soldOutPrices.Max() < new DateOnly(2016, 1, 1), "sold-out security priced after its sale");
            Assert.False(await db.SecurityPrices.AsNoTracking()
                .AnyAsync(p => p.LedgerId == id && p.SecurityId == added.MovedInSecurity), "moved in, never priced");
            Assert.True(await db.Holdings.AsNoTracking()
                .AnyAsync(h => h.LedgerId == id && h.SecurityId == added.MovedInSecurity && h.Quantity > 0));
            Assert.False(await db.Holdings.AsNoTracking()
                .AnyAsync(h => h.LedgerId == id && h.SecurityId == added.SoldOutSecurity && h.Quantity > 0), "sold out");
            Assert.True(await db.RecurringTransactions.AsNoTracking()
                .AnyAsync(r => r.LedgerId == id && r.IsLoanReminder));
        }
        Assert.NotEmpty(dormant.WithoutRows);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/balances/consistency", content: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var report = (await response.Content.ReadFromJsonAsync<Coffer.Api.Contracts.LedgerConsistencyReport>())!;
        var drift = report.Projections.Where(p => !p.Healthy).Select(p =>
            $"{p.Projection}: {p.MismatchedCount} of {p.Checked}\n    "
            + string.Join("\n    ", p.Mismatches.Take(5).Select(m =>
                $"{m.Scope} {m.Field} stored {m.Stored} expected {m.Expected} {m.Reason}")));
        Assert.True(report.Healthy,
            "the long-lived ledger is not one the writers could have produced:\n  " + string.Join("\n  ", drift)
            + $"\n  oversold {report.Oversold.Count}, unbacked prices {report.UnbackedPrices.Count}");
    }
}
