using System.Diagnostics;
using System.Globalization;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the reads that convert into the reporting currency cost (ADR-0114 D7, D9): the
/// overview, the net-worth history over a ledger's whole span, and a category's register
/// page, on the dense stress ledger and on a sparse long-lived one, each in one currency
/// and with a second currency whose rates are stored.
/// </summary>
/// <remarks>
/// <para>Four shapes. <c>4x-default</c> is the stress seed at four times its bank volume,
/// all in USD. <c>4x-long-lived-fx</c> adds <see cref="LongLivedAccounts"/> and
/// <see cref="LongLivedLedger"/>, whose second-currency bank and category are EUR, and a
/// EUR to USD rate for every day of its span. <c>sparse</c> is a household kept for fifteen
/// years: three accounts, four categories, a few rows a month. <c>sparse-fx</c> is that
/// with one account in EUR and its daily rates.</para>
/// <para><b>No timing assertion</b>, per this lane's rule: the figures are the output, and
/// every read runs under <c>statement_timeout</c> at the API's command timeout through the
/// real HTTP pipeline, so a read that would time out in the app fails here. Only HTTP is
/// read, so the same file times the code before conversion existed. What is asserted
/// holds on any hardware: every read answered.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConversionCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public ConversionCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const string StatementTimeout = "30s";

    private static readonly DateOnly Today = new(2026, 6, 15);

    /// <summary>The first day a rate is stored for, before any seeded row.</summary>
    private static readonly DateOnly FirstRateDay = new(2009, 11, 1);

    [Theory]
    [InlineData("4x-default")]
    [InlineData("4x-long-lived-fx")]
    [InlineData("sparse")]
    [InlineData("sparse-fx")]
    public async Task The_converting_reads_print_their_cost(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var watch = Stopwatch.StartNew();
        Guid busyCategory, foreignCategory;
        string from;
        if (shape.StartsWith("4x", StringComparison.Ordinal))
        {
            await StressLedger.SeedLedgerAsync(Db, ledger,
                StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 });
            busyCategory = StressLedger.CategoryId(ledger.LedgerId, 1);
            foreignCategory = busyCategory;
            if (shape.EndsWith("-fx", StringComparison.Ordinal))
            {
                await LongLivedAccounts.AddAsync(ledger);
                var added = await LongLivedLedger.AddAsync(Db, ledger);
                foreignCategory = added.EuroAccounts[1];
                await SeedDailyRatesAsync(ledger, "EUR");
            }
            from = "2009-12-01T00:00:00Z";
        }
        else
        {
            (busyCategory, foreignCategory) = await SeedSparseAsync(ledger, euro: shape.EndsWith("-fx", StringComparison.Ordinal));
            from = "2011-06-01T00:00:00Z";
        }

        int accounts, headers;
        await using (var db = Db.NewDbContext())
        {
            accounts = await db.Accounts.AsNoTracking().CountAsync(a => a.LedgerId == ledger.LedgerId);
            headers = await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId);
            await db.Database.ExecuteSqlRawAsync(
                "ANALYZE accounts; ANALYZE txn_headers; ANALYZE txn_legs; ANALYZE txn_header_account_balances; "
                + "ANALYZE fx_rates; ANALYZE fx_pair_coverage;");
        }
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"conversion-cost {shape}: seed {watch.Elapsed.TotalSeconds:F1}s ({headers:N0} headers, {accounts} accounts)"));

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithConfig("Api:ConnectionString", appConnection);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        client.Timeout = TimeSpan.FromMinutes(5);

        var id = ledger.LedgerId;
        var today = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var reads = new List<(string Name, string Url)>
        {
            ("overview", $"/api/ledgers/{id}/overview?today={today}"),
            ("net-worth-history.month", $"/api/ledgers/{id}/reports/net-worth-history?interval=Month&from={from}&to={today}T23:59:59Z&today={today}"),
            ("net-worth-history.13", $"/api/ledgers/{id}/reports/net-worth-history?interval=Month&from=2025-06-01T00:00:00Z&to=2026-06-30T00:00:00Z&today={today}"),
            ("register.category", $"/api/ledgers/{id}/transactions?account_id={busyCategory}&limit=100"),
        };
        if (foreignCategory != busyCategory)
            reads.Add(("register.foreign-category", $"/api/ledgers/{id}/transactions?account_id={foreignCategory}&limit=100"));

        var figures = new List<string>();
        var failed = new List<string>();
        foreach (var (name, url) in reads)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var (body, ms) = await TimeAsync(() => GetAsync(client, url));
                if (string.IsNullOrWhiteSpace(body)) failed.Add($"{name} returned nothing");
                figures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {ms:F1} ms"));
            }
            catch (Xunit.Sdk.XunitException e)
            {
                failed.Add($"{name}: {e.Message}");
                figures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} FAILED after {started.Elapsed.TotalMilliseconds:F0} ms"));
            }
        }

        _output.WriteLine($"conversion-cost {shape}: {string.Join("; ", figures)} (median of 5, warm)");
        Assert.True(failed.Count == 0, "reads that did not answer:\n  " + string.Join("\n  ", failed));
    }

    /// <summary>
    /// A household kept for fifteen years: a checking account, a savings account and a
    /// card, four categories, and a few rows a month on each account; with
    /// <paramref name="euro"/>, the savings account is in EUR and every day has a rate.
    /// </summary>
    /// <returns>The busiest category, and the one the savings account pays into.</returns>
    private async Task<(Guid Busy, Guid Foreign)> SeedSparseAsync(SyntheticLedger ledger, bool euro)
    {
        var checking = await ledger.AddBankAccountAsync("sparse-checking", openingBalance: 4000m);
        var savings = await ledger.AddBankAccountAsync("sparse-savings", openingBalance: 9000m,
            currencyCode: euro ? "EUR" : "USD");
        var card = await ledger.AddAccountAsync("sparse-card", "credit_card");
        var categories = new List<Guid>();
        for (var i = 1; i <= 4; i++)
            categories.Add((await ledger.AddCategoryAsync($"sparse-category-{i}")).Id);

        var first = new DateTime(2011, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var n = 0;
        for (var month = first; month < new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc); month = month.AddMonths(1))
        {
            // Two rows a month on checking and the card, one on savings.
            await ledger.AddTransactionPairAsync(checking.Id, categories[0], -(40m + (n % 37)), month.AddDays(3));
            await ledger.AddTransactionPairAsync(checking.Id, categories[1], -(12.5m + (n % 11)), month.AddDays(17));
            await ledger.AddTransactionPairAsync(card.Id, categories[0], -(25m + (n % 19)), month.AddDays(9));
            await ledger.AddTransactionPairAsync(card.Id, categories[2], -(60m + (n % 23)), month.AddDays(22));
            await ledger.AddTransactionPairAsync(savings.Id, categories[3], -(15m + (n % 7)), month.AddDays(12));
            n++;
        }
        if (euro) await SeedDailyRatesAsync(ledger, "EUR");
        return (categories[0], categories[3]);
    }

    /// <summary>A fetched <paramref name="from"/> to USD rate for every day from
    /// <see cref="FirstRateDay"/> to the server's yesterday, and coverage through it, as
    /// the ledger's owner writes them.</summary>
    private async Task SeedDailyRatesAsync(SyntheticLedger ledger, string from)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var i = 0;
        for (var day = FirstRateDay; day <= yesterday; day = day.AddDays(1), i++)
        {
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = "USD",
                RateDate = day, Rate = 1.05m + (i % 40) * 0.0025m, Source = FxRateSource.Frankfurter,
                Upstream = "Source A",
            });
        }
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = "USD", FetchedThroughDate = yesterday,
        });
        await db.SaveChangesAsync();
    }

    private static string Head(string text) => text[..Math.Min(text.Length, 400)];

    private static async Task<(T Result, double Ms)> TimeAsync<T>(Func<Task<T>> read)
    {
        await read();   // warm
        var samples = new List<double>();
        T result = default!;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            result = await read();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (result, samples[2]);
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {Head(body)}");
        return body;
    }
}
