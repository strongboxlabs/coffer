using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the trade-price rule's readers cost, run as the app runs them:
/// <c>fn_trade_price_check</c> (the consistency check and its repair),
/// <c>fn_unbacked_price_securities</c> (the advisory beside it), <c>fn_unbacked_price_dates</c>
/// (a security's price list) and <c>fn_trade_execution_for_day</c> (the day's rule, which
/// every write's re-derivation reads with the accounts it can read, once to learn what to
/// lock and again after the lock),
/// and the screens that read the current prices beside them: the
/// securities catalog and the consistency report, through the app's repositories.
/// </summary>
/// <remarks>
/// <para>Two ledgers, because the readers scale with different things. The check reads
/// every security line of the ledger; the price list and the day's rule read one
/// security's lines; the advisory reads the lines of each security holding a trade price
/// once (migration 273), where asking the day's rule once per stored price would grow with
/// prices × lines within a security. Four times the default bank volume puts the check
/// beside a large register at the default investment side (many securities, a short
/// history each). The second ledger holds the investment side's depth instead: few
/// securities, each with a long history of trades on as many days, at twice
/// <see cref="StressLedger.Scale.Deep"/>'s buys and sells. Asked per stored price, the
/// advisory's cost grows with the square of that depth, so at
/// <see cref="StressLedger.Scale.Deep"/>'s own depth it runs close enough to the timeout
/// that a faster host finishes inside it; twice the depth quadruples that cost or more,
/// which puts the per-price form well past the timeout rather than at its edge, while the
/// one-pass form stays far inside it.</para>
/// <para>Each statement runs as <c>coffer_app</c> under row-level security, on a connection
/// whose <c>statement_timeout</c> is the API's command timeout, so a read that would time out
/// in the app fails here rather than finishing slowly. Each is warmed once and timed three
/// times; the median is the figure. Each plan is printed after the timed runs: the check, the
/// advisory and the price list through <c>EXPLAIN (ANALYZE, BUFFERS)</c> as the app role, and
/// the single day's rule through <c>auto_explain</c>, statement and nested statements alike,
/// loaded by the superuser before the session takes the app's role, so the rule's own plan
/// is printed whether or not the caller's plan inlines it.</para>
/// <para>The seed writes no price, so the trade-price repair first writes every day the rule
/// derives, through the product's own rank-gated writer. The check then examines every such
/// day and reports none, which is asserted, so no figure measures an empty or a broken
/// projection. <b>No timing assertion</b> beyond the timeout the app runs under, per this
/// lane's standing rule; the figures are the output.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TradePriceCheckCostTests : DatabaseTest
{
    /// <summary>The API's command timeout, which the app's statements run under here.</summary>
    private const string StatementTimeout = "30s";

    private readonly ITestOutputHelper _output;

    public TradePriceCheckCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    /// <summary>Four times the default bank volume, at the default investment side.</summary>
    [Fact]
    public Task The_trade_price_rules_readers_print_their_cost_and_plans() =>
        PrintCostAsync("bank-4x",
            StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 });

    /// <summary>Few securities with long histories, twice <see cref="StressLedger.Scale.Deep"/>'s
    /// depth: the depth the per-security readers and the advisory scale with, and deep enough
    /// that an advisory asking the day's rule per stored price cannot finish inside the
    /// timeout.</summary>
    [Fact]
    public Task The_trade_price_rules_readers_print_their_cost_and_plans_on_long_histories() =>
        PrintCostAsync("deep-2x", StressLedger.Scale.Deep with
        {
            BuysPerHolding = StressLedger.Scale.Deep.BuysPerHolding * 2,
            SellsPerHolding = StressLedger.Scale.Deep.SellsPerHolding * 2,
        });

    private async Task PrintCostAsync(string shape, StressLedger.Scale scale)
    {
        var prefix = $"trade-price-cost [{shape}]:";
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);

        var repair = Stopwatch.StartNew();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.Database.SetCommandTimeout(600);
            var repaired = (await new LedgerConsistencyRepository(
                    db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
                    TimeProvider.System)
                .RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
            Assert.True(repaired.Healthy, $"the repair left {repaired.MismatchedCount} trade-price findings");
        }
        repair.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} seed {seeded.Elapsed.TotalSeconds:F1}s ({scale.TotalTxns:N0} txns); trade-price repair and the check after it {repair.Elapsed.TotalSeconds:F1}s"));

        await using var app = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString);
        await app.OpenAsync();
        await ActAsUserAsync(app, ledger.UserId);
        // The premise: the app role, under the timeout, sees the ledger.
        Assert.Equal(StatementTimeout, await ScalarAsync<string>(app, "SHOW statement_timeout", _ => { }));
        Assert.Equal("coffer_app", await ScalarAsync<string>(app, "SELECT current_user::text", _ => { }));

        // The security with the longest price history, and the last day a trade prices it.
        var securityId = await ScalarAsync<Guid>(app, """
            SELECT p.security_id FROM security_prices p
             WHERE p.ledger_id = @l
             GROUP BY p.security_id
             ORDER BY count(*) DESC, p.security_id
             LIMIT 1
            """, c => c.Parameters.AddWithValue("l", ledger.LedgerId));
        var day = await ScalarAsync<DateTime>(app, """
            SELECT max(p.price_date)::timestamp FROM security_prices p
             WHERE p.ledger_id = @l AND p.security_id = @s AND p.source = 'trade'
            """, c =>
            {
                c.Parameters.AddWithValue("l", ledger.LedgerId);
                c.Parameters.AddWithValue("s", securityId);
            });
        var today = new DateOnly(2026, 6, 15);
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(today);

        void Bind(NpgsqlCommand c)
        {
            c.Parameters.AddWithValue("l", ledger.LedgerId);
            c.Parameters.AddWithValue("s", securityId);
            c.Parameters.AddWithValue("d", DateOnly.FromDateTime(day));
            c.Parameters.AddWithValue("b", scheduledFrom);
        }

        var reads = new (string Name, string Sql)[]
        {
            ("check", "SELECT * FROM fn_trade_price_check(@l)"),
            ("advisory", "SELECT * FROM fn_unbacked_price_securities(@l, @b)"),
            ("price-list", "SELECT * FROM fn_unbacked_price_dates(@l, @s)"),
            ("one-day", "SELECT * FROM fn_trade_execution_for_day(@l, @s, @d)"),
        };

        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, sql) in reads)
        {
            await CountAsync(app, sql, Bind);   // warm
            var samples = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                rows[name] = await CountAsync(app, sql, Bind);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{prefix} {name} {samples[1]:F1} ms (median of 3, warm; runs {samples[0]:F1}/{samples[1]:F1}/{samples[2]:F1} ms); {rows[name]:N0} rows"));
        }

        // Vacuity guards: the check examined days and reports none, and the day's rule
        // names one execution, with a price, for the day the security last traded.
        Assert.True(rows["check"] > 1000, $"the check examined only {rows["check"]} days");
        Assert.Equal(0, await ScalarAsync<long>(app,
            "SELECT count(*) FROM fn_trade_price_check(@l) WHERE reason IS NOT NULL", Bind));
        Assert.Equal(1, rows["one-day"]);
        Assert.NotNull(await ScalarAsync<decimal?>(app,
            "SELECT price FROM fn_trade_execution_for_day(@l, @s, @d)", Bind));

        // The screens that read the advisory and the current prices, as the app's
        // repositories run them under the same timeout: the securities catalog, which labels
        // each security's price in its currency, and the consistency report the settings
        // page and the scheduled monitor ask for, which names each advisory row's. The seed
        // leaves the advisory empty, so each security first gets the row a deleted trade
        // leaves behind (ADR-0084: the rule derives nothing for the day and keeps the row),
        // dated after every seeded trade, and the report names a currency for every one.
        await using (var appDb = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            var securities = await appDb.Securities.AsNoTracking()
                .Where(s => s.LedgerId == ledger.LedgerId)
                .Select(s => s.Id)
                .ToListAsync();
            foreach (var security in securities)
                await ledger.AddSecurityPriceAsync(
                    security, 1m, new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc), PriceSource.Trade);

            var catalog = new SecuritiesRepository(appDb, TimeProvider.System);
            var consistency = new LedgerConsistencyRepository(
                appDb, new RegisterRepository(appDb, TimeProvider.System), new HoldingsRecomputeService(appDb),
                TimeProvider.System);
            var listed = await TimeAsync(prefix, "catalog",
                async () => (await catalog.ListByLedgerAsync(ledger.LedgerId, null, today)).Count);
            var advisories = await TimeAsync(prefix, "consistency-report",
                async () => (await consistency.CheckAsync(ledger.LedgerId, today)).UnbackedPrices.Count);
            Assert.Equal(securities.Count, listed);
            Assert.Equal(securities.Count, advisories);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{prefix} catalog {listed:N0} securities; consistency report {advisories:N0} advisory rows"));
        }

        foreach (var (name, sql) in reads.Where(r => r.Name != "one-day"))
            await PrintPlanAsync(app, prefix, name, "EXPLAIN (ANALYZE, BUFFERS) " + sql, Bind);

        // The day's rule is read through auto_explain, which prints the statement's plan and
        // any nested statement's, so the rule's own plan is printed whether or not the
        // caller's plan inlines the function. Only the superuser may load it; the session
        // then takes the app's role, so RLS shapes the plan as it does the app's.
        _output.WriteLine($"{prefix} plan one-day");
        await using var su = new NpgsqlConnection(Db.SuperuserConnectionString);
        su.Notice += (_, e) =>
        {
            foreach (var line in e.Notice.MessageText.Split('\n'))
                _output.WriteLine($"{prefix}   {line.TrimEnd()}");
        };
        await su.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_analyze = on;
            SET auto_explain.log_buffers = on;
            SET auto_explain.log_nested_statements = on;
            SET auto_explain.log_level = notice;
            SET statement_timeout = '30s';
            SET ROLE coffer_app;
            """, su))
            await setup.ExecuteNonQueryAsync();
        await ActAsUserAsync(su, ledger.UserId);
        await CountAsync(su, "SELECT * FROM fn_trade_execution_for_day(@l, @s, @d)", Bind);
    }

    /// <summary>A repository read, warmed once and timed three times; prints the median and
    /// returns the last run's row count.</summary>
    private async Task<int> TimeAsync(string prefix, string name, Func<Task<int>> read)
    {
        await read();   // warm
        var samples = new List<double>();
        var count = 0;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            count = await read();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} {name} {samples[1]:F1} ms (median of 3, warm; runs {samples[0]:F1}/{samples[1]:F1}/{samples[2]:F1} ms)"));
        return count;
    }

    private static async Task ActAsUserAsync(NpgsqlConnection connection, Guid userId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.user_id', @u, false)", connection);
        command.Parameters.AddWithValue("u", userId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, Action<NpgsqlCommand> bind)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, Action<NpgsqlCommand> bind)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        var n = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) n++;
        return n;
    }

    private async Task PrintPlanAsync(
        NpgsqlConnection connection, string prefix, string name, string sql, Action<NpgsqlCommand> bind)
    {
        _output.WriteLine($"{prefix} plan {name}");
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            _output.WriteLine($"{prefix}   {reader.GetString(0)}");
    }
}
