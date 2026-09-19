using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Stored budget targets, and the one invariant no constraint can express.
///
/// <para>ADR-0099 D1a: a target may sit on a category OR on its descendants, never
/// both. The reason is arithmetic, not taste — a derived normal rolls up (a parent's
/// normal already describes its subtree, because the aggregation rolled it up) and a
/// TYPED target does not. Allow both and the hero's ceiling, which is the sum over
/// ROOTS, either double-counts the child or silently discards it. One authoritative
/// mark per subtree is what keeps the roots-only sum correct with no special case.</para>
///
/// <para>"No ancestor or descendant holds a target" is a recursive tree predicate, so
/// no CHECK and no unique index can state it and ADR-0032 rules out a trigger. It
/// lives in the repository, which makes it exactly the kind of rule that rots
/// silently — hence this file.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BudgetTargetsTests
{
    private readonly PostgresFixture _fixture;

    public BudgetTargetsTests(PostgresFixture fixture) => _fixture = fixture;

    private BudgetTargetsRepository Repo() => new(_fixture.NewDbContext());

    private static readonly DateOnly Month = new(2026, 3, 1);

    [Fact]
    public async Task A_target_round_trips_and_normalises_to_the_first_of_the_month()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await ledger.AddCategoryAsync("Food");

        // Deliberately mid-month on the way in. The column CHECK only permits the
        // first, so the repository has to normalise — and if it ever stops, this
        // fails on a 23514 rather than quietly storing two rows for one month.
        var outcome = await Repo().SetAsync(
            ledger.LedgerId, food.Id, new DateOnly(2026, 3, 17), 250.00m);
        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, outcome.Result);

        var rows = await Repo().ForMonthAsync(ledger.LedgerId, Month);
        var row = Assert.Single(rows);
        Assert.Equal(food.Id, row.CategoryId);
        Assert.Equal(250.00m, row.Amount);
        Assert.Equal(new DateOnly(2026, 3, 1), row.TargetMonth);
    }

    [Fact]
    public async Task A_targeted_ancestor_refuses_the_child_and_names_the_ancestor()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, taxes.Id, Month, 1000m)).Result);

        var blocked = await Repo().SetAsync(ledger.LedgerId, federal.Id, Month, 600m);

        Assert.Equal(BudgetTargetsRepository.SetResult.AncestorHasTarget, blocked.Result);
        // Naming it is the point: the conflicting category can be several levels up
        // and off screen, and "you cannot set this" alone is not actionable.
        Assert.Equal("Taxes", blocked.ConflictingCategoryName);

        // ...and nothing was written.
        Assert.Single(await Repo().ForMonthAsync(ledger.LedgerId, Month));
    }

    [Fact]
    public async Task A_targeted_descendant_refuses_the_ancestor_and_names_the_descendant()
    {
        // The mirror, and the direction an implementation is likelier to miss:
        // walking UP from the row being set finds nothing, because the conflict is
        // underneath it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, federal.Id, Month, 600m)).Result);

        var blocked = await Repo().SetAsync(ledger.LedgerId, taxes.Id, Month, 1000m);

        Assert.Equal(BudgetTargetsRepository.SetResult.DescendantHasTarget, blocked.Result);
        Assert.Equal("Federal", blocked.ConflictingCategoryName);
    }

    [Fact]
    public async Task The_conflict_reaches_through_an_untargeted_generation()
    {
        // Grandparent -> parent -> child, with the target on the grandchild. A check
        // that only looked one level would let this through, and the ceiling would
        // then count the same money twice.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var home = await ledger.AddCategoryAsync("Home");
        var utilities = await ledger.AddCategoryAsync("Utilities", parentId: home.Id);
        var electric = await ledger.AddCategoryAsync("Electric", parentId: utilities.Id);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, electric.Id, Month, 90m)).Result);

        var blocked = await Repo().SetAsync(ledger.LedgerId, home.Id, Month, 500m);
        Assert.Equal(BudgetTargetsRepository.SetResult.DescendantHasTarget, blocked.Result);
        Assert.Equal("Electric", blocked.ConflictingCategoryName);
    }

    [Fact]
    public async Task Replacing_your_own_target_is_an_edit_and_not_a_conflict()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await ledger.AddCategoryAsync("Food");

        await Repo().SetAsync(ledger.LedgerId, food.Id, Month, 250m);
        var again = await Repo().SetAsync(ledger.LedgerId, food.Id, Month, 275m);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, again.Result);
        var row = Assert.Single(await Repo().ForMonthAsync(ledger.LedgerId, Month));
        Assert.Equal(275m, row.Amount);
    }

    [Fact]
    public async Task Siblings_may_both_hold_targets()
    {
        // The invariant is about ANCESTRY, not about the tree being flat. Getting
        // this wrong would make the feature nearly unusable.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var home = await ledger.AddCategoryAsync("Home");
        var gas = await ledger.AddCategoryAsync("Gas", parentId: home.Id);
        var water = await ledger.AddCategoryAsync("Water", parentId: home.Id);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, gas.Id, Month, 60m)).Result);
        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, water.Id, Month, 40m)).Result);

        Assert.Equal(2, (await Repo().ForMonthAsync(ledger.LedgerId, Month)).Count);
    }

    [Fact]
    public async Task A_target_in_another_month_never_conflicts()
    {
        // The invariant is per MONTH. Last month's parent target must not block this
        // month's child target, or the feature would seize up after one use.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);

        await Repo().SetAsync(ledger.LedgerId, taxes.Id, new DateOnly(2026, 2, 1), 1000m);
        var next = await Repo().SetAsync(ledger.LedgerId, federal.Id, Month, 600m);

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, next.Result);
    }

    [Fact]
    public async Task Deleting_clears_the_way_for_a_descendant()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);

        await Repo().SetAsync(ledger.LedgerId, taxes.Id, Month, 1000m);
        Assert.True(await Repo().DeleteAsync(ledger.LedgerId, taxes.Id, Month));

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, federal.Id, Month, 600m)).Result);

        // Deleting what is not there is not an error: ADR-0099 D1 makes PRESENCE the
        // state, so "no target" is a legitimate destination and a caller clearing an
        // already-clear cell has got what it asked for.
        Assert.False(await Repo().DeleteAsync(ledger.LedgerId, taxes.Id, Month));
    }

    [Fact]
    public async Task A_real_account_cannot_hold_a_target()
    {
        // The composite FK points at accounts(id, ledger_id) and categories ARE rows
        // in accounts, so the database would happily take this.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");

        var outcome = await Repo().SetAsync(ledger.LedgerId, bank.Id, Month, 100m);
        Assert.Equal(BudgetTargetsRepository.SetResult.NotACategory, outcome.Result);
        Assert.Empty(await Repo().ForMonthAsync(ledger.LedgerId, Month));
    }

    [Fact]
    public async Task An_unknown_category_is_refused_rather_than_written()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var outcome = await Repo().SetAsync(ledger.LedgerId, Guid.NewGuid(), Month, 100m);
        Assert.Equal(BudgetTargetsRepository.SetResult.CategoryNotInLedger, outcome.Result);
    }

    [Fact]
    public async Task A_bulk_fill_applies_the_parent_and_reports_the_skipped_child()
    {
        // Copying a month whose hierarchy has since been reparented can put an
        // ancestor and a descendant in the SAME batch. The batch must be validated
        // against itself, not only against what is already stored — and the outcome
        // must not depend on the order the caller happened to send.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);
        var food = await ledger.AddCategoryAsync("Food");

        // Child listed FIRST, to prove ordering is imposed rather than inherited.
        var outcomes = await Repo().SetManyAsync(ledger.LedgerId, Month, new[]
        {
            (federal.Id, 600m),
            (taxes.Id, 1000m),
            (food.Id, 250m),
        });

        var byId = outcomes.ToDictionary(o => o.CategoryId);
        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, byId[taxes.Id].Result);
        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, byId[food.Id].Result);
        Assert.Equal(BudgetTargetsRepository.SetResult.AncestorHasTarget, byId[federal.Id].Result);
        Assert.Equal("Taxes", byId[federal.Id].ConflictingCategoryName);

        // Written: the parent and the unrelated root. The child was skipped, not
        // written as a violation and not silently dropped from the report.
        var stored = await Repo().ForMonthAsync(ledger.LedgerId, Month);
        Assert.Equal(2, stored.Count);
        Assert.DoesNotContain(stored, r => r.CategoryId == federal.Id);
    }

    // -----------------------------------------------------------------
    // The D1a mark: what the bar's tick sits at, and what the ceiling sums.
    // -----------------------------------------------------------------

    private BudgetProgressRepository Progress() =>
        new(new ReportingRepository(_fixture.NewDbContext()), _fixture.NewDbContext());

    private static DateTime JustInside(int year, int month) =>
        new(year, month, 1, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_untargeted_parent_absorbs_its_childs_typed_number()
    {
        // THE arithmetic D1a exists for. A parent's rolled-up normal already
        // CONTAINS its child's normal, so substituting the child's typed target
        // means removing what was counted and adding what was decided. Summing
        // both numbers instead is the 1.87x ceiling bug in a new costume.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var home = await ledger.AddCategoryAsync("Home");
        var electric = await ledger.AddCategoryAsync("Electric", parentId: home.Id);

        // Three trailing months: 70/mo direct to Home, 30/mo to Electric.
        foreach (var m in new[] { 1, 2, 3 })
        {
            await ledger.AddTransactionPairAsync(bank.Id, home.Id, -70m, JustInside(2026, m), payee: "h");
            await ledger.AddTransactionPairAsync(bank.Id, electric.Id, -30m, JustInside(2026, m), payee: "e");
        }

        var before = await Progress().GetAsync(ledger.LedgerId, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 3);
        var homeBefore = before.Rows.Single(r => r.CategoryId == home.Id);
        Assert.Equal(100m, homeBefore.Typical);        // rolled: 70 direct + 30 child
        Assert.Null(homeBefore.Target);
        Assert.Equal(100m, homeBefore.Mark);           // no targets yet: mark == typical

        // Type 50 on the CHILD.
        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(ledger.LedgerId, electric.Id, new DateOnly(2026, 4, 1), 50m)).Result);

        var after = await Progress().GetAsync(ledger.LedgerId, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 3);
        var homeAfter = after.Rows.Single(r => r.CategoryId == home.Id);
        var electricAfter = after.Rows.Single(r => r.CategoryId == electric.Id);

        // The child speaks for itself...
        Assert.Equal(50m, electricAfter.Target);
        Assert.Equal(50m, electricAfter.Mark);
        // ...and the parent absorbs it: 100 - 30 + 50.
        Assert.Null(homeAfter.Target);
        Assert.Equal(100m, homeAfter.Typical);         // the DESCRIPTION is unchanged
        Assert.Equal(120m, homeAfter.Mark);            // the MARK moved
    }

    [Fact]
    public async Task A_targeted_row_marks_at_its_target_and_ignores_its_history()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food");
        foreach (var m in new[] { 1, 2, 3 })
            await ledger.AddTransactionPairAsync(bank.Id, food.Id, -90m, JustInside(2026, m), payee: "f");

        await Repo().SetAsync(ledger.LedgerId, food.Id, new DateOnly(2026, 4, 1), 40m);

        var res = await Progress().GetAsync(ledger.LedgerId, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 3);
        var row = res.Rows.Single(r => r.CategoryId == food.Id);

        Assert.Equal(90m, row.Typical);   // history still described honestly
        Assert.Equal(40m, row.Target);
        Assert.Equal(40m, row.Mark);      // but the decision wins
    }

    [Fact]
    public async Task A_target_on_a_category_with_no_activity_still_renders_a_row()
    {
        // Otherwise typing a number against a quiet category looks like the write
        // failed: the category appears in neither summary, so nothing would come
        // back for it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var quiet = await ledger.AddCategoryAsync("Sabbatical");

        await Repo().SetAsync(ledger.LedgerId, quiet.Id, new DateOnly(2026, 4, 1), 500m);

        var res = await Progress().GetAsync(ledger.LedgerId, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 3);
        var row = res.Rows.Single(r => r.CategoryId == quiet.Id);

        Assert.Equal(0m, row.Actual);
        Assert.Null(row.Typical);
        Assert.Equal(500m, row.Target);
        Assert.Equal(500m, row.Mark);
    }

    // -----------------------------------------------------------------
    // Lifecycle: a merge and a reparent can each manufacture a D1a
    // violation out of two states that were individually legal.
    // -----------------------------------------------------------------

    private AccountsRepository Accounts()
    {
        var db = _fixture.NewDbContext();
        return new AccountsRepository(db, new LegDerivedRecomputeService(db));
    }

    [Fact]
    public async Task A_merge_sums_targets_that_collide_on_the_same_month()
    {
        // ADR-0099 D2 calls a stored target frozen, and this moves one anyway. The
        // justification is that a merge is a new explicit instruction to treat the
        // two categories as ONE: it repoints every leg with no date predicate, so
        // the destination's actuals for months long past grow by the source's spend
        // the instant it commits. Leaving the mark alone would make each of those
        // months read as newly over budget for a reason nobody chose.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddCategoryAsync("Groceries");
        var dest = await ledger.AddCategoryAsync("Food shopping");

        await Repo().SetAsync(ledger.LedgerId, source.Id, Month, 300m);
        await Repo().SetAsync(ledger.LedgerId, dest.Id, Month, 500m);
        // A month only the source has, to prove a non-colliding target is moved
        // rather than merely dropped.
        await Repo().SetAsync(ledger.LedgerId, source.Id, new DateOnly(2026, 2, 1), 120m);

        var outcome = await Accounts().MergeCategoryAsync(
            ledger.LedgerId, source.Id, dest.Id, dryRun: false);
        Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        Assert.Equal(2, outcome.BudgetTargetsMoved);

        var march = await Repo().ForMonthAsync(ledger.LedgerId, Month);
        var row = Assert.Single(march);
        Assert.Equal(dest.Id, row.CategoryId);
        Assert.Equal(800m, row.Amount);          // 500 + 300, not 500 and not 300

        var feb = Assert.Single(await Repo().ForMonthAsync(ledger.LedgerId, new DateOnly(2026, 2, 1)));
        Assert.Equal(dest.Id, feb.CategoryId);
        Assert.Equal(120m, feb.Amount);
    }

    [Fact]
    public async Task A_merge_drops_an_arriving_childs_target_that_the_destination_now_covers()
    {
        // Both sides were legal before: the child sat under the SOURCE, which held
        // no target, and the destination's target covered a different subtree.
        // Reparenting the child under the destination is what creates the clash,
        // so the merge has to resolve it — and report it, rather than lose a typed
        // number quietly.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddCategoryAsync("Utilities");
        var child = await ledger.AddCategoryAsync("Electric", parentId: source.Id);
        var dest = await ledger.AddCategoryAsync("Home");

        await Repo().SetAsync(ledger.LedgerId, child.Id, Month, 90m);
        await Repo().SetAsync(ledger.LedgerId, dest.Id, Month, 400m);

        var outcome = await Accounts().MergeCategoryAsync(
            ledger.LedgerId, source.Id, dest.Id, dryRun: false);

        Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.BudgetTargetsDropped);

        // The ancestor wins, matching the bulk-fill rule.
        var row = Assert.Single(await Repo().ForMonthAsync(ledger.LedgerId, Month));
        Assert.Equal(dest.Id, row.CategoryId);
        Assert.Equal(400m, row.Amount);
    }

    [Fact]
    public async Task A_reparent_that_would_stack_two_targets_is_refused()
    {
        // A move is not a combine. It carries no instruction to treat anything as
        // one thing, so unlike the merge it refuses rather than picking a winner
        // and discarding somebody's number.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var home = await ledger.AddCategoryAsync("Home");
        var loose = await ledger.AddCategoryAsync("Electric");

        await Repo().SetAsync(ledger.LedgerId, home.Id, Month, 400m);
        await Repo().SetAsync(ledger.LedgerId, loose.Id, Month, 90m);

        var result = await Accounts().ReparentCategoryAsync(
            ledger.LedgerId, loose.Id, home.Id, dryRun: false);

        Assert.Equal(AccountsRepository.ReparentCategoryResult.BudgetTargetConflict, result);

        // Nothing moved, and nothing was cleared on the way to finding out.
        Assert.Equal(2, (await Repo().ForMonthAsync(ledger.LedgerId, Month)).Count);
    }

    [Fact]
    public async Task The_reparent_refusal_also_fires_on_a_dry_run()
    {
        // A dry run that answers Ok and then fails for real is worse than having no
        // dry run: the MCP tools and the move dialog both use it to pre-check.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var home = await ledger.AddCategoryAsync("Home");
        var loose = await ledger.AddCategoryAsync("Electric");
        await Repo().SetAsync(ledger.LedgerId, home.Id, Month, 400m);
        await Repo().SetAsync(ledger.LedgerId, loose.Id, Month, 90m);

        Assert.Equal(
            AccountsRepository.ReparentCategoryResult.BudgetTargetConflict,
            await Accounts().ReparentCategoryAsync(
                ledger.LedgerId, loose.Id, home.Id, dryRun: true));
    }

    [Fact]
    public async Task A_reparent_is_allowed_when_the_targets_are_in_different_months()
    {
        // The invariant is per MONTH. Refusing across months would make the tree
        // unreorganisable for anyone who has ever set a target.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var home = await ledger.AddCategoryAsync("Home");
        var loose = await ledger.AddCategoryAsync("Electric");

        await Repo().SetAsync(ledger.LedgerId, home.Id, new DateOnly(2026, 2, 1), 400m);
        await Repo().SetAsync(ledger.LedgerId, loose.Id, Month, 90m);

        Assert.Equal(
            AccountsRepository.ReparentCategoryResult.Ok,
            await Accounts().ReparentCategoryAsync(
                ledger.LedgerId, loose.Id, home.Id, dryRun: false));
    }

    [Fact]
    public async Task Row_level_security_hides_another_users_targets()
    {
        // The rest of this suite runs as coffer_service, which is BYPASSRLS — so
        // every assertion above would pass with NO policies on the table at all.
        // Nothing in CI, preflight or the schema guards enumerates relrowsecurity,
        // and a table added after mig 174 gets no protection unless it declares the
        // pair itself (ALTER DEFAULT PRIVILEGES already grants coffer_app write, so
        // "no policy" means "no refusal"). mig 207 shipped two tables without RLS
        // and it survived to mig 213. This is the assertion that would have caught it.
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var stranger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await mine.AddCategoryAsync("Food");

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok,
            (await Repo().SetAsync(mine.LedgerId, food.Id, Month, 250m)).Result);

        await using var asStranger = _fixture.NewAppDbContextAsUser(stranger.UserId);
        var visible = await asStranger.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == mine.LedgerId)
            .ToListAsync();

        Assert.Empty(visible);

        // And the owner still sees their own, so the policy filters by grant rather
        // than refusing everything — a policy that denied unconditionally would also
        // make the assertion above pass.
        await using var asOwner = _fixture.NewAppDbContextAsUser(mine.UserId);
        Assert.Single(await asOwner.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == mine.LedgerId).ToListAsync());
    }
}
