using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the medium-risk and the previously untimed reads cost on the dense stress ledger
/// and on one that has also gathered the long-lived shape, read as the app reads them: the
/// real HTTP and MCP pipeline, under row-level security and the API's statement timeout.
/// </summary>
/// <remarks>
/// <para>Three shapes, as <see cref="CurrentBalanceCostTests"/> takes: the default seed, four
/// times its bank volume, and that with <see cref="LongLivedAccounts"/> and
/// <see cref="LongLivedLedger"/> added. A read bounded by how far a walk goes is cheap on
/// the first two and shows on the third; one bounded by a ledger-wide scan grows with the
/// second.</para>
/// <para><b>No timing assertion</b>, per this lane's rule: the figures are the output, and
/// every read runs under <c>statement_timeout</c> at the API's command timeout, so a read
/// that would time out in the app fails here rather than finishing slowly. What is asserted
/// holds on any hardware: every read answered, with a body that is not empty. A read whose
/// figure stands out is explained and fixed in its own change, with a guard on its plan.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LongLivedReadCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public LongLivedReadCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const string StatementTimeout = "30s";

    /// <summary>A caller's today inside the seeded span, the one the reminders are laid out around.</summary>
    private static readonly DateOnly Today = LongLivedLedger.Today;

    [Theory]
    [InlineData("1x-default")]
    [InlineData("4x-default")]
    [InlineData("4x-long-lived")]
    public async Task The_reads_print_their_cost(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = shape == "1x-default"
            ? StressLedger.Scale.Default
            : StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        var brokerage = seeded.BrokerageId;
        Guid? active = null;
        if (shape == "4x-long-lived")
        {
            await LongLivedAccounts.AddAsync(ledger);
            active = (await LongLivedLedger.AddAsync(Db, ledger)).Brokerage;
        }
        var bank = StressLedger.BankId(ledger.LedgerId, 1);
        var category = StressLedger.CategoryId(ledger.LedgerId, 1);

        int accounts, headers;
        await using (var db = Db.NewDbContext())
        {
            accounts = await db.Accounts.AsNoTracking().CountAsync(a => a.LedgerId == ledger.LedgerId);
            headers = await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId);
        }
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"long-lived-reads {shape}: seed {seeded.Elapsed.TotalSeconds:F1}s ({headers:N0} headers, {accounts} accounts)"));

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithMcpEnabled()
            .WithConfig("Api:ConnectionString", appConnection);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        client.Timeout = TimeSpan.FromMinutes(5);

        var id = ledger.LedgerId;
        var today = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = "2009-12-01T00:00:00Z";
        var to = $"{today}T23:59:59Z";
        var ago90 = Today.AddDays(-90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ahead30 = Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var reads = new (string Name, string Url)[]
        {
            ("overview", $"/api/ledgers/{id}/overview?today={today}"),
            ("accounts.balances", $"/api/ledgers/{id}/accounts/balances?today={today}"),
            ("net-worth-history", $"/api/ledgers/{id}/reports/net-worth-history?interval=Month&from={from}&to={to}&today={today}"),
            ("allocation.asset_class", $"/api/ledgers/{id}/reports/allocation?dimension=asset_class&today={today}"),
            ("allocation.security", $"/api/ledgers/{id}/reports/allocation?dimension=security&today={today}"),
            ("returns.brokerage", $"/api/ledgers/{id}/reports/returns?accountId={brokerage}&today={today}"),
            ("returns.active-brokerage", $"/api/ledgers/{id}/reports/returns?accountId={active ?? brokerage}&today={today}"),
            ("budget.progress", $"/api/ledgers/{id}/budget/progress?month=2026-05&window=3"),
            ("holdings.page", $"/api/ledgers/{id}/accounts/{brokerage}/holdings?today={today}"),
            ("summary.net-month", $"/api/ledgers/{id}/reports/summary?measure=Net&bucket=Month"),
            ("summary.category-top-n", $"/api/ledgers/{id}/reports/summary?measure=Net&groupBy=Category&bucket=Month&topN=10"),
            ("agenda", $"/api/ledgers/{id}/reminders/upcoming?from={ago90}&to={ahead30}"),
            ("entries", $"/api/ledgers/{id}/transactions/entries"),
            ("register.page", $"/api/ledgers/{id}/transactions?account_id={bank}&limit=50"),
        };

        var figures = new List<string>();
        // Every read is reported before the test fails: a read that times out names itself
        // and its time, and the figures of the reads after it are still taken.
        var failed = new List<string>();
        foreach (var (name, url) in reads.Where(r => active is not null || r.Name != "returns.active-brokerage"))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var (body, ms) = await TimeAsync(() => GetAsync(client, url));
                if (string.IsNullOrWhiteSpace(body)) failed.Add($"{name} returned nothing");
                figures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {ms:F0} ms"));
            }
            catch (Xunit.Sdk.XunitException e)
            {
                failed.Add($"{name}: {e.Message}");
                figures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} FAILED after {watch.Elapsed.TotalMilliseconds:F0} ms"));
            }
        }

        // MCP: the same repositories behind the connected-apps bearer.
        using var mcp = await StressMcpClient.CreateAsync(Db, factory, ledger, $"long-lived-{shape}");
        var tools = new (string Name, object Arguments)[]
        {
            ("list_accounts", new { ledgerId = id }),
            ("net_worth", new { ledgerId = id }),
            ("account_portfolio", new { ledgerId = id, accountId = brokerage }),
        };
        foreach (var (tool, arguments) in tools)
        {
            var (body, ms) = await TimeAsync(async () =>
            {
                using var response = await mcp.SendAsync(StressMcpClient.ToolCall(tool, arguments));
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, $"mcp {tool}: {(int)response.StatusCode} {Head(text)}");
                Assert.DoesNotContain("\"isError\":true", text, StringComparison.Ordinal);
                return text;
            });
            Assert.False(string.IsNullOrWhiteSpace(body), $"mcp {tool} returned nothing");
            figures.Add(string.Create(CultureInfo.InvariantCulture, $"mcp.{tool} {ms:F0} ms"));
        }

        // The per-save recompute: one write to a busy account, through the create route.
        var save = Stopwatch.StartNew();
        using (var created = await client.PostAsJsonAsync($"/api/ledgers/{id}/transactions", new CreateTransactionRequest
        {
            PostedAt = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            Payee = "long-lived-save",
            SourceAccountId = bank,
            Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -12.34m }],
        }))
        {
            var text = await created.Content.ReadAsStringAsync();
            Assert.True(created.IsSuccessStatusCode, $"save: {(int)created.StatusCode} {Head(text)}");
        }
        figures.Add(string.Create(CultureInfo.InvariantCulture, $"save {save.Elapsed.TotalMilliseconds:F0} ms (first, cold)"));

        _output.WriteLine($"long-lived-reads {shape}: {string.Join("; ", figures)} (median of 3, warm)");
        Assert.True(failed.Count == 0, "reads that did not answer:\n  " + string.Join("\n  ", failed));
    }

    private static string Head(string text) => text[..Math.Min(text.Length, 400)];

    private static async Task<(T Result, double Ms)> TimeAsync<T>(Func<Task<T>> read)
    {
        await read();   // warm
        var samples = new List<double>();
        T result = default!;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            result = await read();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (result, samples[1]);
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {Head(body)}");
        return body;
    }
}
