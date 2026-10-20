using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MultiUser;

/// <summary>
/// The keep-one-owner rule cannot be raced, and every privilege change is in the admin
/// audit (FU-090).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrivilegeChangeTests
{
    private readonly PostgresFixture _fixture;

    public PrivilegeChangeTests(PostgresFixture fixture) => _fixture = fixture;

    private LedgersRepository NewLedgers() => new(
        _fixture.NewDbContext(), _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService(), TimeProvider.System);

    private async Task<int> HumanOwnersAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.UserLedgerGrants.CountAsync(
            g => g.LedgerId == ledgerId && g.Role == "owner" && g.UserId != UserRow.SystemUserId);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_once_never_leave_a_ledger_without_one()
    {
        // Counted, then updated, with nothing between, each of two concurrent demotions saw
        // two owners and the ledger was left with none. Raced twenty times over to give the
        // overlap every chance.
        for (var round = 0; round < 20; round++)
        {
            var ledger = await SyntheticLedger.CreateAsync(_fixture);
            var second = await ledger.AddMemberAsync("owner");

            var results = await Task.WhenAll(
                Task.Run(() => NewLedgers().SetMemberRoleAsync(ledger.LedgerId, ledger.UserId, "viewer")),
                Task.Run(() => NewLedgers().SetMemberRoleAsync(ledger.LedgerId, second, "viewer")));

            Assert.Equal(1, await HumanOwnersAsync(ledger.LedgerId));
            Assert.Single(results, r => r == LedgersRepository.MemberChangeResult.Ok);
            Assert.Single(results, r => r == LedgersRepository.MemberChangeResult.LastOwner);
        }
    }

    // --- the admin audit -------------------------------------------------------------

    private async Task<HttpClient> CookieClientForAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();
        await using (var db = _fixture.NewDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash, ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    private async Task<AdminAuditEventRow?> AuditAsync(string action, Guid actor, string detailContains)
    {
        await using var db = _fixture.NewDbContext();
        return await db.AdminAuditEvents.AsNoTracking()
            .Where(e => e.Action == action && e.ActorUserId == actor && e.Detail!.Contains(detailContains))
            .SingleOrDefaultAsync();
    }

    [Fact]
    public async Task Member_role_changes_and_removals_are_audited_and_a_no_op_is_not()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var member = await ledger.AddMemberAsync("viewer");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var owner = await CookieClientForAsync(factory, ledger.UserId);
        var url = $"/api/ledgers/{ledger.LedgerId}/members/{member}";

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync(url, new SetMemberRoleRequest { Role = "editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,   // already an editor: nothing changes
            (await owner.PutAsJsonAsync(url, new SetMemberRoleRequest { Role = "editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(url)).StatusCode);

        var changed = await AuditAsync(AdminAuditActions.MemberRoleChanged, ledger.UserId, member.ToString());
        Assert.NotNull(changed);   // and only one: SingleOrDefault throws on the no-op's row
        Assert.Contains("editor", changed!.Detail);
        Assert.NotNull(await AuditAsync(AdminAuditActions.MemberRemoved, ledger.UserId, member.ToString()));
    }

    [Fact]
    public async Task Disabling_enabling_and_revoking_admin_are_audited()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var target = await ledger.AddMemberAsync("viewer");
        await using (var db = _fixture.NewDbContext())
            await db.Users.Where(u => u.Id == ledger.UserId || u.Id == target)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, _ => true));
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var admin = await CookieClientForAsync(factory, ledger.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
            $"/api/admin/users/{target}/admin", new SetUserAdminRequest { IsAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
            $"/api/admin/users/{target}/disabled", new SetUserDisabledRequest { Disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
            $"/api/admin/users/{target}/disabled", new SetUserDisabledRequest { Disabled = false })).StatusCode);

        Assert.NotNull(await AuditAsync(AdminAuditActions.UserAdminRevoked, ledger.UserId, target.ToString()));
        Assert.NotNull(await AuditAsync(AdminAuditActions.UserDisabled, ledger.UserId, target.ToString()));
        Assert.NotNull(await AuditAsync(AdminAuditActions.UserEnabled, ledger.UserId, target.ToString()));
    }

    [Fact]
    public async Task Issuing_and_revoking_a_ledger_invite_are_audited_without_the_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var owner = await CookieClientForAsync(factory, ledger.UserId);

        var issued = await owner.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/invites",
            new IssueLedgerInviteRequest { Role = "editor" });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var token = (await issued.Content.ReadFromJsonAsync<InviteCreatedResponse>())!.Token;

        var row = await AuditAsync(AdminAuditActions.InviteIssued, ledger.UserId, ledger.LedgerId.ToString());
        Assert.NotNull(row);
        Assert.DoesNotContain(token, row!.Detail);

        Guid inviteId;
        await using (var db = _fixture.NewDbContext())
            inviteId = await db.Invites.Where(i => i.LedgerId == ledger.LedgerId).Select(i => i.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/invites/{inviteId}")).StatusCode);
        Assert.NotNull(await AuditAsync(AdminAuditActions.InviteRevoked, ledger.UserId, inviteId.ToString()));
    }
}
