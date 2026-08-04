using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// End-to-end checks for the Securities catalog API (slice A3).
/// Each test bootstraps an atomic <see cref="SyntheticLedger"/> so
/// concurrent test classes don't share rows. Covers the five
/// endpoints: list / get-by-id / create / patch / list-transactions.
/// Cross-ledger isolation is exercised explicitly — a second ledger
/// is bootstrapped where relevant and queries against the wrong
/// ledger must 422.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SecuritiesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public SecuritiesEndpointsTests(PostgresFixture fixture)
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
    public async Task AddPrice_replaces_an_existing_price_for_the_same_day()
    {
        // ADR-0070 D6: 'manual' tops the source ladder, so a hand-entered price
        // OWNS its day — posting a second price for the same (security, day)
        // REPLACES the first in place (no duplicate row, no 4xx), rather than
        // rejecting as it did under the timestamp-keyed model.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var securityId = await ledger.AddSecurityAsync(name: "Index Fund A", ticker: "IDXA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var basePath = $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices";

        var first = await client.PostAsJsonAsync(basePath,
            new { price = 200m, priceDate = new DateOnly(2026, 5, 1) });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same day, different price → replace in place.
        var second = await client.PostAsJsonAsync(basePath,
            new { price = 211.50m, priceDate = new DateOnly(2026, 5, 1) });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var db = _fixture.NewDbContext();
        var rows = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(211.50m, row.Price);
        Assert.Equal(new DateOnly(2026, 5, 1), row.PriceDate);
        Assert.Equal(PriceSource.Manual, row.Source);
    }

    [Fact]
    public async Task ListPrices_surfaces_the_price_source()
    {
        // The prices table shows where each price came from (security_prices.source)
        // so SimpleFIN-derived rows are distinguishable from manual / imported ones.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var securityId = await ledger.AddSecurityAsync(name: "Index Fund A", ticker: "IDXA");
        await ledger.AddSecurityPriceAsync(
            securityId, 200m, new DateTime(2026, 4, 1), source: PriceSource.Import);
        await ledger.AddSecurityPriceAsync(
            securityId, 211m, new DateTime(2026, 5, 1), source: PriceSource.Simplefin);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var page = await resp.Content.ReadFromJsonAsync<SecurityPricesPage>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.Items.Count);
        // Newest first: 5/1 (simplefin) then 4/1 (import).
        Assert.Equal(PriceSource.Simplefin, page.Items[0].Source);
        Assert.Equal(PriceSource.Import, page.Items[1].Source);
    }

    [Fact]
    public async Task Price_column_enforces_four_decimals()
    {
        // ADR-0070 D8: security_prices.price is NUMERIC(19,4), so single-precision
        // float noise can't be stored — the DB rounds on write regardless of the
        // producer. A 12dp value lands at 4dp (7.150000095367 -> 7.15).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var securityId = await ledger.AddSecurityAsync(name: "Index Fund A", ticker: "IDXA");
        await ledger.AddSecurityPriceAsync(
            securityId, 7.150000095367m, new DateTime(2026, 5, 1), source: PriceSource.Import);

        await using var db = _fixture.NewDbContext();
        var price = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(7.15m, price.Price);
    }

    [Fact]
    public async Task List_returns_catalog_with_total_quantity_and_latest_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var idxa = await ledger.AddSecurityAsync(
            name: "Index Fund A", ticker: "IDXA", assetClass: "equity");
        var mmfa = await ledger.AddSecurityAsync(
            name: "Money Market Fund A", ticker: "MMFA", assetClass: "cash");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, idxa, 50m, 9000m);
        await ledger.AddSecurityPriceAsync(idxa, 200m, new DateTime(2026, 4, 1));
        await ledger.AddSecurityPriceAsync(idxa, 225m, new DateTime(2026, 5, 1));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<List<SecuritySummaryDto>>();
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);

        var vt = rows.Single(r => r.Ticker == "IDXA");
        Assert.Equal(50m, vt.TotalQuantity);
        // The most recent price (5/1) wins, not the earlier 4/1 row.
        Assert.Equal(225m, vt.LatestPrice);
        Assert.Equal(new DateOnly(2026, 5, 1), vt.LatestPriceAsOf!.Value);

        var vu = rows.Single(r => r.Ticker == "MMFA");
        Assert.Equal(0m, vu.TotalQuantity);
        Assert.Null(vu.LatestPrice);
    }

    [Fact]
    public async Task List_search_filters_case_insensitive_substring()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddSecurityAsync(name: "Index Fund A", ticker: "IDXA");
        await ledger.AddSecurityAsync(name: "Money Market Fund A", ticker: "MMFA");
        await ledger.AddSecurityAsync(name: "iShares S&P 500",       ticker: "IVV");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // "fund" matches both IDXA and MMFA by name; IVV doesn't.
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities?q=fund");
        var rows = await response.Content.ReadFromJsonAsync<List<SecuritySummaryDto>>();
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.All(rows, r => Assert.Contains("Fund", r.Name));

        // Ticker substring works too.
        response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities?q=ivv");
        rows = await response.Content.ReadFromJsonAsync<List<SecuritySummaryDto>>();
        Assert.NotNull(rows);
        Assert.Single(rows!);
        Assert.Equal("IVV", rows[0].Ticker);
    }

    [Fact]
    public async Task GetById_returns_hero_data_plus_recent_prices()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var idxa = await ledger.AddSecurityAsync(
            name: "IDXA Fund", ticker: "IDXA", assetClass: "equity");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, idxa, 100m, 19500m);
        // 12 price points; the endpoint returns the most recent 10.
        for (var i = 0; i < 12; i++)
        {
            await ledger.AddSecurityPriceAsync(
                idxa, 200m + i, new DateTime(2026, 4, 1).AddDays(i));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{idxa}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal("IDXA", detail!.Ticker);
        Assert.Equal(100m, detail.TotalQuantity);
        Assert.Equal(19500m, detail.TotalCostBasis);
        // Latest price is day 12 (200 + 11 = 211).
        Assert.Equal(211m, detail.LatestPrice);
        // 10-row cap on recent prices.
        Assert.Equal(10, detail.RecentPrices.Count);
        // Newest-first.
        Assert.Equal(211m, detail.RecentPrices[0].Price);
    }

    [Fact]
    public async Task GetById_rejects_security_in_a_different_ledger()
    {
        // Two independent ledgers. A security exists in ledger B; ledger
        // A's client must NOT be able to fetch it. The composite FK in
        // migration 049 makes this impossible at the schema layer; this
        // test exercises the API gate too — `security-not-in-ledger`
        // 422 surfaces explicitly rather than the silent 404 a generic
        // "not found" would.
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        var secInB = await ledgerB.AddSecurityAsync(name: "B-only", ticker: "BONLY");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledgerA);

        // Ledger A's user requesting ledger B's security via ledger A's
        // path. The endpoint sees: visible(userA, ledgerA) = true, but
        // the repo's WHERE ledger_id = ledgerA filters out the B-row.
        var response = await client.GetAsync(
            $"/api/ledgers/{ledgerA.LedgerId}/securities/{secInB}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_inserts_and_returns_new_id()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest
            {
                Ticker = "IVV",
                Name = "iShares S&P 500",
                AssetClass = "equity",
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newId = doc.RootElement.GetProperty("securityId").GetGuid();
        Assert.NotEqual(Guid.Empty, newId);

        // GET returns the freshly-created row.
        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{newId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_empty_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest { Ticker = "X", Name = "   " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-name-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_rejects_invalid_asset_class()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest
            {
                Name = "Bad",
                AssetClass = "crypto",   // not in the enum
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-asset-class-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_rejects_duplicate_ticker_case_insensitive_within_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddSecurityAsync(name: "First", ticker: "IDXA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Lowercase ticker — the partial unique index keys on LOWER(ticker)
        // (migration 048), so the second create must collide.
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest { Ticker = "idxa", Name = "Second" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-duplicate-ticker",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_allows_same_ticker_across_different_ledgers()
    {
        // The whole point of migration 048's per-ledger uniqueness:
        // ledger A and ledger B can each hold AAPL.
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        await ledgerA.AddSecurityAsync(name: "Apple A", ticker: "AAPL");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var clientB = await AuthedClientAsync(factory, ledgerB);

        var response = await clientB.PostAsJsonAsync(
            $"/api/ledgers/{ledgerB.LedgerId}/securities",
            new CreateSecurityRequest { Ticker = "AAPL", Name = "Apple B" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Patch_renames_security_and_returns_204()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sec = await ledger.AddSecurityAsync(name: "Old name", ticker: "OLD");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}",
            new PatchSecurityRequest { Name = "New name", AssetClass = "equity" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}");
        var detail = await get.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.Equal("New name", detail!.Name);
        Assert.Equal("equity", detail.AssetClass);
    }

    [Fact]
    public async Task Patch_deactivate_flips_is_active()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sec = await ledger.AddSecurityAsync(name: "Retiring", ticker: "RIP");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}",
            new PatchSecurityRequest { IsActive = false });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}");
        var detail = await get.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.False(detail!.IsActive);
    }

    [Fact]
    public async Task Patch_rejects_cross_ledger_security()
    {
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        var secInB = await ledgerB.AddSecurityAsync(name: "B-only", ticker: "BONLY");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var clientA = await AuthedClientAsync(factory, ledgerA);

        var response = await clientA.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerA.LedgerId}/securities/{secInB}",
            new PatchSecurityRequest { Name = "hijack attempt" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_rejects_not_public_without_quote_symbol()
    {
        // ADR-0054 D2: "not public" only makes sense with a quote symbol to keep
        // private — a bare ticker is always public. Mirrors the mig-156 CHECK.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest
            {
                Ticker = "TCK",
                Name = "Ticker only",
                QuoteSymbolPublic = false,   // but no quote symbol
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-quote-symbol-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_allows_private_quote_symbol_and_round_trips()
    {
        // A 529-style feed-only symbol: quote symbol set, marked not public.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities",
            new CreateSecurityRequest
            {
                Name = "Feed-only 529",
                QuoteSymbol = "8918",
                QuoteSymbolPublic = false,
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newId = doc.RootElement.GetProperty("securityId").GetGuid();

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{newId}");
        var detail = await get.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.Equal("8918", detail!.QuoteSymbol);
        Assert.False(detail.QuoteSymbolPublic);
    }

    [Fact]
    public async Task Patch_rejects_marking_not_public_without_quote_symbol()
    {
        // A ticker-only security can't be marked non-public — the ticker is public.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sec = await ledger.AddSecurityAsync(name: "Ticker only", ticker: "TCK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}",
            new PatchSecurityRequest { QuoteSymbolPublic = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-quote-symbol-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_sets_private_quote_symbol_in_one_call_and_round_trips()
    {
        // Set the quote symbol AND mark it non-public in a single PATCH — the guard
        // sees the freshly-applied symbol (order matters), so this succeeds.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sec = await ledger.AddSecurityAsync(name: "MD 529", ticker: "TCK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}",
            new PatchSecurityRequest { QuoteSymbol = "8918", QuoteSymbolPublic = false });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}");
        var detail = await get.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.Equal("8918", detail!.QuoteSymbol);
        Assert.False(detail.QuoteSymbolPublic);
    }

    [Fact]
    public async Task ListTransactions_returns_404_equivalent_for_unknown_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{Guid.NewGuid()}/transactions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The per-security transactions list (and its total-count badge) must
    /// apply the SAME visibility predicate the register + holdings use: exclude
    /// hidden (override OR raw column) and merged-away headers. Regression for
    /// the leak where an import-overlap duplicate the user hid still showed on
    /// the security Detail page as a phantom second buy and inflated the count.
    /// </summary>
    [Fact]
    public async Task ListTransactions_excludes_hidden_and_merged_legs_from_list_and_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> BuyAsync(int month, decimal shares)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = new DateTime(2026, month, 10, 12, 0, 0, DateTimeKind.Utc),
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = shares,
                    Price = 100m,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var visible = await BuyAsync(1, 10m);
        var overrideHidden = await BuyAsync(2, 20m);
        var rawHidden = await BuyAsync(3, 30m);
        var merged = await BuyAsync(4, 40m);

        // Three ways a leg leaves the register's visibility — all must be
        // excluded here too.
        await ledger.HideTransactionAsync(overrideHidden);          // txn_header_overrides.is_hidden
        await using (var db = _fixture.NewDbContext())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE txn_headers SET is_hidden = TRUE WHERE id = {rawHidden}");
        await ledger.MarkTransactionMergedAsync(merged, visible);   // txn_headers.is_merged_into

        var page = await client.GetFromJsonAsync<SecurityTransactionsPage>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/transactions");

        Assert.NotNull(page);
        Assert.Single(page!.Items);
        Assert.Equal(10m, page.Items[0].Quantity);
        Assert.Equal(1, page.TotalCount);
    }
}
