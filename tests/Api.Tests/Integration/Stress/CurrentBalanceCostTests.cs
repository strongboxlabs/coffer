using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the current balance costs on ledgers whose every account is busy, and on one that
/// has also gathered the accounts a long-lived ledger holds, read the way the app reads
/// it: <c>account_balances_as_of_now</c> itself, the sidebar's balances and the overview.
/// </summary>
/// <remarks>
/// <para>Three shapes: the stress seed at the default scale, the same at four times the
/// default bank volume, and the four-times seed with <see cref="LongLivedAccounts"/> added:
/// categories never used, accounts with only an opening balance, brokerages never traded
/// in, and accounts and categories whose last row is years back. A per-account lookup
/// whose cost follows the ledger rather than the account is cheap on the first two and
/// shows on the third; one that reads every row a busy account has ever had shows on the
/// second.</para>
/// <para>The function runs on a <c>coffer_app</c> connection with the ledger's user set,
/// and the two endpoints through the real HTTP pipeline on a cookie session, so both
/// get the plans the app gets under row-level security. Every connection carries
/// <c>statement_timeout</c> at the API's command timeout, so a read that would time out
/// in the app fails here rather than finishing slowly. The function's plan is printed
/// with the statement run, and the endpoints' statements that read it are explained.</para>
/// <para><b>No timing assertion</b>, per this lane's standing rule; the figures are the
/// output. What is asserted holds on any hardware: every account's lookup is bounded by
/// its own running balances (<see cref="CurrentBalancePlan.BoundedLookupViolations"/>),
/// and the function agrees, account by account, with the net-worth history's feeder at
/// the last instant the register counts, a reading that shares no lookup with it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrentBalanceCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public CurrentBalanceCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    /// <summary>The API's command timeout, which every statement here runs under.</summary>
    private const string StatementTimeout = "30s";

    private const string ExplainPrefix = "explain:";

    /// <summary>A caller's today inside the seeded span.</summary>
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Theory]
    [InlineData("1x-default")]
    [InlineData("4x-default")]
    [InlineData("4x-long-lived")]
    public async Task The_current_balance_reads_print_their_cost_and_plans(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = shape == "1x-default"
            ? StressLedger.Scale.Default
            : StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        var added = shape == "4x-long-lived" ? await LongLivedAccounts.AddAsync(ledger) : null;

        int accounts;
        await using (var db = Db.NewDbContext())
            accounts = await db.Accounts.AsNoTracking().CountAsync(a => a.LedgerId == ledger.LedgerId);
        var sparse = added is null
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $", {added.WithoutRows.Count} with no row, {added.YearsBack.Count} last moved {added.LatestRow:yyyy-MM-dd} or earlier");
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"current-balance {shape}: seed {seeded.Elapsed.TotalSeconds:F1}s ({scale.TotalTxns:N0} txns, {accounts} accounts{sparse})"));

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(Today);

        await using var connection = await CurrentBalancePlan.OpenAsUserAsync(appConnection, ledger.UserId);
        // The premise: the connection runs under the timeout.
        await using (var show = new NpgsqlCommand("SHOW statement_timeout", connection))
            Assert.Equal(StatementTimeout, (string?)await show.ExecuteScalarAsync());

        // A diagnostic, first, so it is printed even when a timed read below times out:
        // the function's plan with the statement run, else the plan alone.
        _output.WriteLine($"{ExplainPrefix} {shape} account_balances_as_of_now");
        foreach (var line in await AnalyzedPlanAsync(connection, ledger.LedgerId, scheduledFrom))
            _output.WriteLine($"{ExplainPrefix}   {line}");

        // The function, as the repository reads it.
        var balances = new Dictionary<Guid, decimal>();
        async Task<int> ReadFunctionAsync()
        {
            balances.Clear();
            await using var read = CurrentBalancePlan.Command(connection, ledger.LedgerId, scheduledFrom);
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync()) balances[reader.GetGuid(0)] = reader.GetDecimal(1);
            return balances.Count;
        }
        var (_, functionMs) = await TimeAsync(ReadFunctionAsync);

        // The two endpoints that read it.
        var explain = new ExplainCapture
        {
            Selects = sql => sql.Contains("account_balances_as_of_now", StringComparison.Ordinal),
        };
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithConfig("Api:ConnectionString", appConnection)
            .WithServices(explain.Register);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        client.Timeout = TimeSpan.FromMinutes(5);
        var calls = new (string Name, string Url)[]
        {
            ("accounts.balances", $"/api/ledgers/{ledger.LedgerId}/accounts/balances?today={Today:yyyy-MM-dd}"),
            ("overview", $"/api/ledgers/{ledger.LedgerId}/overview?today={Today:yyyy-MM-dd}"),
        };
        var bodies = new Dictionary<string, string>();
        var callMs = new Dictionary<string, double>();
        foreach (var (name, url) in calls)
            (bodies[name], callMs[name]) = await TimeAsync(() => GetAsync(client, url));

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"current-balance {shape}: account_balances_as_of_now {functionMs:F0} ms; accounts.balances {callMs["accounts.balances"]:F0} ms; overview {callMs["overview"]:F0} ms (median of 3, warm)"));

        // Vacuity: the function answers for every account, the endpoints with accounts.
        Assert.Equal(accounts, balances.Count);
        using (var sidebar = JsonDocument.Parse(bodies["accounts.balances"]))
            Assert.True(sidebar.RootElement.GetArrayLength() > 0);
        using (var overview = JsonDocument.Parse(bodies["overview"]))
            Assert.True(overview.RootElement.GetProperty("accountGroups").EnumerateArray()
                .Sum(g => g.GetProperty("accounts").GetArrayLength()) > 0);

        // The premise of the long-lived shape: what it added has no row, or none since.
        if (added is not null)
        {
            await using var db = Db.NewDbContext();
            Assert.False(await db.TxnHeaderAccountBalances.AsNoTracking()
                .AnyAsync(b => added.WithoutRows.Contains(b.AccountId)));
            var latest = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(b => added.YearsBack.Contains(b.AccountId))
                .Join(db.TxnHeaders, b => b.HeaderId, h => h.Id, (b, h) => h.PostedAt)
                .MaxAsync();
            Assert.True(latest <= added.LatestRow && latest < scheduledFrom.AddYears(-10),
                $"a retired account moved on {latest:yyyy-MM-dd}");
        }

        // Agreement with the net-worth history's feeder at the last counted instant, for
        // every account: a reading that shares no lookup with the function.
        await using (var app = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            await app.Database.OpenConnectionAsync();
            await app.Database.ExecuteSqlRawAsync($"SET statement_timeout = '{StatementTimeout}'");
            var history = await app.AccountBalanceAsOfInstants(
                    ledger.LedgerId, [ScheduledBoundary.LastCountedInstant(Today)], null)
                .ToDictionaryAsync(r => r.AccountId, r => r.Balance);
            Assert.All(balances, b => Assert.Equal(history[b.Key], b.Value));
        }

        // Every account's lookup is bounded by its own running balances.
        var planJson = await CurrentBalancePlan.ExplainJsonAsync(connection, ledger.LedgerId, scheduledFrom);
        var violations = CurrentBalancePlan.BoundedLookupViolations(planJson);

        // A diagnostic: the endpoints' statements that read the function, as they plan and run.
        explain.Enabled = true;
        foreach (var (name, url) in calls)
        {
            await GetAsync(client, url);
            foreach (var (sql, plan) in explain.Drain())
            {
                _output.WriteLine($"{ExplainPrefix} {shape} {name}");
                foreach (var line in sql.Split('\n').Concat(plan.Split('\n')))
                    _output.WriteLine($"{ExplainPrefix}   {line.TrimEnd()}");
            }
        }
        explain.Enabled = false;

        Assert.True(violations.Count == 0,
            "an account's current-balance lookup is not bounded by its own running balances:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// <c>EXPLAIN (ANALYZE, BUFFERS)</c> of the read, else, when it times out, the plan
    /// alone headed by why: the plan of a read too slow to finish is the one most needed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AnalyzedPlanAsync(
        NpgsqlConnection connection, Guid ledgerId, DateTime scheduledFrom)
    {
        var lines = new List<string>();
        try
        {
            await using var analyze = CurrentBalancePlan.Command(
                connection, ledgerId, scheduledFrom, "EXPLAIN (ANALYZE, BUFFERS) ");
            await using var reader = await analyze.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            return lines;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            lines.Clear();
            lines.Add($"(the plan with the statement run failed: {e.MessageText}; the plan alone follows)");
            await using var plain = CurrentBalancePlan.Command(connection, ledgerId, scheduledFrom, "EXPLAIN ");
            await using var reader = await plain.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            return lines;
        }
    }

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
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body[..Math.Min(body.Length, 400)]}");
        return body;
    }
}
