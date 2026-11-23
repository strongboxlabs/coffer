using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// Migration 280 rehearsed against an install that already holds ledgers: each ledger
/// takes the reporting currency <see cref="DisplayCurrency.Of"/> names for it, and each
/// budget target takes its ledger's (ADR-0114 D7, D12).
/// </summary>
/// <remarks>
/// <para>A fresh database has no ledger for the backfill to act on, so only a rehearsal on
/// seeded data exercises it. The seed is what the writers before 280 left: accounts with
/// their codes, a brokerage naming its holdings account, an inactive account, categories,
/// and a holdings account kept in another code than its brokerage's, which the consistency
/// check reports and an earlier currency edit could leave.</para>
/// <para>Pinned: the rule's every exclusion (a category, a holdings account) and its
/// inclusions (an inactive account, every non-category type), the fallback for a ledger
/// with no account and one with categories alone, the agreement with the C# rule on the
/// migrated data, the targets' copy of their ledger's code, and that a second run of the
/// script changes nothing.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReportingCurrencyBackfillTests : DatabaseTest
{
    public ReportingCurrencyBackfillTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Mig280 = "280_reporting_currency_benchmarks_and_exchange_rates.sql";

    // One account set per ledger, each the rule's case.
    private static readonly Guid SingleCurrency = new("28028028-0280-2802-8028-028028028001");
    private static readonly Guid Mixed = new("28028028-0280-2802-8028-028028028002");
    private static readonly Guid NoAccounts = new("28028028-0280-2802-8028-028028028003");
    private static readonly Guid CategoriesOnly = new("28028028-0280-2802-8028-028028028004");
    private static readonly Guid CategoryAndHoldingsIgnored = new("28028028-0280-2802-8028-028028028005");
    private static readonly Guid InactiveCounts = new("28028028-0280-2802-8028-028028028006");

    private static readonly Guid MixedCategory = new("28028028-0280-2802-8028-0280280280c2");
    private static readonly Guid SingleCategory = new("28028028-0280-2802-8028-0280280280c1");

    private static readonly Dictionary<Guid, string> Expected = new()
    {
        // Every net-worth account in one code; the category's other code is not read.
        [SingleCurrency] = "EUR",
        // Bank, credit card and brokerage in three codes: the first in ordinal order.
        [Mixed] = "CHF",
        // Nothing to read: the fallback.
        [NoAccounts] = "USD",
        // Categories only: still nothing to read.
        [CategoriesOnly] = "USD",
        // A category and a holdings account sort first, and neither is read.
        [CategoryAndHoldingsIgnored] = "NZD",
        // An inactive account is one of net worth's.
        [InactiveCounts] = "BRL",
    };

    private (string ConnectionString, string WorkDir) InstallBefore280(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig280-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig280, work));
        return (cs, work);
    }

    private static Task SeedAsync(string cs) => ExecAsync(cs, $"""
        INSERT INTO ledgers (id, name) VALUES
            ('{SingleCurrency}', 'Ledger A'), ('{Mixed}', 'Ledger B'), ('{NoAccounts}', 'Ledger C'),
            ('{CategoriesOnly}', 'Ledger D'), ('{CategoryAndHoldingsIgnored}', 'Ledger E'),
            ('{InactiveCounts}', 'Ledger F');

        INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                              opening_balance, is_active, is_system, holdings_account_id)
        VALUES
            -- A: one code across net worth, a category in another.
            (gen_random_uuid(), '{SingleCurrency}', 'Bank A', 'bank', NULL, 'EUR', 0, TRUE, FALSE, NULL),
            (gen_random_uuid(), '{SingleCurrency}', 'Asset A', 'asset', NULL, 'EUR', 0, TRUE, FALSE, NULL),
            ('{SingleCategory}', '{SingleCurrency}', 'Category A', 'category', 'expense', 'AUD', 0, TRUE, FALSE, NULL),

            -- B: three codes, and a brokerage whose holdings account agrees with it.
            (gen_random_uuid(), '{Mixed}', 'Bank B', 'bank', NULL, 'GBP', 0, TRUE, FALSE, NULL),
            (gen_random_uuid(), '{Mixed}', 'Card B', 'credit_card', NULL, 'CHF', 0, TRUE, FALSE, NULL),
            ('28028028-0280-2802-8028-0280280280b1', '{Mixed}', 'Brokerage B Holdings', 'investment', NULL, 'EUR', 0, TRUE, TRUE, NULL),
            (gen_random_uuid(), '{Mixed}', 'Brokerage B', 'investment', NULL, 'EUR', 0, TRUE, FALSE,
                '28028028-0280-2802-8028-0280280280b1'),
            ('{MixedCategory}', '{Mixed}', 'Category B', 'category', 'expense', 'USD', 0, TRUE, FALSE, NULL),

            -- D: categories alone.
            (gen_random_uuid(), '{CategoriesOnly}', 'Category D', 'category', 'income', 'EUR', 0, TRUE, FALSE, NULL),

            -- E: a category and a holdings account in codes that sort before the bank's.
            (gen_random_uuid(), '{CategoryAndHoldingsIgnored}', 'Category E', 'category', 'expense', 'AUD', 0, TRUE, FALSE, NULL),
            ('28028028-0280-2802-8028-0280280280e1', '{CategoryAndHoldingsIgnored}', 'Brokerage E Holdings', 'investment', NULL, 'CAD', 0, TRUE, TRUE, NULL),
            (gen_random_uuid(), '{CategoryAndHoldingsIgnored}', 'Brokerage E', 'investment', NULL, 'NZD', 0, TRUE, FALSE,
                '28028028-0280-2802-8028-0280280280e1'),
            (gen_random_uuid(), '{CategoryAndHoldingsIgnored}', 'Bank E', 'bank', NULL, 'SEK', 0, TRUE, FALSE, NULL),

            -- F: an inactive account sorts first and is read.
            (gen_random_uuid(), '{InactiveCounts}', 'Bank F', 'bank', NULL, 'CAD', 0, TRUE, FALSE, NULL),
            (gen_random_uuid(), '{InactiveCounts}', 'Old Bank F', 'bank', NULL, 'BRL', 0, FALSE, FALSE, NULL);

        INSERT INTO budget_targets (ledger_id, category_id, target_month, amount)
        VALUES
            ('{Mixed}', '{MixedCategory}', DATE '2026-01-01', 10.00),
            ('{Mixed}', '{MixedCategory}', DATE '2026-02-01', 20.00),
            ('{SingleCurrency}', '{SingleCategory}', DATE '2026-01-01', 30.00);
        """);

    [Fact]
    public async Task Each_ledger_takes_the_display_currency_rule_and_each_target_its_ledgers_code()
    {
        var (cs, work) = InstallBefore280("mig280_backfill");
        await SeedAsync(cs);
        // Everything 280 must not change, read before it runs.
        var ledgersBefore = await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, name, created_at FROM ledgers ORDER BY id");
        var targetsBefore = await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, ledger_id, category_id, target_month, amount FROM budget_targets ORDER BY id");

        UpgradeRehearsal.Apply(cs, work, Mig280);

        Assert.Equal(Expected, await SeededLedgerCurrenciesAsync(cs));

        // The SQL rule and the C# one agree on every seeded ledger: the migration states
        // DisplayCurrency.Of over NetWorthAccounts, and this holds it to that.
        await using (var db = Context(cs))
        {
            foreach (var ledger in Expected.Keys)
                Assert.Equal(Expected[ledger], DisplayCurrency.Of(
                    await DisplayCurrency.NetWorthAccounts(db, ledger).Select(a => a.CurrencyCode).ToListAsync()).Code);
        }

        Assert.Equal(ExpectedTargets, await TargetCurrenciesAsync(cs));

        Assert.Equal(ledgersBefore, await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, name, created_at FROM ledgers ORDER BY id"));
        Assert.Equal(targetsBefore, await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, ledger_id, category_id, target_month, amount FROM budget_targets ORDER BY id"));
    }

    [Fact]
    public async Task A_ledger_created_after_280_without_a_code_is_USD_and_a_target_without_one_is_refused()
    {
        var (cs, work) = InstallBefore280("mig280_defaults");
        UpgradeRehearsal.Apply(cs, work, Mig280);

        var ledger = Guid.NewGuid();
        var category = Guid.NewGuid();
        await ExecAsync(cs, $"""
            INSERT INTO ledgers (id, name) VALUES ('{ledger}', 'Ledger G');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, opening_balance, is_active)
                 VALUES ('{category}', '{ledger}', 'Category G', 'category', 'expense', 'EUR', 0, TRUE);
            """);
        Assert.Equal("USD", (await LedgerCurrenciesAsync(cs))[ledger]);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"""
            INSERT INTO budget_targets (ledger_id, category_id, target_month, amount)
                 VALUES ('{ledger}', '{category}', DATE '2026-01-01', 1.00)
            """));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, refused.SqlState);
        Assert.Equal("currency_code", refused.ColumnName);
    }

    [Fact]
    public async Task Applying_280_twice_changes_nothing_the_first_run_did_not()
    {
        var (cs, work) = InstallBefore280("mig280_rerun");
        await SeedAsync(cs);

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig280);

        Assert.Equal(Expected, await SeededLedgerCurrenciesAsync(cs));
        Assert.Equal(ExpectedTargets, await TargetCurrenciesAsync(cs));
    }

    // In (ledger, month) order: ledger A's one target, then ledger B's two.
    private static readonly List<(Guid Ledger, string Code)> ExpectedTargets =
    [
        (SingleCurrency, "EUR"),
        (Mixed, "CHF"),
        (Mixed, "CHF"),
    ];

    private static AppDbContext Context(string cs) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    /// <summary>The codes of the ledgers <see cref="SeedAsync"/> wrote, and of no other.</summary>
    private static async Task<Dictionary<Guid, string>> SeededLedgerCurrenciesAsync(string cs) =>
        (await LedgerCurrenciesAsync(cs))
            .Where(kv => Expected.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static async Task<Dictionary<Guid, string>> LedgerCurrenciesAsync(string cs)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id, currency_code FROM ledgers", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var codes = new Dictionary<Guid, string>();
        while (await reader.ReadAsync()) codes[reader.GetGuid(0)] = reader.GetString(1);
        return codes;
    }

    private static async Task<List<(Guid Ledger, string Code)>> TargetCurrenciesAsync(string cs)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT ledger_id, currency_code FROM budget_targets ORDER BY ledger_id, target_month", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetString(1)));
        return rows;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
