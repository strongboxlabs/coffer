using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Coffer.Api.Auth;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MultiUser;

/// <summary>
/// What became the owner's on 2026-10-19 (FU-095): granting access to a ledger, admins
/// included; connecting and disconnecting a bank; and choosing where a ledger's events go.
/// Editors keep syncing. Each held at the API, and the last two at RLS too (ADR-0083 D2).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OwnerOnlyAccessTests
{
    private readonly PostgresFixture _fixture;

    public OwnerOnlyAccessTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
    }

    /// <summary>A signed-in client for <paramref name="userId"/>, as InviteAuthorityTests makes one.</summary>
    private async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger, Guid userId)
    {
        _ = ledger;
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
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

    private async Task<Guid> AdminAsync()
    {
        await using var db = _fixture.NewDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var admin = new UserRow
        {
            Id = Guid.NewGuid(), DisplayName = "admin-" + suffix, Username = "admin-" + suffix,
            CreatedBy = "integration-test", IsAdmin = true,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        return admin.Id;
    }

    // --- invites: access to a ledger is its owners' to give -------------------------

    [Fact]
    public async Task An_admin_who_does_not_own_a_ledger_cannot_invite_anyone_into_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var adminId = await AdminAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var admin = await ClientAsync(factory, ledger, adminId);

        var response = await admin.PostAsJsonAsync("/api/admin/invites",
            new { ledgerId = ledger.LedgerId, role = "editor", grantsAdmin = false });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.Invites.AnyAsync(i => i.IssuedByUserId == adminId));
    }

    [Fact]
    public async Task An_admin_who_owns_the_ledger_may()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var adminId = await AdminAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.UserLedgerGrants.Add(new UserLedgerGrantRow { UserId = adminId, LedgerId = ledger.LedgerId, Role = "owner" });
            await db.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var admin = await ClientAsync(factory, ledger, adminId);

        var response = await admin.PostAsJsonAsync("/api/admin/invites",
            new { ledgerId = ledger.LedgerId, role = "editor", grantsAdmin = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_pending_ledger_invite_from_an_admin_who_does_not_own_it_no_longer_redeems()
    {
        // Issued before 0.122.0, when admin alone was enough: it carries authority its sender
        // no longer has, and redemption re-checks it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var adminId = await AdminAsync();
        var invites = new InvitesRepository(_fixture.NewServiceFactory(), TimeProvider.System);
        var token = await invites.CreateAsync(adminId, ledger.LedgerId, "editor", grantsAdmin: false);

        Assert.Null(await invites.GetValidScopeAsync(token));

        // An instance-only invite from the same admin still stands: that is an admin's to give.
        var instanceOnly = await invites.CreateAsync(adminId, null, null, grantsAdmin: false);
        Assert.NotNull(await invites.GetValidScopeAsync(instanceOnly));
    }

    // --- bank connections and notification targets ----------------------------------

    [Fact]
    public async Task An_editor_cannot_connect_a_bank_change_its_mapping_or_add_a_notification_target()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var editor = await ledger.AddMemberAsync("editor");
        var account = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger, editor);
        var ledgerPath = $"/api/ledgers/{ledger.LedgerId}";

        // Every owner-only route. Each would otherwise answer something else for these
        // inputs (not found, a mismatch, an unbound account, or success), so only the
        // owner gate gives ledger-not-owner.
        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["connect"] = await client.PostAsJsonAsync($"{ledgerPath}/feed-connections",
                new { setupToken = "aHR0cHM6Ly9icmlkZ2Uuc2ltcGxlZmluLm9yZy9jbGFpbS94" }),
            ["disconnect"] = await client.DeleteAsync($"{ledgerPath}/feed-connections/{Guid.NewGuid()}"),
            ["map"] = await client.PatchAsJsonAsync($"{ledgerPath}/accounts/{account.Id}/feed-mapping",
                new { feedConnectionId = Guid.NewGuid(), simpleFinAccountId = "ACT-1" }),
            ["unmap"] = await client.DeleteAsync($"{ledgerPath}/accounts/{account.Id}/feed-mapping"),
            ["sync-from"] = await client.PatchAsJsonAsync($"{ledgerPath}/accounts/{account.Id}/sync-from-date",
                new { syncFromDate = (DateTime?)null }),
            ["add target"] = await client.PostAsJsonAsync($"{ledgerPath}/notifications/subscribers",
                new { subscriberKey = "webhook", displayName = "mine", url = "https://hook.invalid/editor", minSeverity = "warning" }),
            ["remove target"] = await client.DeleteAsync($"{ledgerPath}/notifications/subscribers/{Guid.NewGuid()}"),
        };

        foreach (var (route, response) in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{route}: {response.StatusCode}");
            Assert.Equal($"{route}: ledger-not-owner", $"{route}: {await CodeOfAsync(response)}");
        }
    }

    [Fact]
    public async Task An_editor_still_syncs()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var editor = await ledger.AddMemberAsync("editor");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger, editor);

        // No connection with this id: the answer comes from the sync itself, so the role
        // gate let the editor through.
        var sync = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{Guid.NewGuid()}/sync", null);

        Assert.Equal("feed-connection-not-found", await CodeOfAsync(sync));
    }

    private static LedgerNotificationSubscriberRow Target(Guid ledgerId) => new()
    {
        Id = Guid.NewGuid(), LedgerId = ledgerId, SubscriberKey = "webhook", DisplayName = "t",
        ConfigCiphertext = [1, 2, 3], CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static FeedConnectionRow Connection(Guid ledgerId) => new()
    {
        Id = Guid.NewGuid(), LedgerId = ledgerId, Provider = "simplefin", Status = "active",
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task AssertRefusedAsync(Func<Task> write)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(write);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pg?.SqlState);
    }

    [Fact]
    public async Task At_the_database_an_editor_may_sync_a_connection_and_may_not_create_or_remove_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var editor = await ledger.AddMemberAsync("editor");
        var existing = Connection(ledger.LedgerId);
        await using (var svc = _fixture.NewServiceDbContext())
        {
            svc.FeedConnections.Add(existing);
            await svc.SaveChangesAsync();
        }

        await using (var db = _fixture.NewAppDbContextAsUser(editor))
        {
            db.FeedConnections.Add(Connection(ledger.LedgerId));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(editor))
        {
            // What a sync writes.
            Assert.Equal(1, await db.FeedConnections.Where(f => f.Id == existing.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastSyncedAt, DateTime.UtcNow)));
            Assert.Equal(0, await db.FeedConnections.Where(f => f.Id == existing.Id).ExecuteDeleteAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            db.FeedConnections.Add(Connection(ledger.LedgerId));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task At_the_database_only_an_owner_writes_a_notification_target()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var editor = await ledger.AddMemberAsync("editor");

        await using (var db = _fixture.NewAppDbContextAsUser(editor))
        {
            db.LedgerNotificationSubscribers.Add(Target(ledger.LedgerId));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            db.LedgerNotificationSubscribers.Add(Target(ledger.LedgerId));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
    }
}
