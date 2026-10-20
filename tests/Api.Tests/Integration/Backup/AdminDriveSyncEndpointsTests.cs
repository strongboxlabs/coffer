using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Backup.Drive;
using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The admin Drive-sync surface (ADR-0062 §④a, <c>/api/admin/drive-sync</c>):
/// the RequireAdmin gate and the OAuth authorization-code redirect flow, with the
/// Google OAuth + Drive seams faked (no real network). The drive_sync row is a
/// deployment singleton, so each test resets it first (ApiCollection runs
/// sequentially).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminDriveSyncEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public AdminDriveSyncEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewDbContext();   // service role
        await db.Database.ExecuteSqlRawAsync("DELETE FROM drive_sync;");
    }

    private static async Task<HttpClient> CookieClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static ApiFactory WithFakeDrive(ApiFactory factory, FakeOAuthClient? oauth = null) => factory
        .WithService<IDriveOAuthClient>(_ => oauth ?? new FakeOAuthClient())
        .WithService<IDriveClient>(_ => new FakeDriveClient());

    private static async Task ConnectAsync(ApiFactory factory)
    {
        using var noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await (await noRedirect.PostAsJsonAsync(
            "/api/admin/drive-sync/connect/start",
            new DriveConnectStartRequest { ClientId = "cid", ClientSecret = "secret" }))
            .Content.ReadFromJsonAsync<DriveConnectStartResponse>();
        await noRedirect.GetAsync(
            $"/api/admin/drive-sync/oauth/callback?code=fake-code&state={StateFrom(start!.AuthorizationUrl)}");
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    /// <summary>Pulls the CSRF state the service minted out of the fake's auth URL
    /// (<c>…?state=&lt;hex&gt;</c>), so the callback test can echo it back.</summary>
    private static string StateFrom(string authorizationUrl) => authorizationUrl.Split("state=")[^1];

    [Fact]
    public async Task Non_admin_cookie_is_forbidden()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);

        var resp = await client.GetAsync("/api/admin/drive-sync");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.GetAsync("/api/admin/drive-sync");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Status_defaults_to_not_connected()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient();

        var status = await (await client.GetAsync("/api/admin/drive-sync"))
            .Content.ReadFromJsonAsync<DriveSyncStatus>();
        Assert.NotNull(status);
        Assert.False(status!.Connected);
        Assert.False(status.Enabled);
    }

    [Fact]
    public async Task Connect_start_requires_client_credentials()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/admin/drive-sync/connect/start", new DriveConnectStartRequest { ClientId = "", ClientSecret = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("drive-client-required", await CodeOf(resp));
    }

    [Fact]
    public async Task Connect_start_returns_a_google_authorization_url()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient();

        var start = await (await client.PostAsJsonAsync(
            "/api/admin/drive-sync/connect/start",
            new DriveConnectStartRequest { ClientId = "cid", ClientSecret = "secret" }))
            .Content.ReadFromJsonAsync<DriveConnectStartResponse>();
        Assert.NotNull(start);
        Assert.Contains("state=", start!.AuthorizationUrl);
    }

    [Fact]
    public async Task Callback_with_valid_state_connects_and_seals()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        // Don't follow the redirect — assert on the 302 + its Location.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await (await client.PostAsJsonAsync(
            "/api/admin/drive-sync/connect/start",
            new DriveConnectStartRequest { ClientId = "cid", ClientSecret = "secret" }))
            .Content.ReadFromJsonAsync<DriveConnectStartResponse>();
        var state = StateFrom(start!.AuthorizationUrl);

        var callback = await client.GetAsync(
            $"/api/admin/drive-sync/oauth/callback?code=fake-code&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Contains("drive=connected", callback.Headers.Location!.ToString());

        // The sealed token landed in the DB; status never exposes it.
        await using var db = _fixture.NewDbContext();
        var row = await db.DriveSync.AsNoTracking()
            .Where(d => d.Id == (short)1).FirstAsync();
        Assert.NotNull(row.OauthCiphertext);
        Assert.True(row.OauthCiphertext!.Length > 0);
        // Per-install folder: an install id was assigned and the folder name
        // embeds it ("Coffer Backups [<id>]").
        Assert.False(string.IsNullOrEmpty(row.InstallId));
        Assert.Equal($"Coffer Backups [{row.InstallId}]", row.FolderName);
    }

    [Fact]
    public async Task Callback_with_unknown_state_redirects_with_error()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var callback = await client.GetAsync(
            "/api/admin/drive-sync/oauth/callback?code=fake-code&state=never-issued");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Contains("drive=error", callback.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Callback_is_reachable_anonymously()
    {
        // No dev-auth, no cookie — the callback is guarded by the state, not auth,
        // because Google redirects the browser to it cross-site.
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture).WithoutDevAuth());
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var callback = await client.GetAsync(
            "/api/admin/drive-sync/oauth/callback?code=x&state=whatever");
        // Reaches the handler (302 back to the app) rather than 401/403.
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    [Fact]
    public async Task Enable_without_connection_is_422()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient();

        var resp = await client.PutAsJsonAsync(
            "/api/admin/drive-sync/enabled", new DriveEnabledRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("drive-not-connected", await CodeOf(resp));
    }

    [Fact]
    public async Task Upload_all_without_connection_is_422()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/admin/drive-sync/upload-all", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("drive-not-connected", await CodeOf(resp));
    }

    [Fact]
    public async Task Disconnect_after_connect_clears_status()
    {
        await ResetAsync();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture));
        using var noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await (await noRedirect.PostAsJsonAsync(
            "/api/admin/drive-sync/connect/start",
            new DriveConnectStartRequest { ClientId = "cid", ClientSecret = "secret" }))
            .Content.ReadFromJsonAsync<DriveConnectStartResponse>();
        await noRedirect.GetAsync(
            $"/api/admin/drive-sync/oauth/callback?code=fake-code&state={StateFrom(start!.AuthorizationUrl)}");

        using var client = factory.CreateClient();
        var disc = await client.PostAsync("/api/admin/drive-sync/disconnect", content: null);
        Assert.Equal(HttpStatusCode.OK, disc.StatusCode);

        var status = await (await client.GetAsync("/api/admin/drive-sync"))
            .Content.ReadFromJsonAsync<DriveSyncStatus>();
        Assert.False(status!.Connected);
    }

    // --- FU-080: disconnect revokes at Google ---------------------------------

    [Fact]
    public async Task Disconnect_revokes_the_refresh_token_at_google()
    {
        // Before, disconnect forgot the token here and left the grant live at Google.
        await ResetAsync();
        var oauth = new FakeOAuthClient();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture), oauth);
        await ConnectAsync(factory);
        using var client = factory.CreateClient();

        var disc = await client.PostAsync("/api/admin/drive-sync/disconnect", content: null);

        Assert.Equal(new[] { "refresh-xyz" }, oauth.Revoked);
        using var doc = JsonDocument.Parse(await disc.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("revokedAtGoogle").GetBoolean());
    }

    [Fact]
    public async Task Disconnect_still_forgets_the_token_here_when_google_cannot_be_told_and_says_so()
    {
        await ResetAsync();
        var oauth = new FakeOAuthClient { RevokeSucceeds = false };
        await using var factory = WithFakeDrive(new ApiFactory(_fixture), oauth);
        await ConnectAsync(factory);
        using var client = factory.CreateClient();

        var disc = await client.PostAsync("/api/admin/drive-sync/disconnect", content: null);

        using var doc = JsonDocument.Parse(await disc.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("revokedAtGoogle").GetBoolean());
        var status = await (await client.GetAsync("/api/admin/drive-sync"))
            .Content.ReadFromJsonAsync<DriveSyncStatus>();
        Assert.False(status!.Connected);   // a network failure must not block a disconnect
    }

    [Fact]
    public async Task Disconnecting_when_nothing_is_connected_revokes_nothing()
    {
        await ResetAsync();
        var oauth = new FakeOAuthClient();
        await using var factory = WithFakeDrive(new ApiFactory(_fixture), oauth);
        using var client = factory.CreateClient();

        var disc = await client.PostAsync("/api/admin/drive-sync/disconnect", content: null);

        Assert.Equal(HttpStatusCode.OK, disc.StatusCode);
        Assert.Empty(oauth.Revoked);
    }

    [Fact]
    public void The_authorization_url_asks_for_drive_file_only_and_not_previously_granted_scopes()
    {
        // include_granted_scopes would fold every scope the OAuth client was ever granted into
        // the token, widening it past drive.file when the client is reused (FU-080).
        var url = new GoogleDriveOAuthClient(new HttpClient(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GoogleDriveOAuthClient>.Instance)
            .BuildAuthorizationUrl("cid", "https://coffer.test/cb", "st");

        Assert.DoesNotContain("include_granted_scopes", url);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal(GoogleDriveOAuthClient.DriveFileScope, query["scope"]);
    }

    // --- Fakes ---------------------------------------------------------------

    /// <summary>Embeds the state in the auth URL and authorizes any code.</summary>
    private sealed class FakeOAuthClient : IDriveOAuthClient
    {
        public bool RevokeSucceeds { get; init; } = true;
        public List<string> Revoked { get; } = [];

        public Task<bool> RevokeAsync(string token, CancellationToken ct)
        {
            Revoked.Add(token);
            return Task.FromResult(RevokeSucceeds);
        }

        public string BuildAuthorizationUrl(string clientId, string redirectUri, string state) =>
            $"https://example.test/auth?state={state}";

        public Task<DriveTokenResult> ExchangeCodeAsync(
            string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct) =>
            Task.FromResult(new DriveTokenResult(true, RefreshToken: "refresh-xyz"));
    }

    private sealed class FakeDriveClient : IDriveClient
    {
        public const string Email = "fake@example.com";

        public Task<string?> GetAccountEmailAsync(DriveCredentials c, CancellationToken ct) =>
            Task.FromResult<string?>(Email);

        public Task<DriveFolder> EnsureBackupFolderAsync(DriveCredentials c, string name, CancellationToken ct) =>
            Task.FromResult(new DriveFolder("folder-1", name));

        public Task<string> UploadAsync(
            DriveCredentials c, string folderId, string fileName, Stream content,
            DriveContentCheck expected, CancellationToken ct) =>
            Task.FromResult("file-1");

        public Task<IReadOnlyList<DriveArtifact>> ListAsync(DriveCredentials c, string folderId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DriveArtifact>>(Array.Empty<DriveArtifact>());

        public Task DeleteAsync(DriveCredentials c, string fileId, CancellationToken ct) => Task.CompletedTask;
    }
}
