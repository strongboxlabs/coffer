using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Transactions;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the home screen's ledger-wide entries cost at four times the default bank
/// volume, read the way the app reads them, and what the account listing's review count
/// and the balance forecast cost beside them.
/// </summary>
/// <remarks>
/// <para>Every call goes through the real HTTP pipeline on a cookie session, so the
/// database work runs as <c>coffer_app</c> under row-level security, the plans the app
/// gets; the app's connections carry <c>statement_timeout</c> at the API's command
/// timeout, so a read that would time out in the app fails here rather than finishing
/// slowly. After the timed runs each call runs once more with every statement it sends
/// explained (<see cref="ExplainCapture"/>), and the plans are printed.</para>
/// <para>The seed holds no row awaiting review, so the review shape's rows are imported
/// on top through the file importer, more than one page of them, and dated at the old
/// end of the ledger: the newest-first page has to reach past every other entry to
/// find them.</para>
/// <para>The seed holds no reminder either, so the forecast's are created through the
/// API on the seeded bank accounts, monthly and from the start of the year, so each
/// carries overdue occurrences as well as ones ahead; some are transfers between two of
/// those accounts, so a template line lands on a second account in scope. The forecast
/// reads the seeded rows dated after the caller's today as its committed rows.</para>
/// <para><b>No timing assertion</b>, per this lane's standing rule; the figures are the
/// output. What is asserted is that each shape answers with rows, so no figure measures
/// an empty page.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TransactionEntriesCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public TransactionEntriesCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    /// <summary>More rows awaiting review than one page of entries holds.</summary>
    private const int Flagged = (TransactionsEndpoints.EntriesMaxLimit * 2) + 1;

    /// <summary>The API's command timeout, which the app's statements run under here.</summary>
    private const string StatementTimeout = "30s";

    private const string ExplainPrefix = "explain:";

    /// <summary>The bank accounts the stress seed creates (<see cref="StressLedger.BankId"/>).</summary>
    private const int StressBanks = 5;

    /// <summary>Reminders created on them for the balance forecast.</summary>
    private const int Reminders = 60;

    [Fact]
    public async Task The_ledger_wide_entries_print_their_cost_and_plans()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        // The premise: a connection opened from that string runs under the timeout.
        await using (var probe = new NpgsqlConnection(appConnection))
        {
            await probe.OpenAsync();
            await using var show = new NpgsqlCommand("SHOW statement_timeout", probe);
            Assert.Equal(StatementTimeout, (string?)await show.ExecuteScalarAsync());
        }

        var explain = new ExplainCapture();
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithConfig("Api:ConnectionString", appConnection)
            .WithServices(explain.Register);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        client.Timeout = TimeSpan.FromMinutes(5);

        // Rows awaiting review, through the importer, older than every seeded row.
        var bank = StressLedger.BankId(ledger.LedgerId, 1);
        const string AccountNumber = "STRESS-1";
        const decimal FlaggedAmount = -1m;
        var oldest = new DateOnly(2009, 1, 1);
        var imported = Stopwatch.StartNew();
        using (var content = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(TransactionEntriesEndpointTests.OfxBankStatement(
                AccountNumber,
                [.. Enumerable.Range(0, Flagged).Select(i => ($"STRESS-FITID-{i:D4}", oldest.AddDays(i), FlaggedAmount, "STRESS PAYEE"))])));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
            content.Add(file, "file", "statement.qfx");
            content.Add(new StringContent(bank.ToString()), "accountId");
            content.Add(new StringContent($"{TransactionEntriesEndpointTests.BankNumber}:{AccountNumber}"), "providerAccountId");
            var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"import: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        imported.Stop();

        // Reminders on the seeded bank accounts, through the API, monthly from the start of
        // the year: each has occurrences before the caller's today and after it. Every sixth
        // is a transfer to the next seeded account, the rest go to a seeded category.
        var reminderStart = new DateOnly(2026, 1, 1);
        var remindersWatch = Stopwatch.StartNew();
        for (var i = 0; i < Reminders; i++)
        {
            var counterparty = i % 6 == 0
                ? StressLedger.BankId(ledger.LedgerId, 1 + ((i + 1) % StressBanks))
                : StressLedger.CategoryId(ledger.LedgerId, 1 + (i % 20));
            using var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
            {
                Rrule = $"FREQ=MONTHLY;BYMONTHDAY={1 + (i % 28)}",
                StartDate = reminderStart,
                Payee = $"stress-reminder-{i:D2}",
                SourceAccountId = StressLedger.BankId(ledger.LedgerId, 1 + (i % StressBanks)),
                Postings = [new TransactionPosting { CounterpartyAccountId = counterparty, Amount = -10m - i }],
            });
            Assert.True(created.StatusCode == HttpStatusCode.Created,
                $"reminder: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        }
        remindersWatch.Stop();

        // A caller's today inside the seeded span.
        const string Today = "2026-06-15";
        var forecastDay = DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var forecast = $"/api/ledgers/{ledger.LedgerId}/accounts/balance-forecast?today={Today}&days={BalanceForecastRepository.MaxDays}";
        var entries = $"/api/ledgers/{ledger.LedgerId}/transactions/entries";
        var calls = new (string Name, string Url, Func<string, int> Rows)[]
        {
            ("entries.needs-review", $"{entries}?needs_review=true&limit={TransactionsEndpoints.EntriesMaxLimit}", EntryCount),
            ("entries.today", $"{entries}?today={Today}&limit={TransactionsEndpoints.EntriesMaxLimit}", EntryCount),
            ("entries.outflow.today", $"{entries}?direction=outflow&today={Today}&limit={TransactionsEndpoints.EntriesMaxLimit}", EntryCount),
            // Every seeded bank row is money out but a transfer's other side, so a page of
            // inflows reads past many headers that hold none: the batches have to grow.
            ("entries.inflow.today", $"{entries}?direction=inflow&today={Today}&limit={TransactionsEndpoints.EntriesMaxLimit}", EntryCount),
            ("accounts.include-inactive", $"/api/ledgers/{ledger.LedgerId}/accounts?includeInactive=true", ArrayCount),
            // The widest window, over every reminder source.
            ("balance-forecast", forecast, ForecastAccountCount),
            // The other readers of the one current-balance read the forecast's day 0 is:
            // the sidebar's balances and the overview.
            ("accounts.balances", $"/api/ledgers/{ledger.LedgerId}/accounts/balances?today={Today}", ArrayCount),
            ("overview", $"/api/ledgers/{ledger.LedgerId}/overview?today={Today}", OverviewAccountCount),
        };

        // The premise of the forecast's committed-row read: seeded rows dated in its window.
        int committedInWindow;
        await using (var db = Db.NewDbContext())
        {
            var from = ScheduledBoundary.ScheduledFrom(forecastDay);
            var to = from.AddDays(BalanceForecastRepository.MaxDays);
            committedInWindow = await db.TxnHeaders.AsNoTracking().CountAsync(h =>
                h.LedgerId == ledger.LedgerId && !h.IsRecurringTemplate && h.PostedAt >= from && h.PostedAt < to);
        }
        Assert.True(committedInWindow > 0, "no seeded row is dated in the forecast's window");

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"entries-cost: seed {seeded.Elapsed.TotalSeconds:F1}s ({scale.TotalTxns:N0} txns); import of {Flagged} rows awaiting review {imported.Elapsed.TotalSeconds:F1}s"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"entries-cost: {Reminders} reminders through the API {remindersWatch.Elapsed.TotalSeconds:F1}s; {committedInWindow} committed headers in the forecast's window"));

        var bodies = new Dictionary<string, string>();
        foreach (var (name, url, rows) in calls)
        {
            await GetAsync(client, url);   // warm
            var samples = new List<double>();
            string body = "";
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                body = await GetAsync(client, url);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            bodies[name] = body;
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"entries-cost: {name} {samples[1]:F0} ms (median of 3, warm; runs {samples[0]:F0}/{samples[1]:F0}/{samples[2]:F0} ms); {rows(body)} rows"));
        }

        // Vacuity guards: every shape answered with rows, the review page is full with
        // more to follow, the cut and the direction hold, and the review count saw them all.
        using (var review = JsonDocument.Parse(bodies["entries.needs-review"]))
        {
            Assert.Equal(TransactionsEndpoints.EntriesMaxLimit, review.RootElement.GetProperty("entries").GetArrayLength());
            Assert.True(review.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.All(review.RootElement.GetProperty("entries").EnumerateArray(),
                e => Assert.Equal(bank, e.GetProperty("accountId").GetGuid()));
        }
        var cutoff = DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var name in new[] { "entries.today", "entries.outflow.today", "entries.inflow.today" })
        {
            using var page = JsonDocument.Parse(bodies[name]);
            Assert.Equal(TransactionsEndpoints.EntriesMaxLimit, page.RootElement.GetProperty("entries").GetArrayLength());
            Assert.All(page.RootElement.GetProperty("entries").EnumerateArray(),
                e => Assert.True(DateOnly.FromDateTime(RowOf(e).GetProperty("postedAt").GetDateTime()) <= cutoff));
        }
        using (var outflows = JsonDocument.Parse(bodies["entries.outflow.today"]))
        {
            Assert.All(outflows.RootElement.GetProperty("entries").EnumerateArray(), e => Assert.True(ShownAmountOf(e) < 0m));
        }
        using (var inflows = JsonDocument.Parse(bodies["entries.inflow.today"]))
        {
            Assert.All(inflows.RootElement.GetProperty("entries").EnumerateArray(), e => Assert.True(ShownAmountOf(e) > 0m));
        }
        using (var accounts = JsonDocument.Parse(bodies["accounts.include-inactive"]))
        {
            Assert.Equal(Flagged, accounts.RootElement.EnumerateArray()
                .Single(a => a.GetProperty("id").GetGuid() == bank).GetProperty("needsReviewCount").GetInt32());
        }
        // The forecast: every reminder source projected over the whole window, the overdue
        // occurrences placed, and every account moved by what lies ahead.
        using (var all = JsonDocument.Parse(bodies["balance-forecast"]))
        {
            var projected = all.RootElement.GetProperty("accounts");
            Assert.Equal(StressBanks, projected.GetArrayLength());
            Assert.All(projected.EnumerateArray(), a =>
            {
                Assert.Equal(BalanceForecastRepository.MaxDays + 1, a.GetProperty("points").GetArrayLength());
                Assert.Contains(a.GetProperty("points").EnumerateArray(),
                    p => p.GetProperty("balance").GetDecimal() != a.GetProperty("today").GetDecimal());
            });
            Assert.True(all.RootElement.GetProperty("overdueOnDay1").GetInt32() > 0);
            Assert.Equal(0, all.RootElement.GetProperty("excluded").GetArrayLength());
        }
        // Day 0 is the sidebar's balance for every seeded bank account, at this scale too.
        using (var all = JsonDocument.Parse(bodies["balance-forecast"]))
        using (var sidebar = JsonDocument.Parse(bodies["accounts.balances"]))
        {
            var balances = sidebar.RootElement.EnumerateArray()
                .ToDictionary(b => b.GetProperty("accountId").GetGuid(), b => b.GetProperty("balance").GetDecimal());
            Assert.All(all.RootElement.GetProperty("accounts").EnumerateArray(), a =>
                Assert.Equal(balances[a.GetProperty("accountId").GetGuid()], a.GetProperty("today").GetDecimal()));
        }
        Assert.True(OverviewAccountCount(bodies["overview"]) > StressBanks);

        // A diagnostic only: each call's statements as the database plans and runs them.
        explain.Enabled = true;
        foreach (var (name, url, _) in calls)
        {
            await GetAsync(client, url);
            foreach (var (sql, plan) in explain.Drain())
            {
                _output.WriteLine($"{ExplainPrefix} {name}");
                foreach (var line in sql.Split('\n').Concat(plan.Split('\n')))
                    _output.WriteLine($"{ExplainPrefix}   {line.TrimEnd()}");
            }
        }
        explain.Enabled = false;
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body[..Math.Min(body.Length, 400)]}");
        return body;
    }

    private static int EntryCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("entries").GetArrayLength();
    }

    private static int OverviewAccountCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("accountGroups").EnumerateArray().Sum(g => g.GetProperty("accounts").GetArrayLength());
    }

    private static int ForecastAccountCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("accounts").GetArrayLength();
    }

    private static int ArrayCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }

    /// <summary>The row an entry's date is read from: the transaction, or a split's first line.</summary>
    private static JsonElement RowOf(JsonElement item)
    {
        var entry = item.GetProperty("entry");
        return entry.GetProperty("kind").GetString() == "group" ? entry.GetProperty("legs")[0] : entry.GetProperty("txn");
    }

    /// <summary>The amount an entry's row shows: a split group's net on its account, a single row's own amount.</summary>
    private static decimal ShownAmountOf(JsonElement item)
    {
        var entry = item.GetProperty("entry");
        return entry.GetProperty("kind").GetString() == "group"
            ? entry.GetProperty("legs")[0].GetProperty("headerAccountNetAmount").GetDecimal()
            : entry.GetProperty("txn").GetProperty("amount").GetDecimal();
    }
}
