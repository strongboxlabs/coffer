using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Integration tests for <see cref="PruneImportBatch"/> — the surgical
/// removal of a re-import batch (ADR-0052). Proves the selection + twin
/// classification (<see cref="PruneImportBatch.PlanAsync"/>) and the
/// delete + cascade + balance recompute (<see cref="PruneImportBatch.ApplyAsync"/>)
/// against the real migrated schema: templates are kept, pre-batch rows are
/// preserved, deleted headers take their legs with them, and the affected
/// account's running balance is re-derived (inflated-by-dup → corrected).
/// </summary>
[Collection(DbCollection.Name)]
public sealed class PruneImportBatchTests
{
    private readonly PostgresFixture _fixture;

    public PruneImportBatchTests(PostgresFixture fixture) => _fixture = fixture;

    // A re-import stamps one created_at for the whole batch; the seed predates it.
    private static readonly DateTimeOffset PreBatchTs = new(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BatchTs    = new(2026, 6, 15, 11, 45, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowFrom = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowTo   = new(2026, 6, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Plan_then_apply_removes_batch_keeps_template_preserves_prior_and_fixes_balance()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var accountsRepo = new AccountsRepository(conn);
        var checking = await accountsRepo.UpsertByExternalIdAsync(Bank("md-chk", "Checking"));
        var grocery  = await accountsRepo.UpsertByExternalIdAsync(Category("md-cat", "expense", "Groceries"));
        // Template posts to its own accounts so it can never perturb the
        // Checking-balance assertion regardless of how the recompute treats
        // is_recurring_template rows.
        var savings  = await accountsRepo.UpsertByExternalIdAsync(Bank("md-sav", "Savings"));
        var rent     = await accountsRepo.UpsertByExternalIdAsync(Category("md-rent", "expense", "Rent"));

        var may31 = new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);
        var jun02 = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);

        // PRE-BATCH original (the twin) — the reconciled row that must survive.
        var pre  = MakeEvent(checking, grocery, "seed-1",        -100m, may31, "Acme");
        // BATCH: a re-keyed duplicate of `pre` (same account+amount+date), a
        // unique row with no counterpart, and a recurring template.
        var dup  = MakeEvent(checking, grocery, "reimport-dup",  -100m, may31, "Acme");
        var uniq = MakeEvent(checking, grocery, "reimport-uniq",  -55m, jun02, "NewCo");
        var tmpl = MakeEvent(savings,  rent,    "mdreminder:r1", -200m, may31, "Rent");

        await InsertEventAsync(conn, pre,  PreBatchTs, isTemplate: false);
        await InsertEventAsync(conn, dup,  BatchTs,    isTemplate: false);
        await InsertEventAsync(conn, uniq, BatchTs,    isTemplate: false);
        await InsertEventAsync(conn, tmpl, BatchTs,    isTemplate: true);

        // Baseline: with the duplicate present, Checking's running balance is
        // inflated to -255 (pre -100, dup -100, uniq -55).
        await RecomputeBalanceAsync(conn, checking);
        Assert.Equal(-255m, await LatestBalanceAsync(conn, checking));

        // --- Plan (read-only) ---
        var plan = await PruneImportBatch.PlanAsync(
            conn, TestLedger.Id, importSource: "test",
            WindowFrom, WindowTo, includeTemplates: false, transaction: null, default);

        Assert.Equal(2, plan.RegisterRowCount);                  // dup + uniq
        Assert.Equal(0, plan.TemplateCount);                     // template excluded from targets
        Assert.Equal(1, plan.TwinCount);                         // dup has a pre-batch twin
        Assert.Single(plan.NoTwin);                              // uniq has none
        Assert.Contains(plan.Targets, t => t.Id == dup.Header.Id && t.HasTwin);
        Assert.Contains(plan.Targets, t => t.Id == uniq.Header.Id && !t.HasTwin);
        Assert.DoesNotContain(plan.Targets, t => t.Id == tmpl.Header.Id);
        Assert.Contains(checking, plan.AffectedAccounts);
        Assert.Empty(plan.HoldingPairs);                         // no investment legs

        // --- Apply ---
        int deleted;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            deleted = await PruneImportBatch.ApplyAsync(conn, tx, TestLedger.Id, plan, default);
            await tx.CommitAsync();
        }

        Assert.Equal(2, deleted);
        Assert.Equal(0, await HeaderCountAsync(conn, dup.Header.Id, uniq.Header.Id));   // gone
        Assert.Equal(2, await HeaderCountAsync(conn, pre.Header.Id, tmpl.Header.Id));   // kept
        // Legs of the deleted headers cascade away.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM txn_legs WHERE header_id = ANY(@ids);",
            new { ids = new[] { dup.Header.Id, uniq.Header.Id } }));
        // Balance re-derived to the corrected value (only `pre` remains).
        Assert.Equal(-100m, await LatestBalanceAsync(conn, checking));
    }

    [Fact]
    public async Task Plan_returns_empty_when_no_rows_match_window()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var plan = await PruneImportBatch.PlanAsync(
            conn, TestLedger.Id, "test", WindowFrom, WindowTo,
            includeTemplates: false, transaction: null, default);

        Assert.Empty(plan.Targets);
        Assert.Empty(plan.AffectedAccounts);
        Assert.Empty(plan.HoldingPairs);
    }

    // --- helpers (mirror TransactionsRepositoryTests) ---

    private static AccountRow Bank(string externalId, string name) =>
        new(Id: Guid.NewGuid(), LedgerId: TestLedger.Id, ParentId: null, Name: name,
            AccountType: "bank", CategoryKind: null, CurrencyCode: "USD", OpeningBalance: 0m,
            IsActive: true, ExternalId: externalId, IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null, InstitutionName: null, RoutingNumber: null, AccountUrl: null);

    private static AccountRow Category(string externalId, string kind, string name) =>
        new(Id: Guid.NewGuid(), LedgerId: TestLedger.Id, ParentId: null, Name: name,
            AccountType: "category", CategoryKind: kind, CurrencyCode: "USD", OpeningBalance: 0m,
            IsActive: true, ExternalId: externalId, IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null, InstitutionName: null, RoutingNumber: null, AccountUrl: null);

    private static (TxnHeaderRow Header, TxnLegRow Origin, TxnLegRow Counterpart) MakeEvent(
        Guid bankId, Guid categoryId, string externalId, decimal originAmount,
        DateTimeOffset posted, string payee)
    {
        var headerId = Guid.NewGuid();
        var header = new TxnHeaderRow(
            Id: headerId, LedgerId: TestLedger.Id, Origin: "manual", ExternalId: externalId,
            Payee: payee, Memo: null, PostedAt: posted, TransactedAt: null,
            Status: "cleared", CheckNumber: null, IsPending: false, IsHidden: false,
            IsMergedInto: null, ImportSource: "test", ClearedAt: posted, ClearedByUserId: null,
            OnlineMatchFitid: null, OnlineMatchFiId: null, Action: null);
        var origin = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: bankId,
            PostingIndex: 0, LegMemo: null, Amount: originAmount, SecurityId: null, Quantity: null, UnitPrice: null);
        var counterpart = new TxnLegRow(
            Id: Guid.NewGuid(), HeaderId: headerId, LedgerId: TestLedger.Id, AccountId: categoryId,
            PostingIndex: 0, LegMemo: null, Amount: -originAmount, SecurityId: null, Quantity: null, UnitPrice: null);
        return (header, origin, counterpart);
    }

    // Raw insert with an explicit created_at — txn_headers.created_at is
    // immutable post-insert (ADR-0034 trigger), so the batch/seed timestamp
    // discriminator must be set at INSERT time, not via a later UPDATE.
    private static async Task InsertEventAsync(
        NpgsqlConnection conn,
        (TxnHeaderRow Header, TxnLegRow Origin, TxnLegRow Counterpart) e,
        DateTimeOffset createdAt,
        bool isTemplate)
    {
        await conn.ExecuteAsync("""
            INSERT INTO txn_headers
                (id, ledger_id, origin, external_id, payee, posted_at, transacted_at,
                 import_source, created_at, is_recurring_template)
            VALUES
                (@Id, @LedgerId, @Origin, @ExternalId, @Payee, @PostedAt,@PostedAt,
                 @ImportSource, @CreatedAt, @IsTemplate);
            """, new
            {
                e.Header.Id, e.Header.LedgerId, e.Header.Origin, e.Header.ExternalId,
                e.Header.Payee, e.Header.PostedAt, e.Header.ImportSource,
                CreatedAt = createdAt, IsTemplate = isTemplate,
            });

        foreach (var leg in new[] { e.Origin, e.Counterpart })
            await conn.ExecuteAsync("""
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES (@Id, @HeaderId, @LedgerId, @AccountId, @PostingIndex, @Amount);
                """, new { leg.Id, leg.HeaderId, leg.LedgerId, leg.AccountId, leg.PostingIndex, leg.Amount });
    }

    private static Task RecomputeBalanceAsync(NpgsqlConnection conn, Guid accountId) =>
        conn.ExecuteAsync("SELECT fn_recompute_balances_for_account(@accountId, '0001-01-01'::timestamptz);",
            new { accountId });

    private static Task<decimal> LatestBalanceAsync(NpgsqlConnection conn, Guid accountId) =>
        conn.ExecuteScalarAsync<decimal>("""
            SELECT b.balance_after
              FROM txn_header_account_balances b
              JOIN txn_headers h ON h.id = b.header_id
             WHERE b.account_id = @accountId
             ORDER BY h.posted_at DESC, h.seq DESC
             LIMIT 1;
            """, new { accountId });

    private static async Task<int> HeaderCountAsync(NpgsqlConnection conn, params Guid[] ids) =>
        (int)await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM txn_headers WHERE id = ANY(@ids);", new { ids });

    private static async Task ResetAsync(NpgsqlConnection conn) =>
        await conn.ExecuteAsync("TRUNCATE account_external_ids, accounts, txn_headers, txn_legs CASCADE;");
}
