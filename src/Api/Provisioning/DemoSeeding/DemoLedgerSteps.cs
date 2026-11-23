using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The examples steps that set up the Demo's accounts, securities and budget: its account
/// folders, its investment settings and its budget targets (ADR-0115 D3).
/// </summary>
/// <remarks>
/// Each step writes through the repository the app's own endpoint writes through, after
/// the checks that endpoint makes and its repository leaves to it, and treats any outcome
/// but Ok as the step's failure. Every id comes from the first step's match of the
/// manifest, and every date from the Demo's last day.
/// </remarks>
internal static class DemoLedgerSteps
{
    /// <summary>The generator works in cents of the Demo's one currency.</summary>
    private const decimal CentsPerUnit = 100m;

    /// <summary>How many months of budget targets the Demo carries: the year ending in
    /// its last day's month.</summary>
    internal const int BudgetMonths = 12;

    /// <summary>
    /// Step 12: the manifest's account folders, each at the top level of its account type,
    /// with its members moved in. The name passes the endpoint's name rule first; the
    /// repository refuses a member of another type, a category or a system account.
    /// </summary>
    internal static async Task FoldersAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var manifest = run.Context.Manifest;
        if (manifest.Folders.Count == 0) throw new DemoSeedStepException("the manifest names no folder");

        var folders = new AccountFoldersRepository(db);
        foreach (var folder in manifest.Folders)
        {
            var name = AccountFoldersRepository.CheckName(folder.Name);
            if (name != AccountFoldersRepository.NameCheck.Ok)
                throw new DemoSeedStepException($"the folder name '{folder.Name}' is refused: {name}");

            var (created, row) = await folders
                .CreateAsync(run.LedgerId, folder.AccountType, folder.Name, parentId: null, cancellationToken)
                .ConfigureAwait(false);
            if (created != AccountFoldersRepository.CreateResult.Ok)
                throw new DemoSeedStepException($"the folder '{folder.Name}' was refused: {created}");

            foreach (var mdId in folder.AccountIds)
            {
                var placed = await folders
                    .SetAccountFolderAsync(run.LedgerId, run.Ids.AccountsByMdId[mdId], row!.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (placed != AccountFoldersRepository.SetFolderResult.Ok)
                    throw new DemoSeedStepException(
                        $"the account with the Moneydance id {mdId} was refused by the folder '{folder.Name}': {placed}");
            }
        }
    }

    /// <summary>
    /// Step 13: the investment settings an export cannot carry. The brokerage treats its
    /// fees as trade commission, after the endpoint's check that the account is the
    /// ledger's; the target-date fund gets its look-through; and the stable-value fund,
    /// which has no ticker, gets a hand-entered price at each quarter's end since its first
    /// buy. No security's automatic pricing is changed.
    /// </summary>
    internal static async Task InvestmentsAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var ledgerId = run.LedgerId;
        var manifest = run.Context.Manifest;

        var accounts = new AccountsRepository(db, new LegDerivedRecomputeService(db), run.Clock);
        var brokerage = run.Ids.Accounts[DemoAccountRoles.Brokerage];
        if (!await accounts.BelongsToLedgerAsync(ledgerId, brokerage, cancellationToken).ConfigureAwait(false))
            throw new DemoSeedStepException("the brokerage is not in the Demo's ledger");
        var flag = await accounts.SetIsTradeCommissionAsync(ledgerId, brokerage, isTradeCommission: true, cancellationToken)
            .ConfigureAwait(false);
        if (flag != AccountsRepository.SetIsTradeCommissionResult.Ok)
            throw new DemoSeedStepException($"the brokerage's trade-commission setting was refused: {flag}");

        var securities = new SecuritiesRepository(db, run.Clock);
        var components = DemoSeedContent.LookThrough(manifest.TargetDateLookThrough);
        var lookThrough = await securities
            .ReplaceComponentsAsync(ledgerId, run.Ids.Securities[DemoSecurities.TargetDate], components, cancellationToken)
            .ConfigureAwait(false);
        if (lookThrough != SecuritiesRepository.ComponentsResult.Ok)
            throw new DemoSeedStepException($"the target-date fund's look-through was refused: {lookThrough}");

        var stableValue = run.Ids.Securities[DemoSecurities.StableValue];
        var firstBuy = await FirstBuyAsync(db, ledgerId, stableValue, cancellationToken).ConfigureAwait(false)
            ?? throw new DemoSeedStepException("the stable-value fund was never bought");
        var prices = DemoSeedContent.QuarterEndPrices(manifest.StableValueMonthEnds, firstBuy, run.AsOf);
        if (prices.Count == 0)
            throw new DemoSeedStepException("the manifest gives the stable-value fund no quarter-end value");
        foreach (var price in prices)
        {
            var added = await securities.AddPriceAsync(
                    ledgerId, stableValue,
                    new CreateSecurityPriceRequest { Price = price.UnitValue, PriceDate = price.Date },
                    cancellationToken)
                .ConfigureAwait(false);
            if (added.Kind != SecuritiesRepository.AddPriceResult.Ok)
                throw new DemoSeedStepException(
                    $"the stable-value fund's price for {price.Date:yyyy-MM-dd} was refused: {added.Kind}");
        }
    }

    /// <summary>
    /// Step 14: the manifest's budget targets, one write each, for the twelve months
    /// ending in the Demo's last day's month. Every target is checked before the first is
    /// written: an expense category, a month of that year, and an amount the endpoint
    /// would take. The repository then refuses a target above or below one already set in
    /// the same subtree and month.
    /// </summary>
    internal static async Task BudgetsAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var targets = run.Context.Manifest.BudgetTargets;
        if (targets.Count == 0) throw new DemoSeedStepException("the manifest names no budget target");

        var months = BudgetMonthsEndingIn(run.AsOf);
        var planned = new List<(DemoCategoryRef Category, Guid CategoryId, DateOnly Month, decimal Amount)>();
        foreach (var target in targets)
        {
            if (target.Category.Type != MdCategoryType.Expense)
                throw new DemoSeedStepException($"the budget target on {target.Category} is not on an expense category");
            var month = new DateOnly(target.Year, target.Month, 1);
            if (!months.Contains(month))
                throw new DemoSeedStepException(
                    $"the budget target on {target.Category} for {Month(month)} is outside the {BudgetMonths} months "
                    + $"ending in {Month(run.AsOf)}");
            var amount = target.Amount / CentsPerUnit;
            if (!BudgetEndpoints.IsAllowedTargetAmount(amount))
                throw new DemoSeedStepException(
                    $"the budget target on {target.Category} for {Month(month)} is negative");
            planned.Add((target.Category, run.Ids.Category(target.Category), month, amount));
        }

        var repository = new BudgetTargetsRepository(db, run.Clock);
        foreach (var (category, categoryId, month, amount) in planned)
        {
            var outcome = await repository.SetAsync(run.LedgerId, categoryId, month, amount, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Result != BudgetTargetsRepository.SetResult.Ok)
                throw new DemoSeedStepException(
                    $"the budget target on {category} for {Month(month)} was refused: {outcome.Result}"
                    + (outcome.ConflictingCategoryName is { } other ? $" (by '{other}')" : string.Empty));
        }
    }

    /// <summary>The first day of each of the <see cref="BudgetMonths"/> months ending in
    /// <paramref name="asOf"/>'s month, oldest first.</summary>
    internal static IReadOnlyList<DateOnly> BudgetMonthsEndingIn(DateOnly asOf)
    {
        var last = new DateOnly(asOf.Year, asOf.Month, 1);
        return Enumerable.Range(0, BudgetMonths).Select(i => last.AddMonths(i - (BudgetMonths - 1))).ToList();
    }

    /// <summary>The day the ledger first bought <paramref name="securityId"/>, or null when
    /// it never did.</summary>
    private static async Task<DateOnly?> FirstBuyAsync(
        AppDbContext db, Guid ledgerId, Guid securityId, CancellationToken cancellationToken)
    {
        var first = await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == ledgerId && leg.SecurityId == securityId && leg.Quantity > 0m
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                select (DateTime?)header.PostedAt)
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);
        // A posting's date, read from the row it is stored on; nothing here reads a clock.
        return first is { } at ? new DateOnly(at.Year, at.Month, at.Day) : null;
    }

    private static string Month(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
