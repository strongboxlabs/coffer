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
public sealed class ImportValidatorTests : DatabaseTest
{
    public ImportValidatorTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Passes_clean_db()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        Assert.True(report.AllPassed,
            $"Unexpected failures: {string.Join(", ", report.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}"))}");
        // A passed check has nothing to say, to either reader.
        Assert.All(report.Checks, c => Assert.Null(c.Summary));
    }

    [Fact]
    public async Task Catches_duplicate_account_names_with_null_extid()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);

        // Two accounts, same name, one null-extid → drift signal.
        await InsertAccountAsync(conn, ledgerId, "Duplicate", externalId: "ext-1");
        await InsertAccountAsync(conn, ledgerId, "Duplicate", externalId: null);

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var dup = report.Checks.Single(c => c.Name == "account-name-uniqueness");
        Assert.False(dup.Passed);
        Assert.Contains("Duplicate", dup.Message);
        Assert.Equal(CheckSummaries.DuplicateAccountNames(["Duplicate"], more: false), dup.Summary);
    }

    [Fact]
    public async Task Says_at_least_when_more_duplicate_names_exist_than_it_lists()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        foreach (var name in new[] { "Dup A", "Dup B", "Dup C", "Dup D", "Dup E", "Dup F" })
        {
            await InsertAccountAsync(conn, ledgerId, name, externalId: "ext-" + name);
            await InsertAccountAsync(conn, ledgerId, name, externalId: null);
        }

        var dup = (await new ImportValidator(conn).ValidateAsync(ledgerId))
            .Checks.Single(c => c.Name == "account-name-uniqueness");

        Assert.False(dup.Passed);
        Assert.StartsWith("At least 5 account names are used by more than one account: ", dup.Summary);
    }

    [Fact]
    public async Task Catches_off_catalog_investment_actions()
    {
        // CHECK constraint enforces, but the validator scans as a
        // sanity check. Disable the CHECK to seed.
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);

        await conn.ExecuteAsync("ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_action_check;");
        // Legless, so no originating account (mig 263).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, 'misc_income', NOW(), NULL);",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var actionCheck = report.Checks.Single(c => c.Name == "investment-action-conformance");
        Assert.False(actionCheck.Passed);
        Assert.Equal(CheckSummaries.UnusableActions(["misc_income"], more: false), actionCheck.Summary);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Catches_header_without_legs()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);

        // Header with no legs — and so, correctly, no originating account (mig 263).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, NOW(), NULL);",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var check = report.Checks.Single(c => c.Name == "header-has-legs");
        Assert.False(check.Passed);
        Assert.Equal(CheckSummaries.WithoutLines(1), check.Summary);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Catches_a_header_with_legs_and_no_originating_account_and_passes_a_legless_one()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        await InsertAccountAsync(conn, ledgerId, "Acct A", externalId: "a-1");
        await InsertAccountAsync(conn, ledgerId, "Acct B", externalId: "b-1");

        // Legless with NULL: the one state the column allows NULL for.
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(), NOW(), false, false, NOW(), NULL);",
            new { Id = Guid.NewGuid(), LedgerId = ledgerId });
        var legless = await new ImportValidator(conn).ValidateAsync(ledgerId);
        Assert.True(legless.Checks.Single(c => c.Name == "originating-account").Passed);

        // With legs and still NULL: no account owns it.
        var headerId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(), NOW(), false, false, NOW(), NULL);
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT gen_random_uuid(), @Id, @LedgerId, id, 0,
                   CASE name WHEN 'Acct A' THEN -5 ELSE 5 END
              FROM accounts WHERE ledger_id = @LedgerId AND name IN ('Acct A', 'Acct B');",
            new { Id = headerId, LedgerId = ledgerId });

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);
        var check = report.Checks.Single(c => c.Name == "originating-account");
        Assert.False(check.Passed);
        Assert.Equal(CheckSummaries.WithoutOwner(1), check.Summary);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Passes_every_shape_owned_as_the_originating_account_invariants_say()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var a = await SeedOwnershipAccountsAsync(conn, ledgerId);

        await BuyAsync(conn, ledgerId, a, owner: a.BrokP);
        await InKindTransferAsync(conn, ledgerId, a, owner: a.BrokQ);
        await InKindTransferOnSeparatePostingsAsync(conn, ledgerId, a, owner: a.BrokQ);
        await SplitAsync(conn, ledgerId, a, owner: a.BankA);

        var check = (await new ImportValidator(conn).ValidateAsync(ledgerId))
            .Checks.Single(c => c.Name == "originating-account");
        Assert.True(check.Passed, check.Message);
    }

    [Fact]
    public async Task Catches_an_event_owned_by_its_holdings_account()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var a = await SeedOwnershipAccountsAsync(conn, ledgerId);

        // On a leg, so (a) holds; but a holdings account is not a brokerage.
        await BuyAsync(conn, ledgerId, a, owner: a.HoldP);

        AssertBrokenOnly(await new ImportValidator(conn).ValidateAsync(ledgerId), "(b)");
    }

    [Fact]
    public async Task Catches_an_in_kind_transfer_owned_by_its_destination()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var a = await SeedOwnershipAccountsAsync(conn, ledgerId);

        // The shares leave Q; P receives them.
        await InKindTransferAsync(conn, ledgerId, a, owner: a.BrokP);

        AssertBrokenOnly(await new ImportValidator(conn).ValidateAsync(ledgerId), "(c)");
    }

    [Fact]
    public async Task Catches_a_split_owned_by_an_account_on_one_line_only()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var a = await SeedOwnershipAccountsAsync(conn, ledgerId);

        // The bank is on every line; the first category is on the first only.
        await SplitAsync(conn, ledgerId, a, owner: a.CatX);

        AssertBrokenOnly(await new ImportValidator(conn).ValidateAsync(ledgerId), "(d)");
    }

    private static void AssertBrokenOnly(ImportValidator.ValidationReport report, string clause)
    {
        var check = report.Checks.Single(c => c.Name == "originating-account");
        Assert.False(check.Passed);
        // The message tallies the clauses that broke; only the one under test may.
        Assert.EndsWith($": {clause} 1", check.Message ?? "", StringComparison.Ordinal);
    }

    private sealed record OwnershipAccounts(
        Guid BankA, Guid CatX, Guid CatY, Guid BrokP, Guid HoldP, Guid BrokQ, Guid HoldQ, Guid Security);

    private static async Task<OwnershipAccounts> SeedOwnershipAccountsAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var a = new OwnershipAccounts(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                opening_balance, is_active, is_system, holdings_account_id)
            VALUES (@BankA, @LedgerId, 'Acct A',     'bank',       NULL,      'USD', 0, true, false, NULL),
                   (@CatX,  @LedgerId, 'Category X', 'category',   'expense', 'USD', 0, true, false, NULL),
                   (@CatY,  @LedgerId, 'Category Y', 'category',   'expense', 'USD', 0, true, false, NULL),
                   (@HoldP, @LedgerId, 'Holdings P', 'investment', NULL,      'USD', 0, true, true,  NULL),
                   (@HoldQ, @LedgerId, 'Holdings Q', 'investment', NULL,      'USD', 0, true, true,  NULL),
                   (@BrokP, @LedgerId, 'Brokerage P', 'investment', NULL,     'USD', 0, true, false, @HoldP),
                   (@BrokQ, @LedgerId, 'Brokerage Q', 'investment', NULL,     'USD', 0, true, false, @HoldQ);
            INSERT INTO securities (id, ledger_id, name) VALUES (@Security, @LedgerId, 'Security A');",
            new { LedgerId = ledgerId, a.BankA, a.CatX, a.CatY, a.HoldP, a.HoldQ, a.BrokP, a.BrokQ, a.Security });
        return a;
    }

    /// <summary>A buy: shares into P's holdings against P's cash, one posting.</summary>
    private static async Task BuyAsync(NpgsqlConnection conn, Guid ledgerId, OwnershipAccounts a, Guid owner)
    {
        var header = await HeaderAsync(conn, ledgerId, "buy", owner);
        await LegAsync(conn, ledgerId, header, a.HoldP, 0, 400m, a.Security, 8m, "security");
        await LegAsync(conn, ledgerId, header, a.BrokP, 0, -400m, null, null, "security");
    }

    /// <summary>Q to P, each brokerage's zero-cash leg in the posting of its own holdings.</summary>
    private static async Task InKindTransferAsync(NpgsqlConnection conn, Guid ledgerId, OwnershipAccounts a, Guid owner)
    {
        var header = await HeaderAsync(conn, ledgerId, "transfer_shares", owner);
        await LegAsync(conn, ledgerId, header, a.HoldQ, 0, -500m, a.Security, -10m, "security");
        await LegAsync(conn, ledgerId, header, a.BrokQ, 0, 0m, null, null, "security");
        await LegAsync(conn, ledgerId, header, a.HoldP, 1, 500m, a.Security, 10m, "security");
        await LegAsync(conn, ledgerId, header, a.BrokP, 1, 0m, null, null, "security");
    }

    /// <summary>
    /// Q to P with both brokerage legs on a posting of their own, so no posting pairs a
    /// brokerage with its holdings: only the fallback names Q, whose holdings account
    /// carries the outgoing shares.
    /// </summary>
    private static async Task InKindTransferOnSeparatePostingsAsync(
        NpgsqlConnection conn, Guid ledgerId, OwnershipAccounts a, Guid owner)
    {
        var header = await HeaderAsync(conn, ledgerId, "transfer_shares", owner);
        await LegAsync(conn, ledgerId, header, a.HoldQ, 0, -500m, a.Security, -10m, "security");
        await LegAsync(conn, ledgerId, header, a.HoldP, 0, 500m, a.Security, 10m, "security");
        await LegAsync(conn, ledgerId, header, a.BrokQ, 1, 0m, null, null, "security");
        await LegAsync(conn, ledgerId, header, a.BrokP, 1, 0m, null, null, "security");
    }

    /// <summary>Two lines from the bank, one to each category.</summary>
    private static async Task SplitAsync(NpgsqlConnection conn, Guid ledgerId, OwnershipAccounts a, Guid owner)
    {
        var header = await HeaderAsync(conn, ledgerId, null, owner);
        await LegAsync(conn, ledgerId, header, a.BankA, 0, -6m, null, null, null);
        await LegAsync(conn, ledgerId, header, a.CatX, 0, 6m, null, null, null);
        await LegAsync(conn, ledgerId, header, a.BankA, 1, -4m, null, null, null);
        await LegAsync(conn, ledgerId, header, a.CatY, 1, 4m, null, null, null);
    }

    private static async Task<Guid> HeaderAsync(NpgsqlConnection conn, Guid ledgerId, string? action, Guid owner)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(), NOW(), false, false, @Action, NOW(), @Owner);",
            new { Id = id, LedgerId = ledgerId, Action = action, Owner = owner });
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
        return id;
    }

    private static async Task LegAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid header, Guid account, int posting, decimal amount,
        Guid? security, decimal? quantity, string? role)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                security_id, quantity, unit_price, posting_role)
            VALUES (@Id, @Header, @LedgerId, @Account, @Posting, @Amount,
                @Security, @Quantity, @UnitPrice, @Role);",
            new
            {
                Id = Guid.NewGuid(), Header = header, LedgerId = ledgerId, Account = account,
                Posting = posting, Amount = amount, Security = security, Quantity = quantity,
                UnitPrice = quantity is null ? (decimal?)null : 50m, Role = role,
            });
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Catches_account_count_parity_mismatch()
    {
        await using var conn = Db.OpenConnection();
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
        Assert.Equal(CheckSummaries.AccountCount(inExport: 3, inLedger: 1), parity.Summary);
    }

    [Fact]
    public async Task All_checks_pass_when_only_catalog_actions_present()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);

        foreach (var action in new[] { "buy", "buyx", "sell", "sellx",
                 "dividend_cash", "dividend_reinvest", "divx", "transfer", "misc" })
        {
            // Legless, so no originating account (mig 263).
            await conn.ExecuteAsync(@"
                INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                    is_pending, is_hidden, action, created_at, originating_account_id)
                VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                    false, false, @Action, NOW(), NULL);",
                new { Id = Guid.NewGuid(), LedgerId = ledgerId, Action = action });
        }

        var report = await new ImportValidator(conn).ValidateAsync(ledgerId);

        var actionCheck = report.Checks.Single(c => c.Name == "investment-action-conformance");
        Assert.True(actionCheck.Passed);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
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
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        await InsertAccountAsync(conn, ledgerId, "Acct A", externalId: "a-1");
        await InsertAccountAsync(conn, ledgerId, "Acct B", externalId: "b-1");

        var headerId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, NOW(),
                (SELECT id FROM accounts WHERE ledger_id = @LedgerId AND name = 'Acct A'));",
            new { Id = headerId, LedgerId = ledgerId });
        // Two legs sharing posting_index 0; they sum to the one non-zero leg's
        // amount, not zero (one carries the value, the other is zeroed -- the bug shape).
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
        Assert.Equal(CheckSummaries.Unbalanced(1), balance.Summary);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    // --- a two-amount pair (ADR-0114 D13) ----------------------------------------------
    //
    // A posting of exactly two legs on real accounts whose currencies differ is left out
    // of its header's sum and passes when both amounts are nonzero and opposite in sign.
    // Every other leg is summed per header, as before (ADR-0053).

    [Theory]
    // (a) a dollar account to a euro account, opposite nonzero amounts: passes.
    [InlineData("USD", "bank", -50, "EUR", "bank", 45, true)]
    // (b) the same pair with one sign: fails.
    [InlineData("USD", "bank", -50, "EUR", "bank", -45, false)]
    // (c) the same pair with one side zero: fails, since no zero leg is exempt.
    [InlineData("USD", "bank", -50, "EUR", "bank", 0, false)]
    // (c') both sides zero: fails too, though its lines add up, since the pair lacks the
    // two nonzero amounts; its summary is the same sentence (see CheckSummaries.Unbalanced).
    [InlineData("USD", "bank", 0, "EUR", "bank", 0, false)]
    // (d) a euro account to a category stamped in the reporting currency, mirrored:
    // passes as a summed posting, not as a pair; a mirrored zero line passes too, which a
    // pair could not.
    [InlineData("EUR", "bank", -50, "USD", "category", 50, true)]
    [InlineData("EUR", "bank", 0, "USD", "category", 0, true)]
    // (e) a euro account to a category that does not mirror: fails.
    [InlineData("EUR", "bank", -50, "USD", "category", 45, false)]
    public async Task A_posting_between_currencies_passes_only_as_a_pair_of_two_nonzero_opposite_amounts(
        string fromCurrency, string fromType, int fromAmount,
        string toCurrency, string toType, int toAmount, bool passes)
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var from = await InsertAccountAsync(conn, ledgerId, "From", fromType, fromCurrency);
        var to = await InsertAccountAsync(conn, ledgerId, "To", toType, toCurrency);

        await InsertPostingsAsync(conn, ledgerId, from, [(from, to, fromAmount, toAmount)]);

        var balance = (await new ImportValidator(conn).ValidateAsync(ledgerId))
            .Checks.Single(c => c.Name == "header-balance");
        Assert.Equal(passes, balance.Passed);
        Assert.Equal(passes ? null : CheckSummaries.Unbalanced(1), balance.Summary);
    }

    [Fact]
    public async Task A_header_with_a_pair_and_an_unbalanced_line_in_one_currency_fails()
    {
        // The pair is left out of the sum; the rest of the header must still come to zero.
        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var usd = await InsertAccountAsync(conn, ledgerId, "Dollars", "bank", "USD");
        var eur = await InsertAccountAsync(conn, ledgerId, "Euros", "bank", "EUR");
        var cat = await InsertAccountAsync(conn, ledgerId, "Category", "category", "EUR");

        await InsertPostingsAsync(conn, ledgerId, usd, [(usd, eur, -50, 45), (usd, cat, -10, 9)]);

        var balance = (await new ImportValidator(conn).ValidateAsync(ledgerId))
            .Checks.Single(c => c.Name == "header-balance");
        Assert.False(balance.Passed);
    }

    /// <summary>One manual header owned by <paramref name="home"/>, with a two-leg posting
    /// per entry, numbered in order.</summary>
    private static async Task InsertPostingsAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid home,
        IReadOnlyList<(Guid From, Guid To, int FromAmount, int ToAmount)> postings)
    {
        var headerId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at, originating_account_id)
            VALUES (@Id, @LedgerId, 'manual', NOW(), NOW(), false, false, NOW(), @Home);",
            new { Id = headerId, LedgerId = ledgerId, Home = home });
        for (var i = 0; i < postings.Count; i++)
        {
            var (from, to, fromAmount, toAmount) = postings[i];
            await conn.ExecuteAsync(@"
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES (@A, @HeaderId, @LedgerId, @From, @Index, @FromAmount),
                       (@B, @HeaderId, @LedgerId, @To,   @Index, @ToAmount);",
                new
                {
                    A = Guid.NewGuid(), B = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId,
                    From = from, To = to, Index = i, FromAmount = (decimal)fromAmount, ToAmount = (decimal)toAmount,
                });
        }
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    private static async Task<Guid> InsertAccountAsync(
        NpgsqlConnection conn, Guid ledgerId, string name, string accountType, string currencyCode)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                opening_balance, is_active, is_system, external_id)
            VALUES (@Id, @LedgerId, @Name, @AccountType,
                CASE WHEN @AccountType = 'category' THEN 'expense' END, @CurrencyCode,
                0, true, false, @ExternalId);",
            new { Id = id, LedgerId = ledgerId, Name = name, AccountType = accountType,
                  CurrencyCode = currencyCode, ExternalId = "ext-" + name });
        return id;
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
}
