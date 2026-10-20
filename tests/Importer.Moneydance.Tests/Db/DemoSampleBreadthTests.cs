using System.IO;
using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The demo export has to be able to answer the questions a ledger is for.
/// </summary>
/// <remarks>
/// <para>Until FU-009 it could not. The sample was 23 transactions across ten
/// days, every one of them uncleared, and only one of them touching a bank
/// account — so it exercised neither reconciliation nor anything a spending or
/// cash-flow report reads. A reports slice tested against it would have passed
/// while being wrong for every real ledger, which is the failure this file
/// exists to make impossible.</para>
/// <para>These are deliberately FLOOR assertions on breadth, not exact counts.
/// The history is generated (<c>data/samples/moneydance-export-demo.gen.mjs</c>)
/// and tuning it should not break a test; emptying it should. An exact count
/// would be rewritten on every regeneration until someone stopped reading it.
/// </para>
/// <para>The companion <see cref="DemoSampleImportTests"/> guards the other
/// direction — that the action set stays closed over ADR-0027's nine. Breadth
/// here must never be bought by widening that.</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class DemoSampleBreadthTests
{
    private readonly PostgresFixture _fixture;

    public DemoSampleBreadthTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Demo_sample_can_carry_a_report_and_a_reconcile()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ImportAsync(conn);

        // --- a time axis -----------------------------------------------------
        // A monthly cash-flow report needs months to draw. The base sample had
        // one: every transaction fell inside a single ten-day window.
        var months = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT date_trunc('month', posted_at AT TIME ZONE 'UTC'))
            FROM txn_headers WHERE ledger_id = @LedgerId;
            """, new { LedgerId = ledgerId });
        Assert.True(months >= 30, $"expected >= 30 distinct months of history, found {months}");

        // --- something to aggregate ------------------------------------------
        var headers = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_headers WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(headers >= 500, $"expected >= 500 transactions, found {headers}");

        // --- spread across the taxonomy --------------------------------------
        // A spending report over three categories is a list. These counts are
        // what make a top-N or a category breakdown mean anything.
        var expenseCats = await DistinctCategoriesAsync(conn, ledgerId, "expense");
        var incomeCats = await DistinctCategoriesAsync(conn, ledgerId, "income");
        Assert.True(expenseCats >= 15, $"expected >= 15 distinct expense categories used, found {expenseCats}");
        Assert.True(incomeCats >= 2, $"expected >= 2 distinct income categories used, found {incomeCats}");

        // --- both reconciliation states --------------------------------------
        // ADR-0082 models this as a per-leg overlay where an ABSENT row means
        // uncleared, so "has cleared legs" and "has uncleared legs" are two
        // different queries and a sample needs both to be non-zero. All-cleared
        // would be as useless as the all-uncleared file this replaced.
        var clearedLegs = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_leg_recon r
            JOIN txn_legs l ON l.id = r.leg_id
            WHERE l.ledger_id = @LedgerId AND r.status = 'cleared';
            """, new { LedgerId = ledgerId });
        var unclearedLegs = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            WHERE l.ledger_id = @LedgerId
              AND a.account_type <> 'category'
              AND NOT EXISTS (SELECT 1 FROM txn_leg_recon r WHERE r.leg_id = l.id);
            """, new { LedgerId = ledgerId });
        Assert.True(clearedLegs > 0, "no cleared legs — nothing to reconcile against");
        Assert.True(unclearedLegs > 0, "every leg is cleared — nothing left to reconcile");

        // --- a real split ----------------------------------------------------
        // The paycheck: gross pay against five withholdings. One posting index
        // per split, so this counts indexes rather than legs (each split emits
        // an origin leg and a counterpart leg).
        var widestSplit = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(DISTINCT posting_index) AS c
                FROM txn_legs WHERE ledger_id = @LedgerId GROUP BY header_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestSplit >= 5, $"expected a transaction split >= 5 ways, widest was {widestSplit}");

        // --- a check number --------------------------------------------------
        var withCheck = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_headers WHERE ledger_id = @LedgerId AND check_number IS NOT NULL;",
            new { LedgerId = ledgerId });
        Assert.True(withCheck > 0, "no check numbers — the column is never exercised");

        // --- a ledger, not a chequebook --------------------------------------
        // Net worth needs an account whose balance is not a bank feed. Without
        // an asset and a liability the report renders and says nothing, and the
        // mortgage payment — interest against principal — is the everyday
        // transaction most often modelled as a flat expense.
        foreach (var type in new[] { "asset", "liability" })
        {
            var n = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM accounts WHERE ledger_id = @LedgerId AND account_type = @Type;",
                new { LedgerId = ledgerId, Type = type });
            Assert.True(n > 0, $"no {type} account — net worth has no shape to report");
        }

        // --- the brokerage has a history, not a moment -----------------------
        // This was the gap the first FU-009 pass left behind: 36 months of
        // groceries beside an investment account that existed for ten days.
        // Returns, performance and investment income all need a time axis.
        var investmentMonths = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(DISTINCT date_trunc('month', h.posted_at AT TIME ZONE 'UTC'))
              FROM txn_headers h
              WHERE h.ledger_id = @LedgerId
                AND EXISTS (SELECT 1 FROM txn_legs l
                            WHERE l.header_id = h.id AND l.security_id IS NOT NULL);",
            new { LedgerId = ledgerId });
        Assert.True(investmentMonths >= 12,
            $"expected >= 12 months carrying investment activity, found {investmentMonths}");

        // --- a refund, posted negative to an EXPENSE category ---------------
        // Dimension 7 of the fixture audit. Every other expense in the sample
        // is positive, which makes "expenses are positive" look like a rule
        // rather than the usual case — and it was stated as one in review. A
        // report that totalled magnitudes instead of netting would pass every
        // other assertion here.
        var refunds = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM txn_legs l
              JOIN accounts a ON a.id = l.account_id
              WHERE l.ledger_id = @LedgerId
                AND a.account_type = 'category' AND a.category_kind = 'expense'
                AND l.amount < 0;",
            new { LedgerId = ledgerId });
        Assert.True(refunds > 0,
            "no negative posting to an expense category — the sample cannot show a refund");

        // --- tags, at BOTH levels --------------------------------------------
        // ADR-0103 put tags on headers and on postings and gave them different
        // meanings. A sample with tags on only one of them cannot test the
        // distinction, and ReportGroupBy.Tag has to resolve exactly that
        // question before Tag Summary can be built.
        var headerTags = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_header_tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        var legTags = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_leg_tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(headerTags > 0, "no header-level tags");
        Assert.True(legTags > 0, "no posting-level tags — ADR-0103's distinction is untestable");

        // --- and a row that is tagged HEAVILY --------------------------------
        // Dimension 25. ADR-0103's TagLine exists for one case and says so:
        // "chips are fine at two tags and fall apart at eight ... names run to
        // 50 characters, so that is the normal case here, not an outlier."
        //
        // Measured before this assertion was written, the sample had 95 tagged
        // fields and every one carried exactly ONE tag, longest name 16
        // characters. Every tag test in the suite therefore ran against the
        // shape the control was NOT built for, and the row-height blow-up that
        // motivated it could not be reproduced from any fixture.
        //
        // Floors, like everything else here: eight is where the ADR says chips
        // failed, and 48 is near enough 50 to catch a regeneration that quietly
        // shortens the names. The two levels are checked SEPARATELY — folding
        // leg tags up into the header is the exact bug ADR-0103 removed, and a
        // MAX over the union would pass with one of them empty.
        var widestHeaderTagging = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c FROM txn_header_tags
                WHERE ledger_id = @LedgerId GROUP BY header_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestHeaderTagging >= 8,
            $"no header carries 8+ tags — the case TagLine exists for; widest was {widestHeaderTagging}");

        var widestLegTagging = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c FROM txn_leg_tags
                WHERE ledger_id = @LedgerId GROUP BY leg_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestLegTagging >= 8,
            $"no posting carries 8+ tags — only the header level has the wide case; widest was {widestLegTagging}");

        var longestTagName = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(LENGTH(name)), 0) FROM tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(longestTagName >= 48,
            $"longest tag name is {longestTagName} characters — the width TagLine truncates is untested");

        // --- a LOAN, with the terms behind it --------------------------------
        // Dimensions 1 and 40, which are one gap. No fixture carried a
        // Moneydance loan account (`type='o'`), so `loan_terms` — all of
        // ADR-0050 on the import side — had never been written by a test.
        //
        // The row, not just the account: `LoanMapper` returns null and the
        // importer silently seeds NOTHING when principal, rate, payment count
        // or payments-per-year is missing, so asserting the account alone would
        // pass with the mapper never having produced anything.
        var loanAccounts = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @LedgerId AND account_type = 'loan';",
            new { LedgerId = ledgerId });
        Assert.True(loanAccounts > 0, "no loan account — loan_terms has no path into the fixture");

        var terms = await conn.QuerySingleOrDefaultAsync<(
            decimal OriginalPrincipal,
            decimal AnnualInterestRate,
            int PaymentCount,
            int PaymentsPerYear,
            bool PaymentIsComputed,
            decimal? FixedPayment,
            Guid? InterestAccountId,
            DateTime? FirstPaymentDate)?>(
            """
            SELECT original_principal, annual_interest_rate, payment_count,
                   payments_per_year, payment_is_computed, fixed_payment,
                   interest_account_id, first_payment_date
            FROM loan_terms WHERE ledger_id = @LedgerId;
            """, new { LedgerId = ledgerId });
        Assert.NotNull(terms);

        Assert.True(terms!.Value.OriginalPrincipal > 0);
        Assert.True(terms.Value.AnnualInterestRate > 0,
            "a 0% loan would not exercise the interest leg");
        Assert.True(terms.Value.PaymentCount > 0 && terms.Value.PaymentsPerYear > 0);

        // The branch worth having a fixture for: MD sends `monthly_pmt` even
        // when `calc_pmt` says to amortize, and LoanMapper must then store
        // NULL rather than the value it was handed. Nothing else in the suite
        // ever ran that line.
        Assert.True(terms.Value.PaymentIsComputed, "expected an amortizing loan");
        Assert.Null(terms.Value.FixedPayment);

        // The interest category resolved from an MD uuid to a Coffer id. Left
        // null when the cross-walk misses, which is a silent failure.
        Assert.NotNull(terms.Value.InterestAccountId);
        Assert.NotNull(terms.Value.FirstPaymentDate);

        // --- a tax date that is not the posted date -------------------------
        // Dimension 17. The generator set `td` equal to `dt` on every row, so
        // `transacted_at` was populated-but-identical everywhere and FU-018's
        // open half — tax-year grouping on transactedAt — had no data that
        // could distinguish it from grouping on posted_at.
        //
        // The YEAR-CROSSING count is the one that matters. A differing tax date
        // inside one month proves the column round-trips; only one that crosses
        // a period boundary can make two reports disagree.
        var taxDateDiffers = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId
              AND (posted_at AT TIME ZONE 'UTC')::date <> transacted_at::date;
            """, new { LedgerId = ledgerId });
        Assert.True(taxDateDiffers > 0, "every row's tax date equals its posted date");

        var taxYearCrossing = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId
              AND EXTRACT(YEAR FROM posted_at AT TIME ZONE 'UTC')
                  <> EXTRACT(YEAR FROM transacted_at);
            """, new { LedgerId = ledgerId });
        Assert.True(taxYearCrossing > 0,
            "no transaction whose tax year differs from its posted year — the only shape "
            + "that can make a tax-year report disagree with a calendar-year one");

        // --- recurring reminders --------------------------------------------
        // Dimension 15. The import path (ADR-0047) and an integration test both
        // existed, but no FIXTURE carried a reminder — so the demo a new install
        // provisions arrived with an empty Reminders screen and the pipeline's
        // reminder step never ran over the sample.
        //
        // Two rrule branches, chosen because ReminderImportIntegrationTests
        // covers the weekly one and MD's precedence (daily → monthly → weekly →
        // yearly) means each needs the earlier fields clear to be reached at all.
        var rrules = (await conn.QueryAsync<string?>(
            "SELECT rrule FROM recurring_transactions WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId })).ToList();
        Assert.True(rrules.Count >= 2, $"expected >= 2 reminders, found {rrules.Count}");
        Assert.Contains(rrules, r => r is not null && r.StartsWith("FREQ=MONTHLY", StringComparison.Ordinal));
        Assert.Contains(rrules, r => r is not null && r.StartsWith("FREQ=YEARLY", StringComparison.Ordinal));

        // Both halves of auto-commit. MD writes -1 for "manual", which must
        // become NULL rather than being stored as a negative day count.
        var autoCommits = (await conn.QueryAsync<int?>(
            "SELECT auto_commit_days_before FROM recurring_transactions WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId })).ToList();
        Assert.Contains(autoCommits, a => a is not null and >= 0);
        Assert.Contains(autoCommits, a => a is null);

        // A template is NOT a transaction. The keystone of the balance
        // invariant is that it never reaches a balance row, and a fixture that
        // carries reminders is the only way the demo's own balances prove it.
        var templates = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId AND is_recurring_template;
            """, new { LedgerId = ledgerId });
        Assert.Equal(rrules.Count, templates);

        // --- a zero-amount posting the USER entered --------------------------
        // Dimension 11. The sample already had zero-amount legs and the audit scored
        // it "~" on that basis, but every one was an investment security leg,
        // where zero cash is structural. This looks for the ordinary case on a
        // real account: a waived fee, entered so the waiver is on the record.
        //
        // Excludes category legs AND security legs, because either would let
        // the investment rows satisfy it and the assertion would go hollow
        // without failing.
        var zeroOnARealAccount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            WHERE l.ledger_id = @LedgerId
              AND l.amount = 0
              AND l.security_id IS NULL
              AND a.account_type NOT IN ('category', 'investment');
            """, new { LedgerId = ledgerId });
        Assert.True(zeroOnARealAccount > 0,
            "no zero-amount posting on a real account — the only 0.00 legs are investment "
            + "security legs, where zero cash is structural rather than a figure anyone entered");

        // --- an account nobody has transacted in -----------------------------
        // Dimension 4, and the audit had it as a GAP for the demo. Measured,
        // that was wrong: the house, an asset account, has carried a six-figure
        // opening balance and zero transactions since the sample grew a net
        // worth to report. The fixture had the case all along; nothing
        // asserted it, which is a different problem with the same symptom.
        //
        // Non-zero opening balance is the part that bites. An untouched
        // account at 0 proves nothing — every balance path returns 0 for it
        // whether or not it consults the opening balance.
        var untouchedWithBalance = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM accounts a
            WHERE a.ledger_id = @LedgerId
              AND a.account_type NOT IN ('category')
              AND a.opening_balance <> 0
              AND NOT EXISTS (SELECT 1 FROM txn_legs l WHERE l.account_id = a.id);
            """, new { LedgerId = ledgerId });
        Assert.True(untouchedWithBalance > 0,
            "every account with an opening balance has been transacted — nothing exercises "
            + "the path where a balance can only come from the opening figure");

        // --- the feed's own strings, for FU-024 ------------------------------
        // The importer uses ol.orig-payee / ol.orig-memo only as a fallback when
        // the description is empty, so they do not reach a column yet — that IS
        // FU-024. They have to survive into provider_raw_payload regardless, or
        // the work has nothing to be done against: mig 109 keeps the raw row
        // precisely so a later classifier is a SQL migration and not a re-import.
        var withOrigPayee = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId
              AND provider_raw_payload ? 'ol.orig-payee';
            """, new { LedgerId = ledgerId });
        Assert.True(withOrigPayee >= 100,
            $"expected >= 100 headers carrying the feed's own payee string, found {withOrigPayee}");
    }

    private static Task<int> DistinctCategoriesAsync(
        Npgsql.NpgsqlConnection conn, Guid ledgerId, string kind) =>
        conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT a.id)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            WHERE l.ledger_id = @LedgerId
              AND a.account_type = 'category' AND a.category_kind = @Kind;
            """, new { LedgerId = ledgerId, Kind = kind });

    private static async Task<Guid> ImportAsync(Npgsql.NpgsqlConnection conn)
    {
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM ledgers WHERE id = @Id;", new { Id = TestLedger.Id });
        if (existing is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO ledgers (id, name) VALUES (@Id, 'Default');",
                new { Id = TestLedger.Id });
        }

        var export = MdItemReader.ReadFile(LocateSampleFile());
        var context = new ImportContext(export, TestLedger.Id);
        const string importSource = "test:moneydance-export-demo.json";

        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await SecuritySplitImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, importSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, importSource);
        // Added when the sample grew reminders (dimension 15). Without it the
        // fixture would carry them and this test would silently not look.
        await ReminderImportStep.RunAsync(conn, context, importSource);
        return TestLedger.Id;
    }

    private static string LocateSampleFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "data", "samples", "moneydance-export-demo.json");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate data/samples/moneydance-export-demo.json from " + AppContext.BaseDirectory);
    }

    private static Task ResetAsync(Npgsql.NpgsqlConnection conn) =>
        conn.ExecuteAsync("""
            TRUNCATE recurring_transactions, loan_terms, security_splits, lots,
                     holdings, txn_legs, txn_headers, security_prices, securities,
                     accounts
                     RESTART IDENTITY CASCADE;
            """);
}
