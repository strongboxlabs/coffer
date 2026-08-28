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
/// Migration 207 created <c>notification_subscribers</c> and <c>system_events</c> and
/// enabled RLS on neither, so <c>coffer_app</c> had full CRUD on both through mig 017's
/// ALTER DEFAULT PRIVILEGES. One of them stores sealed delivery URLs (ADR-0096 D8).
/// Measured on a copy of a real database before migration 213: a non-admin app identity
/// could UPDATE a delivery target. One row affected, no error.
/// </para>
/// <para>
/// There was no live escalation — the admin endpoints carry RequireAdmin — but this repo
/// treats RLS as the backstop and the filter as the primary check, and 0.65.0 shipped
/// ledger notification endpoints with the filter missing entirely. A credentials table
/// is the last place to depend on an attribute being present.
/// </para>
/// <para>
/// Proved the way LedgerRoleTests proves its RLS: under an RLS-scoped <c>coffer_app</c>
/// context, the write matches ZERO rows for the identity that must not have it, and the
/// SAME statement lands for the identity that must. A test that only asserts the refusal
/// cannot tell a working policy from a broken grant.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DeploymentScopeRlsTests
{
    private readonly PostgresFixture _fixture;

    public DeploymentScopeRlsTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(Guid AdminId, Guid PlainId, Guid TargetId)> SeedAsync()
    {
        await using var db = _fixture.NewServiceDbContext();

        // Deployment-scope tables are not isolated by a per-test ledger, so clear them
        // (the engineering-standards §5.2 exception for global tables).
        await db.NotificationSubscribers.ExecuteDeleteAsync();

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

        await using (var plainDb = _fixture.NewAppDbContextAsUser(plainId))
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
        await using (var adminDb = _fixture.NewAppDbContextAsUser(adminId))
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

        await using (var seed = _fixture.NewServiceDbContext())
        {
            seed.SystemEvents.Add(new SystemEventRow
            {
                Severity = "info",
                Topic = "backup",
                EventKey = "backup.succeeded",
                Summary = "seeded for the RLS test",
                DetailJson = "{}",
            });
            await seed.SaveChangesAsync();
        }

        await using (var plainDb = _fixture.NewAppDbContextAsUser(plainId))
            Assert.False(await plainDb.SystemEvents.AnyAsync());

        await using (var adminDb = _fixture.NewAppDbContextAsUser(adminId))
        {
            Assert.True(await adminDb.SystemEvents.AnyAsync());

            // Read-only even for an admin: the publisher writes it on the service role,
            // and nothing user-facing has business amending an audit trail. Same
            // treatment mig 174 gave the other service-written logs.
            Assert.Equal(0, await adminDb.SystemEvents
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Summary, "rewritten")));
        }
    }
}
