using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Selling shares the account did not hold (migration 256).
/// </summary>
/// <remarks>
/// <para>Until 256 an oversell was silently accepted and corrupted two numbers.
/// The FIFO walk drained every open lot and stopped with the excess unread; the
/// holding then took the FULL sale and went negative, and the realized gain
/// booked the unmatched shares at zero cost. The API defined
/// <c>investment-txn-insufficient-shares</c> and never returned it.</para>
///
/// <para>The editor's lot preview could not stand in for a guard: it read the
/// <c>lots</c> table, which FIFO drains in place and so only knows today. On a
/// since-closed account it warned about every historical edit, while a real
/// oversell went through.</para>
///
/// <para>The guard asks the walk itself, before and after the save, and refuses
/// a save that leaves any position MORE short than it found it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentShortfallGuardTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentShortfallGuardTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Day(int month, int day) =>
        new(2014, month, day, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(
        SyntheticLedger Ledger, Guid BrokerageId, Guid HoldingsId, Guid SecurityId,
        Guid CheckingId, Guid SavingsId);

    private async Task<(Seed Seed, HttpClient Client, IAsyncDisposable Factory)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("401(k)");
        var securityId = await ledger.AddSecurityAsync("Target Date Fund", ticker: "TDFA");
        var checking = await ledger.AddBankAccountAsync("Checking");
        var savings = await ledger.AddBankAccountAsync("Savings");

        var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        return (new Seed(ledger, brokerage.Id, brokerage.HoldingsAccountId!.Value, securityId,
            checking.Id, savings.Id), client, factory);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, Seed s, string action, decimal shares, DateTime postedAt,
        Guid? transferAccountId = null) =>
        await client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = s.BrokerageId,
                PostedAt = postedAt,
                Action = action,
                SecurityId = s.SecurityId,
                Shares = shares,
                Price = 10m,
                TransferAccountId = transferAccountId,
            });

    private static async Task<Guid> CreateAsync(
        HttpClient client, Seed s, string action, decimal shares, DateTime postedAt,
        Guid? transferAccountId = null)
    {
        var resp = await PostAsync(client, s, action, shares, postedAt, transferAccountId);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Seed s, Guid headerId, string action, decimal shares,
        DateTime postedAt, Guid? transferAccountId = null, string? payee = null) =>
        await client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = s.BrokerageId,
                PostedAt = postedAt,
                Action = action,
                SecurityId = s.SecurityId,
                Shares = shares,
                Price = 10m,
                TransferAccountId = transferAccountId,
                Payee = payee,
            });

    private static async Task<string?> CodeAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private async Task<decimal> HeldAsync(Seed s)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == s.HoldingsId && h.SecurityId == s.SecurityId)
            .Select(h => h.Quantity)
            .SingleAsync();
    }

    private async Task<decimal?> SharesOnAsync(Guid headerId, Guid holdingsId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == holdingsId)
            .Select(l => l.Quantity)
            .SingleAsync();
    }

    [Fact]
    public async Task Selling_more_than_was_held_is_refused_and_nothing_is_written()
    {
        // THE DEFECT. Before 256 this returned 201, took the holding negative by
        // the excess and booked the excess shares as gain at zero cost.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        await CreateAsync(client, s, "buy", 10m, Day(1, 10));

        var resp = await PostAsync(client, s, "sell", -12m, Day(2, 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("investment-txn-insufficient-shares", await CodeAsync(resp));
        // Refused means rolled back: the position is exactly the buy.
        Assert.Equal(10m, await HeldAsync(s));
    }

    [Fact]
    public async Task A_sale_on_a_since_closed_position_can_still_be_edited()
    {
        // THE CASE THAT SURFACED THIS: a years-old SellXfr on a workplace plan that
        // has since been sold out, opened to change where the cash went. The preview said
        // "no open lots — server may reject", because it asked about TODAY. The
        // save must go through, and the preview must see what the sale met.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        await CreateAsync(client, s, "buy", 10m, Day(1, 10));
        var sale = await CreateAsync(client, s, "sellx", -10m, Day(2, 1), s.CheckingId);

        var resp = await PatchAsync(client, s, sale, "sellx", -10m, Day(2, 1), s.SavingsId);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Today, nothing is open — which is true, and was the wrong question.
        var lotsUrl = $"/api/ledgers/{s.Ledger.LedgerId}/accounts/{s.BrokerageId}"
            + $"/securities/{s.SecurityId}/lots";
        var today = await client.GetFromJsonAsync<List<InvestmentLotDto>>(lotsUrl);
        Assert.Empty(today!);

        // As of the sale, without the sale: the whole lot it actually met.
        var asOf = Uri.EscapeDataString(Day(2, 1).ToString("O"));
        var met = await client.GetFromJsonAsync<List<InvestmentLotDto>>(
            $"{lotsUrl}?as_of={asOf}&exclude_header_id={sale}");
        var lot = Assert.Single(met!);
        Assert.Equal(10m, lot.Quantity);
    }

    [Fact]
    public async Task Raising_a_historical_sale_is_checked_against_its_own_date_not_today()
    {
        // One lot held in February; an equal lot arrives in March. Today's
        // position — both lots less February's sale — is larger than anything
        // February held, so a check against today would let February sell more
        // than it ever had. February only ever held the first lot.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        await CreateAsync(client, s, "buy", 10m, Day(1, 10));
        var february = await CreateAsync(client, s, "sell", -6m, Day(2, 1));
        await CreateAsync(client, s, "buy", 10m, Day(3, 1));

        var resp = await PatchAsync(client, s, february, "sell", -12m, Day(2, 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("investment-txn-insufficient-shares", await CodeAsync(resp));
        Assert.Equal(-6m, await SharesOnAsync(february, s.HoldingsId));

        // ...and within what February held, the same edit is fine.
        var within = await PatchAsync(client, s, february, "sell", -9m, Day(2, 1));
        Assert.Equal(HttpStatusCode.NoContent, within.StatusCode);
    }

    [Fact]
    public async Task Editing_a_buy_down_under_a_later_sale_is_refused()
    {
        // The indirect way to oversell: the sale is untouched and becomes short
        // because the purchase it relied on shrank. A check on the edited row
        // alone would miss it; the walk does not.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        var buy = await CreateAsync(client, s, "buy", 10m, Day(1, 10));
        await CreateAsync(client, s, "sell", -8m, Day(2, 1));

        var resp = await PatchAsync(client, s, buy, "buy", 5m, Day(1, 10));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("investment-txn-insufficient-shares", await CodeAsync(resp));
        Assert.Equal(10m, await SharesOnAsync(buy, s.HoldingsId));
    }

    [Fact]
    public async Task Moving_a_sale_before_its_shares_arrived_is_refused()
    {
        // The date is an input to the walk too. Same quantity, earlier date, and
        // there is nothing yet to sell.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        await CreateAsync(client, s, "buy", 10m, Day(3, 1));
        var sale = await CreateAsync(client, s, "sell", -5m, Day(4, 1));

        var resp = await PatchAsync(client, s, sale, "sell", -5m, Day(2, 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("investment-txn-insufficient-shares", await CodeAsync(resp));
    }

    [Fact]
    public async Task The_lot_preview_for_an_old_sale_shows_what_that_sale_met_not_today()
    {
        // The false warning: a since-closed position has no open lots TODAY, so the
        // editor's preview of its old sale said "No open lots" and "exceeds open
        // position" on every edit. Asked for the sale's date, without the sale, it
        // sees the shares the sale actually took.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;
        await CreateAsync(client, s, "buy", 10m, Day(1, 10));
        var sale = await CreateAsync(client, s, "sellx", -10m, Day(2, 1), s.CheckingId);
        var lots = $"/api/ledgers/{s.Ledger.LedgerId}/accounts/{s.BrokerageId}/securities/{s.SecurityId}/lots";

        var today = await client.GetFromJsonAsync<List<InvestmentLotDto>>(lots);
        var then = await client.GetFromJsonAsync<List<InvestmentLotDto>>(
            $"{lots}?as_of={Uri.EscapeDataString(Day(2, 1).ToString("O"))}&exclude_header_id={sale}");

        Assert.Empty(today!);
        Assert.Equal(10m, Assert.Single(then!).Quantity);
    }

    [Fact]
    public async Task An_already_short_sale_stays_editable_but_cannot_be_made_worse()
    {
        // WHY THE RULE IS "WORSE", NOT "SHORT". Imported data carries residues —
        // the case that started this was a sale short by a small fraction of a
        // share. Refusing any
        // save on a short position would lock the user out of the very rows they
        // are cleaning up.
        var (s, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        var buy = await CreateAsync(client, s, "buy", 10m, Day(1, 10));
        var sale = await CreateAsync(client, s, "sellx", -10m, Day(2, 1), s.CheckingId);

        // The residue, written directly. This is a state the product really
        // holds — the Moneydance importer writes it, and so did this API until
        // 256 — but no request can create it any more, which is the point.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnLegs
                .Where(l => l.HeaderId == buy && l.AccountId == s.HoldingsId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Quantity, 9.955m));
        }

        // Not made worse: a cash-side edit, and a smaller sale, both save.
        var retarget = await PatchAsync(client, s, sale, "sellx", -10m, Day(2, 1), s.SavingsId);
        Assert.Equal(HttpStatusCode.NoContent, retarget.StatusCode);
        var smaller = await PatchAsync(client, s, sale, "sellx", -9.99m, Day(2, 1), s.SavingsId);
        Assert.Equal(HttpStatusCode.NoContent, smaller.StatusCode);

        // Made worse: refused.
        var deeper = await PatchAsync(client, s, sale, "sellx", -10.5m, Day(2, 1), s.SavingsId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deeper.StatusCode);
        Assert.Equal("investment-txn-insufficient-shares", await CodeAsync(deeper));
    }
}
