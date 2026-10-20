using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// A user sees and ends their own sessions (FU-087; ADR-0013 promised the list), and a
/// credential change ends every other session.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SessionListTests
{
    private readonly PostgresFixture _fixture;

    public SessionListTests(PostgresFixture fixture) => _fixture = fixture;

    private static HttpClient ClientFor(ApiFactory factory, string cookie)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private async Task<List<Guid>> LiveSessionIdsAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.AuthSessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .Select(s => s.Id).ToListAsync();
    }

    [Fact]
    public async Task The_list_is_the_users_live_sessions_with_this_one_marked()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var here = await ledger.IssueSessionCookieAsync();
        await ledger.IssueSessionCookieAsync();              // elsewhere
        await ledger.IssueSessionCookieAsync();              // will sit idle past the timeout
        await other.IssueSessionCookieAsync();               // someone else's
        var ids = await LiveSessionIdsAsync(ledger.UserId);

        await using (var db = _fixture.NewDbContext())
        {
            // Idle past the 7-day timeout: no longer signs anyone in, so not listed.
            var newest = await db.AuthSessions.Where(s => s.UserId == ledger.UserId)
                .OrderByDescending(s => s.CreatedAt).FirstAsync();
            await db.AuthSessions.Where(s => s.Id == newest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, DateTime.UtcNow.AddDays(-8)));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = ClientFor(factory, here);
        var list = await client.GetFromJsonAsync<List<AccountEndpoints.SessionView>>("/api/auth/sessions");

        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.All(list, s => Assert.Contains(s.Id, ids));
        Assert.Single(list, s => s.Current);
    }

    [Fact]
    public async Task A_user_ends_one_of_their_sessions_and_not_someone_elses()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var here = await ledger.IssueSessionCookieAsync();
        await ledger.IssueSessionCookieAsync();
        await other.IssueSessionCookieAsync();
        var mine = (await LiveSessionIdsAsync(ledger.UserId)).ToHashSet();
        var theirs = (await LiveSessionIdsAsync(other.UserId)).Single();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = ClientFor(factory, here);
        var current = (await client.GetFromJsonAsync<List<AccountEndpoints.SessionView>>("/api/auth/sessions"))!
            .Single(s => s.Current).Id;
        var elsewhere = mine.Single(id => id != current);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/auth/sessions/{elsewhere}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.DeleteAsync($"/api/auth/sessions/{theirs}")).StatusCode);

        Assert.Equal([current], await LiveSessionIdsAsync(ledger.UserId));
        Assert.Equal([theirs], await LiveSessionIdsAsync(other.UserId));
    }

    [Fact]
    public async Task Removing_a_passkey_ends_every_other_session()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var keep = await ledger.AddCredentialAsync(nickname: "keep");
        var lost = await ledger.AddCredentialAsync(nickname: "lost");
        var here = await ledger.IssueSessionCookieAsync();
        await ledger.IssueSessionCookieAsync();
        await ledger.IssueSessionCookieAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = ClientFor(factory, here);
        var resp = await StepUpTestHelper.SendWithStepUpAsync(client,
            new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/credentials/{lost.Id}"),
            StepUpActions.PasskeyDelete, keep.CredentialId);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        // This session survives; the two others, which the lost passkey may have opened, do not.
        Assert.Single(await LiveSessionIdsAsync(ledger.UserId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/sessions")).StatusCode);
    }

    [Fact]
    public async Task Regenerating_recovery_codes_ends_every_other_session()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var key = await ledger.AddCredentialAsync(nickname: "key");
        var here = await ledger.IssueSessionCookieAsync();
        await ledger.IssueSessionCookieAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = ClientFor(factory, here);
        var resp = await StepUpTestHelper.SendWithStepUpAsync(client,
            new HttpRequestMessage(HttpMethod.Post, "/api/auth/recovery-codes/regenerate"),
            StepUpActions.RecoveryCodes, key.CredentialId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(await LiveSessionIdsAsync(ledger.UserId));
    }
}
