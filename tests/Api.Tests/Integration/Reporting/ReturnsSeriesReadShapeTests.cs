using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The SHAPE of the returns-series read, which is what its cost follows: the portfolio is
/// valued in two batched feeder statements however many period ends and flows there are, and
/// the whole read is one REPEATABLE READ state.
/// </summary>
/// <remarks>
/// <para><b>No timing assertion</b>, per the ordinary suite's rule. The cost of a series is
/// the feeders' (<c>holdings_market_value_as_of_set</c> and <c>account_balance_as_of_instants</c>,
/// each a pass over the ledger's history per CALL), so a series that valued one instant per
/// statement would cost points times history, and one that valued them all in one costs
/// history. What separates the two is visible in the statements the read sends, on any
/// hardware: how many feeder statements, how many instants each carries, and whether the
/// number of statements grows with the number of period ends. The plan of one feeder
/// statement is read too (<c>EXPLAIN</c>, run as the API runs it), and must call each
/// feeder once, not once per instant.</para>
/// <para>The stress lane times the same read on the long-lived ledger
/// (<c>ReturnsSeriesCostTests</c>); a read that stands out there is fixed with a guard here.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesReadShapeTests : DatabaseTest
{
    public ReturnsSeriesReadShapeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string HoldingsFeeder = "holdings_market_value_as_of_set";
    private const string CashFeeder = "account_balance_as_of_instants";

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Sent(string Sql, IsolationLevel? Isolation, int[] ArrayLengths);

    /// <summary>Records every statement the read sends, with the isolation level of the
    /// transaction it runs in and the lengths of its array parameters (the instants a feeder
    /// is asked for).</summary>
    private sealed class Recorder : DbCommandInterceptor
    {
        public List<Sent> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(new Sent(
                command.CommandText,
                command.Transaction?.IsolationLevel,
                [.. command.Parameters.Cast<DbParameter>()
                    .Select(p => p.Value).OfType<Array>().Select(a => a.Length)]));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private AppDbContext ContextWith(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    private sealed record Seeded(SyntheticLedger Ledger, Guid AId, Guid BId);

    /// <summary>Two brokerages with a flow every month for a year, a position each, and
    /// prices through it.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var b = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 10_000m, Utc(2024, 1, 5));
        await ledger.AddInvestmentBuyAsync(a.Id, a.HoldingsAccountId!.Value, sec, 100m, 50m, Utc(2024, 1, 5));
        await ledger.AddTransactionPairAsync(b.Id, bank.Id, 5_000m, Utc(2024, 1, 6));
        await ledger.AddInvestmentBuyAsync(b.Id, b.HoldingsAccountId!.Value, sec, 50m, 50m, Utc(2024, 1, 6));
        for (var month = 2; month <= 12; month++)
        {
            await ledger.AddTransactionPairAsync(a.Id, bank.Id, 100m, Utc(2024, month, 12));
            await ledger.AddTransactionPairAsync(b.Id, bank.Id, 50m, Utc(2024, month, 20));
            await ledger.AddSecurityPriceAsync(sec, 50m + month, Utc(2024, month, 25));
        }
        return new Seeded(ledger, a.Id, b.Id);
    }

    private async Task<(ReturnsSeriesResult Result, List<Sent> Statements)> ReadAsync(
        Seeded seeded, Guid? accountId, bool includeAccounts, ReportTimeBucket interval, DateTime to)
    {
        var recorder = new Recorder();
        await using var db = ContextWith(recorder);
        var result = await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            seeded.Ledger.LedgerId, accountId, includeAccounts, Utc(2024, 1, 1), to, interval, Utc(2030, 1, 1));
        return (result, recorder.Statements);
    }

    [Fact]
    public async Task The_portfolio_is_valued_in_one_batched_statement_per_feeder_whatever_the_period_ends()
    {
        var seeded = await SeedAsync();

        // Twelve period ends and then forty-eight over the same flows, then a series per account
        // beside the ledger's: more period ends, more series, the same two feeder statements.
        var years = await ReadAsync(seeded, null, false, ReportTimeBucket.Year, Utc(2024, 12, 31));
        var months = await ReadAsync(seeded, null, false, ReportTimeBucket.Month, Utc(2024, 12, 31));
        var long_ = await ReadAsync(seeded, null, false, ReportTimeBucket.Month, Utc(2027, 12, 31));
        var withAccounts = await ReadAsync(seeded, null, true, ReportTimeBucket.Month, Utc(2024, 12, 31));

        Assert.Single(Assert.Single(years.Result.Series).Points);
        Assert.Equal(12, Assert.Single(months.Result.Series).Points.Count);
        Assert.Equal(48, Assert.Single(long_.Result.Series).Points.Count);
        Assert.Equal(3, withAccounts.Result.Series.Count);

        foreach (var (name, read) in new[]
                 {
                     ("years", years), ("months", months), ("long", long_), ("with accounts", withAccounts),
                 })
        {
            var holdings = read.Statements.Where(s => s.Sql.Contains(HoldingsFeeder, StringComparison.Ordinal)).ToList();
            var cash = read.Statements.Where(s => s.Sql.Contains(CashFeeder, StringComparison.Ordinal)).ToList();
            Assert.True(holdings.Count == 1, $"{name}: {holdings.Count} holdings feeder statements");
            Assert.True(cash.Count == 1, $"{name}: {cash.Count} cash feeder statements");
        }

        // The statements sent do not grow with the period ends or the flows: a longer
        // series sends exactly as many as a shorter one over the same ledger.
        Assert.Equal(months.Statements.Count, long_.Statements.Count);
        Assert.Equal(months.Statements.Count, years.Statements.Count);

        // Every period end and every flow instant of the ledger series rides in the one call: the
        // 12 period ends, the 24 flow instants (a funding and eleven monthly flows for each of the
        // two brokerages, on distinct days), the instant the window is valued at before it opens,
        // and the 12 instants each period end's year is valued at before it opens (the last
        // period end is the window's end bound, not a month's last instant, so none of them is
        // the window's own).
        var asked = months.Statements.Single(s => s.Sql.Contains(HoldingsFeeder, StringComparison.Ordinal))
            .ArrayLengths.Max();
        Assert.Equal(12 + 24 + 1 + 12, asked);
        // A series per account adds scope reads (each a few statements), and no feeder call.
        Assert.True(withAccounts.Statements.Count > months.Statements.Count);
    }

    [Fact]
    public async Task Every_statement_of_the_read_runs_in_one_repeatable_read_snapshot()
    {
        var seeded = await SeedAsync();

        var (_, statements) = await ReadAsync(seeded, null, true, ReportTimeBucket.Month, Utc(2024, 12, 31));

        Assert.NotEmpty(statements);
        // The statements that open and close the transaction are not queries; every query the
        // read sends is inside one, at the isolation the repeated reads need.
        Assert.All(statements, s => Assert.Equal(IsolationLevel.RepeatableRead, s.Isolation));
    }

    [Fact]
    public async Task A_write_that_commits_between_the_statements_is_not_seen_by_the_read()
    {
        var seeded = await SeedAsync();
        var bank = (await GetBankAsync(seeded)).Id;

        var baseline = (await ReadAsync(seeded, null, true, ReportTimeBucket.Month, Utc(2024, 12, 31))).Result;

        // A contribution commits from another connection just before the first valuation. The
        // flows were read before it; a valuation that saw it would read cash that is no
        // flow, as return, in every period end after it.
        var armed = new BeforeFirstFeeder(async () =>
            await seeded.Ledger.AddTransactionPairAsync(seeded.AId, bank, 4_000m, Utc(2024, 3, 3)));
        await using (var db = ContextWith(armed))
        {
            var during = await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
                seeded.Ledger.LedgerId, null, true, Utc(2024, 1, 1), Utc(2024, 12, 31),
                ReportTimeBucket.Month, Utc(2030, 1, 1));
            Assert.True(armed.Fired, "the write was never made, so nothing was proven");
            Assert.Equal(baseline.Series.Count, during.Series.Count);
            for (var i = 0; i < baseline.Series.Count; i++)
                Assert.Equal(baseline.Series[i].Points, during.Series[i].Points);
        }

        // The write is real: a read that starts after it sees the contribution as a flow, which
        // dilutes the growth after it, and the two states differ, so the equality above was the
        // snapshot's doing.
        var after = (await ReadAsync(seeded, null, true, ReportTimeBucket.Month, Utc(2024, 12, 31))).Result;
        Assert.NotEqual(baseline.Series[0].Points[^1].Index, after.Series[0].Points[^1].Index);
    }

    private async Task<Coffer.Api.Db.Entities.AccountRow> GetBankAsync(Seeded seeded)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == seeded.Ledger.LedgerId && a.AccountType == "bank");
    }

    /// <summary>Runs <c>action</c> once, just before the first valuation statement is sent.</summary>
    private sealed class BeforeFirstFeeder(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if ((command.CommandText.Contains(HoldingsFeeder, StringComparison.Ordinal)
                 || command.CommandText.Contains(CashFeeder, StringComparison.Ordinal))
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    [Fact]
    public async Task Each_feeder_statement_plans_one_call_over_every_instant_and_not_one_per_instant()
    {
        var seeded = await SeedAsync();

        // The feeders are SQL functions the planner inlines, so a plan holds their bodies and
        // not their names. What it shows of the instants is how they are carried: an array
        // unnested ONCE into the body, which every instant then joins, or a replay of the body
        // per instant. The shape that matters is therefore read on two requests that differ only
        // in how many instants they ask for.
        async Task<Dictionary<string, string>> PlansAsync(ReportTimeBucket interval, DateTime to)
        {
            var capture = new ExplainCapture
            {
                Enabled = true,
                Selects = sql => sql.Contains(HoldingsFeeder, StringComparison.Ordinal)
                                 || sql.Contains(CashFeeder, StringComparison.Ordinal),
            };
            await using var db = ContextWith(capture);
            await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
                seeded.Ledger.LedgerId, null, false, Utc(2024, 1, 1), to, interval, Utc(2030, 1, 1));
            var plans = capture.Drain();
            Assert.Equal(2, plans.Count);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HoldingsFeeder] = plans.Single(p => p.Sql.Contains(HoldingsFeeder, StringComparison.Ordinal)).Plan,
                [CashFeeder] = plans.Single(p => p.Sql.Contains(CashFeeder, StringComparison.Ordinal)).Plan,
            };
        }

        // The base tables a plan reads, one entry per scan, whichever way each is scanned.
        static string[] BaseScans(string plan) =>
        [
            .. Regex.Matches(
                    plan,
                    @"(?:Seq Scan|Index Scan|Index Only Scan|Bitmap Heap Scan)(?: Backward)?(?: using \S+)? on (\w+)",
                    RegexOptions.CultureInvariant)
                .Select(m => m.Groups[1].Value)
                .Order(StringComparer.Ordinal),
        ];

        // 26 instants, then 73, over the same ledger.
        var few = await PlansAsync(ReportTimeBucket.Year, Utc(2024, 12, 31));
        var many = await PlansAsync(ReportTimeBucket.Month, Utc(2027, 12, 31));

        foreach (var feeder in new[] { HoldingsFeeder, CashFeeder })
        {
            Output.WriteLine($"-- {feeder}, many instants");
            Output.WriteLine(many[feeder]);

            foreach (var plan in new[] { few[feeder], many[feeder] })
            {
                // The instants are unnested once, and that scan runs once.
                var unnests = Regex.Matches(plan, @"Function Scan on unnest[^\r\n]*", RegexOptions.CultureInvariant);
                var unnest = Assert.Single(unnests);
                Assert.Contains("loops=1)", unnest.Value, StringComparison.Ordinal);
            }

            // The ledger's tables are read as many times for 73 instants as for 26: the body is not
            // replayed per instant, which would add a scan of them for each.
            var scansFew = BaseScans(few[feeder]);
            var scansMany = BaseScans(many[feeder]);
            Assert.NotEmpty(scansMany);
            Assert.Equal(scansFew, scansMany);
        }
    }
}
