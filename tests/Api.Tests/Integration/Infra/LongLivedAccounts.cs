using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The accounts a long-lived ledger gathers beside its busy ones: categories never used,
/// accounts holding only their opening balance, brokerages never traded in, and accounts
/// and categories whose last row is years back, some of them closed.
/// </summary>
/// <remarks>
/// <para>A per-account read whose cost follows the ledger rather than the account shows
/// only on such accounts. On a ledger where every account has recent rows (the stress
/// seed alone), a lookup that walks the ledger's headers back from today stops after a
/// few headers for each account; for an account with no row it walks the whole ledger.
/// Add this set to a seeded ledger to time a read on both shapes.</para>
/// <para>Every account is created, and every row written, through the
/// <see cref="SyntheticLedger"/> helpers: a one-split between an account and a category,
/// its running balances recomputed as a save recomputes them. The statistics of the
/// tables the reads plan against are refreshed at the end, as autovacuum refreshes them
/// on a live install, so a plan is taken on what the tables hold.</para>
/// </remarks>
public static class LongLivedAccounts
{
    /// <summary>Categories no row ever reached.</summary>
    public const int UnusedCategories = 40;

    /// <summary>Accounts holding only their opening balance.</summary>
    public const int OpeningBalanceOnly = 2;

    /// <summary>Brokerages never traded in, each with its holdings sibling.</summary>
    public const int DormantBrokerages = 2;

    /// <summary>Accounts whose rows all lie years back.</summary>
    public const int RetiredAccounts = 20;

    /// <summary>The categories those rows went to, so their last rows are years back too.</summary>
    public const int RetiredCategories = 10;

    /// <summary>Rows on each retired account.</summary>
    public const int RowsPerRetiredAccount = 3;

    /// <summary>Retired accounts that are also closed.</summary>
    public const int ClosedRetiredAccounts = 5;

    /// <summary>The first retired row; the rest follow it, the last within four years.</summary>
    private static readonly DateTime FirstRetiredRow = new(2010, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>What <see cref="AddAsync"/> added.</summary>
    /// <param name="WithoutRows">Accounts with no row at all: the unused categories, the
    /// accounts with only an opening balance, the dormant brokerages and their holdings
    /// siblings.</param>
    /// <param name="YearsBack">Accounts whose last row is no later than
    /// <paramref name="LatestRow"/>: the retired accounts and their categories.</param>
    /// <param name="LatestRow">The date of the latest row on any of them.</param>
    public sealed record Added(IReadOnlyList<Guid> WithoutRows, IReadOnlyList<Guid> YearsBack, DateTime LatestRow);

    /// <summary>Add the set to <paramref name="ledger"/>, then refresh the statistics.</summary>
    public static async Task<Added> AddAsync(SyntheticLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var withoutRows = new List<Guid>();
        var yearsBack = new List<Guid>();

        for (var i = 1; i <= UnusedCategories; i++)
            withoutRows.Add((await ledger.AddCategoryAsync($"long-lived-unused-{i:D2}",
                kind: i % 4 == 0 ? "income" : "expense", cancellationToken: cancellationToken).ConfigureAwait(false)).Id);

        for (var i = 1; i <= OpeningBalanceOnly; i++)
            withoutRows.Add((await ledger.AddBankAccountAsync($"long-lived-opening-{i:D2}",
                openingBalance: 100m * i, cancellationToken: cancellationToken).ConfigureAwait(false)).Id);

        for (var i = 1; i <= DormantBrokerages; i++)
        {
            var brokerage = await ledger.AddInvestmentAccountAsync($"long-lived-brokerage-{i:D2}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            withoutRows.Add(brokerage.Id);
            withoutRows.Add(brokerage.HoldingsAccountId!.Value);
        }

        var categories = new List<Guid>();
        for (var i = 1; i <= RetiredCategories; i++)
            categories.Add((await ledger.AddCategoryAsync($"long-lived-retired-category-{i:D2}",
                cancellationToken: cancellationToken).ConfigureAwait(false)).Id);
        yearsBack.AddRange(categories);

        var latest = DateTime.MinValue;
        for (var i = 0; i < RetiredAccounts; i++)
        {
            // A card or a loan as well as bank accounts, as a ledger collects them.
            var type = (i % 4) switch { 1 => "credit_card", 3 => "loan", _ => "bank" };
            var account = await ledger.AddAccountAsync($"long-lived-retired-{i + 1:D2}", type,
                openingBalance: 50m * (i + 1), cancellationToken: cancellationToken).ConfigureAwait(false);
            yearsBack.Add(account.Id);
            for (var k = 0; k < RowsPerRetiredAccount; k++)
            {
                var on = FirstRetiredRow.AddDays((i * 60) + (k * 20));
                await ledger.AddTransactionPairAsync(account.Id, categories[i % RetiredCategories],
                    -(10m + i + k), on, payee: "long-lived-payee", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (on > latest) latest = on;
            }
            if (i < ClosedRetiredAccounts)
                await ledger.SetIsActiveAsync(account.Id, isActive: false, cancellationToken).ConfigureAwait(false);
        }

        await using (var db = ledger.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ANALYZE accounts; ANALYZE txn_headers; ANALYZE txn_legs; ANALYZE txn_header_account_balances;",
                cancellationToken).ConfigureAwait(false);
        }

        return new Added(withoutRows, yearsBack, latest);
    }
}
