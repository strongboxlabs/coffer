using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// ADR-0068 MCP write surface (slice A) — the net-new category cleanup repos behind
/// <c>merge_category</c> / <c>delete_category</c> (AccountsRepository), plus the
/// fill-nulls/overwrite logic of the <c>set_security_classification</c> tool. Repo-
/// level integration tests over a bootstrapped synthetic ledger; the service-role
/// context is fine because every method is explicitly ledger-scoped.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpWriteSurfaceTests
{
    private readonly PostgresFixture _fixture;

    public McpWriteSurfaceTests(PostgresFixture fixture) => _fixture = fixture;

    // A guard that permits writes (kill-switch on + write scope), so these tests
    // exercise the real write path; the guard itself is proved in McpWriteGuardTests.
    private static readonly McpWriteGuard Guard = McpTestGuard.Writable();

    private static DateTime Utc(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Merge_moves_transactions_reparents_children_and_deactivates_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Groceries (old)", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var child = await ledger.AddCategoryAsync("Snacks", "expense", parentId: source.Id);
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -50m, Utc(5));
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -20m, Utc(6));

        await using var db = _fixture.NewDbContext();
        var outcome = await new AccountsRepository(db, new LegDerivedRecomputeService(db))
            .MergeCategoryAsync(ledger.LedgerId, source.Id, target.Id, dryRun: false);

        Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        Assert.Equal(2, outcome.TransactionsMoved);
        Assert.Equal(1, outcome.ChildrenReparented);

        await using var read = _fixture.NewDbContext();
        // Every referencing leg moved to the target; none remain on the source.
        Assert.Equal(0, await read.TxnLegs
            .CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.Equal(2, await read.TxnLegs
            .CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == target.Id));
        // Child category reparented to the target.
        var childRow = await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == child.Id);
        Assert.Equal(target.Id, childRow.ParentId);
        // Source deactivated (reversible) — NOT deleted.
        var srcRow = await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id);
        Assert.False(srcRow.IsActive);
    }

    [Fact]
    public async Task Merge_dryRun_reports_counts_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("A", "expense");
        var target = await ledger.AddCategoryAsync("B", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -10m, Utc(7));

        await using var db = _fixture.NewDbContext();
        var outcome = await new AccountsRepository(db, new LegDerivedRecomputeService(db))
            .MergeCategoryAsync(ledger.LedgerId, source.Id, target.Id, dryRun: true);

        Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.TransactionsMoved);

        await using var read = _fixture.NewDbContext();
        // Untouched: leg still on the source, source still active.
        Assert.Equal(1, await read.TxnLegs
            .CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.True((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_guards_kind_mismatch_non_category_and_self()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var expense = await ledger.AddCategoryAsync("Exp", "expense");
        var income = await ledger.AddCategoryAsync("Inc", "income");

        await using var db = _fixture.NewDbContext();
        var repo = new AccountsRepository(db, new LegDerivedRecomputeService(db));

        Assert.Equal(AccountsRepository.MergeCategoryResult.KindMismatch,
            (await repo.MergeCategoryAsync(ledger.LedgerId, expense.Id, income.Id, false)).Result);
        Assert.Equal(AccountsRepository.MergeCategoryResult.NotCategory,
            (await repo.MergeCategoryAsync(ledger.LedgerId, bank.Id, expense.Id, false)).Result);
        Assert.Equal(AccountsRepository.MergeCategoryResult.SameCategory,
            (await repo.MergeCategoryAsync(ledger.LedgerId, expense.Id, expense.Id, false)).Result);
    }

    [Fact]
    public async Task Merge_recomputes_posting_counts_on_co_occurring_split_header()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Source", "expense");
        var target = await ledger.AddCategoryAsync("Target", "expense");
        // One split header touching BOTH categories: posting 0 → source, posting 1 → target.
        var (_, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (source.Id, 30m), (target.Id, 20m) }, Utc(9));

        await using var db = _fixture.NewDbContext();
        await new AccountsRepository(db, new LegDerivedRecomputeService(db))
            .MergeCategoryAsync(ledger.LedgerId, source.Id, target.Id, dryRun: false);

        await using var read = _fixture.NewDbContext();
        // After the merge BOTH postings touch the target, so its legs' denormalized
        // account_postings_on_header must be recomputed to 2 (== header_total): the
        // target now ORIGINATES the header. A stale 1 would prove the posting-count
        // recompute (ADR-0036) was skipped — the gap a balance-only recompute leaves.
        var targetLegs = await read.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == target.Id)
            .ToListAsync();
        Assert.Equal(2, targetLegs.Count);
        Assert.All(targetLegs, l => Assert.Equal(2, l.AccountPostingsOnHeader));
        Assert.All(targetLegs, l => Assert.Equal(2, l.HeaderTotalPostings));
    }

    [Fact]
    public async Task Delete_removes_empty_category_but_guards_in_use()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var empty = await ledger.AddCategoryAsync("Empty", "expense");
        var used = await ledger.AddCategoryAsync("Used", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, used.Id, -5m, Utc(8));

        await using var db = _fixture.NewDbContext();
        var repo = new AccountsRepository(db, new LegDerivedRecomputeService(db));

        var inUse = await repo.DeleteCategoryAsync(ledger.LedgerId, used.Id, dryRun: false);
        Assert.Equal(AccountsRepository.DeleteCategoryResult.InUse, inUse.Result);
        Assert.Equal(1, inUse.TransactionCount);

        var ok = await repo.DeleteCategoryAsync(ledger.LedgerId, empty.Id, dryRun: false);
        Assert.Equal(AccountsRepository.DeleteCategoryResult.Ok, ok.Result);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.Accounts.AnyAsync(a => a.Id == empty.Id));   // gone
        Assert.True(await read.Accounts.AnyAsync(a => a.Id == used.Id));     // preserved
    }

    [Fact]
    public async Task SetSecurityClassification_fills_nulls_then_overwrite_corrects()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var secId = await ledger.AddSecurityAsync("Some Fund", "FUND", assetClass: null);   // unclassified

        await using var db = _fixture.NewDbContext();
        var repo = new SecuritiesRepository(db);

        // Fill an empty field.
        var r1 = await McpWriteTools.SetSecurityClassification(
            Guard, repo, ledger.LedgerId, secId, assetClass: "equity");
        Assert.True(r1.Ok);

        // overwrite=false must NOT clobber an already-set value.
        var r2 = await McpWriteTools.SetSecurityClassification(
            Guard, repo, ledger.LedgerId, secId, assetClass: "fixed_income", overwrite: false);
        Assert.True(r2.Ok);
        await using (var read = _fixture.NewDbContext())
            Assert.Equal("equity",
                (await new SecuritiesRepository(read).GetByIdAsync(ledger.LedgerId, secId))!.AssetClass);

        // overwrite=true is the deliberate correction.
        var r3 = await McpWriteTools.SetSecurityClassification(
            Guard, repo, ledger.LedgerId, secId, assetClass: "real_assets", overwrite: true);
        Assert.True(r3.Ok);
        await using (var read = _fixture.NewDbContext())
            Assert.Equal("real_assets",
                (await new SecuritiesRepository(read).GetByIdAsync(ledger.LedgerId, secId))!.AssetClass);
    }

    [Fact]
    public async Task SetSecurityClassification_dryRun_previews_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var secId = await ledger.AddSecurityAsync("Other Fund", "FND2", assetClass: null);

        await using var db = _fixture.NewDbContext();
        var result = await McpWriteTools.SetSecurityClassification(
            Guard, new SecuritiesRepository(db), ledger.LedgerId, secId,
            assetClass: "equity", dryRun: true);
        Assert.True(result.Ok);
        Assert.Contains("asset=equity", result.After);

        await using var read = _fixture.NewDbContext();
        Assert.Null((await new SecuritiesRepository(read).GetByIdAsync(ledger.LedgerId, secId))!.AssetClass);
    }

    [Fact]
    public async Task SetTransactionCategory_recategorizes_a_simple_transaction()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Dining", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var (fromLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -25m, Utc(10));
        var headerId = await ledger.ResolveHeaderIdAsync(fromLeg);

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db)
            .RecategorizeAsync(ledger.LedgerId, headerId, target.Id, dryRun: false);

        Assert.Equal(TransactionsRepository.RecategorizeResult.Ok, outcome.Result);
        Assert.Equal("Dining", outcome.BeforeCategory);
        Assert.Equal("Groceries", outcome.AfterCategory);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(0, await read.TxnLegs
            .CountAsync(l => l.HeaderId == headerId && l.AccountId == source.Id));
        Assert.Equal(1, await read.TxnLegs
            .CountAsync(l => l.HeaderId == headerId && l.AccountId == target.Id));
    }

    [Fact]
    public async Task SetTransactionCategory_dryRun_previews_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Dining", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var (fromLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -25m, Utc(13));
        var headerId = await ledger.ResolveHeaderIdAsync(fromLeg);

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db)
            .RecategorizeAsync(ledger.LedgerId, headerId, target.Id, dryRun: true);
        Assert.Equal(TransactionsRepository.RecategorizeResult.Ok, outcome.Result);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(1, await read.TxnLegs
            .CountAsync(l => l.HeaderId == headerId && l.AccountId == source.Id));  // unchanged
    }

    [Fact]
    public async Task SetTransactionCategory_rejects_split_and_transfer()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var cat2 = await ledger.AddCategoryAsync("Bills", "expense");

        // Transfer: both legs are non-category → no category leg to move.
        var (xferLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, savings.Id, -100m, Utc(11));
        var xferHeader = await ledger.ResolveHeaderIdAsync(xferLeg);
        // Split: two postings → ambiguous which to recategorize.
        var (_, splitHeader) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (cat.Id, 10m), (cat2.Id, 20m) }, Utc(12));

        await using var db = _fixture.NewDbContext();
        var repo = new TransactionsRepository(db);

        Assert.Equal(TransactionsRepository.RecategorizeResult.NoCategoryLeg,
            (await repo.RecategorizeAsync(ledger.LedgerId, xferHeader, cat.Id, false)).Result);
        Assert.Equal(TransactionsRepository.RecategorizeResult.IsSplit,
            (await repo.RecategorizeAsync(ledger.LedgerId, splitHeader, cat.Id, false)).Result);
    }

    // --- Split-posting recategorize (ADR-0068 slice E): repoint the fromCategory
    //     posting(s) of one or many SPLITS to a new category, leaving the rest;
    //     bank-shape only; best-effort bulk with a reject list. ---

    [Fact]
    public async Task SetSplitPostingCategory_repoints_the_fromCategory_posting_and_leaves_the_rest()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var target = await ledger.AddCategoryAsync("Target", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        // Split: posting 0 → Groceries $30, posting 1 → Target $20.
        var (_, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (groceries.Id, 30m), (target.Id, 20m) }, Utc(9));

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db).RecategorizeSplitPostingsAsync(
            ledger.LedgerId, headerId, groceries.Id, dining.Id, dryRun: false);

        Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.Moved);
        Assert.Equal("Groceries", outcome.FromCategory);
        Assert.Equal("Dining", outcome.ToCategory);

        await using var read = _fixture.NewDbContext();
        var catNames = await (
            from l in read.TxnLegs
            join a in read.Accounts on l.AccountId equals a.Id
            where l.HeaderId == headerId && a.AccountType == "category"
            select a.Name).ToListAsync();
        // The Groceries posting is now Dining; the Target posting is untouched.
        Assert.Equal(2, catNames.Count);
        Assert.Contains("Dining", catNames);
        Assert.Contains("Target", catNames);
        Assert.DoesNotContain("Groceries", catNames);
    }

    [Fact]
    public async Task SetSplitPostingCategory_moves_every_fromCategory_posting_in_a_header()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var target = await ledger.AddCategoryAsync("Target", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        // TWO postings on Groceries ($10, $20) + one on Target ($5). A re-home moves both.
        var (_, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (groceries.Id, 10m), (groceries.Id, 20m), (target.Id, 5m) }, Utc(9));

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db).RecategorizeSplitPostingsAsync(
            ledger.LedgerId, headerId, groceries.Id, dining.Id, dryRun: false);

        Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok, outcome.Result);
        Assert.Equal(2, outcome.Moved);   // both Groceries postings

        await using var read = _fixture.NewDbContext();
        var catNames = await (
            from l in read.TxnLegs
            join a in read.Accounts on l.AccountId equals a.Id
            where l.HeaderId == headerId && a.AccountType == "category"
            select a.Name).ToListAsync();
        Assert.DoesNotContain("Groceries", catNames);
        Assert.Equal(2, catNames.Count(n => n == "Dining"));   // both re-homed to Dining
        Assert.Contains("Target", catNames);
    }

    [Fact]
    public async Task SetSplitPostingCategory_rejects_a_non_split_and_an_investment()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        var (simpleLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -40m, Utc(9));
        var simpleHeader = await ledger.ResolveHeaderIdAsync(simpleLeg);
        var buyLeg = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, sec, 10m, 5m, Utc(10));
        var buyHeader = await ledger.ResolveHeaderIdAsync(buyLeg);

        await using var db = _fixture.NewDbContext();
        var repo = new TransactionsRepository(db);

        // Single-posting bank txn → NotSplit (set_transaction_category handles those).
        Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.NotSplit,
            (await repo.RecategorizeSplitPostingsAsync(
                ledger.LedgerId, simpleHeader, groceries.Id, dining.Id, false)).Result);
        // Investment header (action set) → HeaderNotBankShape; never reshaped.
        Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.HeaderNotBankShape,
            (await repo.RecategorizeSplitPostingsAsync(
                ledger.LedgerId, buyHeader, groceries.Id, dining.Id, false)).Result);
    }

    [Fact]
    public async Task SetSplitPostingCategory_tool_bulk_previews_then_persists_with_rejects()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var target = await ledger.AddCategoryAsync("Target", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        // Two splits each with a Groceries posting + one simple (non-split) txn.
        var (_, split1) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (groceries.Id, 30m), (target.Id, 20m) }, Utc(9));
        var (_, split2) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (groceries.Id, 15m), (target.Id, 5m) }, Utc(10));
        var (simpleLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -40m, Utc(11));
        var simpleHeader = await ledger.ResolveHeaderIdAsync(simpleLeg);
        var ids = new[] { split1, split2, simpleHeader };

        // dryRun: moves 2 (one Groceries posting per split), simple rejected, nothing written.
        await using (var db = _fixture.NewDbContext())
        {
            var preview = await McpWriteTools.SetSplitPostingCategory(
                Guard, new TransactionsRepository(db), ledger.LedgerId, ids,
                groceries.Id, dining.Id, dryRun: true);
            Assert.True(preview.Ok);
            Assert.Equal(2, preview.Moved);
            Assert.Equal("Dining", preview.Category);
            Assert.Contains(preview.Rejects, r => r.HeaderId == simpleHeader && r.Reason == "not-a-split");
        }
        await using (var read = _fixture.NewDbContext())
            Assert.True(await read.TxnLegs.AnyAsync(l => l.HeaderId == split1 && l.AccountId == groceries.Id));

        // Real write.
        await using (var db = _fixture.NewDbContext())
        {
            var result = await McpWriteTools.SetSplitPostingCategory(
                Guard, new TransactionsRepository(db), ledger.LedgerId, ids,
                groceries.Id, dining.Id, dryRun: false);
            Assert.True(result.Ok);
            Assert.Equal(2, result.Moved);
            Assert.Single(result.Rejects);
        }
        await using (var read = _fixture.NewDbContext())
        {
            Assert.False(await read.TxnLegs.AnyAsync(l =>
                (l.HeaderId == split1 || l.HeaderId == split2) && l.AccountId == groceries.Id));
            Assert.Equal(2, await read.TxnLegs.CountAsync(l =>
                (l.HeaderId == split1 || l.HeaderId == split2) && l.AccountId == dining.Id));
        }
    }

    // --- Bulk recategorize (PR A): the tool takes many ids, best-effort, and returns a
    // structured reject list; batch-level failure only for a bad target / empty list. ---

    [Fact]
    public async Task BulkSetTransactionCategory_recategorizes_simple_ones_and_lists_rejects()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var source = await ledger.AddCategoryAsync("Dining", "expense");
        var other = await ledger.AddCategoryAsync("Bills", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");

        var (s1, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -25m, Utc(10));
        var (s2, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -12m, Utc(11));
        var (xfer, _) = await ledger.AddTransactionPairAsync(bank.Id, savings.Id, -100m, Utc(12)); // transfer
        var (_, split) = await ledger.AddMultiSplitAsync(
            bank.Id, new[] { (source.Id, 10m), (other.Id, 20m) }, Utc(13));                        // split
        var h1 = await ledger.ResolveHeaderIdAsync(s1);
        var h2 = await ledger.ResolveHeaderIdAsync(s2);
        var xferH = await ledger.ResolveHeaderIdAsync(xfer);

        await using var db = _fixture.NewDbContext();
        var result = await McpWriteTools.SetTransactionCategory(
            Guard, new TransactionsRepository(db), ledger.LedgerId,
            new[] { h1, h2, xferH, split }, target.Id, dryRun: false);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Recategorized);
        Assert.Equal("Groceries", result.Category);
        Assert.Equal(2, result.Rejects.Count);
        Assert.Contains(result.Rejects, r => r.HeaderId == xferH && r.Reason == "transfer");
        Assert.Contains(result.Rejects, r => r.HeaderId == split && r.Reason == "split");

        await using var read = _fixture.NewDbContext();
        // The two simple txns' category legs moved to the target; the split's stayed put.
        Assert.Equal(2, await read.TxnLegs
            .CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == target.Id));
    }

    [Fact]
    public async Task BulkSetTransactionCategory_rejects_whole_call_when_target_is_not_a_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Dining", "expense");
        var (s1, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -25m, Utc(14));
        var h1 = await ledger.ResolveHeaderIdAsync(s1);

        await using var db = _fixture.NewDbContext();
        // Target is a bank account, not a category → batch-level reject, nothing written.
        var result = await McpWriteTools.SetTransactionCategory(
            Guard, new TransactionsRepository(db), ledger.LedgerId, new[] { h1 }, bank.Id, dryRun: false);

        Assert.False(result.Ok);
        Assert.Equal("target-category-not-in-ledger", result.Error);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(1, await read.TxnLegs
            .CountAsync(l => l.HeaderId == h1 && l.AccountId == source.Id)); // untouched
    }

    [Fact]
    public async Task BulkSetTransactionCategory_dryRun_previews_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Dining", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var (s1, _) = await ledger.AddTransactionPairAsync(bank.Id, source.Id, -25m, Utc(16));
        var h1 = await ledger.ResolveHeaderIdAsync(s1);

        await using var db = _fixture.NewDbContext();
        var result = await McpWriteTools.SetTransactionCategory(
            Guard, new TransactionsRepository(db), ledger.LedgerId, new[] { h1 }, target.Id, dryRun: true);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Equal(1, result.Recategorized);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(1, await read.TxnLegs
            .CountAsync(l => l.HeaderId == h1 && l.AccountId == source.Id)); // unchanged
    }

    // --- Tag lifecycle (PR A) ---

    [Fact]
    public async Task RenameTag_renames_in_place_and_rejects_a_name_clash()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var work = await ledger.AddBareTagAsync("work");
        await ledger.AddBareTagAsync("personal");

        await using var db = _fixture.NewDbContext();
        var repo = new TagsRepository(db);

        Assert.True((await McpWriteTools.RenameTag(Guard, repo, ledger.LedgerId, work, newName: "job")).Ok);
        // Renaming onto an existing name is refused (merge_tags is the tool for that).
        Assert.False((await McpWriteTools.RenameTag(Guard, repo, ledger.LedgerId, work, newName: "personal")).Ok);

        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Tags.AnyAsync(t => t.Id == work && t.Name == "job"));
        Assert.True(await read.Tags.AnyAsync(t => t.Name == "personal" && t.LedgerId == ledger.LedgerId));
    }

    [Fact]
    public async Task MergeTags_repoints_assignments_and_deletes_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(17));
        var (leg2, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(18));
        var h1 = await ledger.ResolveHeaderIdAsync(leg1);
        var reimburse = await ledger.AddTagAsync(leg1, "reimburse");      // source, on h1
        var reimbursable = await ledger.AddTagAsync(leg2, "reimbursable"); // target, on h2

        await using var db = _fixture.NewDbContext();
        var result = await McpWriteTools.MergeTags(
            Guard, new TagsRepository(db), ledger.LedgerId, reimburse, reimbursable);
        Assert.True(result.Ok);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.Tags.AnyAsync(t => t.Id == reimburse));                       // source gone
        Assert.True(await read.TxnHeaderTags.AnyAsync(x => x.HeaderId == h1 && x.TagId == reimbursable)); // repointed
    }

    [Fact]
    public async Task DeleteTag_removes_the_tag_and_cascades_its_assignments()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(19));
        var temp = await ledger.AddTagAsync(leg, "temp");

        await using var db = _fixture.NewDbContext();
        Assert.True((await McpWriteTools.DeleteTag(Guard, new TagsRepository(db), ledger.LedgerId, temp)).Ok);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.Tags.AnyAsync(t => t.Id == temp));            // gone
        Assert.False(await read.TxnHeaderTags.AnyAsync(x => x.TagId == temp)); // assignment cascaded
    }

    [Fact]
    public async Task CleanupUnusedTags_removes_only_zero_use_tags()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(20));
        var used = await ledger.AddTagAsync(leg, "used");
        await ledger.AddBareTagAsync("orphan-a");
        await ledger.AddBareTagAsync("orphan-b");

        await using var db = _fixture.NewDbContext();
        var result = await McpWriteTools.CleanupUnusedTags(Guard, new TagsRepository(db), ledger.LedgerId);
        Assert.True(result.Ok);
        Assert.Contains("removed 2", result.After);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(1, await read.Tags.CountAsync(t => t.LedgerId == ledger.LedgerId)); // only 'used' remains
        Assert.True(await read.Tags.AnyAsync(t => t.Id == used));
    }

    // --- Manual prices (PR A) ---

    [Fact]
    public async Task Price_add_update_delete_round_trips_and_marks_manual()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var secId = await ledger.AddSecurityAsync("Some Fund", "FND");

        await using var db = _fixture.NewDbContext();
        var repo = new SecuritiesRepository(db);

        var add = await McpWriteTools.AddPrice(Guard, repo, ledger.LedgerId, secId, Utc(15), close: 100m);
        Assert.True(add.Ok);
        var priceId = add.Id;

        var upd = await McpWriteTools.UpdatePrice(Guard, repo, ledger.LedgerId, secId, priceId, close: 105m);
        Assert.True(upd.Ok);
        await using (var read = _fixture.NewDbContext())
        {
            var row = await read.SecurityPrices.FirstAsync(p => p.Id == priceId);
            Assert.Equal(105m, row.Price);
            Assert.Equal(PriceSource.Manual, row.Source);   // a hand-entered/edited price owns its day
        }

        Assert.True((await McpWriteTools.DeletePrice(Guard, repo, ledger.LedgerId, secId, priceId)).Ok);
        await using (var read = _fixture.NewDbContext())
            Assert.False(await read.SecurityPrices.AnyAsync(p => p.Id == priceId));
    }

    private static AccountsRepository AccountsRepo(AppDbContext db) =>
        new(db, new LegDerivedRecomputeService(db));

    [Fact]
    public async Task CreateCategory_creates_a_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var r = await McpWriteTools.CreateCategory(Guard, AccountsRepo(db), ledger.LedgerId, "Snacks", "expense");
        Assert.True(r.Ok);
        Assert.NotEqual(Guid.Empty, r.Id);

        await using var read = _fixture.NewDbContext();
        var acct = await read.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.Id);
        Assert.NotNull(acct);
        Assert.Equal("Snacks", acct!.Name);
        Assert.Equal("category", acct.AccountType);
        Assert.Equal("expense", acct.CategoryKind);
    }

    [Fact]
    public async Task RenameCategory_renames()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var cat = await ledger.AddCategoryAsync("Old", "expense");
        await using var db = _fixture.NewDbContext();
        var r = await McpWriteTools.RenameCategory(Guard, AccountsRepo(db), ledger.LedgerId, cat.Id, "New");
        Assert.True(r.Ok);

        await using var read = _fixture.NewDbContext();
        Assert.Equal("New", (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == cat.Id)).Name);
    }

    [Fact]
    public async Task ReparentCategory_moves_then_blocks_a_cycle()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var parent = await ledger.AddCategoryAsync("Parent", "expense");
        var child = await ledger.AddCategoryAsync("Child", "expense");
        await using var db = _fixture.NewDbContext();
        var repo = AccountsRepo(db);

        var moved = await McpWriteTools.ReparentCategory(Guard, repo, ledger.LedgerId, child.Id, parent.Id);
        Assert.True(moved.Ok);
        await using (var read = _fixture.NewDbContext())
            Assert.Equal(parent.Id, (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == child.Id)).ParentId);

        // Moving the parent under its own (now) child would close a loop → rejected.
        var cycle = await McpWriteTools.ReparentCategory(Guard, repo, ledger.LedgerId, parent.Id, child.Id);
        Assert.False(cycle.Ok);
        Assert.Equal("WouldCycle", cycle.Error);
    }

    [Fact]
    public async Task UpdateSecurity_sets_ticker_and_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var secId = await ledger.AddSecurityAsync("Old Name", ticker: null);
        await using var db = _fixture.NewDbContext();
        var r = await McpWriteTools.UpdateSecurity(
            Guard, new SecuritiesRepository(db), ledger.LedgerId, secId, ticker: "NEWX", name: "New Name");
        Assert.True(r.Ok);

        await using var read = _fixture.NewDbContext();
        var sec = await new SecuritiesRepository(read).GetByIdAsync(ledger.LedgerId, secId);
        Assert.Equal("NEWX", sec!.Ticker);
        Assert.Equal("New Name", sec.Name);
    }

    [Fact]
    public async Task SetSecurityComponents_sets_lookthrough_sleeves()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var secId = await ledger.AddSecurityAsync("Balanced Fund", "BAL", assetClass: "multi_asset");
        await using var db = _fixture.NewDbContext();
        var sleeves = new[]
        {
            new SecurityComponentDto("equity", "us", 60m),
            new SecurityComponentDto("fixed_income", null, 40m),
        };
        var r = await McpWriteTools.SetSecurityComponents(Guard, new SecuritiesRepository(db), ledger.LedgerId, secId, sleeves);
        Assert.True(r.Ok);

        await using var read = _fixture.NewDbContext();
        var comps = await new SecuritiesRepository(read).GetComponentsAsync(ledger.LedgerId, secId);
        Assert.Equal(2, comps!.Count);
        Assert.Contains(comps, c => c.AssetClass == "equity" && c.Region == "us" && c.Weight == 60m);
        Assert.Contains(comps, c => c.AssetClass == "fixed_income" && c.Weight == 40m);
    }

    [Fact]
    public async Task MergeSecurities_repoints_mappings_and_deactivates_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddSecurityAsync("Dup Fund", "DUP");
        var target = await ledger.AddSecurityAsync("Keeper Fund", "KEEP");
        await using (var seed = _fixture.NewDbContext())
        {
            seed.ProviderSecurityMappings.Add(new ProviderSecurityMappingRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId,
                ProviderKey = "simplefin", ProviderSecurityId = "AAA", SecurityId = source,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.NewDbContext();
        var r = await McpWriteTools.MergeSecurities(Guard, new SecuritiesRepository(db), ledger.LedgerId, source, target, dryRun: false);
        Assert.True(r.Ok);

        await using var read = _fixture.NewDbContext();
        // Source deactivated (reversible), not deleted.
        Assert.False((await read.Securities.AsNoTracking().FirstAsync(s => s.Id == source)).IsActive);
        Assert.True(await read.Securities.AsNoTracking().AnyAsync(s => s.Id == source));
        // Provider mapping repointed to the keeper; none left on the source.
        Assert.Equal(0, await read.ProviderSecurityMappings.CountAsync(m => m.SecurityId == source));
        Assert.Equal(1, await read.ProviderSecurityMappings
            .CountAsync(m => m.SecurityId == target && m.ProviderSecurityId == "AAA"));
    }

    // ---- set_transaction_tags (ADR-0081 D6 — the bulk exception) ----

    private static async Task<List<string>> TagNamesForHeader(AppDbContext db, Guid ledgerId, Guid headerId) =>
        await (from p in db.TxnHeaderTags.AsNoTracking()
               where p.HeaderId == headerId && p.LedgerId == ledgerId
               join t in db.Tags.AsNoTracking() on p.TagId equals t.Id
               select t.Name).ToListAsync();

    [Fact]
    public async Task SetTransactionTags_bulk_tags_many_headers_and_inserts_a_new_tag_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(3));
        var (leg2, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(4));
        var h1 = await ledger.ResolveHeaderIdAsync(leg1);
        var h2 = await ledger.ResolveHeaderIdAsync(leg2);

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db)
            .SetTransactionTagsAsync(ledger.LedgerId, new[] { h1, h2 }, new[] { "reimbursable" }, dryRun: false);

        Assert.Equal(TransactionsRepository.SetTagsResult.Ok, outcome.Result);
        Assert.Equal(2, outcome.HeaderCount);

        await using var read = _fixture.NewDbContext();
        // Both headers carry the tag...
        Assert.Equal(2, await read.TxnHeaderTags.CountAsync(t => t.LedgerId == ledger.LedgerId));
        // ...and the brand-new tag hit the dictionary EXACTLY once (resolve-once across
        // the batch — a per-header re-resolve would have inserted a duplicate row).
        Assert.Equal(1, await read.Tags
            .CountAsync(t => t.LedgerId == ledger.LedgerId && t.Name == "reimbursable"));
    }

    [Fact]
    public async Task SetTransactionTags_is_a_replace_set_and_empty_clears()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(5));
        var h = await ledger.ResolveHeaderIdAsync(leg);

        await using (var db = _fixture.NewDbContext())
            await new TransactionsRepository(db)
                .SetTransactionTagsAsync(ledger.LedgerId, new[] { h }, new[] { "a", "b" }, dryRun: false);

        // Replace: {a,b} -> {b,c}
        await using (var db = _fixture.NewDbContext())
            await new TransactionsRepository(db)
                .SetTransactionTagsAsync(ledger.LedgerId, new[] { h }, new[] { "b", "c" }, dryRun: false);
        await using (var read = _fixture.NewDbContext())
            Assert.Equal(new[] { "b", "c" },
                (await TagNamesForHeader(read, ledger.LedgerId, h)).OrderBy(n => n).ToArray());

        // Clear: [] removes every pairing.
        await using (var db = _fixture.NewDbContext())
            await new TransactionsRepository(db)
                .SetTransactionTagsAsync(ledger.LedgerId, new[] { h }, Array.Empty<string>(), dryRun: false);
        await using (var read = _fixture.NewDbContext())
            Assert.Empty(await TagNamesForHeader(read, ledger.LedgerId, h));
    }

    [Fact]
    public async Task SetTransactionTags_rejects_whole_batch_when_a_header_is_foreign()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(6));
        var h = await ledger.ResolveHeaderIdAsync(leg);
        var foreign = Guid.NewGuid();

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db)
            .SetTransactionTagsAsync(ledger.LedgerId, new[] { h, foreign }, new[] { "x" }, dryRun: false);

        Assert.Equal(TransactionsRepository.SetTagsResult.HeadersNotInLedger, outcome.Result);
        Assert.Contains(foreign, outcome.UnknownHeaderIds);

        // All-or-nothing: the valid header was NOT tagged, no tag row created.
        await using var read = _fixture.NewDbContext();
        Assert.Empty(await TagNamesForHeader(read, ledger.LedgerId, h));
        Assert.Equal(0, await read.Tags.CountAsync(t => t.LedgerId == ledger.LedgerId && t.Name == "x"));
    }

    [Fact]
    public async Task SetTransactionTags_dryRun_reports_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(7));
        var h = await ledger.ResolveHeaderIdAsync(leg);

        await using var db = _fixture.NewDbContext();
        var outcome = await new TransactionsRepository(db)
            .SetTransactionTagsAsync(ledger.LedgerId, new[] { h }, new[] { "preview" }, dryRun: true);

        Assert.Equal(TransactionsRepository.SetTagsResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.HeaderCount);
        Assert.Contains("preview", outcome.Tags);

        await using var read = _fixture.NewDbContext();
        Assert.Empty(await TagNamesForHeader(read, ledger.LedgerId, h));   // nothing written
        Assert.Equal(0, await read.Tags.CountAsync(t => t.LedgerId == ledger.LedgerId && t.Name == "preview"));
    }

    [Fact]
    public async Task SetTransactionTags_tool_summarizes_ok_and_flags_unknown_and_empty()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(8));
        var h = await ledger.ResolveHeaderIdAsync(leg);

        await using var db = _fixture.NewDbContext();
        var repo = new TransactionsRepository(db);

        var ok = await McpWriteTools.SetTransactionTags(Guard, repo, ledger.LedgerId, new[] { h }, new[] { "x" });
        Assert.True(ok.Ok);
        Assert.Contains("tagged 1", ok.After);

        var bad = await McpWriteTools.SetTransactionTags(
            Guard, repo, ledger.LedgerId, new[] { Guid.NewGuid() }, new[] { "x" });
        Assert.False(bad.Ok);
        Assert.Contains("not-in-ledger", bad.Error);

        var empty = await McpWriteTools.SetTransactionTags(
            Guard, repo, ledger.LedgerId, Array.Empty<Guid>(), new[] { "x" });
        Assert.False(empty.Ok);
        Assert.Equal("no-transactions", empty.Error);
    }
}
