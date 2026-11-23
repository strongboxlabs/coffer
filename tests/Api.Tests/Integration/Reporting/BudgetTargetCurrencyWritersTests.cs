using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Which currency each budget-target writer stamps (ADR-0114 D12), across a real change of
/// the ledger's reporting currency through its owner-only PATCH: a typed target and a fill
/// from the average take the reporting currency current when they are written; a copy of
/// last month keeps the currency each copied target was typed in.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BudgetTargetCurrencyWritersTests : DatabaseTest
{
    public BudgetTargetCurrencyWritersTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task SetCurrencyAsync(HttpClient client, Guid ledgerId, string code)
    {
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledgerId}")
        {
            Content = JsonContent.Create(new { currencyCode = code }),
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    private async Task<Dictionary<Guid, (decimal Amount, string Currency)>> TargetsAsync(Guid ledgerId, DateOnly month)
    {
        await using var db = Db.NewDbContext();
        return await db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && t.TargetMonth == month)
            .ToDictionaryAsync(t => t.CategoryId, t => (t.Amount, t.CurrencyCode));
    }

    [Fact]
    public async Task A_copy_keeps_each_targets_currency_while_typed_and_averaged_targets_take_the_current_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var food = await ledger.AddCategoryAsync("Food");
        var rent = await ledger.AddCategoryAsync("Rent");
        var travel = await ledger.AddCategoryAsync("Travel");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        // May is typed while the ledger reports in USD.
        foreach (var (category, amount) in new[] { (food.Id, 250m), (rent.Id, 900m) })
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/budget/targets",
                new SetBudgetTargetRequest(category, "2026-05", amount))).StatusCode);

        await SetCurrencyAsync(client, ledger.LedgerId, "EUR");

        // June: a copy of May, then a typed target and a fill from the average.
        var copy = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets/fill",
            new { month = "2026-06", copyFromMonth = "2026-05" });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal(2, (await copy.Content.ReadFromJsonAsync<FillBudgetTargetsResponse>())!.Applied);
        await client.PutAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(travel.Id, "2026-06", 120m));

        var june = await TargetsAsync(ledger.LedgerId, new DateOnly(2026, 6, 1));
        Assert.Equal((250m, "USD"), june[food.Id]);
        Assert.Equal((900m, "USD"), june[rent.Id]);
        Assert.Equal((120m, "EUR"), june[travel.Id]);

        var average = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets/fill",
            new { month = "2026-07", entries = new[] { new { categoryId = food.Id, amount = 260m } } });
        Assert.Equal(HttpStatusCode.OK, average.StatusCode);
        Assert.Equal((260m, "EUR"), (await TargetsAsync(ledger.LedgerId, new DateOnly(2026, 7, 1)))[food.Id]);
    }

    [Fact]
    public async Task A_copy_from_an_empty_month_applies_nothing_and_a_copy_with_entries_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var food = await ledger.AddCategoryAsync("Food");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var empty = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets/fill",
            new { month = "2026-06", copyFromMonth = "2026-05" });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var body = (await empty.Content.ReadFromJsonAsync<FillBudgetTargetsResponse>())!;
        Assert.Equal((0, 0), (body.Applied, body.Skipped.Count));

        foreach (var request in new object[]
                 {
                     new { month = "2026-06", copyFromMonth = "2026-05", entries = new[] { new { categoryId = food.Id, amount = 1m } } },
                     new { month = "2026-06", copyFromMonth = "May" },
                 })
        {
            var refused = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets/fill", request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            using var doc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("budget-range-invalid", doc.RootElement.GetProperty("code").GetString());
        }
        Assert.Empty(await TargetsAsync(ledger.LedgerId, new DateOnly(2026, 6, 1)));
    }

    // ---- a category merge rounds the converted share once (ADR-0114 D12, D15) -----------

    private static readonly DateOnly March = new(2026, 3, 1);

    /// <summary>A source category with a target typed while the ledger reported in one
    /// currency, a destination whose target for the same month is stamped with a whole-unit
    /// currency, and the stored rate between the two for the month's last day, at which the
    /// source's share lands exactly on half of one whole unit.</summary>
    private async Task<(SyntheticLedger Ledger, Guid Source, Guid Dest)> MergeAcrossCurrenciesAsync(decimal destAmount)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Category A");
        var dest = await ledger.AddCategoryAsync("Category B");
        await new Coffer.Api.Db.Repositories.BudgetTargetsRepository(Db.NewDbContext(), TimeProvider.System)
            .SetAsync(ledger.LedgerId, source.Id, March, 1.25m);
        await using (var service = Db.NewServiceDbContext())
        {
            await service.Ledgers.Where(l => l.Id == ledger.LedgerId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.CurrencyCode, "JPY"));
        }
        await using (var writer = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            // Stored directly: a figure stamped with a whole-unit currency may carry the two
            // places every target had before each currency kept its own (the stamping
            // migration), which no typed write can produce now.
            writer.BudgetTargets.Add(new Coffer.Api.Db.Entities.BudgetTargetRow
            {
                LedgerId = ledger.LedgerId, CategoryId = dest.Id, TargetMonth = March,
                Amount = destAmount, CurrencyCode = "JPY",
                CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.FxRates.Add(new Coffer.Api.Db.Entities.FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "USD", ToCurrency = "JPY",
                RateDate = new DateOnly(2026, 3, 31), Rate = 2m, Source = Coffer.Api.Db.Entities.FxRateSource.Frankfurter,
            });
            writer.FxPairCoverage.Add(new Coffer.Api.Db.Entities.FxPairCoverageRow
            {
                LedgerId = ledger.LedgerId, FromCurrency = "USD", ToCurrency = "JPY",
                FetchedThroughDate = new DateOnly(2026, 4, 30),
            });
            await writer.SaveChangesAsync();
        }
        return (ledger, source.Id, dest.Id);
    }

    private async Task<(decimal Amount, string Currency)> MergedAsync(SyntheticLedger ledger, Guid source, Guid dest)
    {
        var db = Db.NewDbContext();
        var accounts = new Coffer.Api.Db.Repositories.AccountsRepository(
            db, new Coffer.Api.Db.Repositories.LegDerivedRecomputeService(db), TimeProvider.System);
        var outcome = await accounts.MergeCategoryAsync(ledger.LedgerId, source, dest, dryRun: false);
        Assert.Equal(Coffer.Api.Db.Repositories.AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        var merged = await TargetsAsync(ledger.LedgerId, March);
        return Assert.Single(merged, t => t.Key == dest).Value;
    }

    [Fact]
    public async Task A_merges_converted_share_rounds_half_away_from_zero_at_the_destinations_places()
    {
        var (ledger, source, dest) = await MergeAcrossCurrenciesAsync(10m);

        // The share is exactly half of one whole unit over a whole figure, and rounds up.
        Assert.Equal((13m, "JPY"), await MergedAsync(ledger, source, dest));
    }

    [Fact]
    public async Task A_merge_adds_the_rounded_share_to_a_stored_destination_figure_without_rounding_it_again()
    {
        var (ledger, source, dest) = await MergeAcrossCurrenciesAsync(10.50m);

        // The share is rounded on its own; the stored figure that does not fit is kept.
        Assert.Equal((13.50m, "JPY"), await MergedAsync(ledger, source, dest));
    }
}
