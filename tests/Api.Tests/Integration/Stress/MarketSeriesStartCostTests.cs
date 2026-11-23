using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the market-series start trigger (ADR-0114 D11) costs a dated write: the read of
/// what the ledger needs (<see cref="NeededFxPairs.StartsAsync"/>), taken once before the
/// write and once after it, against the latency of the write itself, on a large
/// multi-currency ledger and on a sparse long-lived one.
/// </summary>
/// <remarks>
/// <para>Two shapes. <c>large</c> is the stress seed at four times its bank volume, with
/// <see cref="LongLivedAccounts"/> and <see cref="LongLivedLedger"/> added (a EUR bank and
/// category, a third brokerage with priced, sold-out and moved-in securities), a GBP and a
/// EUR brokerage that bought some of the seeded securities through the API, GBP prices on
/// some of them, and a benchmark: every source of a needed pair, and the first invested
/// day, are read; its first buys share the bank stream's first day. <c>late</c> is the
/// stress seed at the same volume with every investment event dated
/// <see cref="LateInvestingFromYears"/> years into the bank stream, the GBP and EUR
/// brokerages buying after that, GBP prices and a benchmark: a ledger that banked for years
/// before it invested, whose first invested day lies behind most of its headers.
/// <c>sparse</c> is a household kept for fifteen years: three accounts, one
/// of them in EUR, four categories and a few rows a month, a brokerage buying each quarter
/// from eight years in (so the first invested day is far from the earliest day), GBP prices
/// on one of its securities, and a benchmark.</para>
/// <para>Each figure is the median of seven warm runs. <c>starts</c> is the read alone, on
/// a fresh context of the app role under row-level security, as a request reads it; its
/// statements are timed one by one (to the reader's close), each one's plan is read with
/// <c>EXPLAIN (ANALYZE, BUFFERS)</c> after the tables are analyzed, and the slowest three are
/// printed.
/// <c>bank.create</c> and <c>bank.patch</c> are the HTTP writes through the real pipeline,
/// trigger included: a one-split dated inside the ledger's span, and a date edit of it
/// inside the span, so neither starts a backfill. The trigger's share of a write is two
/// reads over the write's latency.</para>
/// <para><b>No timing assertion</b>, per this lane's rule: the figures are the output.
/// Every read runs under <c>statement_timeout</c> at the API's command timeout, so one the
/// app would abandon fails here. Asserted on any hardware: the read found pairs and a
/// benchmark start, so no figure measures a ledger that needs nothing; the late shape's
/// first invested day lies years after its earliest day; and on the two large shapes no
/// statement's plan reads half or more of the ledger's headers or legs at any node, counted
/// from <c>EXPLAIN ANALYZE</c> as the rows each node returned plus those its filter or
/// recheck discarded, over all its loops, so an index walked in order is charged as a scan
/// is.</para>
/// <para>Measured on the code before ADR-0114 D11's read was reshaped, the same harness
/// printed, median of seven: <c>large</c> starts 340 ms, bank.create 1307 ms, bank.patch
/// 1340 ms (the first invested day's statement, a scan of every leg and header, 169 ms and
/// sent twice a read); <c>sparse</c> starts 30 ms, bank.create 121 ms, bank.patch 109 ms.
/// With one derivation, the investment accounts passed by id to the walk of the headers in
/// posting order, and one connection: large starts 17 ms, bank.create 745 ms, bank.patch
/// 666 ms; sparse starts 10 ms, bank.create 85 ms, bank.patch 51 ms; late starts 279 ms,
/// bank.create 1170 ms, bank.patch 1125 ms, the first invested day's statement 261 ms, a
/// walk over about seven in ten of the ledger's headers probing each one's legs (the guard
/// fails it). With the two
/// reads taken out of the bank create and PATCH by hand, once: large bank.create 687 ms,
/// bank.patch 632 ms; sparse 45 ms and 31 ms. With the first invested day read from the
/// investment accounts' legs, each distinct header's day by key: large starts 31 ms (that
/// statement 16 ms, over the investment events' headers alone), bank.create 729 ms, bank.patch 653 ms; late starts
/// 30 ms (15 ms), bank.create 721 ms, bank.patch 636 ms; sparse starts 10 ms, bank.create
/// 75 ms, bank.patch 52 ms. The writes vary by tens of milliseconds run to run. Synthetic
/// ledgers only, on the fixture's database.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesStartCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public MarketSeriesStartCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const string StatementTimeout = "30s";

    private const int Runs = 7;

    private static readonly DateOnly Today = LongLivedLedger.Today;

    /// <summary>Years the <c>late</c> shape banks before its first investment event: most
    /// of its hourly bank stream, which runs about twenty years at four times the stress
    /// seed's volume.</summary>
    private const int LateInvestingFromYears = 15;

    [Theory]
    [InlineData("large")]
    [InlineData("late")]
    [InlineData("sparse")]
    public async Task The_start_trigger_prints_its_cost_against_the_write(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seedWatch = Stopwatch.StartNew();
        Guid bank, category;
        var bulk = shape is "large" or "late";
        if (shape == "large")
        {
            await StressLedger.SeedLedgerAsync(Db, ledger,
                StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 });
            await LongLivedAccounts.AddAsync(ledger);
            await LongLivedLedger.AddAsync(Db, ledger);
            bank = StressLedger.BankId(ledger.LedgerId, 1);
            category = StressLedger.CategoryId(ledger.LedgerId, 1);
        }
        else if (shape == "late")
        {
            // The stress seed with every investment event dated years into its bank
            // stream, and nothing invested earlier: the ledger's first invested day lies
            // behind most of its headers.
            await StressLedger.SeedLedgerAsync(Db, ledger,
                StressLedger.Scale.Default with
                {
                    BankTxns = StressLedger.Scale.Default.BankTxns * 4,
                    InvestingFromYears = LateInvestingFromYears,
                });
            bank = StressLedger.BankId(ledger.LedgerId, 1);
            category = StressLedger.CategoryId(ledger.LedgerId, 1);
        }
        else
        {
            (bank, category) = await SeedSparseAsync(ledger);
        }
        await AddBenchmarkAsync(ledger);

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithConfig("Api:ConnectionString", appConnection);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        client.Timeout = TimeSpan.FromMinutes(5);

        if (shape == "large") await AddForeignBrokeragesAsync(ledger, client, [2016, 2021]);
        // Bought after the seeded investing starts, so the first invested day stays late.
        if (shape == "late")
            await AddForeignBrokeragesAsync(ledger, client,
                [StressLedger.BaseYear + LateInvestingFromYears, StressLedger.BaseYear + LateInvestingFromYears]);

        int headers, legs, securityLegs, prices;
        await using (var db = Db.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ANALYZE accounts; ANALYZE txn_headers; ANALYZE txn_legs; ANALYZE holdings; "
                + "ANALYZE securities; ANALYZE security_prices; ANALYZE ledger_benchmarks; "
                + "ANALYZE txn_header_account_balances;");
            headers = await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId);
            legs = await db.TxnLegs.AsNoTracking().CountAsync(l => l.LedgerId == ledger.LedgerId);
            securityLegs = await db.TxnLegs.AsNoTracking().CountAsync(l => l.LedgerId == ledger.LedgerId && l.SecurityId != null);
            prices = await db.SecurityPrices.AsNoTracking().CountAsync(p => p.LedgerId == ledger.LedgerId);
        }
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"start-cost {shape}: seed {seedWatch.Elapsed.TotalSeconds:F1}s ({headers:N0} headers, {legs:N0} legs, "
            + $"{securityLegs:N0} security legs, {prices:N0} prices)"));

        var log = new CommandLog();
        AppDbContext NewContext(bool logged) => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(appConnection)
            .AddInterceptors(logged
                ? [new FixedUserConnectionInterceptor(ledger.UserId), log]
                : [new FixedUserConnectionInterceptor(ledger.UserId)])
            .Options);

        // ---- The read alone, and each of its statements. ----
        NeededStarts starts = null!;
        var startsMs = await MedianAsync(async () =>
        {
            await using var db = NewContext(logged: false);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            starts = await NeededFxPairs.StartsAsync(db, ledger.LedgerId, limit.Token);
        });
        var statementRuns = new List<List<Statement>>();
        for (var i = 0; i < Runs; i++)
        {
            log.Clear();
            await using var db = NewContext(logged: true);
            await NeededFxPairs.StartsAsync(db, ledger.LedgerId);
            statementRuns.Add([.. log.Statements]);
        }
        var statementMs = Enumerable.Range(0, statementRuns[0].Count)
            .Select(i => Median(statementRuns.Select(r => r[i].Ms)))
            .ToList();

        // ---- The writes, trigger included. ----
        var n = 0;
        var createMs = await MedianAsync(async () =>
        {
            n++;
            using var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Today.AddDays(-5 - n % 3).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
                    Payee = "start-cost",
                    SourceAccountId = bank,
                    Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -(10m + n) }],
                });
            Assert.True(created.IsSuccessStatusCode, $"create: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        });
        Guid edited;
        using (var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
                   new CreateTransactionRequest
                   {
                       PostedAt = Today.AddDays(-4).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
                       Payee = "start-cost-edit",
                       SourceAccountId = bank,
                       Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -7m }],
                   }))
        {
            edited = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        }
        var patchMs = await MedianAsync(async () =>
        {
            n++;
            var day = Today.AddDays(-4 - n % 2).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            using var patched = await client.PatchAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions/{edited}", new { postedAt = day });
            Assert.True(patched.IsSuccessStatusCode, $"patch: {(int)patched.StatusCode} {await patched.Content.ReadAsStringAsync()}");
        });

        var perStatement = string.Join(", ", statementMs.Select(ms => ms.ToString("F1", CultureInfo.InvariantCulture)));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"start-cost {shape}: starts {startsMs:F1} ms ({starts.Pairs.Count} pairs; statements {perStatement} ms); "
            + $"bank.create {createMs:F1} ms (trigger {200 * startsMs / createMs:F0}%); "
            + $"bank.patch {patchMs:F1} ms (trigger {200 * startsMs / patchMs:F0}%) (median of {Runs}, warm)"));

        // ---- Every statement's plan; the slowest three printed. ----
        var plans = new List<string>();
        foreach (var statement in statementRuns[0])
        {
            await using var db = NewContext(logged: false);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + statement.Sql, connection);
            foreach (var parameter in statement.Parameters) explain.Parameters.Add(parameter.Clone());
            var plan = new StringBuilder();
            await using (var reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
            plans.Add(plan.ToString());
        }
        foreach (var (ms, index) in statementMs.Select((ms, i) => (ms, i)).OrderByDescending(s => s.ms).Take(3))
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"start-cost {shape}: statement {index + 1} ({ms:F1} ms)\n{statementRuns[0][index].Sql}\n{plans[index]}"));

        // Vacuity: the read found what a write could widen.
        Assert.NotEmpty(starts.Pairs);
        Assert.NotNull(starts.LedgerEarliest);
        Assert.NotNull(starts.BenchmarkStart);

        // The late shape is the one it claims to be: its first invested day lies years
        // after its earliest day, behind most of its headers.
        if (shape == "late")
        {
            Assert.True(starts.LedgerEarliest < new DateOnly(StressLedger.BaseYear + 1, 1, 1),
                $"late: earliest day {starts.LedgerEarliest}");
            Assert.True(starts.BenchmarkStart >= new DateOnly(StressLedger.BaseYear + LateInvestingFromYears, 1, 1),
                $"late: first invested day {starts.BenchmarkStart}");
        }

        // The plan guard, on the shapes whose tables are large enough for the planner to
        // choose: no statement reads most of the ledger's legs or headers, whatever the
        // node that reads them (a scan, or an index walked in order). A count of rows read,
        // not a time, so it holds on any hardware. Before the first invested day named its
        // accounts, it scanned both, twice a read; while it walked the headers in posting
        // order, the late shape read most of them, and probed each one's legs.
        if (bulk)
        {
            var heavy = plans
                .Select((plan, i) => (Plan: plan, Index: i,
                    Reads: TableReads(plan).Where(r => r.Rows * 2 >= (r.Table == "txn_headers" ? headers : legs)).ToList()))
                .Where(p => p.Reads.Count > 0 || SeqScanOfLegsOrHeaders.IsMatch(p.Plan))
                .Select(p => $"statement {p.Index + 1} ("
                    + string.Join(", ", p.Reads.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Table} {r.Rows:N0} rows")))
                    + $"):\n{statementRuns[0][p.Index].Sql}\n{p.Plan}")
                .ToList();
            Assert.True(heavy.Count == 0, string.Create(CultureInfo.InvariantCulture,
                $"statements that read half or more of the ledger's {headers:N0} headers or {legs:N0} legs:\n")
                + string.Join("\n", heavy));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SeqScanOfLegsOrHeaders =
        new(@"Seq Scan on (txn_legs|txn_headers)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A plan node reading <c>txn_headers</c> or <c>txn_legs</c>, any scan kind.</summary>
    private static readonly System.Text.RegularExpressions.Regex TableNode =
        new(@" on (txn_headers|txn_legs)\b.*\(actual time=[0-9.]+\.\.[0-9.]+ rows=([0-9.]+) loops=([0-9]+)\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Rows a node discarded after reading them, per loop.</summary>
    private static readonly System.Text.RegularExpressions.Regex RowsRemoved =
        new(@"^\s*Rows Removed by (Filter|Index Recheck): ([0-9.]+)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The rows each <c>txn_headers</c> or <c>txn_legs</c> node of an <c>EXPLAIN ANALYZE</c>
    /// plan read, over all its loops: those it returned and those its filter or recheck
    /// discarded. A walk of an index in order is charged for every row it stepped over, as
    /// a scan is.
    /// </summary>
    private static List<(string Table, long Rows)> TableReads(string plan)
    {
        var reads = new List<(string Table, long Rows)>();
        string? table = null;
        double rows = 0, loops = 0;
        void Flush()
        {
            if (table is not null) reads.Add((table, (long)Math.Round(rows * loops)));
            table = null;
        }
        foreach (var line in plan.Split('\n'))
        {
            var node = TableNode.Match(line);
            if (node.Success)
            {
                Flush();
                table = node.Groups[1].Value;
                rows = double.Parse(node.Groups[2].Value, CultureInfo.InvariantCulture);
                loops = double.Parse(node.Groups[3].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (line.Contains("->", StringComparison.Ordinal)) { Flush(); continue; }
            var removed = RowsRemoved.Match(line);
            if (removed.Success && table is not null)
                rows += double.Parse(removed.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        Flush();
        return reads;
    }

    /// <summary>A benchmark in USD, as the add stores it, so the first invested day and
    /// every benchmark pair are read.</summary>
    private async Task AddBenchmarkAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        db.LedgerBenchmarks.Add(new LedgerBenchmarkRow
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Symbol = "SYNTHB", Name = "Synthetic Index B",
            CurrencyCode = "USD", Position = 1, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A GBP and a EUR brokerage, each buying ten of the seeded securities once in
    /// each of <paramref name="years"/> through the API, and GBP prices on ten others:
    /// position pairs from a price currency into a foreign account, and from a foreign
    /// price into the seeded brokerages.</summary>
    private async Task AddForeignBrokeragesAsync(SyntheticLedger ledger, HttpClient client, int[] years)
    {
        List<Guid> securities;
        await using (var db = Db.NewDbContext())
            securities = await db.Securities.AsNoTracking()
                .Where(s => s.LedgerId == ledger.LedgerId && s.Name.StartsWith("Stress Security "))
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(30)
                .ToListAsync();
        Assert.Equal(30, securities.Count);
        var brokerages = new[]
        {
            await ledger.AddInvestmentAccountAsync("start-cost-gbp", currencyCode: "GBP"),
            await ledger.AddInvestmentAccountAsync("start-cost-eur", currencyCode: "EUR"),
        };
        for (var b = 0; b < brokerages.Length; b++)
        {
            for (var s = 0; s < 10; s++)
            {
                foreach (var year in years)
                {
                    using var bought = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                        new CreateInvestmentTransactionRequest
                        {
                            BrokerageAccountId = brokerages[b].Id, Action = "buy", SecurityId = securities[b * 10 + s],
                            Shares = 2m, Price = 25m + s,
                            PostedAt = new DateTime(year, 3 + s, 10, 12, 0, 0, DateTimeKind.Utc),
                        });
                    Assert.True(bought.IsSuccessStatusCode, $"buy: {(int)bought.StatusCode} {await bought.Content.ReadAsStringAsync()}");
                }
            }
        }
        for (var s = 20; s < 30; s++)
        {
            for (var month = 1; month <= 12; month++)
            {
                using var priced = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/securities/{securities[s]}/prices",
                    new CreateSecurityPriceRequest { Price = 20m + month, PriceDate = new DateOnly(2019, month, 15), CurrencyCode = "GBP" });
                Assert.True(priced.IsSuccessStatusCode, $"price: {(int)priced.StatusCode} {await priced.Content.ReadAsStringAsync()}");
            }
        }
    }

    /// <summary>The year the sparse household starts investing, years into its history, so
    /// its first invested day is far from its earliest day.</summary>
    private const int InvestingFrom = 2019;

    /// <summary>
    /// A household kept for fifteen years: a checking account, a EUR savings account and a
    /// card, four categories, and a few rows a month; from <see cref="InvestingFrom"/>, a
    /// brokerage buying one of two securities each quarter; and monthly GBP prices on the
    /// second.
    /// </summary>
    /// <returns>The checking account and its busiest category.</returns>
    private static async Task<(Guid Bank, Guid Category)> SeedSparseAsync(SyntheticLedger ledger)
    {
        var checking = await ledger.AddBankAccountAsync("sparse-checking", openingBalance: 4000m);
        var savings = await ledger.AddBankAccountAsync("sparse-savings", openingBalance: 9000m, currencyCode: "EUR");
        var card = await ledger.AddAccountAsync("sparse-card", "credit_card");
        var categories = new List<Guid>();
        for (var i = 1; i <= 4; i++)
            categories.Add((await ledger.AddCategoryAsync($"sparse-category-{i}")).Id);
        var brokerage = await ledger.AddInvestmentAccountAsync("sparse-brokerage");
        var fund = await ledger.AddSecurityAsync("sparse-fund-a", "SPFA");
        var foreign = await ledger.AddSecurityAsync("sparse-fund-b", "SPFB");

        var first = new DateTime(2011, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var n = 0;
        for (var month = first; month < new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc); month = month.AddMonths(1))
        {
            await ledger.AddTransactionPairAsync(checking.Id, categories[0], -(40m + (n % 37)), month.AddDays(3));
            await ledger.AddTransactionPairAsync(checking.Id, categories[1], -(12.5m + (n % 11)), month.AddDays(17));
            await ledger.AddTransactionPairAsync(card.Id, categories[0], -(25m + (n % 19)), month.AddDays(9));
            await ledger.AddTransactionPairAsync(card.Id, categories[2], -(60m + (n % 23)), month.AddDays(22));
            await ledger.AddTransactionPairAsync(savings.Id, categories[3], -(15m + (n % 7)), month.AddDays(12));
            if (month.Month % 3 == 0 && month.Year >= InvestingFrom)
                await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value,
                    n % 2 == 0 ? fund : foreign, 3m, 20m + (n % 13), month.AddDays(14).AddHours(12));
            await ledger.AddSecurityPriceAsync(foreign, 18m + (n % 9), month.AddDays(20), currencyCode: "GBP");
            n++;
        }
        await ledger.RecomputeTradePricesAsync();
        return (checking.Id, categories[0]);
    }

    private static async Task<double> MedianAsync(Func<Task> run)
    {
        await run();   // warm
        var samples = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await run();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        return Median(samples);
    }

    private static double Median(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private sealed record Statement(string Sql, NpgsqlParameter[] Parameters, double Ms);

    /// <summary>Each statement a context ran, with its time from execution to the reader's
    /// close, so a statement that returns many rows is charged for reading them.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<Statement> _statements = [];
        private readonly Dictionary<DbCommand, (string Sql, NpgsqlParameter[] Parameters, long Started)> _open = [];

        public IReadOnlyList<Statement> Statements => _statements;

        public void Clear() => _statements.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _open[command] = (command.CommandText,
                [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone())], Stopwatch.GetTimestamp());
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Closed(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            Closed(command);
            return result;
        }

        private void Closed(DbCommand command)
        {
            if (!_open.Remove(command, out var open)) return;
            _statements.Add(new Statement(open.Sql, open.Parameters, Stopwatch.GetElapsedTime(open.Started).TotalMilliseconds));
        }
    }
}
