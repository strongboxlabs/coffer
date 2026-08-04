using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Integration tests for the ADR-0022 header + legs bulk-insert path.
/// Each MD txn becomes one <c>txn_headers</c> row + N postings of two
/// <c>txn_legs</c> each. Seed-once (ADR-0052 D2): the importer only ever
/// seeds an EMPTY ledger, so the write is a plain insert and the
/// proposed → persisted id maps are identity.
/// </summary>
/// <remarks>
/// Running-balance correctness is now owned by the header-walk trigger
/// family (ADR-0034 / mig 090) and asserted against
/// <c>txn_header_account_balances</c>. Per-header-walk assertions live
/// in the API integration tests; this fixture's focus is the insert
/// path itself.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class TransactionsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public TransactionsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static AccountRow Bank(string externalId, string name) =>
        new(Id: Guid.NewGuid(), LedgerId: TestLedger.Id,
            ParentId: null, Name: name, AccountType: "bank",
            CategoryKind: null, CurrencyCode: "USD", OpeningBalance: 0m,
            IsActive: true, ExternalId: externalId,
            IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null);

    private static AccountRow Category(string externalId, string kind, string name) =>
        new(Id: Guid.NewGuid(), LedgerId: TestLedger.Id,
            ParentId: null, Name: name, AccountType: "category",
            CategoryKind: kind, CurrencyCode: "USD", OpeningBalance: 0m,
            IsActive: true, ExternalId: externalId,
            IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null);

    /// <summary>
    /// Build a (header, [origin leg, counterpart leg]) tuple representing
    /// one Moneydance split under ADR-0022 — one header per event, two
    /// legs sharing posting_index = 0.
    /// </summary>
    private static (TxnHeaderRow Header, TxnLegRow Origin, TxnLegRow Counterpart) MakeEvent(
        Guid bankId,
        Guid categoryId,
        string externalId,
        decimal originAmount,
        DateTimeOffset posted,
        string payee = "p")
    {
        var headerId = Guid.NewGuid();
        var header = new TxnHeaderRow(
            Id: headerId, LedgerId: TestLedger.Id,
            Origin: "manual", ExternalId: externalId,
            Payee: payee, Memo: null,
            PostedAt: posted, TransactedAt: null,
            // Status is the normalized 3-state vocabulary post-migration 030;
            // the mapper translates MD's raw "X" letter-code to "cleared"
            // before constructing this row — see TransactionMapper.NormalizeMdStatus.
            // The DB CHECK requires cleared_at to be set whenever status='cleared',
            // so we stamp it with posted_at as the importer-side default.
            Status: "cleared", CheckNumber: null,
            IsPending: false, IsHidden: false,
            IsMergedInto: null, ImportSource: "test",
            ClearedAt: posted, ClearedByUserId: null,
            OnlineMatchFitid: null, OnlineMatchFiId: null,
            Action: null);

        var origin = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: bankId,
            PostingIndex: 0, LegMemo: null, Amount: originAmount,
            SecurityId: null, Quantity: null, UnitPrice: null);

        var counterpart = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: categoryId,
            PostingIndex: 0, LegMemo: null, Amount: -originAmount,
            SecurityId: null, Quantity: null, UnitPrice: null);

        return (header, origin, counterpart);
    }

    [Fact]
    public async Task BulkUpsert_writes_header_and_legs()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var accountsRepo = new AccountsRepository(connection);
        var bankId = await accountsRepo.UpsertByExternalIdAsync(Bank("md-bank", "Checking"));
        var groceryId = await accountsRepo.UpsertByExternalIdAsync(Category("md-cat", "expense", "Groceries"));

        var posted = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var (header, origin, counterpart) = MakeEvent(
            bankId, groceryId, "md-txn-1", originAmount: -42.50m, posted, payee: "Whole Foods");

        var txnsRepo = new TransactionsRepository(connection);
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await txnsRepo.BulkUpsertAsync(
                new[] { header },
                new[] { origin, counterpart });
            await tx.CommitAsync();
        }

        Assert.Equal(1, await txnsRepo.CountHeadersAsync());
        Assert.Equal(2, await txnsRepo.CountLegsAsync());

        var persistedPayee = await connection.ExecuteScalarAsync<string>(
            "SELECT payee FROM txn_headers WHERE id = @id;", new { id = header.Id });
        Assert.Equal("Whole Foods", persistedPayee);

        var legAmounts = (await connection.QueryAsync<decimal>(
            "SELECT amount FROM txn_legs WHERE header_id = @id ORDER BY account_id;",
            new { id = header.Id })).ToList();
        Assert.Equal(2, legAmounts.Count);
        Assert.Contains(-42.50m, legAmounts);
        Assert.Contains(42.50m,  legAmounts);
    }

    [Fact]
    public async Task BulkUpsert_writes_reconciliation_per_leg_not_fanned_across_accounts()
    {
        // ADR-0082: a transfer cleared in one account must stay uncleared in the
        // other. The bank importer supplies per-leg recon seeds; the persist
        // must write ONLY the seeded legs (not fan a single status across every
        // leg of the header).
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var accountsRepo = new AccountsRepository(connection);
        var checkingId = await accountsRepo.UpsertByExternalIdAsync(Bank("md-chk", "Checking"));
        var savingsId  = await accountsRepo.UpsertByExternalIdAsync(Bank("md-sav", "Savings"));

        var posted = new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
        // Transfer Checking -> Savings; both are real accounts (counterpart is a
        // bank, not a category), so both legs are eligible for reconciliation.
        var (header, origin, counterpart) = MakeEvent(
            checkingId, savingsId, "md-xfer", originAmount: -100m, posted, payee: "Move to savings");

        // Cleared on the Checking (origin) side only. The Savings (counterpart)
        // leg gets no seed and must read uncleared (absent overlay row). Note
        // the header still carries Status='cleared' from MakeEvent — the per-leg
        // path must ignore it and honour the seeds instead.
        var legRecons = new[] { new LegReconSeed(origin.Id, "cleared", posted) };

        var txnsRepo = new TransactionsRepository(connection);
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await txnsRepo.BulkUpsertAsync(
                new[] { header }, new[] { origin, counterpart }, legRecons: legRecons);
            await tx.CommitAsync();
        }

        var checkingStatus = await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM txn_leg_recon WHERE leg_id = @id;", new { id = origin.Id });
        Assert.Equal("cleared", checkingStatus);

        var savingsRows = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM txn_leg_recon WHERE leg_id = @id;", new { id = counterpart.Id });
        Assert.Equal(0, savingsRows);
    }

    [Fact]
    public async Task BulkUpsert_with_empty_legRecons_writes_no_recon_even_when_header_cleared()
    {
        // A non-null but EMPTY legRecons is authoritative (the caller computed
        // per-leg status and there was none) — the persist must NOT fall back to
        // the header-fan, even though the header carries Status='cleared'. Guards
        // the investment case where a sec-split cleared under an uncleared parent
        // yields no per-leg seeds and would otherwise re-flatten via the header.
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var accountsRepo = new AccountsRepository(connection);
        var bankId = await accountsRepo.UpsertByExternalIdAsync(Bank("md-e-bank", "Checking"));
        var catId  = await accountsRepo.UpsertByExternalIdAsync(Category("md-e-cat", "expense", "Groceries"));

        var posted = new DateTimeOffset(2024, 4, 1, 12, 0, 0, TimeSpan.Zero);
        // MakeEvent stamps header.Status = "cleared".
        var (header, origin, counterpart) = MakeEvent(bankId, catId, "md-e-1", -25m, posted);

        var txnsRepo = new TransactionsRepository(connection);
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await txnsRepo.BulkUpsertAsync(
                new[] { header }, new[] { origin, counterpart },
                legRecons: Array.Empty<LegReconSeed>());
            await tx.CommitAsync();
        }

        var reconRows = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM txn_leg_recon WHERE ledger_id = @lid;",
            new { lid = TestLedger.Id });
        Assert.Equal(0, reconRows);
    }

    [Fact]
    public async Task BulkUpsert_respects_caller_rollback()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetAsync(connection);

        var accountsRepo = new AccountsRepository(connection);
        var bankId = await accountsRepo.UpsertByExternalIdAsync(
            Bank("md-rb-bank", "Checking") with { OpeningBalance = 0m });
        var catId = await accountsRepo.UpsertByExternalIdAsync(
            Category("md-rb-cat", "expense", "Food"));

        var txnsRepo = new TransactionsRepository(connection);
        var beforeHeaders = await txnsRepo.CountHeadersAsync();
        var beforeLegs    = await txnsRepo.CountLegsAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var posted = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
            var headers = new List<TxnHeaderRow>();
            var legs    = new List<TxnLegRow>();
            for (var i = 0; i < 5; i++)
            {
                var (h, o, c) = MakeEvent(bankId, catId, $"rb-{i}", -10m, posted.AddDays(i));
                headers.Add(h);
                legs.Add(o);
                legs.Add(c);
            }

            await txnsRepo.BulkUpsertAsync(headers, legs);

            // Mid-transaction visibility on the same connection.
            Assert.Equal(beforeHeaders + 5,  await txnsRepo.CountHeadersAsync());
            Assert.Equal(beforeLegs    + 10, await txnsRepo.CountLegsAsync());

            await transaction.RollbackAsync();
        }

        Assert.Equal(beforeHeaders, await txnsRepo.CountHeadersAsync());
        Assert.Equal(beforeLegs,    await txnsRepo.CountLegsAsync());
    }

    private static async Task ResetAsync(Npgsql.NpgsqlConnection connection)
    {
        await connection.ExecuteAsync(
            "TRUNCATE account_external_ids, accounts, txn_headers, txn_legs CASCADE;");
    }
}
