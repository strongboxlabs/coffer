using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MultiUser;

/// <summary>
/// An invite lasts only as long as its sender's authority to send it (2026-10-12
/// security audit, M6).
/// </summary>
/// <remarks>
/// Redemption checked only the token, that it was unused and unexpired. A removed owner
/// could keep an owner invite they had sent as a way back in; a demoted admin could
/// accept their own admin invite. Now an invite dies with the role — deleted when the
/// sender loses it, and refused at redemption if one is ever missed.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InviteAuthorityTests
{
    private readonly PostgresFixture _fixture;

    public InviteAuthorityTests(PostgresFixture fixture) => _fixture = fixture;

    private InvitesRepository Invites => new(_fixture.NewServiceFactory(), TimeProvider.System);

    /// <summary>A session for any user id (SyntheticLedger issues one only for its own user).</summary>
    private async Task<HttpClient> CookieClientForAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();
        await using (var db = _fixture.NewDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    private async Task MakeAdminAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, _ => true));
    }

    private static async Task<bool> PreviewWorksAsync(HttpClient anonymous, string token)
    {
        var response = await anonymous.GetAsync($"/api/auth/invite/{token}");
        if (response.StatusCode == HttpStatusCode.OK) return true;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invite-invalid", doc.RootElement.GetProperty("code").GetString());
        return false;
    }

    private async Task<int> PendingIssuedByAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Invites.CountAsync(i => i.IssuedByUserId == userId && i.ConsumedAt == null);
    }

    [Fact]
    public async Task A_removed_owners_invites_are_deleted_and_stop_working()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);   // ledger.UserId owns it
        var coOwner = await ledger.AddMemberAsync("owner");
        var token = await Invites.CreateAsync(ledger.UserId, ledger.LedgerId, "owner", grantsAdmin: false);
        var coOwnersToken = await Invites.CreateAsync(coOwner, ledger.LedgerId, "viewer", grantsAdmin: false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.True(await PreviewWorksAsync(anonymous, token));

        using var coOwnerClient = await CookieClientForAsync(factory, coOwner);
        var removed = await coOwnerClient.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/members/{ledger.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        Assert.Equal(0, await PendingIssuedByAsync(ledger.UserId));
        Assert.False(await PreviewWorksAsync(anonymous, token));
        // The remaining owner's invites are theirs to keep.
        Assert.True(await PreviewWorksAsync(anonymous, coOwnersToken));
    }

    [Fact]
    public async Task An_invite_from_a_sender_who_lost_the_role_is_refused_even_if_it_was_not_deleted()
    {
        // The redemption-time check on its own: the role is changed without the
        // cleanup running, as any future path that forgets it would.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var token = await Invites.CreateAsync(ledger.UserId, ledger.LedgerId, "owner", grantsAdmin: false);
        await using (var db = _fixture.NewDbContext())
            await db.UserLedgerGrants
                .Where(g => g.UserId == ledger.UserId && g.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Role, _ => "viewer"));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.False(await PreviewWorksAsync(anonymous, token));
        Assert.Null(await Invites.GetValidScopeAsync(token));
    }

    [Fact]
    public async Task A_demoted_admins_admin_invites_are_deleted_and_stop_working()
    {
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(first.UserId);
        await MakeAdminAsync(second.UserId);
        var token = await Invites.CreateAsync(second.UserId, ledgerId: null, role: null, grantsAdmin: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.True(await PreviewWorksAsync(anonymous, token));

        using var firstClient = await CookieClientForAsync(factory, first.UserId);
        var demoted = await firstClient.PutAsJsonAsync($"/api/admin/users/{second.UserId}/admin", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.NoContent, demoted.StatusCode);

        Assert.Equal(0, await PendingIssuedByAsync(second.UserId));
        Assert.False(await PreviewWorksAsync(anonymous, token));
    }

    [Fact]
    public async Task A_demoted_admins_deployment_invites_are_deleted_and_stop_working()
    {
        // An invite with no ledger and no admin grant only creates an account, but only
        // the admin endpoint can issue one, so it is admin authority all the same.
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(first.UserId);
        await MakeAdminAsync(second.UserId);
        var token = await Invites.CreateAsync(second.UserId, ledgerId: null, role: null, grantsAdmin: false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.True(await PreviewWorksAsync(anonymous, token));

        using var firstClient = await CookieClientForAsync(factory, first.UserId);
        var demoted = await firstClient.PutAsJsonAsync($"/api/admin/users/{second.UserId}/admin", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.NoContent, demoted.StatusCode);

        Assert.Equal(0, await PendingIssuedByAsync(second.UserId));
        Assert.False(await PreviewWorksAsync(anonymous, token));

        // And refused at redemption on its own: one written after the demotion, as a
        // cleanup that missed it would leave.
        var missed = await Invites.CreateAsync(second.UserId, ledgerId: null, role: null, grantsAdmin: false);
        Assert.Null(await Invites.GetValidScopeAsync(missed));
    }

    [Fact]
    public async Task A_disabled_senders_invites_are_deleted()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);
        var sender = await SyntheticLedger.CreateAsync(_fixture);
        var token = await Invites.CreateAsync(sender.UserId, sender.LedgerId, "editor", grantsAdmin: false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var adminClient = await CookieClientForAsync(factory, admin.UserId);
        var disabled = await adminClient.PutAsJsonAsync($"/api/admin/users/{sender.UserId}/disabled", new { disabled = true });
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);

        Assert.Equal(0, await PendingIssuedByAsync(sender.UserId));
        Assert.Null(await Invites.GetValidScopeAsync(token));
    }
}
