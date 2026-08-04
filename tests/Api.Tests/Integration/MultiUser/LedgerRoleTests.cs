using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MultiUser;

/// <summary>
/// ADR-0083 per-ledger role enforcement (owner/editor/viewer). The DB backstop —
/// role-aware RLS (migration 174) — is proved under an RLS-scoped <c>coffer_app</c>
/// context (<see cref="PostgresFixture.NewAppDbContextAsUser"/>): a viewer can READ
/// but a write matches ZERO rows, while editor/owner writes land. Plus the
/// ledger-scoped members management with its ≥1-owner guard, and the admin
/// user-management happy paths.
/// </summary>
/// <remarks>
/// The REST (<c>RequireLedgerAccess</c>) and MCP (<c>McpLedgerWriteAuthFilter</c>)
/// API-layer gates that turn the silent 0-row write into a clean 422 are dev-validated
/// with a live client (the project's MCP/endpoint-filter convention). The ≥1-admin
/// guard's positive trigger is a GLOBAL-count invariant that can't be exercised
/// atomically on the shared integration DB, so only its safe (non-triggering) paths are
/// asserted here; the trigger is dev-validated.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerRoleTests
{
    private readonly PostgresFixture _fixture;

    public LedgerRoleTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Viewer_reads_but_cannot_write_while_editor_and_owner_write()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var account = await ledger.AddBankAccountAsync("checking");
        var viewerId = await ledger.AddMemberAsync("viewer");
        var editorId = await ledger.AddMemberAsync("editor");

        // Viewer: SELECT is allowed — the _read policy passes for any grant.
        await using (var viewerDb = _fixture.NewAppDbContextAsUser(viewerId))
        {
            Assert.True(await viewerDb.Accounts.AnyAsync(a => a.Id == account.Id));

            // ...but a write matches ZERO rows: the role-aware write policy (mig 174)
            // excludes a viewer, so the UPDATE silently affects nothing — exactly the
            // silent no-op the API-layer filter turns into a clean 422.
            var affected = await viewerDb.Accounts
                .Where(a => a.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "viewer-was-here"));
            Assert.Equal(0, affected);
        }

        // Editor: the same write lands.
        await using (var editorDb = _fixture.NewAppDbContextAsUser(editorId))
        {
            var affected = await editorDb.Accounts
                .Where(a => a.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "editor-edit"));
            Assert.Equal(1, affected);
        }

        // Owner: also writes.
        await using (var ownerDb = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            var affected = await ownerDb.Accounts
                .Where(a => a.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "owner-edit"));
            Assert.Equal(1, affected);
        }
    }

    [Fact]
    public async Task Members_hide_system_and_enforce_at_least_one_human_owner()
    {
        // A synthetic ledger has two owners: the test user + the system identity.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var viewerId = await ledger.AddMemberAsync("viewer");
        var repo = new LedgersRepository(
            _fixture.NewDbContext(), _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        // The system service identity is hidden; the human owner + viewer show, and
        // exactly one HUMAN owner is counted.
        var members = await repo.ListMembersAsync(ledger.LedgerId);
        Assert.DoesNotContain(members, m => m.UserId == UserRow.SystemUserId);
        Assert.Contains(members, m => m.UserId == ledger.UserId && m.Role == "owner");
        Assert.Contains(members, m => m.UserId == viewerId && m.Role == "viewer");
        Assert.Equal(1, members.Count(m => m.Role == "owner"));

        // The system account can't be changed or removed.
        Assert.Equal(LedgersRepository.MemberChangeResult.SystemUser,
            await repo.SetMemberRoleAsync(ledger.LedgerId, UserRow.SystemUserId, "editor"));
        Assert.Equal(LedgersRepository.MemberChangeResult.SystemUser,
            await repo.RemoveMemberAsync(ledger.LedgerId, UserRow.SystemUserId));

        // Bad inputs.
        Assert.Equal(LedgersRepository.MemberChangeResult.NotAMember,
            await repo.SetMemberRoleAsync(ledger.LedgerId, Guid.NewGuid(), "editor"));
        Assert.Equal(LedgersRepository.MemberChangeResult.InvalidRole,
            await repo.SetMemberRoleAsync(ledger.LedgerId, viewerId, "superuser"));

        // The sole HUMAN owner can't be demoted or removed — the system owner doesn't count.
        Assert.Equal(LedgersRepository.MemberChangeResult.LastOwner,
            await repo.SetMemberRoleAsync(ledger.LedgerId, ledger.UserId, "viewer"));
        Assert.Equal(LedgersRepository.MemberChangeResult.LastOwner,
            await repo.RemoveMemberAsync(ledger.LedgerId, ledger.UserId));

        // Promote the viewer to owner → two human owners → the original can now be removed.
        Assert.Equal(LedgersRepository.MemberChangeResult.Ok,
            await repo.SetMemberRoleAsync(ledger.LedgerId, viewerId, "owner"));
        Assert.Equal(LedgersRepository.MemberChangeResult.Ok,
            await repo.RemoveMemberAsync(ledger.LedgerId, ledger.UserId));
    }

    [Fact]
    public async Task Admin_user_management_lists_hides_system_and_toggles_flags()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var second = await ledger.AddMemberAsync("viewer");
        var repo = new UsersRepository(_fixture.NewDbContext(), _fixture.NewServiceFactory());

        // The ledger owner appears as a non-admin, non-disabled user with ≥1 ledger;
        // the synthetic system identity is hidden.
        var users = await repo.ListAllAsync();
        var me = users.Single(u => u.Id == ledger.UserId);
        Assert.False(me.IsAdmin);
        Assert.False(me.IsDisabled);
        Assert.True(me.LedgerCount >= 1);
        Assert.DoesNotContain(users, u => u.Id == UserRow.SystemUserId);

        // Disable / re-enable a non-admin (never trips the ≥1-admin guard).
        Assert.Equal(UsersRepository.AdminUserChangeResult.Ok,
            await repo.SetDisabledAsync(ledger.UserId, true));
        Assert.True((await repo.ListAllAsync()).Single(u => u.Id == ledger.UserId).IsDisabled);
        Assert.Equal(UsersRepository.AdminUserChangeResult.Ok,
            await repo.SetDisabledAsync(ledger.UserId, false));

        // Grant admin (never trips the guard); a demote is safe while a second admin
        // exists — proving the guard doesn't over-fire.
        Assert.Equal(UsersRepository.AdminUserChangeResult.Ok, await repo.SetAdminAsync(second, true));
        Assert.Equal(UsersRepository.AdminUserChangeResult.Ok, await repo.SetAdminAsync(ledger.UserId, true));
        Assert.Equal(UsersRepository.AdminUserChangeResult.Ok, await repo.SetAdminAsync(ledger.UserId, false));

        Assert.Equal(UsersRepository.AdminUserChangeResult.NotFound,
            await repo.SetAdminAsync(Guid.NewGuid(), true));
    }
}
