using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the consistency report's minor-units readers cost on their own (ADR-0114 D13 and
/// D15): the Category amounts check, through its single-projection entry, and the
/// statements of the "Amounts with more decimals than their currency" advisory that compare
/// the ledger's lines with their currency's places, one statement per places value.
/// </summary>
/// <remarks>
/// <para>Two shapes: the stress seed at its default volume, and at four times its bank
/// volume. Each adds a JPY bank and a JPY category, and a KWD bank whose rows go to a seeded
/// USD category, so the advisory compares lines at three places values (0, 2 and 3) and the
/// category lines beside real lines of each, and the Category amounts check reads postings
/// of every currency.</para>
/// <para>Every statement runs as <c>coffer_app</c> under row-level security, on a connection
/// whose <c>statement_timeout</c> is the API's command timeout, so a read the app would
/// abandon fails here. The tables are analyzed after the seed. Each figure is warmed once
/// and is the median of three runs. <c>category-amounts</c> is the single-projection entry
/// (<see cref="LedgerConsistencyRepository.RewriteAsync(Guid, string, Guid, CancellationToken)"/>
/// on a healthy ledger, which checks and writes nothing), its statements timed one by one.
/// The advisory's line statements are taken from a whole report as the app sends them, then
/// each is run alone with the same parameters; their sum is the advisory's line cost. Each
/// statement's plan is printed from <c>EXPLAIN (ANALYZE, BUFFERS)</c>, with the rows its
/// <c>txn_legs</c> and <c>txn_headers</c> nodes read (returned plus discarded, over all
/// loops) against the ledger's.</para>
/// <para>Asserted, so no figure measures an empty projection: the check counts postings,
/// the ledger is healthy (the entry wrote nothing), the advisory sent its line statements
/// for each places value, each places value has lines to compare, and the advisory lists
/// nothing (every seeded figure fits). <b>No timing assertion</b> beyond the timeout the app
/// runs under; the figures are the output. Synthetic ledgers only, on the fixture's
/// database.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyMinorUnitsCostTests : DatabaseTest
{
    /// <summary>The API's command timeout, which the app's statements run under here.</summary>
    private const string StatementTimeout = "30s";

    private const int Runs = 3;

    /// <summary>Rows each added foreign account carries.</summary>
    private const int ForeignRows = 300;

    private readonly ITestOutputHelper _output;

    public ConsistencyMinorUnitsCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("bank-4x")]
    public async Task The_minor_units_readers_print_their_cost_and_plans(string shape)
    {
        var prefix = $"minor-units-cost [{shape}]:";
        var scale = shape == "bank-4x"
            ? StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 }
            : StressLedger.Scale.Default;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);

        var jpyBank = await ledger.AddBankAccountAsync("minor-units-jpy-bank", openingBalance: 50_000m, currencyCode: "JPY");
        var jpyCategory = await ledger.AddCategoryAsync("minor-units-jpy-category", currencyCode: "JPY");
        var kwdBank = await ledger.AddBankAccountAsync("minor-units-kwd-bank", openingBalance: 900.125m, currencyCode: "KWD");
        var usdCategory = StressLedger.CategoryId(ledger.LedgerId, 1);
        var first = new DateTime(2018, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < ForeignRows; i++)
        {
            await ledger.AddTransactionPairAsync(jpyBank.Id, jpyCategory.Id, -(1_200m + i), first.AddDays(i * 3));
            await ledger.AddTransactionPairAsync(kwdBank.Id, usdCategory, -(4.125m + (i % 17) / 1000m), first.AddDays(i * 3 + 1));
        }

        int headers, legs, jpyLegs, kwdLegs, categoryLegs;
        await using (var db = Db.NewDbContext())
        {
            db.Database.SetCommandTimeout(600);
            await db.Database.ExecuteSqlRawAsync(
                "ANALYZE accounts; ANALYZE txn_headers; ANALYZE txn_legs; ANALYZE loan_terms; ANALYZE budget_targets;");
            headers = await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId);
            legs = await db.TxnLegs.AsNoTracking().CountAsync(l => l.LedgerId == ledger.LedgerId);
            jpyLegs = await db.TxnLegs.AsNoTracking().CountAsync(l => l.AccountId == jpyBank.Id);
            kwdLegs = await db.TxnLegs.AsNoTracking().CountAsync(l => l.AccountId == kwdBank.Id);
            categoryLegs = await db.TxnLegs.AsNoTracking()
                .CountAsync(l => l.LedgerId == ledger.LedgerId && db.Accounts.Any(a => a.Id == l.AccountId && a.AccountType == "category"));
        }
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} seed {seeded.Elapsed.TotalSeconds:F1}s ({headers:N0} headers, {legs:N0} legs, {categoryLegs:N0} category legs; "
            + $"{jpyLegs:N0} JPY and {kwdLegs:N0} KWD bank legs)"));
        // The premise: each places value the advisory compares at has lines to compare.
        Assert.True(jpyLegs >= ForeignRows && kwdLegs >= ForeignRows && categoryLegs > legs / 4,
            $"lines to compare: JPY {jpyLegs}, KWD {kwdLegs}, category {categoryLegs} of {legs}");

        var appConnection = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Options = $"-c statement_timeout={StatementTimeout}",
        }.ConnectionString;
        var log = new CommandLog();
        AppDbContext NewContext(bool logged) => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(appConnection)
            .AddInterceptors(logged
                ? [new FixedUserConnectionInterceptor(ledger.UserId), log]
                : [new FixedUserConnectionInterceptor(ledger.UserId)])
            .Options);
        LedgerConsistencyRepository Repository(AppDbContext db) =>
            new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System);

        await using (var db = NewContext(logged: false))
        {
            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var show = new NpgsqlCommand("SELECT current_setting('statement_timeout') || ' ' || current_user::text", connection);
            Assert.Equal($"{StatementTimeout} coffer_app", (string?)await show.ExecuteScalarAsync());
        }

        // ---- (a) The Category amounts check, through its single-projection entry. ----
        ProjectionConsistency categoryAmounts = null!;
        var categoryMs = await MedianAsync(async () =>
        {
            await using var db = NewContext(logged: false);
            categoryAmounts = await Repository(db).RewriteAsync(
                ledger.LedgerId, ConsistencyProjections.CategoryAmounts, ledger.UserId, CancellationToken.None);
        });
        Assert.True(categoryAmounts.Healthy, $"the Category amounts check reported {categoryAmounts.MismatchedCount} lines");
        Assert.True(categoryAmounts.Checked > headers / 2, $"the Category amounts check counted {categoryAmounts.Checked} postings");
        var categoryStatements = await StatementsAsync(log, () => NewContext(logged: true),
            db => Repository(db).RewriteAsync(
                ledger.LedgerId, ConsistencyProjections.CategoryAmounts, ledger.UserId, CancellationToken.None));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} category-amounts {categoryMs:F1} ms (median of {Runs}, warm); {categoryAmounts.Checked:N0} postings checked; "
            + $"{categoryStatements.Count} statements"));

        // ---- (b) The advisory's line statements, from a whole report, each run alone. ----
        LedgerConsistencyReport report = null!;
        var reportMs = await MedianAsync(async () =>
        {
            await using var db = NewContext(logged: false);
            report = await Repository(db).CheckAsync(ledger.LedgerId, new DateOnly(2026, 6, 15));
        });
        Assert.Empty(report.UnfitAmounts);
        var reportStatements = await StatementsAsync(log, () => NewContext(logged: true),
            db => Repository(db).CheckAsync(ledger.LedgerId, new DateOnly(2026, 6, 15)));
        var unfitStatements = reportStatements
            .Where(s => s.Sql.Contains("round(", StringComparison.Ordinal) && s.Sql.Contains("txn_legs", StringComparison.Ordinal))
            .ToList();
        // Real and beside lines at 0, 2 and 3 places, and between-category lines at 0 and 2.
        Assert.True(unfitStatements.Count == 8,
            $"the advisory sent {unfitStatements.Count} line statements:\n" + string.Join("\n---\n", unfitStatements.Select(s => s.Sql)));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} whole report {reportMs:F1} ms (median of {Runs}, warm); {reportStatements.Count} statements, "
            + $"{unfitStatements.Count} of them the advisory's line comparisons"));

        var categoryTotal = 0.0;
        for (var i = 0; i < categoryStatements.Count; i++)
            categoryTotal += await PrintStatementAsync(prefix, $"category-amounts statement {i + 1}", categoryStatements[i],
                () => NewContext(logged: false), headers, legs);
        var unfitTotal = 0.0;
        for (var i = 0; i < unfitStatements.Count; i++)
            unfitTotal += await PrintStatementAsync(prefix, $"unfit-amounts statement {i + 1}", unfitStatements[i],
                () => NewContext(logged: false), headers, legs);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} sums: category-amounts statements {categoryTotal:F1} ms; unfit-amounts line statements {unfitTotal:F1} ms"));
    }

    /// <summary>The statements one run of <paramref name="read"/> sends, as the app sends them.</summary>
    private static async Task<List<Statement>> StatementsAsync(
        CommandLog log, Func<AppDbContext> newContext, Func<AppDbContext, Task> read)
    {
        log.Clear();
        await using (var db = newContext())
            await read(db);
        return [.. log.Statements];
    }

    /// <summary>One statement alone, with its parameters, on a fresh context of the app role:
    /// warmed, timed <see cref="Runs"/> times, and its plan printed with the rows its ledger
    /// table nodes read. Returns the median.</summary>
    private async Task<double> PrintStatementAsync(
        string prefix, string name, Statement statement, Func<AppDbContext> newContext, int headers, int legs)
    {
        await using var db = newContext();
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        async Task<int> RunAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in statement.Parameters) command.Parameters.Add(parameter.Clone());
            var n = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) n++;
            return n;
        }

        await RunAsync(statement.Sql);   // warm
        var samples = new List<double>();
        var rows = 0;
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            rows = await RunAsync(statement.Sql);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();

        var plan = new StringBuilder();
        await using (var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + statement.Sql, connection))
        {
            foreach (var parameter in statement.Parameters) explain.Parameters.Add(parameter.Clone());
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
        }
        var reads = TableReads(plan.ToString())
            .GroupBy(r => r.Table)
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{g.Key} {g.Sum(r => r.Rows):N0} of {(g.Key == "txn_headers" ? headers : legs):N0}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{prefix} {name} {samples[1]:F1} ms (runs {samples[0]:F1}/{samples[1]:F1}/{samples[2]:F1} ms); {rows:N0} rows; "
            + $"read {string.Join(", ", reads)}\n{statement.Sql}\n{plan}"));
        return samples[1];
    }

    /// <summary>A plan node reading <c>txn_headers</c> or <c>txn_legs</c>, any scan kind.</summary>
    private static readonly Regex TableNode =
        new(@" on (txn_headers|txn_legs)\b.*\(actual time=[0-9.]+\.\.[0-9.]+ rows=([0-9.]+) loops=([0-9]+)\)",
            RegexOptions.CultureInvariant);

    /// <summary>Rows a node discarded after reading them, per loop.</summary>
    private static readonly Regex RowsRemoved =
        new(@"^\s*Rows Removed by (Filter|Index Recheck): ([0-9.]+)", RegexOptions.CultureInvariant);

    /// <summary>The rows each <c>txn_headers</c> or <c>txn_legs</c> node of an
    /// <c>EXPLAIN ANALYZE</c> plan read, over all its loops: those it returned and those its
    /// filter or recheck discarded.</summary>
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
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private sealed record Statement(string Sql, NpgsqlParameter[] Parameters);

    /// <summary>Each statement a context ran, with its parameters.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<Statement> _statements = [];

        public IReadOnlyList<Statement> Statements => _statements;

        public void Clear() => _statements.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _statements.Add(new Statement(command.CommandText,
                [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone())]));
            return ValueTask.FromResult(result);
        }
    }
}
