using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ledgers;

/// <summary>
/// A ledger's reporting currency is held to the account rule (ADR-0114 D15): an ISO 4217
/// currency with at most 3 places. Create-ledger, the ledger PATCH and the Moneydance
/// import's chosen currency refuse any other code; a ledger whose code was stored before the
/// rule keeps it when a PATCH resends it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LedgerCurrencyAcceptanceTests : DatabaseTest
{
    public LedgerCurrencyAcceptanceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Invalid = "ledger-currency-invalid";

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid ledgerId, UpdateLedgerRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledgerId}")
        {
            Content = JsonContent.Create(body),
        });

    private async Task<(string Name, string CurrencyCode)> StoredAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var row = await db.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
        return (row.Name, row.CurrencyCode);
    }

    [Fact]
    public async Task A_change_to_a_code_coffer_does_not_accept_is_refused_and_changes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var before = await StoredAsync(ledger.LedgerId);

        Assert.Equal(Invalid, await CodeAsync(await PatchAsync(client, ledger.LedgerId,
            new UpdateLedgerRequest { Name = "Ledger B", CurrencyCode = "CLF" })));
        Assert.Equal(Invalid, await CodeAsync(await PatchAsync(client, ledger.LedgerId,
            new UpdateLedgerRequest { CurrencyCode = "XAU" })));

        Assert.Equal(before, await StoredAsync(ledger.LedgerId));

        // The anchor: an accepted code of any places is taken.
        Assert.Equal(HttpStatusCode.NoContent,
            (await PatchAsync(client, ledger.LedgerId, new UpdateLedgerRequest { CurrencyCode = "KWD" })).StatusCode);
        Assert.Equal("KWD", (await StoredAsync(ledger.LedgerId)).CurrencyCode);
    }

    [Fact]
    public async Task A_stored_code_coffer_no_longer_accepts_is_kept_when_a_patch_resends_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "XAU");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(client, ledger.LedgerId,
            new UpdateLedgerRequest { Name = "Ledger B", CurrencyCode = "XAU" })).StatusCode);
        Assert.Equal(("Ledger B", "XAU"), await StoredAsync(ledger.LedgerId));

        // A change to another refused code is still refused.
        Assert.Equal(Invalid, await CodeAsync(await PatchAsync(client, ledger.LedgerId,
            new UpdateLedgerRequest { CurrencyCode = "UYW" })));
        Assert.Equal("XAU", (await StoredAsync(ledger.LedgerId)).CurrencyCode);
    }

    [Fact]
    public async Task Create_ledger_refuses_a_code_coffer_does_not_accept()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var name = $"Ledger {Guid.NewGuid():N}";

        Assert.Equal(Invalid, await CodeAsync(await client.PostAsJsonAsync("/api/ledgers",
            new CreateLedgerRequest { Name = name, CurrencyCode = "UYW", SeedDefaultCategories = false })));

        await using var db = Db.NewDbContext();
        Assert.False(await db.Ledgers.AsNoTracking().AnyAsync(l => l.Name == name));

        var created = await client.PostAsJsonAsync("/api/ledgers",
            new CreateLedgerRequest { Name = name, CurrencyCode = "JPY", SeedDefaultCategories = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task The_moneydance_imports_chosen_currency_is_held_to_the_same_rule()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        // The chosen currency is read before the file is parsed, so any file will do.
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("{}"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "export.json");
        content.Add(new StringContent("CLF"), "currencyCode");

        Assert.Equal(Invalid, await CodeAsync(await client.PostAsync("/api/imports/moneydance/preview", content)));
    }
}
