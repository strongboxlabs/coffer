using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The Coffer ids of everything the manifest names, found in the imported Demo by the
/// examples phase's first step. Every later step reads its ids from here.
/// </summary>
public sealed record DemoSeedIds
{
    /// <summary>Accounts by role (<see cref="DemoAccountRoles"/>).</summary>
    public required IReadOnlyDictionary<string, Guid> Accounts { get; init; }

    /// <summary>Accounts by Moneydance id: every account the manifest names.</summary>
    public required IReadOnlyDictionary<string, Guid> AccountsByMdId { get; init; }

    /// <summary>Categories by role (<see cref="DemoManifestBuilder.CategoryRoles"/>).</summary>
    public required IReadOnlyDictionary<string, Guid> Categories { get; init; }

    /// <summary>Categories by path (<see cref="DemoCategoryRef.ToString"/>): every
    /// category the manifest names, the budget targets' included.</summary>
    public required IReadOnlyDictionary<string, Guid> CategoriesByPath { get; init; }

    /// <summary>Transactions by Moneydance txn id: every row the manifest names.</summary>
    public required IReadOnlyDictionary<string, Guid> Transactions { get; init; }

    /// <summary>Securities by <see cref="DemoSecurity.Key"/>: the target-date fund, its
    /// look-through components and the stable-value fund.</summary>
    public required IReadOnlyDictionary<string, Guid> Securities { get; init; }

    /// <summary>The imported monthly Internet reminder.</summary>
    public required Guid InternetReminder { get; init; }

    /// <summary>The category <paramref name="category"/> names.</summary>
    public Guid Category(DemoCategoryRef category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return CategoriesByPath[category.ToString()];
    }
}

/// <summary>
/// Finds the imported Demo's rows by what the manifest names them by, LINQ only: accounts
/// and categories by their Moneydance id, transactions by their Moneydance txn id,
/// reminders by their Moneydance reminder id, and securities by ticker, or by exact name
/// for the one fund with no ticker.
/// </summary>
/// <remarks>
/// Every lookup must find exactly one row in the Demo's ledger. One that finds none or
/// several fails the step, which then names every lookup that did, so no later step works
/// from a guess. A field the manifest leaves empty fails it too: the generator fills every
/// field, so an empty one is a defect upstream, not an example to leave out.
/// </remarks>
public static class DemoSeedResolver
{
    /// <summary>At most this many lookups are named in a failure.</summary>
    public const int MaxProblemsNamed = 20;

    /// <summary>Resolve <paramref name="manifest"/> in <paramref name="ledgerId"/>.</summary>
    /// <exception cref="DemoSeedStepException">A lookup did not find exactly one row.</exception>
    public static async Task<DemoSeedIds> ResolveAsync(
        AppDbContext db, Guid ledgerId, DemoManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<string>();

        if (manifest.CheckingReconcile is null) problems.Add("the manifest names no checking statement to reconcile");
        if (manifest.CardStatement is null) problems.Add("the manifest names no card statement");
        if (manifest.InternetReminderId is null) problems.Add("the manifest names no Internet reminder");
        if (manifest.AccountIds.Count == 0) problems.Add("the manifest names no account");
        if (manifest.TargetDateLookThrough.Count == 0) problems.Add("the manifest names no look-through");

        // Accounts and categories are both accounts rows, keyed by their Moneydance id.
        var categoryMdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in manifest.Categories.Values
                     .Concat(manifest.BudgetTargets.Select(t => t.Category)))
        {
            var path = category.ToString();
            if (categoryMdIds.ContainsKey(path)) continue;
            try
            {
                categoryMdIds[path] = DemoBase.Instance.Category(category.Type, [.. category.Segments]);
            }
            catch (InvalidOperationException)
            {
                problems.Add($"the category {path} is not in the Demo's base");
            }
        }

        var accountMdIds = manifest.AccountIds.Values
            .Concat(manifest.Folders.SelectMany(f => f.AccountIds))
            .ToHashSet(StringComparer.Ordinal);
        var wantedAccounts = accountMdIds.Concat(categoryMdIds.Values).ToHashSet(StringComparer.Ordinal);
        var accountRows = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.ExternalId != null && wantedAccounts.Contains(a.ExternalId))
            .Select(a => new { a.Id, ExternalId = a.ExternalId! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var accountsByMdId = ExactlyOne(accountRows, r => r.ExternalId, r => r.Id, wantedAccounts, "account", problems);

        var txnIds = manifest.DuplicateTxnIds
            .Concat(manifest.EditCandidates.Select(e => e.TxnId))
            .Concat(manifest.CheckingReconcile?.ReconcilingTxnIds ?? [])
            .Concat(manifest.CardStatement?.ExportedRows.Select(r => r.TxnId) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var txnRows = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.ExternalId != null && txnIds.Contains(h.ExternalId))
            .Select(h => new { h.Id, ExternalId = h.ExternalId! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var transactions = ExactlyOne(txnRows, r => r.ExternalId, r => r.Id, txnIds, "transaction", problems);

        var reminderId = Guid.Empty;
        if (manifest.InternetReminderId is { } mdReminderId)
        {
            var reminders = await db.RecurringTransactions.AsNoTracking()
                .Where(r => r.LedgerId == ledgerId && r.ExternalId == mdReminderId)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reminders.Count == 1) reminderId = reminders[0];
            else problems.Add(Found(reminders.Count, $"reminder with the Moneydance id {mdReminderId}"));
        }

        var tickers = manifest.TargetDateLookThrough.Keys
            .Append(DemoSecurities.TargetDate)
            .ToHashSet(StringComparer.Ordinal);
        var stableValueName = DemoSecurities.Get(DemoSecurities.StableValue).Name;
        var securityRows = await db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId
                && ((s.Ticker != null && tickers.Contains(s.Ticker)) || s.Name == stableValueName))
            .Select(s => new { s.Id, s.Ticker, s.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var securities = ExactlyOne(
            securityRows.Where(s => s.Ticker != null && tickers.Contains(s.Ticker)).ToList(),
            s => s.Ticker!, s => s.Id, tickers, "security with the ticker", problems);
        var stableValue = securityRows.Where(s => s.Name == stableValueName).Select(s => s.Id).ToList();
        if (stableValue.Count == 1) securities[DemoSecurities.StableValue] = stableValue[0];
        else problems.Add(Found(stableValue.Count, $"security with the name {stableValueName}"));

        if (problems.Count > 0)
            throw new DemoSeedStepException(
                "the imported rows do not match the manifest: "
                + string.Join("; ", problems.Take(MaxProblemsNamed))
                + (problems.Count > MaxProblemsNamed ? $"; and {problems.Count - MaxProblemsNamed} more" : string.Empty));

        return new DemoSeedIds
        {
            Accounts = manifest.AccountIds.ToDictionary(p => p.Key, p => accountsByMdId[p.Value], StringComparer.Ordinal),
            AccountsByMdId = accountMdIds.ToDictionary(id => id, id => accountsByMdId[id], StringComparer.Ordinal),
            Categories = manifest.Categories.ToDictionary(
                p => p.Key, p => accountsByMdId[categoryMdIds[p.Value.ToString()]], StringComparer.Ordinal),
            CategoriesByPath = categoryMdIds.ToDictionary(p => p.Key, p => accountsByMdId[p.Value], StringComparer.Ordinal),
            Transactions = transactions,
            Securities = securities,
            InternetReminder = reminderId,
        };
    }

    /// <summary>The one row each wanted key finds, recording a problem for each key that
    /// finds none or several.</summary>
    private static Dictionary<string, Guid> ExactlyOne<T>(
        IReadOnlyList<T> rows,
        Func<T, string> key,
        Func<T, Guid> id,
        IReadOnlySet<string> wanted,
        string what,
        List<string> problems)
    {
        var byKey = rows.GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var wantedKey in wanted.Order(StringComparer.Ordinal))
        {
            var matches = byKey.GetValueOrDefault(wantedKey) ?? [];
            if (matches.Count == 1) found[wantedKey] = id(matches[0]);
            else problems.Add(Found(matches.Count, what == "security with the ticker"
                ? $"{what} {wantedKey}"
                : $"{what} with the Moneydance id {wantedKey}"));
        }
        return found;
    }

    private static string Found(int count, string what) =>
        count == 0 ? $"no {what}" : $"{count} rows for the {what}";
}
