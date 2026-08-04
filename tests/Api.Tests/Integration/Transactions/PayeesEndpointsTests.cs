using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for <c>GET /api/ledgers/{ledgerId}/payees</c> —
/// the typeahead source the SPA's payee field consumes. Verifies the
/// resolved-payee precedence (override beats header), the
/// count-then-recency sort, and the hidden/merged exclusions.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PayeesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public PayeesEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Get_returns_distinct_payees_ranked_by_count_then_recency()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        // 3× "Amazon" (high count), 1× "Bulk Mart" (recent), 2× "Whole Foods" (older).
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -10m, baseTime.AddDays(0), payee: "Amazon");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -11m, baseTime.AddDays(1), payee: "Amazon");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -12m, baseTime.AddDays(2), payee: "Whole Foods");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -13m, baseTime.AddDays(3), payee: "Whole Foods");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -14m, baseTime.AddDays(9), payee: "Bulk Mart");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -15m, baseTime.AddDays(5), payee: "Amazon");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/payees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payees = (await response.Content.ReadFromJsonAsync<List<PayeeSuggestion>>())!;

        // Amazon: 3 uses, max date day-5. Whole Foods: 2, day-3. Bulk Mart: 1, day-9.
        Assert.Equal(3, payees.Count);
        Assert.Equal("Amazon", payees[0].Name);
        Assert.Equal(3, payees[0].Count);
        Assert.Equal("Whole Foods", payees[1].Name);
        Assert.Equal(2, payees[1].Count);
        // Bulk Mart (count 1) lands last despite being the most recent overall —
        // count is the primary sort.
        Assert.Equal("Bulk Mart", payees[2].Name);
        Assert.Equal(1, payees[2].Count);
    }

    [Fact]
    public async Task Get_uses_the_overridden_payee_when_an_override_row_exists()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        var postedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var (legId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -10m, postedAt, payee: "AMZ MKT *RP4...");

        // Overwrite the imported payee with the user's cleaned-up name.
        await ledger.SetHeaderOverrideAsync(legId, payee: "Amazon");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/payees");
        var payees = (await response.Content.ReadFromJsonAsync<List<PayeeSuggestion>>())!;

        // The override-applied name shows up; the original feed-side
        // payee is invisible.
        Assert.Contains(payees, p => p.Name == "Amazon");
        Assert.DoesNotContain(payees, p => p.Name == "AMZ MKT *RP4...");
    }

    [Fact]
    public async Task Get_excludes_hidden_and_merged_headers()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        var postedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var (visibleLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -10m, postedAt.AddDays(0), payee: "Visible");
        var (hiddenLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -11m, postedAt.AddDays(1), payee: "Hidden");
        var (mergedLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -12m, postedAt.AddDays(2), payee: "Merged");

        await ledger.HideTransactionAsync(hiddenLegId);
        await ledger.MarkTransactionMergedAsync(mergedLegId, visibleLegId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/payees");
        var payees = (await response.Content.ReadFromJsonAsync<List<PayeeSuggestion>>())!;

        Assert.Contains(payees, p => p.Name == "Visible");
        Assert.DoesNotContain(payees, p => p.Name == "Hidden");
        Assert.DoesNotContain(payees, p => p.Name == "Merged");
    }

    [Fact]
    public async Task Get_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        // Caller is the first ledger's user but probes the second.
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync($"/api/ledgers/{otherLedger.LedgerId}/payees");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }
}
