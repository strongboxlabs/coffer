using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Clears the deployment-scope notification delivery targets.
/// </summary>
/// <remarks>
/// <para>
/// Rotation and reconciliation re-wrap <c>notification_subscribers</c> and
/// <c>ledger_notification_subscribers</c> now, and rotation aborts on any secret it
/// cannot open — the same <c>OpenOrThrow</c> contract the ledger keys, the backup
/// passphrase and the Drive token have always had, because a half-rotated database is
/// worse than a refused rotation.
/// </para>
/// <para>
/// That makes every rotation test sensitive to a table nothing isolates. Neither table
/// has a <c>ledger_id</c> or any per-test scope, so rows another notification test left
/// behind — deliberately sealed under a FOREIGN key, in the reconciliation tests' case —
/// abort a rotation that has nothing to do with them. Which is the correct production
/// behaviour meeting a broken test arrangement: a rotation test has to control the
/// contents of every table rotation reads.
/// </para>
/// <para>
/// Lives here rather than in each test class because two classes needed it the moment
/// rotation's scope widened, and a third will the moment anything else triggers a
/// rotation. The reason is subtle enough that restating it per class would guarantee the
/// copies drift.
/// </para>
/// <para>
/// The engineering-standards §5.2 exception for global tables, same as
/// NotificationRoutingTests and DeploymentScopeRlsTests.
/// </para>
/// </remarks>
internal static class NotificationTargetReset
{
    /// <summary>
    /// Deletes every row from both delivery-target tables, children first.
    /// </summary>
    public static async Task ClearAsync(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var db = fixture.NewServiceDbContext();
        await db.LedgerNotificationSubscribers.ExecuteDeleteAsync().ConfigureAwait(false);
        await db.NotificationSubscribers.ExecuteDeleteAsync().ConfigureAwait(false);
    }
}
