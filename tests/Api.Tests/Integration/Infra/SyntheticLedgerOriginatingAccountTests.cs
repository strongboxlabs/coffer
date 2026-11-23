using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Every header shape the <see cref="SyntheticLedger"/> seeds keeps the
/// originating-account invariants, so no test runs against an owner no writer produces,
/// and leaves the running balances and posting counts as an EF save would.
/// </summary>
/// <remarks>
/// <para>A seed writes its owner by hand, past every writer that would choose it, so a
/// wrong one goes unnoticed: the tests that use the seed read other things. One test
/// builds every shape and runs the shared check over the ledger.</para>
///
/// <para>Fail closed: a source scan finds every seed method that inserts a header and
/// requires it to be in the list the test builds, so a new seed cannot skip the check,
/// and a removed one cannot leave a stale entry.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class SyntheticLedgerOriginatingAccountTests : DatabaseTest
{
    public SyntheticLedgerOriginatingAccountTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime At = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The seed methods the test below builds a header with.</summary>
    private static readonly string[] Built =
    [
        nameof(SyntheticLedger.AddTransactionPairAsync),
        nameof(SyntheticLedger.AddMultiSplitAsync),
        nameof(SyntheticLedger.AddInvestmentBuyAsync),
        nameof(SyntheticLedger.AddInvestmentSellAsync),
        nameof(SyntheticLedger.AddSharesInAsync),
        nameof(SyntheticLedger.AddInvestmentDividendAsync),
        nameof(SyntheticLedger.AddTransferSharesAsync),
    ];

    [Fact]
    public async Task Every_seeded_header_keeps_the_originating_account_invariants()
    {
        var ledger = await SeedEveryShapeAsync();
        await OriginatingAccountCheck.AssertHoldsAsync(Db, ledger.LedgerId);
    }

    /// <summary>
    /// Every shape leaves the running balances and posting counts as an EF save would.
    /// A raw seed reaches neither interceptor, so a helper that skips its recompute
    /// leaves its cash invisible to every as-of valuation, and a test reading one then
    /// passes or fails with the order its seeds ran in.
    /// </summary>
    /// <remarks>
    /// Checked after EACH seed, before the next one runs. The shapes share accounts,
    /// and every helper recomputes its accounts from the beginning, so a later seed
    /// rewrites the balance rows of every earlier header on an account it touches.
    /// Checked once at the end, a helper that skipped a recompute would pass whenever
    /// a later seed touched the same account.
    /// </remarks>
    [Fact]
    public async Task Every_seeded_header_leaves_balances_and_posting_counts_as_a_save_would()
    {
        var checkedSeeds = new List<string>();
        await SeedEveryShapeAsync(async (ledger, seed) =>
        {
            await AssertSavedAsync(ledger, seed);
            checkedSeeds.Add(seed);
        });

        // Fail closed: every shape was checked on its own.
        Assert.Equal(Built, checkedSeeds);
    }

    /// <summary>The ledger as it stands holds what an EF save of every header would
    /// leave: a running-balance row for each account a header touches, and balances
    /// and posting counts that match a recompute.</summary>
    private async Task AssertSavedAsync(SyntheticLedger ledger, string seed)
    {
        await using var db = Db.NewDbContext();

        // Every account a header touches has its running-balance row for that header,
        // what a skipped recompute would leave out. Asked of the legs directly; the
        // consistency report below asks it of the walk, and both must hold.
        var touched = await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledger.LedgerId)
            .Select(l => new { l.AccountId, l.HeaderId })
            .Distinct()
            .ToListAsync();
        var stored = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledger.LedgerId)
            .Select(b => new { b.AccountId, b.HeaderId })
            .ToListAsync();
        Assert.True(touched.Count > 0, $"{seed}: no header touched any account");
        var missing = touched.Except(stored).ToList();
        Assert.True(missing.Count == 0,
            $"{seed}: {missing.Count} account(s) a header touches have no balance row for it");

        var report = await new LedgerConsistencyRepository(
                db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
                TimeProvider.System)
            .CheckAsync(ledger.LedgerId);
        foreach (var name in new[] { ConsistencyProjections.Balances, ConsistencyProjections.PostingCounts })
        {
            var projection = Assert.Single(report.Projections, p => p.Projection == name);
            Assert.True(projection.Checked > 0, $"{seed}: {name} checked nothing");
            Assert.True(projection.Healthy, $"{seed}: {name}: {projection.MismatchedCount} mismatched");
        }
    }

    /// <summary>One header of every shape in <see cref="Built"/>, in that order.
    /// <paramref name="afterEach"/>, when given, runs after each seed with the seed's
    /// name, before the next seed can touch the same accounts.</summary>
    private async Task<SyntheticLedger> SeedEveryShapeAsync(
        Func<SyntheticLedger, string, Task>? afterEach = null)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var other = await ledger.AddBankAccountAsync("Account B");
        var category = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var dest = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");

        async Task Seeded(string seed)
        {
            if (afterEach is not null) await afterEach(ledger, seed);
        }

        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, At);
        await Seeded(nameof(SyntheticLedger.AddTransactionPairAsync));
        await ledger.AddMultiSplitAsync(bank.Id, [(category.Id, -5m), (other.Id, -7m)], At);
        await Seeded(nameof(SyntheticLedger.AddMultiSplitAsync));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, security, 10m, 20m, At);
        await Seeded(nameof(SyntheticLedger.AddInvestmentBuyAsync));
        await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, security, 4m, 25m, At);
        await Seeded(nameof(SyntheticLedger.AddInvestmentSellAsync));
        await ledger.AddSharesInAsync(brokerage.Id, holdings, category.Id, security, 3m, 60m, At);
        await Seeded(nameof(SyntheticLedger.AddSharesInAsync));
        await ledger.AddInvestmentDividendAsync(brokerage.Id, holdings, security, category.Id, 2m, At);
        await Seeded(nameof(SyntheticLedger.AddInvestmentDividendAsync));
        await ledger.AddTransferSharesAsync(
            brokerage.Id, holdings, dest.Id, dest.HoldingsAccountId!.Value, security, 2m, 20m, At);
        await Seeded(nameof(SyntheticLedger.AddTransferSharesAsync));
        return ledger;
    }

    [GeneratedRegex(@"public\s+(?:async\s+)?Task(?:<[^\n]*?>)?\s+(?<name>\w+)\s*\(")]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(@"INSERT\s+INTO\s+(?:public\.)?txn_headers\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderInsert();

    [Fact]
    public void Every_seed_method_that_inserts_a_header_is_built_above()
    {
        var infra = Path.Combine(RepoRoot(), "tests", "Api.Tests", "Integration", "Infra");
        var files = Directory.EnumerateFiles(infra, "SyntheticLedger*.cs")
            .Where(f => !f.EndsWith(nameof(SyntheticLedgerOriginatingAccountTests) + ".cs", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var inserting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var declarations = MethodDeclaration().Matches(code);
            foreach (Match insert in HeaderInsert().Matches(code))
            {
                var owner = declarations.LastOrDefault(d => d.Index < insert.Index);
                Assert.True(owner is not null, $"{Path.GetFileName(file)}: a header insert outside any method");
                inserting.Add(owner!.Groups["name"].Value);
            }
        }
        // Fail closed: a scan that found nothing would pass anything.
        Assert.True(inserting.Count >= 5, $"found only {inserting.Count} seed methods that insert a header");

        Assert.Empty(inserting.Except(Built));   // a seed the test does not build
        Assert.Empty(Built.Except(inserting));   // a stale entry
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
