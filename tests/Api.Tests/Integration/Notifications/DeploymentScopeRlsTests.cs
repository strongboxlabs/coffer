using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The deployment-scope notification tables refuse a non-admin at the DATABASE, not
/// only at the endpoint filter.
/// </summary>
/// <remarks>
/// <para>
/// Without RLS on <c>notification_subscribers</c> and <c>system_events</c>,
/// <c>coffer_app</c> has full CRUD on both through mig 017's ALTER DEFAULT PRIVILEGES,
/// and a non-admin app identity can UPDATE a delivery target: one row affected, no
/// error. One of them stores sealed delivery URLs (ADR-0096 D8). Migration 213 enables
/// RLS on both.
/// </para>
/// <para>
/// The admin endpoints carry RequireAdmin, but this repo treats RLS as the backstop and
/// the filter as the primary check, because an endpoint can lose its filter. A
/// credentials table is the last place to depend on an attribute being present.
/// </para>
/// <para>
/// Proved the way LedgerRoleTests proves its RLS: under an RLS-scoped <c>coffer_app</c>
/// context, the write matches ZERO rows for the identity that must not have it, and the
/// SAME statement lands for the identity that must. A test that only asserts the refusal
/// cannot tell a working policy from a broken grant.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DeploymentScopeRlsTests : DatabaseTest
{
    public DeploymentScopeRlsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private async Task<(Guid AdminId, Guid PlainId, Guid TargetId)> SeedAsync()
    {
        await using var db = Db.NewServiceDbContext();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = new UserRow { Id = Guid.NewGuid(), Username = "adm-" + suffix, DisplayName = "adm", IsAdmin = true };
        var plain = new UserRow { Id = Guid.NewGuid(), Username = "usr-" + suffix, DisplayName = "usr", IsAdmin = false };
        db.Users.AddRange(admin, plain);

        var target = new NotificationSubscriberRow
        {
            SubscriberKey = "webhook",
            DisplayName = "seeded",
            ConfigCiphertext = [1, 2, 3],
        };
        db.NotificationSubscribers.Add(target);
        await db.SaveChangesAsync();
        return (admin.Id, plain.Id, target.Id);
    }

    [Fact]
    public async Task A_non_admin_can_neither_read_nor_change_a_delivery_target()
    {
        var (adminId, plainId, targetId) = await SeedAsync();

        await using (var plainDb = Db.NewAppDbContextAsUser(plainId))
        {
            // Invisible: the row holds a sealed delivery URL, and the list of targets is
            // a map of someone's integrations even without the URLs themselves.
            Assert.False(await plainDb.NotificationSubscribers.AnyAsync(t => t.Id == targetId));

            // And unwritable. Before mig 213 this affected one row.
            Assert.Equal(0, await plainDb.NotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DisplayName, "hijacked")));

            Assert.Equal(0, await plainDb.NotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteDeleteAsync());
        }

        // The same statements as an admin: the policy has to DISCRIMINATE, not merely
        // deny. If these also returned zero the table would just be inaccessible, and
        // the admin panel would be broken rather than protected.
        await using (var adminDb = Db.NewAppDbContextAsUser(adminId))
        {
            Assert.True(await adminDb.NotificationSubscribers.AnyAsync(t => t.Id == targetId));
            Assert.Equal(1, await adminDb.NotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DisplayName, "renamed-by-admin")));
        }
    }

    [Fact]
    public async Task The_deployment_event_log_is_admin_read_only()
    {
        var (adminId, plainId, _) = await SeedAsync();

        var seededId = Guid.NewGuid();
        await using (var seed = Db.NewServiceDbContext())
        {
            seed.SystemEvents.Add(new SystemEventRow
            {
                Id = seededId,
                Severity = "info",
                Topic = "backup",
                EventKey = "backup.succeeded",
                Summary = "seeded for the RLS test",
                DetailJson = "{}",
            });
            await seed.SaveChangesAsync();
        }

        await using (var plainDb = Db.NewAppDbContextAsUser(plainId))
            Assert.False(await plainDb.SystemEvents.AnyAsync());

        await using (var adminDb = Db.NewAppDbContextAsUser(adminId))
        {
            // The row this test seeded, not merely any row: the template carries the
            // backup monitor's first alert, which would satisfy a bare AnyAsync.
            Assert.True(await adminDb.SystemEvents.AnyAsync(e => e.Id == seededId));

            // Read-only even for an admin: the publisher writes it on the service role,
            // and nothing user-facing has business amending an audit trail. Same
            // treatment mig 174 gave the other service-written logs. RLS admitted no
            // row; since migration 254 the privilege is gone as well (FU-086), so the
            // write is refused before RLS is asked.
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => adminDb.SystemEvents
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Summary, "rewritten")));
            Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }
}
