using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Header free-text is stored trimmed, on BOTH write paths.
/// </summary>
/// <remarks>
/// <para>The bank editor trimmed payee / memo / check number in six inline
/// expressions; the investment editor sent <c>draft.payee || null</c>, where
/// <c>"  "</c> is truthy. So the same keystrokes stored different bytes
/// depending on which register the user happened to be on — and a padded payee
/// is a DIFFERENT payee to every grouping key in the app: the payee vocabulary,
/// similar-payees recall (which matches feed payees exactly), and the register's
/// payee search.</para>
///
/// <para>These assert the STORED value, not what a client sent, because the fix
/// is deliberately at the point of persistence. Fixing the investment editor
/// would have made the two clients agree and left MCP, a direct PATCH and the
/// next client able to store <c>"Acme  "</c>.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HeaderTextNormalizationTests
{
    private readonly PostgresFixture _fixture;

    public HeaderTextNormalizationTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private async Task<TxnHeaderRowView> StoredAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        var h = await db.TxnHeaders.AsNoTracking().SingleAsync(x => x.Id == headerId);
        return new TxnHeaderRowView(h.Payee, h.Memo, h.CheckNumber);
    }

    private sealed record TxnHeaderRowView(string? Payee, string? Memo, string? CheckNumber);

    /// <summary>
    /// The INVESTMENT create path trims, and stores whitespace-only as null.
    /// </summary>
    [Fact]
    public async Task Investment_create_stores_header_text_trimmed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TRIM", ticker: "TRIM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Utc(2026, 5, 4),
                Action = "buy",
                SecurityId = securityId,
                Shares = 1m,
                Price = 10m,
                Amount = 10m,
                Payee = "  Acme Brokerage  ",
                Memo = "  padded memo  ",
                // Whitespace-only: `|| null` kept this as "   " because a
                // non-empty string is truthy.
                CheckNumber = "   ",
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();

        var stored = await StoredAsync(headerId);
        Assert.Equal("Acme Brokerage", stored.Payee);
        Assert.Equal("padded memo", stored.Memo);
        Assert.Null(stored.CheckNumber);
    }

    /// <summary>
    /// The INVESTMENT patch path trims too — the editor re-sends every field, so
    /// an untrimmed value arrives here on any edit, not only on create.
    /// </summary>
    [Fact]
    public async Task Investment_patch_stores_header_text_trimmed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TRIM", ticker: "TRIM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = Utc(2026, 5, 4);
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 1m,
                Price = 10m,
                Amount = 10m,
                Payee = "Clean",
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 1m,
                Price = 10m,
                Amount = 10m,
                Payee = "  Acme Brokerage  ",
                Memo = "\t tabbed \t",
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var stored = await StoredAsync(headerId);
        Assert.Equal("Acme Brokerage", stored.Payee);
        Assert.Equal("tabbed", stored.Memo);
    }

    /// <summary>
    /// The BANK patch path trims at the same place, not only in its editor.
    /// </summary>
    /// <remarks>
    /// Bank's client already trimmed, so this is not a behaviour change for the
    /// SPA — it closes the same hole for every other caller. Asserted so that
    /// "bank was already fine" cannot be used to move the rule back into the
    /// editor, where it only ever covered one of the clients.
    /// </remarks>
    [Fact]
    public async Task Bank_patch_stores_header_text_trimmed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var (legId, _) = await ledger.AddTransactionPairAsync(
            checking.Id, other.Id, 25m, Utc(2026, 5, 1), "seed");

        await using var db0 = _fixture.NewDbContext();
        var headerId = (await db0.TxnLegs.AsNoTracking()
            .SingleAsync(l => l.Id == legId)).HeaderId;
        await db0.DisposeAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Payee = "  Padded Payee  ",
                Memo = "  padded memo  ",
                CheckNumber = "  ",
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var stored = await StoredAsync(headerId);
        Assert.Equal("Padded Payee", stored.Payee);
        Assert.Equal("padded memo", stored.Memo);
        Assert.Null(stored.CheckNumber);
    }

    /// <summary>
    /// An absent field stays absent — normalization must not turn null into "".
    /// </summary>
    [Fact]
    public async Task A_null_header_field_stays_null()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TRIM", ticker: "TRIM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Utc(2026, 5, 4),
                Action = "buy",
                SecurityId = securityId,
                Shares = 1m,
                Price = 10m,
                Amount = 10m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();

        var stored = await StoredAsync(headerId);
        Assert.Null(stored.Payee);
        Assert.Null(stored.Memo);
        Assert.Null(stored.CheckNumber);
    }
}
