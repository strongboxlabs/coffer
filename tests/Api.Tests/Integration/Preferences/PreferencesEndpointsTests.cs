using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Preferences;

/// <summary>
/// The per-(user, ledger) preferences (ADR-0057): the quotes preference and provider
/// catalog (<c>GET/PUT …/preferences/quotes</c>, <c>GET …/quote-providers</c>) and the
/// dashboard layout (<c>GET/PUT/DELETE …/preferences/dashboard</c>).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PreferencesEndpointsTests : DatabaseTest
{
    public PreferencesEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Quotes_pref_defaults_empty_then_round_trips()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
    public async Task Frankfurter_is_an_opt_in_provider_listed_after_yahoo_and_off_by_default()
    {
        // ADR-0114 D8: exchange rates are a new opt-in egress, off by default, the Quotes
        // tab's second row. It is not a quote pull provider, so only the catalog knows it.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = $"/api/ledgers/{ledger.LedgerId}/preferences/quotes";

        var catalog = (await client.GetFromJsonAsync<List<QuoteProviderDto>>(
            $"/api/ledgers/{ledger.LedgerId}/quote-providers"))!;
        Assert.Equal(["yahoo", "frankfurter"], catalog.Select(p => p.Key));

        var initial = (await client.GetFromJsonAsync<QuotesPrefs>(path))!;
        Assert.DoesNotContain("frankfurter", initial.EnabledProviders);

        var put = await client.PutAsJsonAsync(path, new QuotesPrefs { EnabledProviders = ["yahoo", "frankfurter"] });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = (await client.GetFromJsonAsync<QuotesPrefs>(path))!;
        Assert.Equal(["yahoo", "frankfurter"], after.EnabledProviders);
    }

    [Theory]
    // The public service by default, an operator's own container by its host alone (no
    // port, path or query), and nothing for a configured URL the client refuses to use.
    [InlineData(null, "api.frankfurter.dev")]
    [InlineData("http://rates.example.test:8080/fx/v1?x=1", null)]
    [InlineData("https://rates.example.test:8443/fx/v1/", "rates.example.test")]
    public async Task The_catalog_names_the_host_each_provider_sends_to(string? baseUrl, string? frankfurterHost)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        if (baseUrl is not null) factory.WithConfig("Quotes:Frankfurter:BaseUrl", baseUrl);
        using var client = await AuthedClientAsync(factory, ledger);

        var catalog = (await client.GetFromJsonAsync<List<QuoteProviderDto>>(
            $"/api/ledgers/{ledger.LedgerId}/quote-providers"))!;

        Assert.Equal(
            [("yahoo", (string?)"query1.finance.yahoo.com"), ("frankfurter", frankfurterHost)],
            catalog.Select(p => (p.Key, p.Host)));
    }

    [Fact]
    public async Task Quotes_pref_rejects_unknown_provider()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/preferences/quotes",
            new QuotesPrefs { EnabledProviders = new[] { "not-a-provider" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    // ---- dashboard layout (version 2) -----------------------------------------------

    private static string DashboardPath(SyntheticLedger ledger) =>
        $"/api/ledgers/{ledger.LedgerId}/preferences/dashboard";

    /// <summary>A version-2 layout, with the read's <c>updatedAt</c> echoed (or null)
    /// and any extra root property spliced in.</summary>
    private static string V2(string? updatedAt, string widgets, string extra = "") =>
        "{\"version\":2,\"columns\":\"wide-narrow\",\"widgets\":[" + widgets + "],"
        + extra
        + "\"updatedAt\":" + (updatedAt is null ? "null" : $"\"{updatedAt}\"") + "}";

    private const string TwoWidgets =
        "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":true,\"column\":\"main\","
        + "\"settings\":{\"horizonDays\":14,\"includeIncome\":true,\"note\":\"x\",\"ids\":[\"a\"],\"none\":null}},"
        + "{\"id\":\"net-worth\",\"key\":\"net-worth\",\"visible\":false,\"column\":\"side\",\"settings\":{}}";

    private static async Task<HttpResponseMessage> PutRawAsync(HttpClient client, string path, string json) =>
        await client.PutAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string? UpdatedAt(JsonElement document) =>
        document.GetProperty("updatedAt").ValueKind == JsonValueKind.Null
            ? null
            : document.GetProperty("updatedAt").GetString();

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dashboard_layout_is_empty_then_saves_and_reads_back_with_its_updatedAt()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);

        var initial = await ReadAsync(await client.GetAsync(path));
        Assert.Empty(initial.GetProperty("widgets").EnumerateArray());
        Assert.Null(UpdatedAt(initial));

        // A blank id and a repeated id are dropped, the first occurrence winning.
        var widgets = TwoWidgets
            + ",{\"id\":\"bills\",\"key\":\"bills\",\"visible\":false,\"column\":\"side\",\"settings\":{}}"
            + ",{\"id\":\" \",\"key\":\"review\",\"visible\":true,\"column\":\"main\",\"settings\":{}}";
        var saved = await ReadAsync(await PutRawAsync(client, path, V2(null, widgets)));
        Assert.NotNull(UpdatedAt(saved));

        var read = await ReadAsync(await client.GetAsync(path));
        Assert.Equal(UpdatedAt(saved), UpdatedAt(read));
        Assert.Equal(2, read.GetProperty("version").GetInt32());
        Assert.Equal("wide-narrow", read.GetProperty("columns").GetString());
        var rows = read.GetProperty("widgets").EnumerateArray().ToList();
        Assert.Equal(["bills", "net-worth"], rows.Select(w => w.GetProperty("id").GetString()!).ToArray());
        Assert.True(rows[0].GetProperty("visible").GetBoolean());
        Assert.Equal("main", rows[0].GetProperty("column").GetString());
        Assert.Equal(14, rows[0].GetProperty("settings").GetProperty("horizonDays").GetInt32());
        Assert.False(rows[1].GetProperty("visible").GetBoolean());
    }

    [Fact]
    public async Task Dashboard_save_after_save_succeeds_and_a_stale_one_writes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);

        var first = UpdatedAt(await ReadAsync(await PutRawAsync(client, path, V2(null, TwoWidgets))));
        var second = UpdatedAt(await ReadAsync(await PutRawAsync(client, path, V2(first, TwoWidgets))));
        Assert.NotEqual(first, second);

        // The first read's updatedAt is out of date now, and so is "no row".
        await AssertRefusedAsync(await PutRawAsync(client, path, V2(first, "")), "dashboard-prefs-stale");
        await AssertRefusedAsync(await PutRawAsync(client, path, V2(null, "")), "dashboard-prefs-stale");

        var read = await ReadAsync(await client.GetAsync(path));
        Assert.Equal(second, UpdatedAt(read));
        Assert.Equal(2, read.GetProperty("widgets").GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_reset_deletes_the_layout_and_a_pre_reset_save_is_stale()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);

        var before = UpdatedAt(await ReadAsync(await PutRawAsync(client, path, V2(null, TwoWidgets))));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        var reset = await ReadAsync(await client.GetAsync(path));
        Assert.Empty(reset.GetProperty("widgets").EnumerateArray());
        Assert.Null(UpdatedAt(reset));

        await AssertRefusedAsync(await PutRawAsync(client, path, V2(before, TwoWidgets)), "dashboard-prefs-stale");
        Assert.NotNull(UpdatedAt(await ReadAsync(await PutRawAsync(client, path, V2(null, TwoWidgets)))));
    }

    [Fact]
    public async Task A_version_1_layout_reads_verbatim_and_a_v2_save_echoing_its_updatedAt_replaces_it()
    {
        // The upgrade path: a layout saved by an earlier release, read, then saved as v2
        // by the SPA's migration.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using (var db = Db.NewDbContext())
        {
            db.UserPreferences.Add(new UserPreferenceRow
            {
                UserId = ledger.UserId,
                LedgerId = ledger.LedgerId,
                Namespace = "dashboard",
                ValueJson = "{\"widgets\":[{\"key\":\"upcoming\",\"visible\":false},{\"key\":\"accounts\",\"visible\":true}]}",
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);

        var v1 = await ReadAsync(await client.GetAsync(path));
        Assert.False(v1.TryGetProperty("version", out _));
        var keys = v1.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(["upcoming", "accounts"], keys);
        var stamp = UpdatedAt(v1);
        Assert.NotNull(stamp);

        var saved = await ReadAsync(await PutRawAsync(client, path, V2(stamp, TwoWidgets)));
        Assert.Equal(2, saved.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_version_1_save_or_a_malformed_layout_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);

        foreach (var body in new[]
                 {
                     "{\"widgets\":[{\"key\":\"accounts\",\"visible\":true}]}",
                     V2(null, "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":true,\"column\":\"\",\"settings\":{}}"),
                     V2(null, "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":\"yes\",\"column\":\"main\",\"settings\":{}}"),
                     V2(null, "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":true,\"column\":\"main\",\"settings\":{\"x\":{\"y\":1}}}"),
                     V2(null, "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":true,\"column\":\"main\",\"settings\":{\"x\":[1]}}"),
                     "{\"version\":2,\"widgets\":[],\"updatedAt\":null}",
                 })
        {
            await AssertRefusedAsync(await PutRawAsync(client, path, body), "dashboard-prefs-invalid");
        }

        // Nothing was written by any of them.
        Assert.Null(UpdatedAt(await ReadAsync(await client.GetAsync(path))));
    }

    [Fact]
    public async Task The_length_and_depth_caps_hold_at_their_bounds()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var path = DashboardPath(ledger);
        const int maxLength = 64 * 1024;

        // A layout exactly at the length cap is accepted; one character more is not.
        string Padded(int length, string? updatedAt)
        {
            var shell = V2(updatedAt, "{\"id\":\"bills\",\"key\":\"bills\",\"visible\":true,\"column\":\"main\",\"settings\":{\"pad\":\"\"}}");
            return shell.Replace("\"pad\":\"\"", "\"pad\":\"" + new string('x', length - shell.Length) + "\"");
        }
        await AssertRefusedAsync(await PutRawAsync(client, path, Padded(maxLength + 1, null)), "dashboard-prefs-invalid");
        var atCap = Padded(maxLength, null);
        Assert.Equal(maxLength, atCap.Length);
        var stamp = UpdatedAt(await ReadAsync(await PutRawAsync(client, path, atCap)));

        // Nested exactly to the depth cap (the root object is the first level) is
        // accepted; one level more is not.
        string Nested(int levels) =>
            "\"extra\":" + new string('[', levels - 1) + new string(']', levels - 1) + ",";
        await AssertRefusedAsync(await PutRawAsync(client, path, V2(stamp, "", Nested(17))), "dashboard-prefs-invalid");
        Assert.NotNull(UpdatedAt(await ReadAsync(await PutRawAsync(client, path, V2(stamp, "", Nested(16))))));
    }

    [Fact]
    public async Task Quote_providers_catalog_lists_opt_in_providers()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var catalog = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/quote-providers"))
            .Content.ReadFromJsonAsync<List<QuoteProviderDto>>())!;

        // Yahoo is the opt-in provider; the no-egress simplefin-holdings is not listed.
        Assert.Contains(catalog, p => p.Key == "yahoo");
        Assert.DoesNotContain(catalog, p => p.Key == "simplefin-holdings");
    }
}
