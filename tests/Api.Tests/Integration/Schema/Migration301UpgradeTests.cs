using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// Migration 301 (ADR-0114 D15) rehearsed against an install seeded the way the code
/// before it wrote: every leg at 2 places whatever its account's currency, realized
/// gains and a budget target at scale 2, and originals rows with no amount.
/// </summary>
/// <remarks>
/// <para>The install has accounts in a 0-place, a 2-place and a 3-place currency and one
/// in a code an account can no longer take, a ledger whose reporting currency has 0
/// places with a 2-place target in it (the shape migration 280 stamps), brokerages in a
/// 0-place and a 3-place currency with a sale whose cost basis has a fraction, an
/// originals row as an edit captured it and one as a Moneydance seed wrote it.</para>
/// <para>What it proves: the ceiling moved from 2 places to 3 and nothing stored changed
/// value; the widened columns hold every value they held; the sidecar's columns arrive
/// empty and the view keeps every column it had, in order, and its RLS boundary; the
/// two ingest carriers lose their scale limit and the views dropped for that come back
/// with the same columns, RLS boundary, grant and comment; the
/// two realized-gains functions changed only where they round; and a realized gain
/// stored at 2 places on a 0-place account is drift that the existing repair fixes.
/// Each assertion is anchored by a pre-assertion that the install is at 300.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class Migration301UpgradeTests : DatabaseTest
{
    private const string Mig301 = "301_each_currency_keeps_its_minor_units.sql";

    internal static readonly Guid Ledger = new("30100000-0000-4000-8000-000000000001");
    internal static readonly Guid Category = new("30100000-0000-4000-8000-000000000002");
    private static readonly Guid ZeroPlaceBank = new("30100000-0000-4000-8000-000000000010");
    private static readonly Guid TwoPlaceBank = new("30100000-0000-4000-8000-000000000011");
    private static readonly Guid ThreePlaceBank = new("30100000-0000-4000-8000-000000000012");
    private static readonly Guid RefusedCodeBank = new("30100000-0000-4000-8000-000000000013");
    private static readonly Guid ZeroPlaceBrokerage = new("30100000-0000-4000-8000-000000000020");
    private static readonly Guid ZeroPlaceHoldings = new("30100000-0000-4000-8000-000000000021");
    private static readonly Guid ThreePlaceBrokerage = new("30100000-0000-4000-8000-000000000022");
    private static readonly Guid ThreePlaceHoldings = new("30100000-0000-4000-8000-000000000023");
    private static readonly Guid Security = new("30100000-0000-4000-8000-000000000030");
    private static readonly Guid EditedHeader = new("30100000-0000-4000-8000-000000000041");
    private static readonly Guid SeededHeader = new("30100000-0000-4000-8000-000000000042");
    private static readonly Guid PlainHeader = new("30100000-0000-4000-8000-000000000043");
    private static readonly Guid RefusedCodeHeader = new("30100000-0000-4000-8000-000000000044");
    private static readonly Guid ZeroPlaceBuy = new("30100000-0000-4000-8000-000000000051");
    private static readonly Guid ZeroPlaceSell = new("30100000-0000-4000-8000-000000000052");
    private static readonly Guid ThreePlaceBuy = new("30100000-0000-4000-8000-000000000053");
    private static readonly Guid ThreePlaceSell = new("30100000-0000-4000-8000-000000000054");
    private static readonly Guid Target = new("30100000-0000-4000-8000-000000000060");

    private static readonly string[] WidenedRealizedGains =
        ["proceeds", "proceeds_lt", "cost_basis_sold", "cost_basis_sold_lt", "realized_gain", "realized_gain_lt"];

    private static readonly string[] IngestCarriers = ["ingest_amount", "ingest_fee"];

    /// <summary>The views 301 drops and creates again besides resolved_transactions, whose
    /// columns are checked on their own above.</summary>
    private static readonly string[] RecreatedHeaderViews =
        ["live_txn_headers", "template_txn_headers", "balance_walk_headers"];

    private static readonly string[] ReviewColumns = ["review_cleared_amount", "review_cleared_by_match"];

    private static readonly string[] AppendedViewColumns =
        ["original_amount", "amount_rounded_at_import", "counterparty_amount", "counterparty_currency_code", .. ReviewColumns];

    /// <summary>The columns 301 appends to a recreated header view: the review marker's
    /// two on live_txn_headers, none on the others.</summary>
    private static IReadOnlyList<string> AppendedTo(string view) =>
        view == "live_txn_headers" ? ReviewColumns : [];

    public Migration301UpgradeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private (string ConnectionString, string WorkDir) InstallBefore301(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig301-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig301, work));
        return (cs, work);
    }

    /// <summary>The install as the code before 301 left it.</summary>
    /// <remarks>The rows 301 reads or rewrites (txn_legs, budget_targets,
    /// txn_header_originals) are written with fixed keys and stamps, so two databases
    /// seeded with it hold the same such rows. Ledgers, accounts, securities and headers
    /// take their own clock stamps from their column defaults; the holdings and
    /// realized_gains rows the recompute derives take a key and a stamp of their own. A
    /// comparison across two seeded databases holds over those three tables, and over
    /// realized_gains without its key and stamp, not over the rest. The old full backup restored forward in <c>PostUpgradeBackupRestoreTests</c> is
    /// this install.</remarks>
    internal static async Task SeedAsync(string cs)
    {
        await ExecAsync(cs, $"""
            INSERT INTO ledgers (id, name, currency_code) VALUES ('{Ledger}', 'Home', 'JPY');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code)
            VALUES ('{Category}', '{Ledger}', 'Groceries', 'category', 'expense', 'JPY');
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code) VALUES
                ('{ZeroPlaceBank}',    '{Ledger}', 'Zero-place bank',  'bank', 'JPY'),
                ('{TwoPlaceBank}',     '{Ledger}', 'Two-place bank',   'bank', 'USD'),
                ('{ThreePlaceBank}',   '{Ledger}', 'Three-place bank', 'bank', 'BHD'),
                ('{RefusedCodeBank}',  '{Ledger}', 'Refused-code bank', 'bank', 'XAU');
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, is_system) VALUES
                ('{ZeroPlaceHoldings}',  '{Ledger}', 'Zero-place Holdings',  'investment', 'JPY', true),
                ('{ThreePlaceHoldings}', '{Ledger}', 'Three-place Holdings', 'investment', 'BHD', true);
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id) VALUES
                ('{ZeroPlaceBrokerage}',  '{Ledger}', 'Zero-place brokerage',  'investment', 'JPY', '{ZeroPlaceHoldings}'),
                ('{ThreePlaceBrokerage}', '{Ledger}', 'Three-place brokerage', 'investment', 'BHD', '{ThreePlaceHoldings}');
            INSERT INTO securities (id, ledger_id, name) VALUES ('{Security}', '{Ledger}', 'Fund');

            -- A 2-place target in the 0-place ledger, with a fraction.
            INSERT INTO budget_targets (id, ledger_id, category_id, target_month, amount, currency_code,
                                        created_at, updated_at)
            VALUES ('{Target}', '{Ledger}', '{Category}', DATE '2026-01-01', 12.50, 'JPY',
                    TIMESTAMPTZ '2026-01-01 00:00:00+00', TIMESTAMPTZ '2026-01-01 00:00:00+00');
            """);

        // A bank posting on each account, at 2 places, which is all the old CHECK allowed.
        await BankPostingAsync(cs, EditedHeader, TwoPlaceBank, -12.34m, "2026-01-02");
        await BankPostingAsync(cs, SeededHeader, ThreePlaceBank, -1.23m, "2026-01-03");
        await BankPostingAsync(cs, PlainHeader, ZeroPlaceBank, -12.50m, "2026-01-04");
        await BankPostingAsync(cs, RefusedCodeHeader, RefusedCodeBank, -4.56m, "2026-01-05");

        // An originals row as the edit capture wrote it, and one as the Moneydance seed did.
        await ExecAsync(cs, $"""
            INSERT INTO txn_header_originals
                (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number, captured_at)
            VALUES
                ('{EditedHeader}', '{Ledger}', 'Shop', NULL,
                 TIMESTAMPTZ '2026-01-02 00:00:00+00', TIMESTAMPTZ '2026-01-02 00:00:00+00', NULL,
                 TIMESTAMPTZ '2026-02-01 00:00:00+00'),
                ('{SeededHeader}', '{Ledger}', 'SHOP 1234 FEED', 'feed memo',
                 TIMESTAMPTZ '2026-01-03 00:00:00+00', TIMESTAMPTZ '2026-01-03 00:00:00+00', '101',
                 TIMESTAMPTZ '2026-01-03 00:00:00+00');
            """);

        // On each brokerage, two shares bought and one sold more than a year later, so the
        // sale's cost basis is half the buy's odd total: a fraction the old writer stored
        // at 2 places.
        await TradeAsync(cs, ZeroPlaceBuy, ZeroPlaceBrokerage, ZeroPlaceHoldings, "buy", 2m, 2001.00m, "2024-01-02");
        await TradeAsync(cs, ZeroPlaceSell, ZeroPlaceBrokerage, ZeroPlaceHoldings, "sell", -1m, -1500.00m, "2025-06-02");
        await TradeAsync(cs, ThreePlaceBuy, ThreePlaceBrokerage, ThreePlaceHoldings, "buy", 2m, 2.01m, "2024-01-02");
        await TradeAsync(cs, ThreePlaceSell, ThreePlaceBrokerage, ThreePlaceHoldings, "sell", -1m, -1.50m, "2025-06-02");
        await ExecAsync(cs, $"""
            SELECT recompute_holdings_cost_basis(NULL, '{ZeroPlaceHoldings}', '{Security}');
            SELECT recompute_holdings_cost_basis(NULL, '{ThreePlaceHoldings}', '{Security}');
            """);
    }

    /// <summary>A one-posting bank row against the category, each leg keyed by its header
    /// and account and stamped on the row's own day.</summary>
    /// <remarks>Raw SQL reaches no writer, so the header's stored memo-and-tags
    /// presentation is then stored as the upgrade stores it, and the install is one the
    /// code before 301 could leave.</remarks>
    internal static async Task BankPostingAsync(string cs, Guid header, Guid account, decimal amount, string date)
    {
        await ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, originating_account_id)
            VALUES ('{header}', '{Ledger}', 'manual', 'Shop',
                    TIMESTAMPTZ '{date} 00:00:00+00', TIMESTAMPTZ '{date} 00:00:00+00', '{account}');
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, created_at) VALUES
                (md5('{header}/{account}')::uuid, '{header}', '{Ledger}', '{account}', 0, {Sql(amount)},
                 TIMESTAMPTZ '{date} 00:00:00+00'),
                (md5('{header}/{Category}')::uuid, '{header}', '{Ledger}', '{Category}', 0, {Sql(-amount)},
                 TIMESTAMPTZ '{date} 00:00:00+00');
            """);
        await LegNotesBackfill.RunAsync(cs, Ledger, CancellationToken.None, headerIds: [header]);
    }

    /// <summary>A trade as the investment seed shape has it: the holdings leg carries the
    /// shares and the basis or the disposal, the brokerage leg the cash.</summary>
    /// <remarks>Its stored presentation is then stored as for
    /// <see cref="BankPostingAsync"/>.</remarks>
    private static async Task TradeAsync(
        string cs, Guid header, Guid brokerage, Guid holdings, string action, decimal shares, decimal holdingsAmount,
        string date)
    {
        await ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, originating_account_id)
            VALUES ('{header}', '{Ledger}', 'manual', '{action}', '{action}',
                    TIMESTAMPTZ '{date} 00:00:00+00', TIMESTAMPTZ '{date} 00:00:00+00', '{brokerage}');
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                                  security_id, quantity, posting_role, created_at) VALUES
                (md5('{header}/{holdings}')::uuid, '{header}', '{Ledger}', '{holdings}', 0, {Sql(holdingsAmount)},
                 '{Security}', {Sql(shares)}, 'security', TIMESTAMPTZ '{date} 00:00:00+00'),
                (md5('{header}/{brokerage}')::uuid, '{header}', '{Ledger}', '{brokerage}', 0, {Sql(-holdingsAmount)},
                 NULL, NULL, 'security', TIMESTAMPTZ '{date} 00:00:00+00');
            """);
        await LegNotesBackfill.RunAsync(cs, Ledger, CancellationToken.None, headerIds: [header]);
    }

    [Fact]
    public async Task An_install_before_301_keeps_every_figure_while_the_ceiling_widens_and_the_sidecar_gains_its_columns()
    {
        var (cs, work) = InstallBefore301("mig301_upgrade");
        await SeedAsync(cs);

        // ---- At 300 ---------------------------------------------------------
        Assert.Equal(["ck_txn_legs_amount_scale_2"], await ListAsync(cs,
            "SELECT conname FROM pg_constraint WHERE conrelid = 'txn_legs'::regclass AND conname LIKE 'ck_txn_legs_amount%'"));
        Assert.Empty(await ListAsync(cs, "SELECT proname FROM pg_proc WHERE proname = 'currency_minor_units'"));
        Assert.Equal(["2"], await ListAsync(cs,
            "SELECT numeric_scale::text FROM information_schema.columns WHERE table_name = 'budget_targets' AND column_name = 'amount'"));
        Assert.DoesNotContain("amount", await ColumnsAsync(cs, "txn_header_originals"));
        // The old writer stored the fractional basis at 2 places.
        Assert.Equal([1000.50m], await DecimalsAsync(cs,
            $"SELECT cost_basis_sold FROM realized_gains WHERE account_id = '{ZeroPlaceHoldings}'"));

        const string legsDigestSql = "SELECT id, header_id, account_id, posting_index, amount::text FROM txn_legs ORDER BY id";
        const string originalsDigestSql = """
            SELECT header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number, captured_at
              FROM txn_header_originals ORDER BY header_id
            """;
        var legsBefore = await UpgradeRehearsal.DigestAsync(cs, legsDigestSql);
        var originalsBefore = await UpgradeRehearsal.DigestAsync(cs, originalsDigestSql);
        var gainsBefore = await RealizedGainsAsync(cs);
        var targetsBefore = await DecimalsAsync(cs, "SELECT amount FROM budget_targets ORDER BY id");
        var viewColumnsBefore = await ColumnsAsync(cs, "resolved_transactions");
        foreach (var carrier in IngestCarriers)
        {
            Assert.Equal(["19,4"], await ListAsync(cs, $"""
                SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns
                 WHERE table_name = 'txn_headers' AND column_name = '{carrier}'
                """));
        }
        var recreatedColumnsBefore = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var view in RecreatedHeaderViews)
            recreatedColumnsBefore[view] = await ColumnsAsync(cs, view);
        var walkCommentBefore = await ListAsync(cs, "SELECT obj_description('balance_walk_headers'::regclass, 'pg_class')");
        Assert.NotEmpty(Assert.Single(walkCommentBefore));
        var overridesBefore = await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, has_overrides FROM resolved_transactions ORDER BY id");
        var recomputeBefore = await FunctionDefAsync(cs, "recompute_holdings_cost_basis(uuid, uuid, uuid)");
        var walkBefore = await FunctionDefAsync(cs, "realized_gains_walk(uuid, uuid)");
        // One sale on each brokerage, six money figures each.
        Assert.Equal(12, gainsBefore.Count);

        // Applied, then run a second time against the database it changed.
        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig301);
        var overridesAfter = await UpgradeRehearsal.DigestAsync(cs,
            "SELECT id, has_overrides FROM resolved_transactions ORDER BY id");

        // ---- The ceiling ----------------------------------------------------
        var ceiling = Assert.Single(await ConstraintsAsync(cs, "txn_legs", "ck_txn_legs_amount%"));
        Assert.Equal("ck_txn_legs_amount_at_most_3_places", ceiling.Name);
        Assert.True(ceiling.Validated);
        Assert.Equal("CHECK ((amount = round(amount, 3)))", ceiling.Definition);
        // A 3-place leg on the 3-place account inserts, which the old CHECK refused...
        await ExecAsync(cs, $"""
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES (gen_random_uuid(), '{SeededHeader}', '{Ledger}', '{ThreePlaceBank}', 1, -1.234),
                   (gen_random_uuid(), '{SeededHeader}', '{Ledger}', '{Category}', 1, 1.234);
            """);
        // ...and a 4-place one is refused by the new CHECK.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"""
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES (gen_random_uuid(), '{SeededHeader}', '{Ledger}', '{ThreePlaceBank}', 2, -1.2345);
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Equal("ck_txn_legs_amount_at_most_3_places", refused.ConstraintName);
        // The two legs that went in reached no writer: store the header's presentation.
        await LegNotesBackfill.RunAsync(cs, Ledger, CancellationToken.None, headerIds: [SeededHeader]);

        // ---- The widen -------------------------------------------------------
        Assert.Equal(["20,3"], await ListAsync(cs, """
            SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns
             WHERE table_name = 'budget_targets' AND column_name = 'amount'
            """));
        foreach (var column in WidenedRealizedGains)
        {
            Assert.Equal(["20,3"], await ListAsync(cs, $"""
                SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns
                 WHERE table_name = 'realized_gains' AND column_name = '{column}'
                """));
        }
        Assert.Equal(["25,12"], await ListAsync(cs, """
            SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns
             WHERE table_name = 'realized_gains' AND column_name = 'quantity'
            """));
        // Every value is numerically what it was (a decimal compares by value, so the
        // scale that widened does not count).
        Assert.Equal(gainsBefore, await RealizedGainsAsync(cs));
        Assert.Equal(targetsBefore, await DecimalsAsync(cs, "SELECT amount FROM budget_targets ORDER BY id"));
        // The 2-place target in the 0-place ledger is kept, and nothing on budget_targets
        // checks a currency's places.
        Assert.Equal([12.5m], targetsBefore);
        Assert.Empty(await ListAsync(cs, """
            SELECT conname FROM pg_constraint
             WHERE conrelid = 'budget_targets'::regclass
               AND pg_get_constraintdef(oid) LIKE '%currency_minor_units%'
            """));

        // ---- The sidecar and the view ----------------------------------------
        Assert.Equal(["numeric||YES|"], await ListAsync(cs, """
            SELECT data_type || '|' || COALESCE(numeric_precision::text, '') || '|' || is_nullable
                   || '|' || COALESCE(column_default, '')
              FROM information_schema.columns
             WHERE table_name = 'txn_header_originals' AND column_name = 'amount'
            """));
        Assert.Equal(["boolean|NO|false"], await ListAsync(cs, """
            SELECT data_type || '|' || is_nullable || '|' || column_default
              FROM information_schema.columns
             WHERE table_name = 'txn_header_originals' AND column_name = 'rounded_at_import'
            """));
        Assert.Equal(originalsBefore, await UpgradeRehearsal.DigestAsync(cs, originalsDigestSql));
        Assert.Equal(["|false", "|false"], await ListAsync(cs,
            "SELECT COALESCE(amount::text, '') || '|' || rounded_at_import FROM txn_header_originals ORDER BY header_id"));

        var viewColumnsAfter = await ColumnsAsync(cs, "resolved_transactions");
        Assert.Equal(viewColumnsBefore, viewColumnsAfter.Take(viewColumnsBefore.Count).ToList());
        Assert.Equal(AppendedViewColumns, viewColumnsAfter.Skip(viewColumnsBefore.Count).ToList());
        Assert.Equal(overridesBefore, overridesAfter);
        // Neither a header with no originals nor one captured before 301 has an amount.
        Assert.Equal(["|false"], await ListAsync(cs, $"""
            SELECT DISTINCT COALESCE(original_amount::text, '') || '|' || amount_rounded_at_import
              FROM resolved_transactions WHERE header_id IN ('{EditedHeader}', '{SeededHeader}', '{PlainHeader}')
            """));
        // The other leg's amount and its account's currency, on a posting with two sides.
        Assert.Equal(["12.3400|JPY"], await ListAsync(cs, $"""
            SELECT counterparty_amount::text || '|' || counterparty_currency_code
              FROM resolved_transactions WHERE header_id = '{EditedHeader}' AND account_id = '{TwoPlaceBank}'
            """));
        Assert.Equal(["security_invoker=true"], await ListAsync(cs,
            "SELECT unnest(reloptions) FROM pg_class WHERE relname = 'resolved_transactions' AND relnamespace = 'public'::regnamespace"));
        Assert.Equal(["SELECT"], await ListAsync(cs,
            "SELECT privilege_type FROM information_schema.role_table_grants WHERE table_name = 'resolved_transactions' AND grantee = 'coffer_app'"));

        // ---- The ingest carriers and the views over txn_headers ---------------
        // The provider's figure as sent: no scale limit on either carrier.
        foreach (var carrier in IngestCarriers)
        {
            Assert.Equal(["numeric||"], await ListAsync(cs, $"""
                SELECT data_type || '|' || COALESCE(numeric_precision::text, '') || '|' || COALESCE(numeric_scale::text, '')
                  FROM information_schema.columns
                 WHERE table_name = 'txn_headers' AND column_name = '{carrier}'
                """));
        }
        // The views dropped to change the type come back as they were: the same
        // columns in the same order, with only the review marker's appended, the RLS
        // boundary, coffer_app's SELECT and the comment.
        foreach (var view in RecreatedHeaderViews)
        {
            Assert.Equal([.. recreatedColumnsBefore[view], .. AppendedTo(view)], await ColumnsAsync(cs, view));
        }
        // An existing header reads no review figure, and the flag cannot claim a match
        // without one.
        Assert.Equal(["|false"], await ListAsync(cs,
            "SELECT DISTINCT COALESCE(review_cleared_amount::text, '') || '|' || review_cleared_by_match FROM txn_headers"));
        var unmatched = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs,
            $"UPDATE txn_headers SET review_cleared_by_match = true WHERE id = '{PlainHeader}'"));
        Assert.Equal("ck_txn_headers_review_cleared_by_match_has_amount", unmatched.ConstraintName);
        foreach (var view in RecreatedHeaderViews.Append("resolved_transactions"))
        {
            Assert.Equal(["security_invoker=true"], await ListAsync(cs,
                $"SELECT unnest(reloptions) FROM pg_class WHERE relname = '{view}' AND relnamespace = 'public'::regnamespace"));
            Assert.Equal(["SELECT"], await ListAsync(cs,
                $"SELECT privilege_type FROM information_schema.role_table_grants WHERE table_name = '{view}' AND grantee = 'coffer_app'"));
        }
        Assert.Equal(walkCommentBefore,
            await ListAsync(cs, "SELECT obj_description('balance_walk_headers'::regclass, 'pg_class')"));
        // A fee and a total finer than 4 places read back whole through the views.
        await ExecAsync(cs, $"UPDATE txn_headers SET ingest_fee = 1.00495, ingest_amount = 250.123456 WHERE id = '{PlainHeader}'");
        Assert.Equal(["1.00495|250.123456"], await ListAsync(cs, $"""
            SELECT DISTINCT ingest_fee::text || '|' || ingest_amount::text
              FROM resolved_transactions WHERE header_id = '{PlainHeader}'
            """));

        // The bank's figure is kept exactly, finer than a leg's NUMERIC(19,4) holds, and
        // the mark cannot be unset.
        await ExecAsync(cs, $"UPDATE txn_header_originals SET amount = -12.3456789 WHERE header_id = '{EditedHeader}'");
        Assert.Equal(["-12.3456789"], await ListAsync(cs,
            $"SELECT original_amount::text FROM resolved_transactions WHERE header_id = '{EditedHeader}' AND account_id = '{TwoPlaceBank}'"));
        var notNull = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs,
            $"UPDATE txn_header_originals SET rounded_at_import = NULL WHERE header_id = '{EditedHeader}'"));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, notNull.SqlState);

        // ---- The two realized-gains functions ---------------------------------
        AssertOnlyTheRoundingChanged("recompute_holdings_cost_basis", recomputeBefore,
            await FunctionDefAsync(cs, "recompute_holdings_cost_basis(uuid, uuid, uuid)"));
        AssertOnlyTheRoundingChanged("realized_gains_walk", walkBefore,
            await FunctionDefAsync(cs, "realized_gains_walk(uuid, uuid)"));

        // ---- Nothing stored changed value ------------------------------------
        await ExecAsync(cs, $"DELETE FROM txn_legs WHERE header_id = '{SeededHeader}' AND posting_index = 1");
        await LegNotesBackfill.RunAsync(cs, Ledger, CancellationToken.None, headerIds: [SeededHeader]);
        Assert.Equal(legsBefore, await UpgradeRehearsal.DigestAsync(cs, legsDigestSql));
    }

    /// <summary>
    /// A realized gain the old writer stored at 2 places on a 0-place account is ordinary
    /// realized_gains drift after the upgrade, and the existing repair derives it again
    /// at 0 places.
    /// </summary>
    [Fact]
    public async Task A_realized_gain_stored_at_two_places_on_a_zero_place_account_is_drift_the_repair_fixes()
    {
        var (cs, work) = InstallBefore301("mig301_gains_drift");
        await SeedAsync(cs);

        UpgradeRehearsal.Apply(cs, work, Mig301);

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

        var before = Assert.Single((await consistency.CheckAsync(Ledger)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.False(before.Healthy);
        Assert.Contains(before.Mismatches, m =>
            m.AccountId == ZeroPlaceHoldings && m.Field == "cost_basis_sold"
            && m.Stored == 1000.50m && m.Expected == 1001m);

        var repaired = await consistency.RepairAsync(Ledger, ConsistencyProjections.RealizedGains);
        var after = Assert.Single(repaired.After!.Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(after.Healthy, string.Join("; ", after.Mismatches.Select(m => $"{m.Scope} {m.Field} {m.Stored} {m.Expected}")));
        Assert.Equal([1001m], await DecimalsAsync(cs,
            $"SELECT cost_basis_sold FROM realized_gains WHERE account_id = '{ZeroPlaceHoldings}'"));
    }

    /// <summary>
    /// The functions' definitions before and after differ only in the lines that round
    /// money: six <c>ROUND(…, 2)</c> lines out, six at the account currency's places in,
    /// and the lines that look those places up. Anything else (the write lock, the lot
    /// retirement, the signature) would show as another line removed.
    /// </summary>
    private static void AssertOnlyTheRoundingChanged(string name, string before, string after)
    {
        var removed = Subtract(Lines(before), Lines(after));
        var added = Subtract(Lines(after), Lines(before));

        var roundAtTwo = new Regex(@"^ROUND\(.+, 2\),?$");
        Assert.True(removed.All(l => roundAtTwo.IsMatch(l)),
            $"{name}: 301 removed more than the 2-place rounding: " + string.Join(" | ", removed));
        Assert.Equal(6, removed.Count);

        Assert.True(added.All(l => l.Length == 0 || l.StartsWith("--", StringComparison.Ordinal)
                                   || l.Contains("v_places", StringComparison.Ordinal)
                                   || l.Contains("currency_minor_units", StringComparison.Ordinal)),
            $"{name}: 301 added more than the places lookup: " + string.Join(" | ", added));
        Assert.Equal(6, added.Count(l => l.StartsWith("ROUND(", StringComparison.Ordinal)));

        static List<string> Lines(string def) =>
            def.Split('\n').Select(l => l.Trim()).ToList();

        static List<string> Subtract(List<string> from, List<string> take)
        {
            var left = new List<string>(from);
            foreach (var line in take) left.Remove(line);
            return left;
        }
    }

    private static async Task<string> FunctionDefAsync(string cs, string signature) =>
        Assert.Single(await ListAsync(cs, $"SELECT pg_get_functiondef('public.{signature}'::regprocedure)"));

    /// <summary>Every realized_gains money figure, keyed by row and column, by value.</summary>
    private static async Task<Dictionary<string, decimal>> RealizedGainsAsync(string cs)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT id::text, {string.Join(", ", WidenedRealizedGains)} FROM realized_gains ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var i = 0; i < WidenedRealizedGains.Length; i++)
                result[$"{reader.GetString(0)}.{WidenedRealizedGains[i]}"] = reader.GetDecimal(i + 1);
        }
        return result;
    }

    private static async Task<List<(string Name, bool Validated, string Definition)>> ConstraintsAsync(
        string cs, string table, string namePattern)
    {
        var result = new List<(string, bool, string)>();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT conname, convalidated, pg_get_constraintdef(oid) FROM pg_constraint "
            + "WHERE conrelid = @t::regclass AND conname LIKE @n ORDER BY conname", connection);
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("n", namePattern);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
        return result;
    }

    private static Task<List<string>> ColumnsAsync(string cs, string relation) => ListAsync(cs,
        $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{relation}' ORDER BY ordinal_position");

    private static async Task<List<decimal>> DecimalsAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<decimal>();
        while (await reader.ReadAsync()) result.Add(reader.GetDecimal(0));
        return result;
    }

    private static async Task<List<string>> ListAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
            result.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        return result;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Sql(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
