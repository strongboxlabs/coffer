using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Tests for the P3 posting-cardinality trigger (migration 065). Every
/// (header_id, posting_index) must have exactly 2 legs. The trigger
/// rejects a 3rd leg insert; the deferred completeness check catches
/// 1-leg leftovers at transaction commit.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class PostingInvariantTests
{
    private readonly PostgresFixture _fixture;

    public PostingInvariantTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Accepts_exactly_2_legs_per_posting()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var ctx = await BuildLedgerWithAccountsAsync(conn);

        var headerId = await InsertHeaderAsync(conn, ctx.LedgerId);

        // Two legs on the same posting, on different accounts → valid.
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, created_at)
            VALUES
              (@A, @H, @L, @AcctA, 0, -50, NOW()),
              (@B, @H, @L, @AcctB, 0,  50, NOW());",
            new { A = Guid.NewGuid(), B = Guid.NewGuid(),
                  H = headerId, L = ctx.LedgerId,
                  AcctA = ctx.BankId, AcctB = ctx.CategoryId });

        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_legs WHERE header_id = @Id;", new { Id = headerId });
        Assert.Equal(2, count);
    }

    // Rejects_third_leg_at_same_posting (formerly here) exercised the
    // cardinality trigger directly via raw SQL. Migration 085 dropped
    // the trigger per ADR-0032 — the invariant (exactly 2 legs per
    // posting, ADR-0019) is upheld by API code that constructs
    // symmetric leg pairs in BuildPostings (InvestmentTransactionsRepository,
    // IngestOrchestrator, Importer.Moneydance.TransactionsRepository).
    // API-level integration tests in InvestmentTransactionsEndpointsTests
    // assert post-state on legs after Create + PATCH paths.

    // Rejects_single_leg_left_at_transaction_commit (formerly here)
    // exercised the deferred completeness trigger via raw SQL.
    // Migration 086 dropped the trigger per ADR-0032 — the invariant
    // (0 or 2 legs per posting at commit, ADR-0019) is upheld by API
    // code that writes leg pairs atomically within a single
    // transaction. API-level integration tests cover the post-state
    // on every Create / PATCH / DELETE path.

    private sealed record TestCtx(Guid LedgerId, Guid BankId, Guid CategoryId);

    private static async Task<TestCtx> BuildLedgerWithAccountsAsync(NpgsqlConnection conn)
    {
        var ledgerId = TestLedger.Id;
        var bankId = Guid.NewGuid();
        var catId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO ledgers (id, name) VALUES (@Id, 'Default')
            ON CONFLICT (id) DO NOTHING;", new { Id = ledgerId });
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                opening_balance, is_active, is_system)
            VALUES (@A, @L, 'Bank A',  'bank',     NULL,      'USD', 0, true, false),
                   (@C, @L, 'Groceries','category','expense', 'USD', 0, true, false);",
            new { A = bankId, C = catId, L = ledgerId });
        return new TestCtx(ledgerId, bankId, catId);
    }

    private static async Task<Guid> InsertHeaderAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, NOW());",
            new { Id = id, LedgerId = ledgerId });
        return id;
    }

    private static async Task ResetAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            TRUNCATE security_splits, lots, holdings, txn_legs, txn_headers,
                     security_prices, securities,
                     account_external_ids, accounts RESTART IDENTITY CASCADE;");
    }
}
