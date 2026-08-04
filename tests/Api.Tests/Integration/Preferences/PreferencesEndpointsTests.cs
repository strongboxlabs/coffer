using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Preferences;

/// <summary>
/// The per-(user, ledger) quotes preference + provider catalog (ADR-0057):
/// <c>GET/PUT /api/ledgers/{id}/preferences/quotes</c> and
/// <c>GET /api/ledgers/{id}/quote-providers</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PreferencesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public PreferencesEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Quotes_pref_defaults_empty_then_round_trips()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/preferences/quotes";

        // Default: no row → empty (opt-out).
        var initial = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<QuotesPrefs>())!;
        Assert.Empty(initial.EnabledProviders);

        // Enable Yahoo (a registered opt-in provider) → persists.
        var put = await client.PutAsJsonAsync(path, new QuotesPrefs { EnabledProviders = new[] { "yahoo" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<QuotesPrefs>())!;
        Assert.Equal(new[] { "yahoo" }, after.EnabledProviders);
    }

    [Fact]
    public async Task Quotes_pref_rejects_unknown_provider()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/preferences/quotes",
            new QuotesPrefs { EnabledProviders = new[] { "not-a-provider" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Dashboard_pref_defaults_empty_then_round_trips_order_and_visibility()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/preferences/dashboard";

        var initial = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<DashboardPrefs>())!;
        Assert.Empty(initial.Widgets);

        // Save an order with one widget hidden; duplicate + blank keys are dropped.
        var put = await client.PutAsJsonAsync(path, new DashboardPrefs
        {
            Widgets = new[]
            {
                new DashboardWidgetPref("accounts", true),
                new DashboardWidgetPref("upcoming", false),
                new DashboardWidgetPref("accounts", true),   // dup → dropped
                new DashboardWidgetPref("", true),           // blank → dropped
                new DashboardWidgetPref("net-worth", true),
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<DashboardPrefs>())!;
        Assert.Equal(
            new[] { "accounts", "upcoming", "net-worth" },
            after.Widgets.Select(w => w.Key));
        Assert.False(after.Widgets.Single(w => w.Key == "upcoming").Visible);
    }

    [Fact]
    public async Task Quote_providers_catalog_lists_opt_in_providers()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var catalog = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/quote-providers"))
            .Content.ReadFromJsonAsync<List<QuoteProviderDto>>())!;

        // Yahoo is the opt-in provider; the no-egress simplefin-holdings is not listed.
        Assert.Contains(catalog, p => p.Key == "yahoo");
        Assert.DoesNotContain(catalog, p => p.Key == "simplefin-holdings");
    }
}
