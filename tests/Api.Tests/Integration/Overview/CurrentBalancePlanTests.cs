using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// Each account's current-balance lookup costs no more than the account's own running
/// balances: a count of them, a walk of the register's index bounded by that count, and,
/// only when the walk finds none, the account's own rows.
/// </summary>
/// <remarks>
/// <para><c>account_balances_as_of_now</c> takes, per account, the running balance of the
/// latest visible header before the boundary. Found by walking the ledger's headers back
/// from the boundary with nothing to stop the walk sooner, that costs the ledger's header
/// count for an account with no row, and nearly as much for one whose last row is years
/// back, and a long-lived ledger holds many of both. Migration 279 bounds the walk by the
/// account's own row count and falls back to the account's own rows. Which reads run is
/// the planner's choice, so this holds the plan: on a ledger with busy accounts and the
/// quiet ones a long-lived ledger gathers (<see cref="LongLivedAccounts"/>), planned as the
/// app plans it, as <c>coffer_app</c> under row-level security, the only reads of the
/// running balances and the headers are the count, the walk under a Limit of its own, and
/// the fallback through <c>idx_thab_account_ledger</c> with each header probed by key.</para>
/// <para>Judged on plan nodes, relation and index names and conditions
/// (<see cref="CurrentBalancePlan.BoundedLookupViolations"/>), never on a time, and fail
/// closed: any other read, or a plan missing one of the three, is reported. The stress lane
/// times the read on the default, four-times and long-lived shapes
/// (<c>CurrentBalanceCostTests</c>).</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrentBalancePlanTests : DatabaseTest
{
    public CurrentBalancePlanTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The caller's today, years after every quiet account's last row.</summary>
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Fact]
    public async Task Each_accounts_lookup_costs_no_more_than_its_own_running_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedAsync(Db, ledger, StressLedger.Scale.Smoke);
        var added = await LongLivedAccounts.AddAsync(ledger);
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(Today);

        await using var connection = await CurrentBalancePlan.OpenAsUserAsync(Db.AppConnectionString, ledger.UserId);
        var plan = await CurrentBalancePlan.ExplainJsonAsync(connection, ledger.LedgerId, scheduledFrom);
        var violations = CurrentBalancePlan.BoundedLookupViolations(plan);
        Assert.True(violations.Count == 0,
            "an account's current-balance lookup is not bounded by its own running balances:\n  "
            + string.Join("\n  ", violations) + "\nplan:\n" + plan);

        // Vacuity: the read the plan is for answers every account, the quiet ones as the
        // net-worth history's feeder does at the last instant the register counts, a
        // reading that shares no lookup with it.
        var balances = new Dictionary<Guid, decimal>();
        await using (var read = CurrentBalancePlan.Command(connection, ledger.LedgerId, scheduledFrom))
        await using (var reader = await read.ExecuteReaderAsync())
            while (await reader.ReadAsync()) balances[reader.GetGuid(0)] = reader.GetDecimal(1);

        await using var app = Db.NewAppDbContextAsUser(ledger.UserId);
        Assert.Equal(await app.Accounts.AsNoTracking().CountAsync(a => a.LedgerId == ledger.LedgerId), balances.Count);
        var history = await app.AccountBalanceAsOfInstants(
                ledger.LedgerId, [ScheduledBoundary.LastCountedInstant(Today)], null)
            .ToDictionaryAsync(r => r.AccountId, r => r.Balance);
        Assert.All(added.WithoutRows.Concat(added.YearsBack), id => Assert.Equal(history[id], balances[id]));
        Assert.Contains(added.YearsBack, id => balances[id] != 0m);
    }
}
