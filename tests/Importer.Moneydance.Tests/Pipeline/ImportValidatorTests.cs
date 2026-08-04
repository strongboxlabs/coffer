using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Pipeline;
using Coffer.Importer.Moneydance.Tests.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Pipeline;

/// <summary>
/// DB-level tests for the beefed <see cref="ImportValidator"/>.
/// Verifies each check fires when the invariant it guards is violated
/// and passes when it isn't.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class ImportValidatorTests
{
    private readonly PostgresFixture _fixture;

    public ImportValidatorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Passes_clean_db()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        Assert.True(report.AllPassed,
            $"Unexpected failures: {string.Join(", ", report.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}"))}");
    }

    [Fact]
    public async Task Catches_duplicate_account_names_with_null_extid()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        // Two accounts, same name, one null-extid → drift signal.
        await InsertAccountAsync(conn, ledgerId, "Duplicate", externalId: "ext-1");
        await InsertAccountAsync(conn, ledgerId, "Duplicate", externalId: null);

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var dup = report.Checks.Single(c => c.Name == "account-name-uniqueness");
        Assert.False(dup.Passed);
        Assert.Contains("Duplicate", dup.Message);
    }

    [Fact]
    public async Task Catches_off_catalog_investment_actions()
    {
        // CHECK constraint enforces, but the validator scans as a
        // sanity check. Disable the CHECK to seed.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        await conn.ExecuteAsync("ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_action_check;");
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at,
                is_pending, is_hidden, action, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),
                false, false, 'misc_income', NOW());",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var actionCheck = report.Checks.Single(c => c.Name == "investment-action-conformance");
        Assert.False(actionCheck.Passed);
    }

    [Fact]
    public async Task Catches_header_without_legs()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        // Header with no legs.
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at,
                is_pending, is_hidden, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),
                false, false, NOW());",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var check = report.Checks.Single(c => c.Name == "header-has-legs");
        Assert.False(check.Passed);
    }

    [Fact]
    public async Task Catches_account_count_parity_mismatch()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        await InsertAccountAsync(conn, ledgerId, "Bank A", externalId: "ext-a");
        // Insert junction row so it counts as 'moneydance' source.
        await conn.ExecuteAsync(@"
            INSERT INTO account_external_ids (account_id, ledger_id, source, external_id)
            SELECT id, ledger_id, 'moneydance', external_id FROM accounts WHERE external_id = 'ext-a';");

        // We claim MD had 3 accounts; DB has 1.
        var report = await new ImportValidator(conn).ValidateAsync(ledgerId, expectedMdAccountCount: 3);

        var parity = report.Checks.Single(c => c.Name == "account-count-parity");
        Assert.False(parity.Passed);
        Assert.Contains("MD export had 3", parity.Message ?? "");
    }

    [Fact]
    public async Task All_checks_pass_when_only_catalog_actions_present()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);

        foreach (var action in new[] { "buy", "buyx", "sell", "sellx",
                 "dividend_cash", "dividend_reinvest", "divx", "transfer", "misc" })
        {
            await conn.ExecuteAsync(@"
                INSERT INTO txn_headers (id, ledger_id, origin, posted_at,
                    is_pending, is_hidden, action, created_at)
                VALUES (@Id, @LedgerId, 'manual', NOW(),
                    false, false, @Action, NOW());",
                new { Id = Guid.NewGuid(), LedgerId = ledgerId, Action = action });
        }

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var actionCheck = report.Checks.Single(c => c.Name == "investment-action-conformance");
        Assert.True(actionCheck.Passed);
    }

    [Fact]
    public async Task Catches_header_whose_legs_do_not_sum_to_zero()
    {
        // Regression guard for ADR-0053: a header whose legs don't sum to
        // zero must FAIL the balance check. The prior validator EXEMPTED any
        // posting that contained a zero-amount leg -- exactly the shape the
        // self-ref buysellxfr bug produced (the sell proceeds zeroed). A
        // non-investment header (action NULL) is used so the legs carry NULL
        // posting_role and the mig-057 trigger is satisfied; the balance
        // check is action-agnostic.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ledgerId = await ProvisionLedgerAsync(conn);
        await InsertAccountAsync(conn, ledgerId, "Acct A", externalId: "a-1");
        await InsertAccountAsync(conn, ledgerId, "Acct B", externalId: "b-1");

        var headerId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at,
                is_pending, is_hidden, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),
                false, false, NOW());",
            new { Id = headerId, LedgerId = ledgerId });
        // Two legs sharing posting_index 0; they sum to +100, not zero
        // (one carries the value, the other is zeroed -- the bug shape).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT @Id, @HeaderId, @LedgerId, id, 0, 100 FROM accounts
             WHERE ledger_id = @LedgerId AND name = 'Acct A';",
            new { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId });
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT @Id, @HeaderId, @LedgerId, id, 0, 0 FROM accounts
             WHERE ledger_id = @LedgerId AND name = 'Acct B';",
            new { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var balance = report.Checks.Single(c => c.Name == "header-balance");
        Assert.False(balance.Passed);
        Assert.Contains("do not sum to zero", balance.Message ?? "");
    }

    private static async Task<Guid> ProvisionLedgerAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO ledgers (id, name) VALUES (@Id, 'Default')
            ON CONFLICT (id) DO NOTHING;",
            new { Id = TestLedger.Id });
        return TestLedger.Id;
    }

    private static async Task InsertAccountAsync(
        NpgsqlConnection conn, Guid ledgerId, string name, string? externalId)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                opening_balance, is_active, is_system, external_id)
            VALUES (@Id, @LedgerId, @Name, 'bank', 'USD', 0, true, false, @ExternalId);",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId, Name = name, ExternalId = externalId });
    }

    private static async Task ResetAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            TRUNCATE security_splits, lots, holdings, txn_legs, txn_headers,
                     security_prices, securities,
                     account_external_ids, accounts RESTART IDENTITY CASCADE;");
    }
}
