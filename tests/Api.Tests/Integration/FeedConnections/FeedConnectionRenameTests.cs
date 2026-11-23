using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.FeedConnections;

/// <summary>
/// <c>PATCH /api/ledgers/{ledgerId}/feed-connections/{connectionId}</c>: an owner renames a
/// connection. The name is trimmed and must be 1 to 80 characters; two connections may
/// share one; only an owner may, as only an owner may disconnect; and a sync never puts
/// SimpleFIN's name back.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FeedConnectionRenameTests : DatabaseTest
{
    public FeedConnectionRenameTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string StartingName = "Starting name";

    private static string ConnectionPath(SyntheticLedger ledger, Guid connectionId) =>
        $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connectionId}";

    private async Task<FeedConnectionRow> ConnectionAsync(SyntheticLedger ledger, string? name = StartingName)
    {
        await using var seed = Db.NewDbContext();
        return await new FeedConnectionsRepository(seed).CreateSimpleFinAsync(
            ledger.LedgerId, ledger.UserId, [1, 2, 3], name);
    }

    private async Task<string?> StoredNameAsync(Guid connectionId)
    {
        await using var db = Db.NewDbContext();
        return await db.FeedConnections.AsNoTracking()
            .Where(c => c.Id == connectionId).Select(c => c.InstitutionName).SingleAsync();
    }

    /// <summary>A signed-in client for <paramref name="userId"/>.</summary>
    private async Task<HttpClient> ClientAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using (var db = Db.NewDbContext())
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

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string path, string? name) =>
        client.PatchAsJsonAsync(path, new { name });

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    [Fact]
    public async Task An_owner_renames_a_connection_and_the_name_is_trimmed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connection = await ConnectionAsync(ledger);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);

        var response = await RenameAsync(client, ConnectionPath(ledger, connection.Id), "  Renamed feed  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await response.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        Assert.Equal(connection.Id, summary.Id);
        Assert.Equal(ledger.LedgerId, summary.LedgerId);
        Assert.Equal("Renamed feed", summary.InstitutionName);
        Assert.Equal("active", summary.Status);
        Assert.Equal("Renamed feed", await StoredNameAsync(connection.Id));

        var list = (await client.GetFromJsonAsync<FeedConnectionSummary[]>(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections"))!;
        Assert.Equal("Renamed feed", Assert.Single(list).InstitutionName);
    }

    [Fact]
    public async Task A_connection_with_no_name_takes_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connection = await ConnectionAsync(ledger, name: null);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);

        var response = await RenameAsync(client, ConnectionPath(ledger, connection.Id), "Renamed feed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed feed", await StoredNameAsync(connection.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused_and_changes_nothing(string? name)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connection = await ConnectionAsync(ledger);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);

        var response = await RenameAsync(client, ConnectionPath(ledger, connection.Id), name);

        Assert.Equal("feed-connection-name-required", await CodeOfAsync(response));
        Assert.Equal(StartingName, await StoredNameAsync(connection.Id));
    }

    [Fact]
    public async Task A_name_longer_than_80_characters_is_refused_and_one_of_80_is_kept()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connection = await ConnectionAsync(ledger);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);
        var path = ConnectionPath(ledger, connection.Id);

        var tooLong = await RenameAsync(client, path, new string('n', 81));
        Assert.Equal("feed-connection-name-too-long", await CodeOfAsync(tooLong));
        using (var doc = JsonDocument.Parse(await tooLong.Content.ReadAsStringAsync()))
            Assert.Equal("A connection's name is limited to 80 characters.",
                doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal(StartingName, await StoredNameAsync(connection.Id));

        // The limit is on the trimmed name: 80 characters inside spaces is kept.
        var longest = new string('n', 80);
        var kept = await RenameAsync(client, path, $"  {longest}  ");
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(longest, await StoredNameAsync(connection.Id));
    }

    [Fact]
    public async Task Two_connections_may_share_a_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var first = await ConnectionAsync(ledger, "Shared name");
        var second = await ConnectionAsync(ledger);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);

        var response = await RenameAsync(client, ConnectionPath(ledger, second.Id), "Shared name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Shared name", await StoredNameAsync(first.Id));
        Assert.Equal("Shared name", await StoredNameAsync(second.Id));
    }

    [Fact]
    public async Task A_connection_of_another_ledger_or_none_is_not_found_and_keeps_its_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var other = await SyntheticLedger.CreateAsync(Db);
        var theirs = await ConnectionAsync(other);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger.UserId);

        // Named under the caller's own ledger, the other ledger's connection is not there.
        var crossLedger = await RenameAsync(client, ConnectionPath(ledger, theirs.Id), "Renamed feed");
        Assert.Equal("feed-connection-not-found", await CodeOfAsync(crossLedger));
        var unknown = await RenameAsync(client, ConnectionPath(ledger, Guid.NewGuid()), "Renamed feed");
        Assert.Equal("feed-connection-not-found", await CodeOfAsync(unknown));
        // Under the other ledger, the caller is no member.
        var notMember = await RenameAsync(client, ConnectionPath(other, theirs.Id), "Renamed feed");
        Assert.Equal("ledger-not-visible", await CodeOfAsync(notMember));

        Assert.Equal(StartingName, await StoredNameAsync(theirs.Id));
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("viewer")]
    public async Task Only_an_owner_renames_as_only_an_owner_disconnects(string role)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connection = await ConnectionAsync(ledger);
        var member = await ledger.AddMemberAsync(role);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, member);
        var path = ConnectionPath(ledger, connection.Id);

        // A valid name, so only the role gate can refuse it.
        var rename = await RenameAsync(client, path, "Renamed feed");
        var disconnect = await client.DeleteAsync(path);

        Assert.Equal("ledger-not-owner", await CodeOfAsync(rename));
        Assert.Equal("ledger-not-owner", await CodeOfAsync(disconnect));
        Assert.Equal(StartingName, await StoredNameAsync(connection.Id));
    }

    // ----- a sync after a rename -----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(impl(request));
    }

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public async Task A_sync_after_a_rename_keeps_the_new_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        // The feed lists one institution, so the connection starts with its name, and
        // every sync's pull lists it again.
        const string accounts = """
            {"connections":[{"conn_id":"c-1","name":"Institution A","org_id":"org-a","sfin_url":"https://sfin/a"}],
             "errlist":[],
             "accounts":[{"id":"sf-1","conn_id":"c-1","name":"Account A","currency":"USD","balance":"0.00","transactions":[]}]}
            """;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(new HttpClient(new StubHandler(req =>
                req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin/access/abc") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accounts) })),
                TimeProvider.System));
        using var client = await ClientAsync(factory, ledger.UserId);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/claim") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        Assert.Equal("Institution A", connection.InstitutionName);

        var renamed = await RenameAsync(client, ConnectionPath(ledger, connection.Id), "Renamed feed");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var sync = await client.PostAsync($"{ConnectionPath(ledger, connection.Id)}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal("active", (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!.ConnectionStatus);

        Assert.Equal("Renamed feed", await StoredNameAsync(connection.Id));
    }
}
