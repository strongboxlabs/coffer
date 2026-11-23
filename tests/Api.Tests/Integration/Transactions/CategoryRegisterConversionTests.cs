using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A category's register (ADR-0114, "The FU-106 inventory, corrected"): each row stays in
/// the currency of the money it moved and says which; the Balance converts each
/// transaction at its own date into the ledger's reporting currency, as a running sum in
/// the walk's order, rounded once; a missing rate makes that balance and every later one
/// null, naming the pair and day (ADR-0114 D9); and a category whose ledger is in one currency
/// reads its stored balances, with no rate read at all.
/// </summary>
/// <remarks>
/// Transactions are written through <c>POST /transactions</c>, the register's own
/// writer, so every stored running balance is the one a save leaves. Rates and coverage
/// are written as the ledger's owner through EF, the role and path the rate writers use.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CategoryRegisterConversionTests : DatabaseTest
{
    public CategoryRegisterConversionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Jan(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateOnly Day(int day) => new(2026, 1, day);

    /// <summary>Every statement a read sends.</summary>
    private sealed class Statements : DbCommandInterceptor
    {
        public List<string> Sent { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public IEnumerable<string> RateReads => Sent.Where(s =>
            s.Contains("fx_rates", StringComparison.Ordinal) || s.Contains("fx_pair_coverage", StringComparison.Ordinal));
    }

    private sealed record Spend(int Day, bool Euro, decimal Amount);

    /// <summary>
    /// The category's rows, oldest first: three in USD and three in EUR, two of them on one
    /// day, the USD one first, so a balance that read the day's later EUR row too would
    /// show.
    /// </summary>
    private static readonly Spend[] Spends =
    [
        new(5, false, 10m),
        new(6, false, 1m),
        new(6, true, 20m),
        new(8, true, 30m),
        new(10, false, 5m),
        new(12, true, 7.77m),
    ];

    /// <summary>The EUR to USD rate each row converts at: its day's, or the last before it.</summary>
    private static readonly decimal[] RateOfRow = [1m, 1m, 1.1m, 1.123456789012m, 1m, 1.2m];

    private sealed record Fixture(
        SyntheticLedger Ledger, AccountRow Dollars, AccountRow Euros, AccountRow Category, IReadOnlyList<Guid> Headers);

    private async Task<Fixture> SeedAsync(bool euroAccount = true)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var dollars = await ledger.AddBankAccountAsync("Account A");
        var euros = await ledger.AddBankAccountAsync("Account B", currencyCode: euroAccount ? "EUR" : "USD");
        var category = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headers = new List<Guid>();
        foreach (var s in Spends)
        {
            using var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Jan(s.Day),
                    SourceAccountId = s.Euro ? euros.Id : dollars.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -s.Amount }],
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            headers.Add((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid());
        }
        return new Fixture(ledger, dollars, euros, category, headers);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return client;
    }

    /// <summary>EUR to USD on the 1st, 6th, 7th and 12th, fetched through
    /// <paramref name="coveredThrough"/>.</summary>
    private async Task SeedRatesAsync(SyntheticLedger ledger, DateOnly coveredThrough)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        foreach (var (day, rate) in new[] { (1, 0.9m), (6, 1.1m), (7, 1.123456789012m), (12, 1.2m) })
        {
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = Day(day), Rate = rate, Source = FxRateSource.Frankfurter, Upstream = "Source A",
            });
        }
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = coveredThrough,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(RegisterPage Page, Statements Statements)> PageAsync(
        Fixture f, Guid accountId, int limit = 100, string? cursor = null, Guid? startingAt = null,
        RegisterFilter? filter = null, RegisterSort? sort = null)
    {
        var statements = new Statements();
        await using var db = Db.NewRequestDbContextAsUser(f.Ledger.UserId, statements);
        var page = await new RegisterRepository(db, TimeProvider.System).GetPageAsync(
            f.Ledger.LedgerId, accountId, cursor, RegisterRepository.DirectionBefore, startingAt, limit,
            hidden: false, filter, sort);
        return (page, statements);
    }

    private static Dictionary<Guid, RegisterRowDto> ByHeader(RegisterPage page) =>
        page.Entries.SelectMany(e => e.Txn is { } t ? [t] : e.Legs!).ToDictionary(r => r.HeaderId);

    /// <summary>The running sum of each row converted at its rate, rounded once at the
    /// stored money scale: what the Balance must show, oldest row first.</summary>
    private static decimal[] ExpectedBalances()
    {
        var sum = 0m;
        return [.. Spends.Select((s, i) =>
        {
            sum += s.Amount * RateOfRow[i];
            return Math.Round(sum, 4, MidpointRounding.AwayFromZero);
        })];
    }

    [Fact]
    public async Task Each_row_keeps_its_currency_and_the_balance_is_the_running_sum_of_converted_amounts()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));

        var (page, _) = await PageAsync(f, f.Category.Id);
        var rows = ByHeader(page);
        var expected = ExpectedBalances();
        for (var i = 0; i < Spends.Length; i++)
        {
            var row = rows[f.Headers[i]];
            Assert.Equal(Spends[i].Amount, row.Amount);   // native
            Assert.Equal(Spends[i].Euro ? "EUR" : "USD", row.CurrencyCode);
            Assert.Equal("USD", row.BalanceCurrencyCode);
            Assert.Equal(expected[i], row.BalanceAfter);
            Assert.Null(row.BalanceRateUnavailable);
        }
        // A sum that was not rounded once would carry the rate's twelve places.
        Assert.Equal(81.0277m, expected[^1]);

        // The paying accounts' own registers stay native: balance and amount in their currency.
        var (euroPage, _) = await PageAsync(f, f.Euros.Id);
        var euroRows = ByHeader(euroPage);
        Assert.All(euroRows.Values, r => Assert.Equal(("EUR", "EUR"), (r.CurrencyCode, r.BalanceCurrencyCode)));
        Assert.Equal(-(20m + 30m + 7.77m), euroRows[f.Headers[5]].BalanceAfter);
    }

    [Fact]
    public async Task The_wire_names_each_rows_currency_and_the_balances()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, f.Ledger);

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{f.Ledger.LedgerId}/transactions?account_id={f.Category.Id}");
        var newest = json.GetProperty("entries")[0].GetProperty("txn");
        Assert.Equal("EUR", newest.GetProperty("currencyCode").GetString());
        Assert.Equal("USD", newest.GetProperty("balanceCurrencyCode").GetString());
        Assert.Equal(ExpectedBalances()[^1], newest.GetProperty("balanceAfter").GetDecimal());
        Assert.False(newest.TryGetProperty("balanceRateUnavailable", out _));
    }

    [Fact]
    public async Task A_missing_rate_nulls_that_balance_and_every_later_one_naming_each_pair_and_day()
    {
        var f = await SeedAsync();
        // Fetched through the 7th: the EUR rows of the 8th and the 12th are after coverage.
        await SeedRatesAsync(f.Ledger, Day(7));
        // A third currency whose own rate is there, on a row after the gap.
        var pounds = await f.Ledger.AddBankAccountAsync("Account C", currencyCode: "GBP");
        await using (var owner = Db.NewAppDbContextAsUser(f.Ledger.UserId))
        {
            owner.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = f.Ledger.LedgerId, FromCurrency = "GBP", ToCurrency = "USD",
                RateDate = Day(1), Rate = 1.3m, Source = FxRateSource.Frankfurter, Upstream = "Source A",
            });
            owner.FxPairCoverage.Add(new FxPairCoverageRow
            {
                LedgerId = f.Ledger.LedgerId, FromCurrency = "GBP", ToCurrency = "USD", FetchedThroughDate = Day(31),
            });
            await owner.SaveChangesAsync();
        }
        Guid poundRow;
        await using (var factory = new ApiFactory(Db).WithoutDevAuth())
        using (var client = await ClientAsync(factory, f.Ledger))
        {
            using var resp = await client.PostAsJsonAsync($"/api/ledgers/{f.Ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Jan(9),
                    SourceAccountId = pounds.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = f.Category.Id, Amount = -4m }],
                });
            poundRow = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        }

        var (page, _) = await PageAsync(f, f.Category.Id);
        var rows = ByHeader(page);
        var expected = ExpectedBalances();
        for (var i = 0; i < 3; i++)
            Assert.Equal(expected[i], rows[f.Headers[i]].BalanceAfter);

        var eighth = new RateUnavailable("EUR", "USD", Day(8));
        var twelfth = new RateUnavailable("EUR", "USD", Day(12));
        Assert.Null(rows[f.Headers[3]].BalanceAfter);
        Assert.Equal([eighth], rows[f.Headers[3]].BalanceRateUnavailable!);
        // Rows after the gap have no balance either, though their own rate is there or
        // they need none: the running sum behind them is not known.
        Assert.Equal(("GBP", (decimal?)null), (rows[poundRow].CurrencyCode, rows[poundRow].BalanceAfter));
        Assert.Equal([eighth], rows[poundRow].BalanceRateUnavailable!);
        Assert.Null(rows[f.Headers[4]].BalanceAfter);
        Assert.Equal([eighth], rows[f.Headers[4]].BalanceRateUnavailable!);
        Assert.Null(rows[f.Headers[5]].BalanceAfter);
        Assert.Equal([eighth, twelfth], rows[f.Headers[5]].BalanceRateUnavailable!);

        // The after-save refresh answers the same, so it never puts a figure back.
        await using var db = Db.NewRequestDbContextAsUser(f.Ledger.UserId);
        var refreshed = (await new RegisterRepository(db, TimeProvider.System)
                .GetBalancesForHeadersAsync(f.Ledger.LedgerId, f.Category.Id, f.Headers))
            .ToDictionary(b => b.HeaderId);
        for (var i = 0; i < Spends.Length; i++)
        {
            Assert.Equal(rows[f.Headers[i]].BalanceAfter, refreshed[f.Headers[i]].BalanceAfter);
            Assert.Equal(rows[f.Headers[i]].BalanceRateUnavailable, refreshed[f.Headers[i]].BalanceRateUnavailable);
            Assert.Equal(Spends[i].Amount, refreshed[f.Headers[i]].NetAmount);
        }
    }

    [Fact]
    public async Task Paging_filtering_sorting_and_anchoring_never_move_a_rows_balance()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));

        var (whole, _) = await PageAsync(f, f.Category.Id);
        var truth = ByHeader(whole).ToDictionary(kv => kv.Key, kv => kv.Value.BalanceAfter);
        Assert.Equal(Spends.Length, truth.Count);

        void Same(RegisterPage page)
        {
            foreach (var (header, row) in ByHeader(page))
                Assert.Equal(truth[header], row.BalanceAfter);
        }

        // Pages of two, walked toward older rows: each page is read on its own.
        var seen = 0;
        string? cursor = null;
        do
        {
            var (page, _) = await PageAsync(f, f.Category.Id, limit: 2, cursor: cursor);
            Same(page);
            seen += page.Entries.Count;
            cursor = page.CursorForOlder;
        }
        while (cursor is not null);
        Assert.Equal(Spends.Length, seen);

        // A filter that keeps rows apart in the walk, an amount sort, and an anchored page.
        Same((await PageAsync(f, f.Category.Id, filter: new RegisterFilter(AmountMin: 7m))).Page);
        Same((await PageAsync(f, f.Category.Id, sort: new RegisterSort("amount", Descending: false))).Page);
        Same((await PageAsync(f, f.Category.Id, limit: 2, startingAt: f.Headers[3])).Page);
        var narrowed = ByHeader((await PageAsync(f, f.Category.Id, filter: new RegisterFilter(AmountMin: 7m))).Page);
        Assert.Equal(new[] { f.Headers[0], f.Headers[2], f.Headers[3], f.Headers[5] }.Order(), narrowed.Keys.Order());
    }

    [Fact]
    public async Task A_category_on_a_one_currency_ledger_reads_its_stored_balances_and_no_rate()
    {
        var f = await SeedAsync(euroAccount: false);

        var (page, statements) = await PageAsync(f, f.Category.Id);

        await using var service = Db.NewDbContext();
        var stored = await service.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.AccountId == f.Category.Id)
            .ToDictionaryAsync(b => b.HeaderId, b => b.BalanceAfter);
        var rows = ByHeader(page);
        Assert.Equal(Spends.Length, rows.Count);
        Assert.All(rows, kv =>
        {
            Assert.Equal(stored[kv.Key], kv.Value.BalanceAfter);
            Assert.Equal(("USD", "USD"), (kv.Value.CurrencyCode, kv.Value.BalanceCurrencyCode));
        });
        // The page's two statements, and the one that reads the accounts' currencies and
        // finds no other currency in the ledger. No leg history, no rate.
        Assert.Empty(statements.RateReads);
        Assert.Equal(3, statements.Sent.Count);

        // The refresh after a save: its own statement, the currencies, nothing more.
        var refresh = new Statements();
        await using var db = Db.NewRequestDbContextAsUser(f.Ledger.UserId, refresh);
        var balances = await new RegisterRepository(db, TimeProvider.System)
            .GetBalancesForHeadersAsync(f.Ledger.LedgerId, f.Category.Id, f.Headers);
        Assert.All(balances, b => Assert.Equal(stored[b.HeaderId], b.BalanceAfter));
        Assert.Equal(2, refresh.Sent.Count);
        Assert.Empty(refresh.RateReads);
    }

    [Fact]
    public async Task A_category_whose_rows_are_all_in_the_reporting_currency_reads_no_rate_on_a_mixed_ledger()
    {
        var f = await SeedAsync(euroAccount: false);
        await f.Ledger.AddBankAccountAsync("Account C", currencyCode: "EUR");   // the ledger spans currencies

        var (page, statements) = await PageAsync(f, f.Category.Id);

        await using var service = Db.NewDbContext();
        var stored = await service.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.AccountId == f.Category.Id)
            .ToDictionaryAsync(b => b.HeaderId, b => b.BalanceAfter);
        Assert.All(ByHeader(page), kv => Assert.Equal(stored[kv.Key], kv.Value.BalanceAfter));
        Assert.Empty(statements.RateReads);
    }

    /// <summary>
    /// A posting between two categories, which only the Moneydance import writes (the API
    /// refuses a category as a source), has no real account: each side is in its category's
    /// own code, and converts from it.
    /// </summary>
    [Fact]
    public async Task A_posting_between_two_categories_converts_from_each_categorys_own_code()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));
        var euroCategory = await f.Ledger.AddCategoryAsync("Category B", currencyCode: "EUR");
        await f.Ledger.AddTransactionPairAsync(f.Category.Id, euroCategory.Id, 50m, Jan(9));

        var (page, _) = await PageAsync(f, euroCategory.Id);
        var row = Assert.Single(ByHeader(page).Values);
        Assert.Equal(("EUR", "USD"), (row.CurrencyCode, row.BalanceCurrencyCode));
        // At the 7th's rate, the last on or before the 9th.
        Assert.Equal(-56.1728m, row.BalanceAfter);

        // The USD category's side of it is in its own USD, after the four rows before it.
        var usd = ByHeader((await PageAsync(f, f.Category.Id)).Page).Values.Single(r => r.Amount == 50m);
        Assert.Equal("USD", usd.CurrencyCode);
        Assert.Equal(Math.Round(10m + 1m + 22m + 30m * 1.123456789012m + 50m, 4, MidpointRounding.AwayFromZero),
            usd.BalanceAfter);
    }

    /// <summary>
    /// The foreign-leg read is bounded by the category, not the ledger: it reaches the
    /// category's legs through <c>idx_txn_legs_account_id</c> and each leg's paying side
    /// through the posting key, and never scans <c>txn_legs</c>. A scan would cost every
    /// category page the whole ledger's legs.
    /// </summary>
    [Fact]
    public async Task The_foreign_leg_read_reaches_the_category_through_its_account_index()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Smoke);
        var euros = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var category = StressLedger.CategoryId(ledger.LedgerId, 1);
        for (var day = 1; day <= 10; day++)
            await ledger.AddTransactionPairAsync(euros.Id, category, -(day + 0.5m), Jan(day));
        await SeedRatesAsync(ledger, Day(31));
        await using (var conn = Db.OpenServiceConnection())
        await using (var analyze = new Npgsql.NpgsqlCommand(
                         "ANALYZE txn_legs; ANALYZE txn_headers; ANALYZE accounts; ANALYZE txn_header_account_balances", conn))
            await analyze.ExecuteNonQueryAsync();

        var capture = new ExplainCapture
        {
            // The foreign-leg read is the one statement that groups legs with the stored
            // balances beside them.
            Selects = sql => sql.Contains("UNION ALL", StringComparison.Ordinal)
                             && sql.Contains("txn_header_account_balances", StringComparison.Ordinal)
                             && sql.Contains("GROUP BY", StringComparison.Ordinal),
            Enabled = true,
        };
        RegisterPage page;
        await using (var db = Db.NewRequestDbContextAsUser(ledger.UserId, capture))
            page = await new RegisterRepository(db, TimeProvider.System).GetPageAsync(
                ledger.LedgerId, category, null, RegisterRepository.DirectionBefore, null, 50, hidden: false);
        Assert.Contains(ByHeader(page).Values, r => r.CurrencyCode == "EUR" && r.BalanceAfter is not null);

        var plan = Assert.Single(capture.Drain()).Plan;
        Output.WriteLine(plan);
        Assert.Contains("idx_txn_legs_account_id", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on txn_legs", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on txn_headers", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_with_no_category_row_reads_only_its_accounts_currencies()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));

        var (page, statements) = await PageAsync(f, f.Euros.Id);

        Assert.Equal(3, page.Entries.Count);
        Assert.Empty(statements.RateReads);
        Assert.Equal(3, statements.Sent.Count);
    }

    // -----------------------------------------------------------------
    // The bulk bar's Σ follows the Balance's rule (ADR-0114 D7, D9).
    // -----------------------------------------------------------------

    private async Task<SelectionSummary> SelectionSummaryAsync(Fixture f, object selection)
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, f.Ledger);
        using var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{f.Ledger.LedgerId}/transactions/selection-summary", selection);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
    }

    [Fact]
    public async Task A_category_selection_in_two_currencies_sums_each_leg_converted_at_its_date()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));

        var all = await SelectionSummaryAsync(f, new
        {
            kind = "all", accountId = f.Category.Id, statusFilter = "all", selectedAt = DateTime.UtcNow.AddSeconds(1),
        });

        // Each leg at its own day's rate, summed at full precision and rounded once.
        var expected = ConvertedTotal.Round(Spends.Select((s, i) => s.Amount * RateOfRow[i]).Sum());
        Assert.Equal((6, expected, "USD"), (all.TotalCount, all.SumOnAccount, all.CurrencyCode));
        Assert.Null(all.RateUnavailable);
    }

    [Fact]
    public async Task A_category_selection_in_one_currency_sums_it_natively_in_that_currency()
    {
        var f = await SeedAsync();   // no rate at all: a sum in one currency needs none

        var euroRows = f.Headers.Where((_, i) => Spends[i].Euro).ToArray();
        var euros = await SelectionSummaryAsync(f, new { kind = "explicit", accountId = f.Category.Id, headerIds = euroRows });
        Assert.Equal((57.77m, "EUR"), (euros.SumOnAccount, euros.CurrencyCode));

        var dollarRows = f.Headers.Where((_, i) => !Spends[i].Euro).ToArray();
        var dollars = await SelectionSummaryAsync(f, new { kind = "explicit", accountId = f.Category.Id, headerIds = dollarRows });
        Assert.Equal((16m, "USD"), (dollars.SumOnAccount, dollars.CurrencyCode));
        Assert.Null(dollars.RateUnavailable);
    }

    [Fact]
    public async Task A_category_selection_needing_a_missing_rate_has_no_sum_and_names_it()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(10));   // the 12th's EUR leg is past the coverage

        var all = await SelectionSummaryAsync(f, new
        {
            kind = "all", accountId = f.Category.Id, statusFilter = "all", selectedAt = DateTime.UtcNow.AddSeconds(1),
        });

        Assert.Null(all.SumOnAccount);
        Assert.Equal("USD", all.CurrencyCode);
        Assert.Equal([new RateUnavailable("EUR", "USD", Day(12))], all.RateUnavailable);
    }

    [Fact]
    public async Task A_real_accounts_selection_keeps_its_native_sum_in_its_own_currency()
    {
        var f = await SeedAsync();   // no rate at all

        var all = await SelectionSummaryAsync(f, new
        {
            kind = "all", accountId = f.Euros.Id, statusFilter = "all", selectedAt = DateTime.UtcNow.AddSeconds(1),
        });

        Assert.Equal((3, -57.77m, "EUR"), (all.TotalCount, all.SumOnAccount, all.CurrencyCode));
        Assert.Null(all.RateUnavailable);
    }

    /// <summary>
    /// The Σ's twin of <see cref="A_posting_between_two_categories_converts_from_each_categorys_own_code"/>:
    /// a leg with no real account on the other side is in its category's own code, so the
    /// Σ and the Balance read one rule (ADR-0114 D7).
    /// </summary>
    [Fact]
    public async Task A_category_selection_of_a_posting_between_two_categories_is_in_the_categorys_own_code()
    {
        var f = await SeedAsync();
        await SeedRatesAsync(f.Ledger, Day(31));
        var euroCategory = await f.Ledger.AddCategoryAsync("Category B", currencyCode: "EUR");
        await f.Ledger.AddTransactionPairAsync(f.Category.Id, euroCategory.Id, 50m, Jan(9));
        object All() => new
        {
            kind = "all", accountId = euroCategory.Id, statusFilter = "all", selectedAt = DateTime.UtcNow.AddSeconds(1),
        };

        // Alone, the leg is one currency, the category's own, summed natively.
        var alone = await SelectionSummaryAsync(f, All());
        Assert.Equal((1, -50m, "EUR"), (alone.TotalCount, alone.SumOnAccount, alone.CurrencyCode));
        Assert.Null(alone.RateUnavailable);

        // Beside a row paid from the USD account, the leg converts from EUR at its date.
        await using (var factory = new ApiFactory(Db).WithoutDevAuth())
        using (var client = await ClientAsync(factory, f.Ledger))
        {
            using var resp = await client.PostAsJsonAsync($"/api/ledgers/{f.Ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Jan(10),
                    SourceAccountId = f.Dollars.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = euroCategory.Id, Amount = -5m }],
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
        var both = await SelectionSummaryAsync(f, All());
        // The EUR leg at the 7th's rate, the last on or before the 9th; the USD leg as it is.
        var expected = ConvertedTotal.Round(5m - (50m * 1.123456789012m));
        Assert.Equal((2, expected, "USD"), (both.TotalCount, both.SumOnAccount, both.CurrencyCode));
        Assert.Null(both.RateUnavailable);
    }
}
