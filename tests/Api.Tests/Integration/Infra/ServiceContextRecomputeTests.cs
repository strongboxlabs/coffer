using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// A write through the SERVICE-role context recomputes what it changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the rest of the suite does not cover this.</b> Every scheduler tick runs over
/// <c>ServiceDbContextFactory</c>, which used to build its options with
/// <c>UseNpgsql</c> and nothing else — no recompute interceptors — while
/// <c>Program.cs</c> attaches three to the request-scoped context. So a background
/// writer saved legs and left <c>txn_header_account_balances</c>, holdings, lots and
/// trade-sourced prices untouched, and nothing noticed: <c>IngestOrchestrator</c>
/// states in three places that balance recompute "is automatic via
/// LegDerivedRecomputeInterceptor", which is true on the request path and was false on
/// the worker path <c>FeedSyncJobHandler</c> actually uses.
/// </para>
/// <para>
/// <b>The context under test must come from the FACTORY.</b>
/// <c>PostgresFixture.NewServiceDbContext()</c> builds bare options of its own, so a
/// test written over it would exercise the fixture's wiring and stay green with the
/// production fix deleted — the can't-fail shape this repo treats as a defect in its
/// own right. <c>NewServiceFactory()</c> constructs a real
/// <c>ServiceDbContextFactory</c>, which is why the writes below go through it.
/// </para>
/// <para>
/// And the write must go through EF. <see cref="SyntheticLedger"/>'s seeding helpers
/// insert with raw SQL and then call <c>fn_recompute_balances_for_account</c> by hand,
/// precisely because a raw seed bypasses the interceptor — borrowing one here would
/// produce the right answer for the wrong reason.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ServiceContextRecomputeTests
{
    private readonly PostgresFixture _fixture;

    public ServiceContextRecomputeTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Running balances and the denormalised posting count — both derived, both written
    /// by interceptors rather than by the caller.
    /// </summary>
    [Fact]
    public async Task A_leg_written_through_the_service_factory_gets_its_balance_recomputed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddBankAccountAsync("Groceries");

        var headerId = Guid.NewGuid();
        var postedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = _fixture.NewServiceFactory().Create())
        {
            db.Add(new TxnHeaderRow
            {
                Id = headerId,
                LedgerId = ledger.LedgerId,
                Origin = "manual",
                Payee = "Market",
                PostedAt = postedAt,
                TransactedAt = postedAt,
                CreatedAt = postedAt,
            });
            db.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = ledger.LedgerId,
                AccountId = checking.Id,
                PostingIndex = 0,
                Amount = -25.00m,
            });
            db.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = ledger.LedgerId,
                AccountId = groceries.Id,
                PostingIndex = 1,
                Amount = 25.00m,
            });

            // No explicit recompute call anywhere in this test. That absence IS the
            // assertion — the interceptor has to do it on save.
            await db.SaveChangesAsync();
        }

        await using var read = _fixture.NewServiceDbContext();

        var balance = await read.Set<TxnHeaderAccountBalanceRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.HeaderId == headerId && b.AccountId == checking.Id);

        Assert.True(
            balance is not null,
            "no txn_header_account_balances row for the account this leg debited — the "
            + "service context saved the leg without recomputing the running balance, "
            + "which is what every scheduled writer does on every tick");
        Assert.Equal(-25.00m, balance!.BalanceAfter);

        var postings = await read.Set<TxnLegRow>()
            .AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => l.HeaderTotalPostings)
            .ToListAsync();

        Assert.Equal(2, postings.Count);
        Assert.All(postings, p => Assert.Equal(2, p));
    }

    /// <summary>
    /// The balance interceptor alone is not the fix.
    /// </summary>
    /// <remarks>
    /// Attaching one of the three and calling it done passes the balance test above
    /// while investment writes still go unprojected, so the investment shape is
    /// asserted separately. A scheduled importer pulling trades is exactly the writer
    /// that would hit this.
    /// </remarks>
    [Fact]
    public async Task An_investment_leg_written_through_the_service_factory_projects_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        var securityId = Guid.NewGuid();
        await using (var seed = _fixture.NewServiceDbContext())
        {
            seed.Add(new SecurityRow
            {
                Id = securityId,
                LedgerId = ledger.LedgerId,
                Ticker = "TSTX",
                Name = "Test Security",
            });
            await seed.SaveChangesAsync();
        }

        var headerId = Guid.NewGuid();
        var postedAt = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = _fixture.NewServiceFactory().Create())
        {
            db.Add(new TxnHeaderRow
            {
                Id = headerId,
                LedgerId = ledger.LedgerId,
                Origin = "manual",
                Action = "buy",
                Payee = "buy",
                PostedAt = postedAt,
                TransactedAt = postedAt,
                CreatedAt = postedAt,
            });
            db.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = ledger.LedgerId,
                AccountId = holdingsAccountId,
                PostingIndex = 0,
                Amount = 100.00m,
                SecurityId = securityId,
                Quantity = 10m,
                UnitPrice = 10.00m,
                PostingRole = "security",
            });
            db.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = ledger.LedgerId,
                AccountId = brokerage.Id,
                PostingIndex = 0,
                Amount = -100.00m,
                PostingRole = "security",
            });

            await db.SaveChangesAsync();
        }

        await using var read = _fixture.NewServiceDbContext();

        var quantity = await read.Set<HoldingRow>()
            .AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => (decimal?)h.Quantity)
            .SingleOrDefaultAsync();

        Assert.True(
            quantity is not null,
            "no holdings row after an investment buy through the service context — the "
            + "holdings interceptor is not attached, so a scheduled importer's trades "
            + "would never reach the positions projection");
        Assert.Equal(10m, quantity!.Value);
    }
}
