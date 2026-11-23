using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// The SHAPE of the read every dated write takes twice for the market-series start trigger
/// (<see cref="NeededFxPairs.StartsAsync"/>, ADR-0114 D11): one derivation, each statement
/// sent once, answering exactly what the separate reads answer.
/// </summary>
/// <remarks>
/// <para><b>No timing assertion</b>, per the ordinary suite's rule. The read is taken before
/// and after every dated write, so what it costs is charged to every save; the stress lane
/// times it against the write on two large ledgers and a sparse one
/// (<c>MarketSeriesStartCostTests</c>), and checks there that no statement of it reads most
/// of the ledger's legs or headers. What this class holds on any hardware is that the starts do
/// not read the ledger's earliest day, its first invested day or its accounts twice, and
/// send their statements on one connection.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NeededStartsReadShapeTests : DatabaseTest
{
    public NeededStartsReadShapeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Counts the statements a context sends.</summary>
    private sealed class Counter : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Counts the connections a context opens.</summary>
    private sealed class Opens : DbConnectionInterceptor
    {
        public int Count { get; private set; }

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }
    }

    /// <summary>
    /// A ledger in USD with every source of a needed pair and both days: a EUR bank account
    /// (a net-worth pair from the earliest day), a USD brokerage funded before its first
    /// trade, a security it holds with a GBP price (a position pair), and a benchmark (the
    /// first invested day, and a benchmark pair into the reporting currency).
    /// </summary>
    private async Task<SyntheticLedger> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Spending");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, Utc(2021, 3, 4));
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", openingBalance: 1000m,
            openedOn: new DateOnly(2022, 1, 10));
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 2m, 10m,
            Utc(2022, 6, 1));
        await ledger.AddSecurityPriceAsync(security, 9m, Utc(2022, 2, 1), currencyCode: "GBP");
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        db.LedgerBenchmarks.Add(new LedgerBenchmarkRow
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Symbol = "SYNTHB", Name = "Synthetic Index B",
            CurrencyCode = "EUR", Position = 1, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return ledger;
    }

    [Fact]
    public async Task The_starts_send_each_statement_of_the_one_derivation_once()
    {
        var ledger = await SeedAsync();
        var counter = new Counter();
        var opens = new Opens();

        NeededStarts starts;
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                         .UseNpgsql(Db.ServiceConnectionString)
                         .AddInterceptors(counter, opens)
                         .Options))
            starts = await NeededFxPairs.StartsAsync(db, ledger.LedgerId);

        // The reporting currency, the accounts, the price currencies, the held positions, the
        // ledger's earliest day, the benchmarks and the first invested day: seven, none twice,
        // on one connection, so the app role's user is set once, not once a statement.
        Assert.True(counter.Statements.Count == 7,
            $"{counter.Statements.Count} statements:\n" + string.Join("\n----\n", counter.Statements));
        Assert.Equal(1, opens.Count);

        // The same answer as the separate reads, so sharing the statements changed nothing.
        await using var read = Db.NewDbContext();
        Assert.Equal(new DateOnly(2021, 3, 4), starts.LedgerEarliest);
        Assert.Equal(await NeededFxPairs.LedgerEarliestDayAsync(read, ledger.LedgerId), starts.LedgerEarliest);
        Assert.Equal(new DateOnly(2022, 1, 10), starts.BenchmarkStart);
        Assert.Equal(await InvestmentReportingRepository.FirstInvestedDayAsync(read, ledger.LedgerId), starts.BenchmarkStart);
        var pairs = await NeededFxPairs.ForLedgerAsync(read, ledger.LedgerId);
        Assert.Equal(
            pairs.Select(p => ((p.FromCurrency, p.ToCurrency), p.NeededFrom)).ToList(),
            starts.Pairs.OrderBy(p => p.Key.From, StringComparer.Ordinal).ThenBy(p => p.Key.To, StringComparer.Ordinal)
                .Select(p => (p.Key, p.Value)).ToList());
        Assert.Equal(
            [("EUR", "USD"), ("GBP", "USD")],
            starts.Pairs.Keys.Order().ToList());
    }

    /// <summary>
    /// The first invested day reads its accounts' legs rather than walking the ledger's
    /// headers (<see cref="NeededFxPairs.FirstLivePostingOfFewAsync"/>), and counts the same
    /// live postings the walk counts: a hidden and a merged-away brokerage funding dated
    /// before the first live one move it no earlier.
    /// </summary>
    /// <remarks>Mutation caught: the legs joined to every header of the ledger rather than
    /// its live ones (the first invested day reads the hidden funding's day).</remarks>
    [Fact]
    public async Task The_first_invested_day_counts_only_live_postings_on_its_accounts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Home bank");
        var category = await ledger.AddCategoryAsync("Spending");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, Utc(2018, 3, 4));
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var (hidden, _) = await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500m, Utc(2019, 1, 5));
        await ledger.HideTransactionAsync(hidden);
        var (live, _) = await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 700m, Utc(2020, 2, 3));
        var (folded, _) = await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 700m, Utc(2019, 6, 7));
        await ledger.MarkTransactionMergedAsync(folded, live);
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 2m, 10m,
            Utc(2020, 5, 1));

        await using var read = Db.NewDbContext();
        List<Guid> investment = [brokerage.Id, brokerage.HoldingsAccountId!.Value];
        Assert.Equal(new DateOnly(2020, 2, 3), await InvestmentReportingRepository.FirstInvestedDayAsync(read, ledger.LedgerId));
        Assert.Equal(
            await NeededFxPairs.FirstLivePostingAsync(read, ledger.LedgerId, investment, CancellationToken.None),
            await NeededFxPairs.FirstLivePostingOfFewAsync(read, ledger.LedgerId, investment, CancellationToken.None));
    }
}
