using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Integration tests for <see cref="InvestmentRepository"/> and the
/// migration-052/053 <c>recompute_holdings_cost_basis</c> PL/pgSQL
/// function. The function is the source of truth for the average-cost
/// recompute that runs at the end of every investment import (Pass 5
/// of <c>InvestmentTransactionImportStep</c>) and as the one-shot
/// scrub in migration 053.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class InvestmentRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecomputeCostBasis_fifo_through_buy_sell_buy_sell_sequence()
    {
        // Scenario: 4 events on one security in posted_at order, under FIFO
        // (ADR-0064 — basis consumes the OLDEST lots first):
        //   Jan 2025: Buy   100 @ $10 (lot A: 100 @ $10, basis $1000, qty 100)
        //   Feb 2025: Sell   50 @ $11 (consume 50 of lot A → basis $500, qty 50)
        //   Mar 2025: Buy   100 @ $20 (lot B: 100 @ $20, basis $2500, qty 150)
        //   Apr 2025: Sell   60 @ $25 (consume remaining 50 of lot A = $500 + 10
        //                              of lot B = $200 → basis $1800, qty 90)
        //
        // Remaining = 90 shares of lot B @ $20 = $1800. (Average cost would give
        // $1500; this asserts FIFO.) The unit_price on the Sell legs ($11, $25)
        // is the market price at sale time — it doesn't affect basis, only the
        // consumed FIFO-lot cost does.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        // Holdings.account_id is the sibling (matches the importer's
        // ctx.HoldingsAccountId stamping — confirmed in production data).
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 90m);

        var buy1 = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity:  100m, unitPrice: 10m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity:  -50m, unitPrice: 11m);
        var buy2 = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity:  100m, unitPrice: 20m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity:  -60m, unitPrice: 25m);

        // Seed a lot per buy (production's create path / importer does this; FIFO
        // basis consumes lots, ADR-0064).
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy1, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy2, 100m, 20m,
            acquiredAt: new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        var updated = await repo.RecomputeCostBasisAsync(ledgerId);

        Assert.Equal(1, updated);

        var costBasis = await conn.ExecuteScalarAsync<decimal>(
            "SELECT cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });

        // Expected $1800 under FIFO (qty=90, all from lot B @ $20). Exact — no
        // repeating-decimal rounding, unlike the average-cost method.
        Assert.Equal(1800m, costBasis);
    }

    [Fact]
    public async Task RecomputeCostBasis_floors_at_zero_when_sells_overdraw_running_pool()
    {
        // Defensive: if for any reason a Sell appears before its
        // corresponding Buys (data drift, partial scrub), the function
        // must NOT produce a negative cost_basis. It floors at zero and
        // moves on.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 0m);

        // Sell with no prior Buys.
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -10m, unitPrice: 5m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var costBasis = await conn.ExecuteScalarAsync<decimal>(
            "SELECT cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });
        Assert.Equal(0m, costBasis);
    }

    [Fact]
    public async Task RecomputeCostBasis_returns_zero_when_all_shares_sold()
    {
        // Sell-everything path: after a Buy and a matched Sell, both
        // running qty and running basis must converge to zero (otherwise
        // a re-bought lot would inherit basis from sold shares).
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 0m);

        var buy = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 50m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -100m, unitPrice: 60m);

        // Seed the buy's lot (production creates it); FIFO consumes it on the sell.
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy, 100m, 50m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var costBasis = await conn.ExecuteScalarAsync<decimal>(
            "SELECT cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });
        Assert.Equal(0m, costBasis);
    }

    // --- B0.4: posting_role + per-brokerage is_trade_commission (migration 056) ---

    [Fact]
    public async Task RecomputeCostBasis_ignores_fee_legs_when_brokerage_flag_is_false()
    {
        // Default behavior: brokerage's is_trade_commission = FALSE means
        // any posting_role='fee' in the header is structurally ignored by
        // basis math. The "fee-ness" of the posting is identified
        // correctly (the posting_role marker), but the brokerage's policy
        // decides whether to honor it. Matches the 401k-style account
        // where in-transaction fees are administrative, not commissions.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();
        var feeCat    = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling,
            isTradeCommission: false);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);
        await InsertCategoryAsync(conn, ledgerId, feeCat, "Investment Fees", kind: "expense");

        await PostBuyWithFeeAsync(conn, ledgerId, brokerage, sibling, security, feeCat,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m, feeAmount: 5m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var costBasis = await conn.ExecuteScalarAsync<decimal>(
            "SELECT cost_basis FROM holdings WHERE id = @Id;", new { Id = holding });
        Assert.Equal(1000m, costBasis);
    }

    [Fact]
    public async Task RecomputeCostBasis_includes_fee_postings_when_brokerage_flag_is_true()
    {
        // Brokerage's is_trade_commission = TRUE means posting_role='fee'
        // amounts in the same header flow into basis. Buy 100 @ $10 +
        // $5 fee → basis = $1005, unit_cost = $10.05.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();
        var feeCat    = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling,
            isTradeCommission: true);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);
        await InsertCategoryAsync(conn, ledgerId, feeCat, "Brokerage Commission", kind: "expense");

        var buyHeader = await PostBuyWithFeeAsync(conn, ledgerId, brokerage, sibling, security, feeCat,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m, feeAmount: 5m);
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeader, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var (basis, unitCost) = await conn.QueryFirstAsync<(decimal basis, decimal unitCost)>(@"
            SELECT h.cost_basis, l.unit_cost
              FROM holdings h JOIN lots l ON l.holding_id = h.id
             WHERE h.id = @Id;", new { Id = holding });
        Assert.Equal(1005m, basis);
        Assert.Equal(10.05m, unitCost);
    }

    [Fact]
    public async Task RecomputeCostBasis_treats_fee_role_independent_of_category_assignment()
    {
        // Key property of the new model: the category that a fee posting
        // is assigned to is irrelevant — only posting_role='fee' matters.
        // Here the user assigned the fee to a category named "Random
        // Misc Expense" (not "Brokerage Commission") but the posting was
        // explicitly marked as a fee. With the brokerage flag TRUE, it
        // still flows into basis.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();
        var randomCat = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling,
            isTradeCommission: true);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);
        await InsertCategoryAsync(conn, ledgerId, randomCat, "Random Misc Expense",
            kind: "expense");

        await PostBuyWithFeeAsync(conn, ledgerId, brokerage, sibling, security, randomCat,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m, feeAmount: 5m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var costBasis = await conn.ExecuteScalarAsync<decimal>(
            "SELECT cost_basis FROM holdings WHERE id = @Id;", new { Id = holding });
        Assert.Equal(1005m, costBasis);
    }

    [Fact]
    public async Task RecomputeCostBasis_brokerage_flag_flip_is_idempotent_and_reversible()
    {
        // Flip brokerage flag TRUE → run → basis & unit_cost include fee.
        // Flip back FALSE → run → both revert. The function refreshes
        // both holdings.cost_basis AND lots.unit_cost on every call so
        // toggling the flag propagates fully.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();
        var feeCat    = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling,
            isTradeCommission: false);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);
        await InsertCategoryAsync(conn, ledgerId, feeCat, "Brokerage Commission", kind: "expense");

        var buyHeader = await PostBuyWithFeeAsync(conn, ledgerId, brokerage, sibling, security, feeCat,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m, feeAmount: 5m);
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeader, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);

        // Flag = FALSE
        await repo.RecomputeCostBasisAsync(ledgerId);
        var (basisOff, unitCostOff) = await conn.QueryFirstAsync<(decimal basis, decimal unitCost)>(@"
            SELECT h.cost_basis, l.unit_cost
              FROM holdings h JOIN lots l ON l.holding_id = h.id
             WHERE h.id = @Id;", new { Id = holding });
        Assert.Equal(1000m, basisOff);
        Assert.Equal(10m, unitCostOff);

        // Flag → TRUE on the brokerage
        await conn.ExecuteAsync(
            "UPDATE accounts SET is_trade_commission = TRUE WHERE id = @Id;",
            new { Id = brokerage });
        await repo.RecomputeCostBasisAsync(ledgerId);
        var (basisOn, unitCostOn) = await conn.QueryFirstAsync<(decimal basis, decimal unitCost)>(@"
            SELECT h.cost_basis, l.unit_cost
              FROM holdings h JOIN lots l ON l.holding_id = h.id
             WHERE h.id = @Id;", new { Id = holding });
        Assert.Equal(1005m, basisOn);
        Assert.Equal(10.05m, unitCostOn);

        // Flag → FALSE again; both revert
        await conn.ExecuteAsync(
            "UPDATE accounts SET is_trade_commission = FALSE WHERE id = @Id;",
            new { Id = brokerage });
        await repo.RecomputeCostBasisAsync(ledgerId);
        var (basisBack, unitCostBack) = await conn.QueryFirstAsync<(decimal basis, decimal unitCost)>(@"
            SELECT h.cost_basis, l.unit_cost
              FROM holdings h JOIN lots l ON l.holding_id = h.id
             WHERE h.id = @Id;", new { Id = holding });
        Assert.Equal(1000m, basisBack);
        Assert.Equal(10m, unitCostBack);
    }

    // The two Trigger_rejects_* tests that lived here previously
    // exercised trg_validate_posting_role (migration 057). Migration
    // 084 dropped that trigger — the invariant
    //   posting_role IS NOT NULL ⇔ txn_headers.action IS NOT NULL
    // is now upheld by repository code (InvestmentTransactionsRepository
    // .CreateAsync / .PatchAsync, IngestOrchestrator, this repository)
    // and verified at the API integration layer
    // (InvestmentTransactionsEndpointsTests, e.g. lines 118 / 347 / 529
    // assert PostingRole on the inserted legs after Create + PATCH).

    [Fact]
    public async Task Multi_posting_misc_header_is_accepted_post_062()
    {
        // Migration 061 dropped the single-posting MiscInc invariant.
        // Migration 062 renames the action to `misc` (ADR-0027 / A4.b).
        // The misc-with-fee shape is a legitimate user-creatable MD
        // event — `inc` txn with both `inc` and `fee` splits → 2
        // postings under one header. The DB must accept it.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var income    = Guid.NewGuid();
        var fee       = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertCategoryAsync(conn, ledgerId, income, "Income", kind: "income");
        await InsertCategoryAsync(conn, ledgerId, fee,    "Fee",    kind: "expense");

        var headerId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, 'misc', NOW());",
            new { Id = headerId, LedgerId = ledgerId });

        // Posting 0 (income side).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                amount, posting_role, created_at)
            VALUES
              (@A, @H, @L, @Cash, 0, 100, 'income', NOW()),
              (@B, @H, @L, @Inc,  0, -100, 'income', NOW());",
            new { A = Guid.NewGuid(), B = Guid.NewGuid(), H = headerId, L = ledgerId,
                  Cash = brokerage, Inc = income });

        // Posting 1 (fee side).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                amount, posting_role, created_at)
            VALUES
              (@A, @H, @L, @Cash, 1, -5, 'fee', NOW()),
              (@B, @H, @L, @Fee,  1,  5, 'fee', NOW());",
            new { A = Guid.NewGuid(), B = Guid.NewGuid(), H = headerId, L = ledgerId,
                  Cash = brokerage, Fee = fee });

        var distinctPostings = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT posting_index) FROM txn_legs WHERE header_id = @Id;",
            new { Id = headerId });
        Assert.Equal(2, distinctPostings);
    }

    [Fact]
    public async Task Action_check_rejects_pre_A4_actions()
    {
        // Migration 062 dropped `interest`, `misc_income`, `misc_expense`
        // from the txn_headers.action CHECK. All three should fail
        // check_violation on insert.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        foreach (var oldAction in new[] { "interest", "misc_income", "misc_expense" })
        {
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => conn.ExecuteAsync(@"
                INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                    is_pending, is_hidden, action, created_at)
                VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                    false, false, @Action, NOW());",
                new { Id = Guid.NewGuid(), LedgerId = TestLedger.Id, Action = oldAction }));
            Assert.Equal("23514", ex.SqlState);
        }
    }

    [Fact]
    public async Task Action_check_accepts_all_new_compound_actions()
    {
        // Migration 062 added `buyx`, `sellx`, `divx`, `misc` to the
        // CHECK. Each should insert cleanly.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        foreach (var newAction in new[] { "buyx", "sellx", "divx", "misc" })
        {
            await conn.ExecuteAsync(@"
                INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                    is_pending, is_hidden, action, created_at)
                VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                    false, false, @Action, NOW());",
                new { Id = Guid.NewGuid(), LedgerId = TestLedger.Id, Action = newAction });
        }
    }

    [Fact]
    public async Task CheckConstraint_prevents_is_trade_commission_true_on_non_investment_account()
    {
        // Migration 056 added accounts_is_trade_commission_only_on_investment.
        // Flipping the flag on a category, bank, or credit_card account
        // must fail at the DB level — the flag semantically lives on
        // brokerages.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId = TestLedger.Id;
        var catId    = Guid.NewGuid();
        await InsertCategoryAsync(conn, ledgerId, catId, "Some Category", kind: "expense");

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            conn.ExecuteAsync(
                "UPDATE accounts SET is_trade_commission = TRUE WHERE id = @Id;",
                new { Id = catId }));
        Assert.Equal("23514", ex.SqlState);  // check_violation
    }

    // --- B0.2: FIFO lot closure (migration 054) --------------------------

    [Fact]
    public async Task RecomputeCostBasis_closes_lots_FIFO_on_partial_sell()
    {
        // Buy 100, Buy 100, Sell 150 → first lot fully closed, second
        // lot has remaining qty 50, both basis and lots agree.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 50m);

        var buy1HeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        var buy2HeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 20m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -150m, unitPrice: 25m);

        // Insert open lots for the two Buys so the function has lots
        // to close. Mirrors what migration 054's rebuild produces.
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy1HeaderId, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy2HeaderId, 100m, 20m,
            acquiredAt: new DateTime(2025, 2, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var lots = (await conn.QueryAsync<(decimal quantity, bool isClosed)>(@"
            SELECT quantity, is_closed FROM lots
            WHERE holding_id = @Id
            ORDER BY acquired_at;",
            new { Id = holding })).ToList();

        Assert.Equal(2, lots.Count);
        Assert.True(lots[0].isClosed);
        Assert.Equal(0m, lots[0].quantity);
        Assert.False(lots[1].isClosed);
        Assert.Equal(50m, lots[1].quantity);
    }

    [Fact]
    public async Task RecomputeCostBasis_FIFO_closure_is_idempotent_across_reruns()
    {
        // Critical for the "flip flag, re-run" workflow: state at the
        // start of each holding's loop is reset from txn_legs, so
        // running the function N times produces the same result.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 50m);

        var buyHeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -50m, unitPrice: 12m);

        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeaderId, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);
        await repo.RecomputeCostBasisAsync(ledgerId);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var lot = await conn.QueryFirstAsync<(decimal quantity, bool isClosed)>(@"
            SELECT quantity, is_closed FROM lots WHERE holding_id = @Id;",
            new { Id = holding });
        Assert.Equal(50m, lot.quantity);
        Assert.False(lot.isClosed);
    }

    [Fact]
    public async Task RecomputeCostBasis_FIFO_closes_lot_fully_when_sell_drains_it_exactly()
    {
        // Buy 100, Sell 100 → lot closed with qty=0.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 0m);

        var buyHeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -100m, unitPrice: 15m);

        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeaderId, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var lot = await conn.QueryFirstAsync<(decimal quantity, bool isClosed)>(@"
            SELECT quantity, is_closed FROM lots WHERE holding_id = @Id;",
            new { Id = holding });
        Assert.Equal(0m, lot.quantity);
        Assert.True(lot.isClosed);
    }

    // --- B0.7: security_splits + split-aware recompute (migration 060) ---

    [Fact]
    public async Task RecomputeCostBasis_applies_split_ratio_to_holdings_quantity()
    {
        // Buy 100 → split 2.0 → final holdings.quantity should be 200,
        // cost_basis preserved at 1000 (per-share basis halves implicitly).
        // Pre-060 the importer's Pass 3 aggregation summed leg deltas
        // (100) with no split awareness. Post-060 the recompute walks
        // the unified event stream and produces 200.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);

        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await InsertSplitAsync(conn, ledgerId, security,
            splitAt: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ratio: 2.0m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var (qty, basis) = await conn.QueryFirstAsync<(decimal qty, decimal basis)>(
            "SELECT quantity, cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });
        Assert.Equal(200m,  qty);
        Assert.Equal(1000m, basis);
    }

    [Fact]
    public async Task RecomputeCostBasis_applies_split_ratio_to_open_lots()
    {
        // Lot-level: a 2-for-1 forward split must double an open lot's
        // remaining quantity. Closed lots stay closed at quantity=0.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);

        var buyHeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeaderId, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        await InsertSplitAsync(conn, ledgerId, security,
            splitAt: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ratio: 2.0m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var lot = await conn.QueryFirstAsync<(decimal quantity, bool isClosed)>(
            "SELECT quantity, is_closed FROM lots WHERE holding_id = @Id;",
            new { Id = holding });
        Assert.Equal(200m, lot.quantity);
        Assert.False(lot.isClosed);
    }

    [Fact]
    public async Task RecomputeCostBasis_split_application_is_idempotent_across_reruns()
    {
        // Re-running recompute must NOT compound the multiplier. The
        // function resets every lot for the holding at the start of each
        // pass; the running-qty walk produces the same 200 every time.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);

        var buyHeaderId = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buyHeaderId, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        await InsertSplitAsync(conn, ledgerId, security,
            splitAt: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ratio: 2.0m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);
        await repo.RecomputeCostBasisAsync(ledgerId);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var (qty, lotQty) = await conn.QueryFirstAsync<(decimal qty, decimal lotQty)>(@"
            SELECT h.quantity, l.quantity
              FROM holdings h JOIN lots l ON l.holding_id = h.id
             WHERE h.id = @Id;", new { Id = holding });
        Assert.Equal(200m, qty);
        Assert.Equal(200m, lotQty);
    }

    [Fact]
    public async Task RecomputeCostBasis_sell_after_split_uses_post_split_basis()
    {
        // Buy 100 @ $10 (basis 1000) → split 2x (qty=200, per-share basis $5)
        // → Sell 50 @ $7 → expected: qty=150, basis = 1000 - (5*50) = 750.
        // Validates that the basis is correctly carried through the split
        // and that the avg-cost reduction on the post-split sell uses the
        // post-split share count.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 150m);

        var buy = await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 100m, unitPrice: 10m);
        await InsertSplitAsync(conn, ledgerId, security,
            splitAt: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ratio: 2.0m);
        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 9, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: -50m, unitPrice: 7m);

        // Seed the buy's lot (production creates it); the split scales it (qty×2,
        // unit_cost÷2) and FIFO consumes from it on the post-split sell.
        await InsertLotForHeaderAsync(conn, ledgerId, holding, buy, 100m, 10m,
            acquiredAt: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var (qty, basis) = await conn.QueryFirstAsync<(decimal qty, decimal basis)>(
            "SELECT quantity, cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });
        Assert.Equal(150m, qty);
        Assert.Equal(750m, basis);
    }

    [Fact]
    public async Task RecomputeCostBasis_supports_reverse_split()
    {
        // Buy 200 @ $10 (basis 2000) → reverse 1-for-2 (ratio 0.5) →
        // qty=100, basis=2000 preserved (per-share doubles to $20).
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ledgerId  = TestLedger.Id;
        var brokerage = Guid.NewGuid();
        var sibling   = Guid.NewGuid();
        var security  = Guid.NewGuid();
        var holding   = Guid.NewGuid();

        await InsertBrokerageAndSiblingAsync(conn, ledgerId, brokerage, sibling);
        await InsertSecurityAsync(conn, ledgerId, security);
        await InsertHoldingAsync(conn, ledgerId, holding, sibling, security, quantity: 100m);

        await PostInvestmentEventAsync(conn, ledgerId, brokerage, sibling, security,
            posted: new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            quantity: 200m, unitPrice: 10m);
        await InsertSplitAsync(conn, ledgerId, security,
            splitAt: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ratio: 0.5m);

        var repo = new InvestmentRepository(conn);
        await repo.RecomputeCostBasisAsync(ledgerId);

        var (qty, basis) = await conn.QueryFirstAsync<(decimal qty, decimal basis)>(
            "SELECT quantity, cost_basis FROM holdings WHERE id = @Id;",
            new { Id = holding });
        Assert.Equal(100m,  qty);
        Assert.Equal(2000m, basis);
    }

    [Fact]
    public async Task CheckConstraint_action_no_longer_allows_split_value()
    {
        // Migration 060 removed 'split' from the action CHECK. Splits are
        // security metadata now, not transactions — inserting a split-
        // action header must fail with a check_violation.
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at)
            VALUES (@Id, @LedgerId, 'manual', NOW(),NOW(),
                false, false, 'split', NOW());",
            new { Id = Guid.NewGuid(), LedgerId = TestLedger.Id }));
        Assert.Equal("23514", ex.SqlState);    // check_violation
    }

    private static async Task InsertSplitAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid securityId,
        DateTime splitAt, decimal ratio)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO security_splits (id, ledger_id, security_id, split_at, ratio,
                old_shares, new_shares, external_id)
            VALUES (@Id, @LedgerId, @SecurityId, @SplitAt, @Ratio,
                NULL, NULL, NULL);",
            new {
                Id         = Guid.NewGuid(),
                LedgerId   = ledgerId,
                SecurityId = securityId,
                SplitAt    = splitAt,
                Ratio      = ratio,
            });
    }

    // --- helpers --------------------------------------------------------

    /// <summary>
    /// Insert a brokerage account + its Holdings sibling. The brokerage
    /// points at the sibling via <c>holdings_account_id</c>; the sibling
    /// has no <c>holdings_account_id</c> of its own (ADR-0019 / ADR-0011
    /// — one cash account, one Holdings sibling per brokerage).
    /// </summary>
    private static async Task InsertBrokerageAndSiblingAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid brokerageId, Guid siblingId,
        bool isTradeCommission = false)
    {
        // Sibling first because the brokerage's FK references it.
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                opening_balance, is_active, is_system, holdings_account_id)
            VALUES (@Id, @LedgerId, 'TestBroker Holdings', 'investment', 'USD',
                0, true, true, NULL);",
            new { Id = siblingId, LedgerId = ledgerId });

        // Brokerage gets the is_trade_commission flag. Migration 056
        // constrains this flag to investment accounts only; categories
        // can't carry it.
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                opening_balance, is_active, is_system, holdings_account_id,
                is_trade_commission)
            VALUES (@Id, @LedgerId, 'TestBroker', 'investment', 'USD',
                0, true, false, @SiblingId, @IsTradeCommission);",
            new { Id = brokerageId, LedgerId = ledgerId, SiblingId = siblingId,
                  IsTradeCommission = isTradeCommission });
    }

    private static async Task InsertSecurityAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid securityId)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO securities (id, ledger_id, ticker, name, asset_class,
                is_active, share_decimals)
            VALUES (@Id, @LedgerId, 'TST', 'Test Security', 'equity', true, 4);",
            new { Id = securityId, LedgerId = ledgerId });
    }

    private static async Task InsertHoldingAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid holdingId, Guid siblingId,
        Guid securityId, decimal quantity)
    {
        // Initial cost_basis is set to a deliberately-wrong sentinel so
        // we can confirm the recompute actually overwrites it.
        await conn.ExecuteAsync(@"
            INSERT INTO holdings (id, ledger_id, account_id, security_id, quantity,
                cost_basis, as_of)
            VALUES (@Id, @LedgerId, @AccountId, @SecurityId, @Quantity,
                999999.99, NOW());",
            new { Id = holdingId, LedgerId = ledgerId, AccountId = siblingId,
                  SecurityId = securityId, Quantity = quantity });
    }

    /// <summary>
    /// Post one investment event: a single posting (posting_index=0)
    /// with two legs — brokerage cash side, Holdings sibling side. The
    /// Holdings leg carries <c>security_id</c>, <c>quantity</c>, and
    /// <c>unit_price</c>; the brokerage cash leg is dollar-only.
    /// Returns the header id so the caller can attach lots to it.
    /// </summary>
    private static async Task<Guid> PostInvestmentEventAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid brokerageId, Guid siblingId,
        Guid securityId, DateTime posted, decimal quantity, decimal unitPrice)
    {
        var headerId = Guid.NewGuid();
        var holdingsLegId = Guid.NewGuid();
        var cashLegId = Guid.NewGuid();
        var holdingsAmount = quantity * unitPrice;
        var cashAmount = -holdingsAmount;
        var action = quantity > 0 ? "buy" : "sell";

        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at)
            VALUES (@Id, @LedgerId, 'manual', @PostedAt,@PostedAt,
                false, false, @Action, @PostedAt);",
            new { Id = headerId, LedgerId = ledgerId, PostedAt = posted, Action = action });

        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                amount, security_id, quantity, unit_price, posting_role, created_at)
            VALUES
                (@HoldingsLegId, @HeaderId, @LedgerId, @SiblingId, 0,
                    @HoldingsAmount, @SecurityId, @Quantity, @UnitPrice, 'security', @PostedAt),
                (@CashLegId,     @HeaderId, @LedgerId, @BrokerageId, 0,
                    @CashAmount, NULL, NULL, NULL, 'security', @PostedAt);",
            new {
                HoldingsLegId  = holdingsLegId,
                CashLegId      = cashLegId,
                HeaderId       = headerId,
                LedgerId       = ledgerId,
                SiblingId      = siblingId,
                BrokerageId    = brokerageId,
                SecurityId     = securityId,
                Quantity       = quantity,
                UnitPrice      = unitPrice,
                HoldingsAmount = holdingsAmount,
                CashAmount     = cashAmount,
                PostedAt       = posted,
            });

        return headerId;
    }

    /// <summary>
    /// Insert an expense or income category account. Per migration 056,
    /// categories can no longer carry <c>is_trade_commission</c> — that
    /// flag is constrained to investment accounts. Categories are just
    /// targets for the amount side of postings; fee/income classification
    /// lives on <c>txn_legs.posting_role</c>.
    /// </summary>
    private static async Task InsertCategoryAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid categoryId, string name,
        string kind)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind,
                currency_code, opening_balance, is_active, is_system,
                holdings_account_id)
            VALUES (@Id, @LedgerId, @Name, 'category', @Kind, 'USD',
                0, true, false, NULL);",
            new { Id = categoryId, LedgerId = ledgerId, Name = name, Kind = kind });
    }

    /// <summary>
    /// Post a Buy event WITH an additional fee posting (posting_index=1).
    /// Both fee legs are stamped <c>posting_role = 'fee'</c> per migration
    /// 056. The fee posting's cash side defaults to the brokerage (normal
    /// commission shape). Returns the header id so callers can attach
    /// lots to it.
    /// </summary>
    private static Task<Guid> PostBuyWithFeeAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid brokerageId, Guid siblingId,
        Guid securityId, Guid feeCategoryId,
        DateTime posted, decimal quantity, decimal unitPrice, decimal feeAmount)
        => PostBuyWithFeeFromAccountAsync(conn, ledgerId, brokerageId, siblingId,
            securityId, feeCategoryId, feeCounterpartyAccount: brokerageId,
            posted, quantity, unitPrice, feeAmount);

    /// <summary>
    /// Post a Buy event with a fee posting whose cash side is on a
    /// caller-specified account. The fee posting's legs are both
    /// stamped <c>posting_role = 'fee'</c>. Per migration 056, this
    /// alone identifies it as a fee — the category it lands on is
    /// irrelevant to the recompute function.
    /// </summary>
    private static async Task<Guid> PostBuyWithFeeFromAccountAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid brokerageId, Guid siblingId,
        Guid securityId, Guid feeCategoryId, Guid feeCounterpartyAccount,
        DateTime posted, decimal quantity, decimal unitPrice, decimal feeAmount)
    {
        var headerId = Guid.NewGuid();
        var holdingsLegId = Guid.NewGuid();
        var cashLegId = Guid.NewGuid();
        var feeCatLegId = Guid.NewGuid();
        var feeCashLegId = Guid.NewGuid();
        var holdingsAmount = quantity * unitPrice;
        var cashAmount = -holdingsAmount;

        await conn.ExecuteAsync(@"
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                is_pending, is_hidden, action, created_at)
            VALUES (@Id, @LedgerId, 'manual', @PostedAt,@PostedAt,
                false, false, 'buy', @PostedAt);",
            new { Id = headerId, LedgerId = ledgerId, PostedAt = posted });

        // Posting 0: shares (security role). Posting 1: fee (fee role).
        await conn.ExecuteAsync(@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                amount, security_id, quantity, unit_price, posting_role, created_at)
            VALUES
                (@HoldingsLegId, @HeaderId, @LedgerId, @SiblingId, 0,
                    @HoldingsAmount, @SecurityId, @Quantity, @UnitPrice, 'security', @PostedAt),
                (@CashLegId, @HeaderId, @LedgerId, @BrokerageId, 0,
                    @CashAmount, NULL, NULL, NULL, 'security', @PostedAt),
                (@FeeCatLegId, @HeaderId, @LedgerId, @FeeCategoryId, 1,
                    @FeeAmount, NULL, NULL, NULL, 'fee', @PostedAt),
                (@FeeCashLegId, @HeaderId, @LedgerId, @FeeCounterparty, 1,
                    @FeeCashAmount, NULL, NULL, NULL, 'fee', @PostedAt);",
            new {
                HoldingsLegId   = holdingsLegId,
                CashLegId       = cashLegId,
                FeeCatLegId     = feeCatLegId,
                FeeCashLegId    = feeCashLegId,
                HeaderId        = headerId,
                LedgerId        = ledgerId,
                SiblingId       = siblingId,
                BrokerageId     = brokerageId,
                FeeCategoryId   = feeCategoryId,
                FeeCounterparty = feeCounterpartyAccount,
                SecurityId      = securityId,
                Quantity        = quantity,
                UnitPrice       = unitPrice,
                HoldingsAmount  = holdingsAmount,
                CashAmount      = cashAmount,
                FeeAmount       = feeAmount,
                FeeCashAmount   = -feeAmount,
                PostedAt        = posted,
            });

        return headerId;
    }

    /// <summary>
    /// Insert an open lot tied to the holdings-side leg of an
    /// already-posted investment header. Mimics what migration 054's
    /// rebuild produces — needed because the test sets up the data
    /// the function then operates on.
    /// </summary>
    private static async Task InsertLotForHeaderAsync(
        NpgsqlConnection conn, Guid ledgerId, Guid holdingId, Guid headerId,
        decimal quantity, decimal unitCost, DateTime acquiredAt)
    {
        var legId = await conn.QuerySingleAsync<Guid>(@"
            SELECT id FROM txn_legs
            WHERE header_id = @HeaderId AND quantity IS NOT NULL AND quantity > 0
            LIMIT 1;",
            new { HeaderId = headerId });

        await conn.ExecuteAsync(@"
            INSERT INTO lots (id, ledger_id, holding_id, leg_id, quantity, unit_cost,
                acquired_at, is_closed)
            VALUES (@Id, @LedgerId, @HoldingId, @LegId, @Quantity, @UnitCost,
                @AcquiredAt, false);",
            new {
                Id         = Guid.NewGuid(),
                LedgerId   = ledgerId,
                HoldingId  = holdingId,
                LegId      = legId,
                Quantity   = quantity,
                UnitCost   = unitCost,
                AcquiredAt = acquiredAt,
            });
    }

    private static async Task ResetAsync(NpgsqlConnection conn)
    {
        // Order matters: lots → holdings → txn_legs → txn_headers →
        // security_splits → securities → accounts. Each step's FK target follows.
        await conn.ExecuteAsync(@"
            TRUNCATE security_splits, lots, holdings, txn_legs, txn_headers,
                     securities, accounts RESTART IDENTITY CASCADE;");
    }
}
