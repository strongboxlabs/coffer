using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Coffer.Api.Backup;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Upgrades;

using static Coffer.Api.Contracts.BackupContracts;

namespace Coffer.Api.Tests.Integration.Upgrades;

/// <summary>
/// The Upgrade tab's surface (ADR-0110, <c>/api/admin/upgrade</c>): admin only, the typed phrase,
/// the host and release checks, the passkey tap, and the sequence a started upgrade runs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminUpgradeEndpointsTests : DatabaseTest, IDisposable
{
    private const string Latest = "99.0.0";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coffer-upe-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBackups _backups = new();
    private readonly GitHubStub _github = new();

    public AdminUpgradeEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeBackups : IUpgradeBackups
    {
        public BackupRunCoordinator.BackupRunState? Next { get; set; } =
            new(Guid.NewGuid(), "succeeded", DateTime.UtcNow, DateTime.UtcNow, "b-up-endpoint", 1, null);
        public int Starts { get; private set; }
        public bool IsRunning { get; set; }

        public BackupRunCoordinator.BackupRunState? TryStart()
        {
            Starts++;
            return Next;
        }

        public BackupRunCoordinator.BackupRunState? Get(Guid runId) => Next is { } n && n.RunId == runId ? n : null;
    }

    private sealed class GitHubStub : HttpMessageHandler
    {
        public string Tag { get; set; } = "v" + Latest;
        public bool Down { get; set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Down) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = JsonSerializer.Serialize(new
            {
                tag_name = Tag, body = "<script>alert(1)</script> notes",
                html_url = $"https://github.com/o/r/releases/tag/{Tag}", published_at = "2026-10-30T12:00:00Z",
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ApiFactory NewFactory(bool devAuth = true, string hostNote = "")
    {
        var factory = new ApiFactory(Db)
            .WithConfig("Api:Upgrade:MailboxDir", _dir)
            .WithConfig("Api:Upgrade:Repository", "o/r")
            .WithConfig("Api:Upgrade:HostNote", hostNote)
            .WithServices(services =>
            {
                services.RemoveAll<IUpgradeBackups>();
                services.AddSingleton<IUpgradeBackups>(_backups);
                services.AddHttpClient<ReleaseCheckService>().ConfigurePrimaryHttpMessageHandler(() => _github);
            });
        return devAuth ? factory : factory.WithoutDevAuth();
    }

    private void HostHeartbeat(string state = "running", TimeSpan? age = null)
    {
        Directory.CreateDirectory(_dir);
        var epoch = DateTimeOffset.UtcNow.Add(-(age ?? TimeSpan.Zero)).ToUnixTimeSeconds();
        foreach (var old in Directory.GetFiles(_dir, "hb_v1_*")) File.Delete(old);
        File.WriteAllBytes(Path.Combine(_dir, $"hb_v1_{state}_{epoch}"), []);
    }

    private async Task ResetAsync()
    {
        await using var db = Db.NewDbContext();
        await db.BackupPins.ExecuteDeleteAsync();
        await db.UpgradeRepoToken.ExecuteDeleteAsync();
        await db.SystemSettings.Where(s => s.Key.StartsWith("upgrade.")).ExecuteDeleteAsync();
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    /// <summary>An upgrade takes a backup, which needs the backup passphrase; most paths need one set.</summary>
    private static async Task SetPassphraseAsync(HttpClient client) =>
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/admin/backups/passphrase", new SetBackupPassphraseRequest("a-good-passphrase"))).StatusCode);

    private static Task<HttpResponseMessage> Preflight(HttpClient client, string version = Latest, string phrase = "upgrade") =>
        client.PostAsync("/api/admin/upgrade/preflight", Json(new UpgradeStartRequest(version, phrase)));

    private void StaleRequest(TimeSpan age)
    {
        Directory.CreateDirectory(_dir);
        var epoch = DateTimeOffset.UtcNow.Add(-age).ToUnixTimeSeconds();
        File.WriteAllBytes(Path.Combine(_dir, $"req_v1_{Guid.NewGuid():N}_{Latest}_{epoch}"), []);
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Start(HttpClient client, string version = Latest, string phrase = "upgrade") =>
        client.PostAsync("/api/admin/upgrade", Json(new UpgradeStartRequest(version, phrase)));

    private static async Task<UpgradeStatusResponse> StatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UpgradeStatusResponse>("/api/admin/upgrade", new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    // ---- who may call it

    [Fact]
    public async Task Only_an_admin_session_reaches_any_of_it()
    {
        await ResetAsync();
        var alice = await SyntheticLedger.CreateAsync(Db);   // not an admin
        await using var factory = NewFactory(devAuth: false);
        var cookie = await alice.IssueSessionCookieAsync();
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        member.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        foreach (var client in new[] { member, anonymous })
        {
            var denied = client == member ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            Assert.Equal(denied, (await client.GetAsync("/api/admin/upgrade")).StatusCode);
            Assert.Equal(denied, (await Start(client)).StatusCode);
            Assert.Equal(denied, (await Preflight(client)).StatusCode);
            Assert.Equal(denied, (await client.PostAsync("/api/admin/upgrade/check", null)).StatusCode);
            Assert.Equal(denied, (await client.PutAsync("/api/admin/upgrade/settings", Json(new UpgradeSettingsRequest(true, true, true)))).StatusCode);
            Assert.Equal(denied, (await client.PutAsync("/api/admin/upgrade/token", Json(new UpgradeTokenRequest("x")))).StatusCode);
            Assert.Equal(denied, (await client.DeleteAsync("/api/admin/upgrade/token")).StatusCode);
        }
        Assert.Equal(0, _backups.Starts);
        Assert.Equal(0, _github.Calls);
    }

    // ---- reading it

    [Fact]
    public async Task The_status_starts_with_the_documented_defaults_and_no_host()
    {
        await ResetAsync();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var status = await StatusAsync(client);

        Assert.True(status.InAppEnabled);
        Assert.False(status.AutoCheck);
        Assert.False(status.PrivateRepo);
        Assert.False(status.TokenStored);
        Assert.Equal("absent", status.Host.State);
        Assert.False(status.CanStart);
        Assert.Null(status.Latest);
        Assert.Equal(0, _github.Calls);   // the automatic check is off, so reading the screen is not a request
    }

    [Fact]
    public async Task Check_now_reads_the_release_and_the_notes_travel_as_plain_text()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        await SetPassphraseAsync(client);
        var response = await client.PostAsync("/api/admin/upgrade/check", null);
        var status = await response.Content.ReadFromJsonAsync<UpgradeStatusResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(Latest, status!.Latest!.Version);
        Assert.True(status.UpdateAvailable);
        Assert.True(status.CanStart);
        Assert.Contains("<script>", status.Latest.Notes);   // data; the screen renders it escaped
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
    }

    // ---- settings and the token

    [Fact]
    public async Task Settings_round_trip_and_the_change_is_audited()
    {
        await ResetAsync();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/api/admin/upgrade/settings", Json(new UpgradeSettingsRequest(false, true, true)))).StatusCode);

        var status = await StatusAsync(client);
        Assert.False(status.InAppEnabled);
        Assert.True(status.AutoCheck);
        Assert.True(status.PrivateRepo);
        await using var db = Db.NewDbContext();
        Assert.Contains(await db.AdminAuditEvents.AsNoTracking().ToListAsync(),
            e => e.Action == AdminAuditActions.UpgradeSettingsChanged && e.Detail == "in-app=False, auto-check=True, private-repo=True");
    }

    [Fact]
    public async Task The_token_is_stored_sealed_never_returned_and_audited_without_its_value()
    {
        await ResetAsync();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        const string token = "synthetic-endpoint-token";

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/admin/upgrade/token", Json(new UpgradeTokenRequest(token)))).StatusCode);

        var raw = await client.GetStringAsync("/api/admin/upgrade");
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        Assert.True((await StatusAsync(client)).TokenStored);
        await using (var db = Db.NewDbContext())
        {
            var audit = await db.AdminAuditEvents.AsNoTracking().Where(e => e.Action == AdminAuditActions.UpgradeTokenChanged).ToListAsync();
            Assert.All(audit, e => Assert.DoesNotContain(token, e.Detail ?? "", StringComparison.Ordinal));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/admin/upgrade/token")).StatusCode);
        Assert.False((await StatusAsync(client)).TokenStored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has a space")]
    [InlineData("tab\there")]
    public async Task A_token_that_is_not_one_plain_value_is_refused(string token)
    {
        await ResetAsync();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsync("/api/admin/upgrade/token", Json(new UpgradeTokenRequest(token)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("upgrade-token-invalid", await CodeOf(response));
        Assert.False((await StatusAsync(client)).TokenStored);
    }

    // ---- starting it

    [Theory]
    [InlineData("")]
    [InlineData("upgrad")]
    [InlineData("yes")]
    public async Task A_wrong_phrase_is_refused_before_anything_else(string phrase)
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await Start(client, phrase: phrase);

        Assert.Equal("upgrade-phrase-wrong", await CodeOf(response));
        Assert.Equal(0, _github.Calls);
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task Without_a_running_host_a_switched_off_setting_or_a_host_note_nothing_starts()
    {
        await ResetAsync();
        foreach (var (arrange, note) in new (Action, string)[]
                 {
                     (() => { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }, ""),                // no host
                     (() => HostHeartbeat("disabled"), ""),                                                    // disabled on the host
                     (() => HostHeartbeat("running", TimeSpan.FromMinutes(10)), ""),                           // stale
                     (() => HostHeartbeat(), "wsl"),                                                           // host cannot run it here
                 })
        {
            arrange();
            await using var factory = NewFactory(hostNote: note);
            using var client = factory.CreateClient();

            var response = await Start(client);

            Assert.Equal("upgrade-unavailable", await CodeOf(response));
        }
        Assert.Equal(0, _backups.Starts);
        Assert.Equal(0, _github.Calls);
    }

    [Fact]
    public async Task The_switch_off_in_settings_refuses_a_start()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await client.PutAsync("/api/admin/upgrade/settings", Json(new UpgradeSettingsRequest(false, false, false)));

        Assert.Equal("upgrade-unavailable", await CodeOf(await Start(client)));
        Assert.Equal(0, _backups.Starts);
    }

    [Theory]
    [InlineData("98.0.0")]    // not the latest
    [InlineData("100.0.0")]   // newer than the latest: the host would refuse it too
    [InlineData("latest")]
    public async Task Only_the_latest_release_can_be_requested(string version)
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);

        Assert.Equal("upgrade-nothing-newer", await CodeOf(await Start(client, version)));
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task A_latest_release_that_is_not_newer_than_the_installed_one_is_refused()
    {
        await ResetAsync();
        HostHeartbeat();
        _github.Tag = "v0.0.1";
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);

        Assert.Equal("upgrade-nothing-newer", await CodeOf(await Start(client, "0.0.1")));
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task A_session_without_a_fresh_passkey_tap_cannot_start_and_no_backup_is_taken()
    {
        await ResetAsync();
        HostHeartbeat();
        var admin = await SyntheticLedger.CreateAsync(Db);
        await using (var db = Db.NewDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET is_admin = true WHERE id = {0}", admin.UserId);
        await using (var setup = NewFactory())
        {
            using var setupClient = setup.CreateClient();
            await SetPassphraseAsync(setupClient);
        }
        await using var factory = NewFactory(devAuth: false);
        var cookie = await admin.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        var response = await Start(client);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-required", await CodeOf(response));
        Assert.Equal(0, _backups.Starts);
        Assert.Empty(Directory.GetFiles(_dir, "req_v1_*"));
    }

    [Fact]
    public async Task A_started_upgrade_takes_the_backup_pins_it_writes_the_request_and_is_audited()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);

        var response = await Start(client, phrase: "  Upgrade ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        UpgradeStatusResponse status;
        var waited = 0;
        do
        {
            await Task.Delay(50);
            status = await StatusAsync(client);
        } while (status.Attempt?.Phase == "backing-up" && ++waited < 100);

        Assert.Equal("requested", status.Attempt!.Phase);
        var request = Assert.Single(Directory.GetFiles(_dir, "req_v1_*"));
        Assert.Contains($"_{Latest}_", Path.GetFileName(request), StringComparison.Ordinal);
        await using var db = Db.NewDbContext();
        Assert.Contains("b-up-endpoint", await new BackupPinsRepository(Db.NewServiceFactory()).GetPinnedIdsAsync());
        Assert.Contains(await db.AdminAuditEvents.AsNoTracking().ToListAsync(),
            e => e.Action == AdminAuditActions.UpgradeStarted && e.Detail!.EndsWith($" to {Latest}", StringComparison.Ordinal));
        Assert.False((await StatusAsync(client)).CanStart);   // one at a time
    }

    [Fact]
    public async Task A_second_start_while_one_is_in_progress_is_refused_and_takes_no_second_backup()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, (await Start(client)).StatusCode);

        var second = await Start(client);

        Assert.Equal("upgrade-in-progress", await CodeOf(second));
        Assert.Equal(1, _backups.Starts);
    }

    // ---- the checks that come before the passkey tap

    [Fact]
    public async Task Preflight_answers_nothing_to_refuse_without_a_passkey_a_backup_or_a_request()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);

        var response = await Preflight(client);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, _backups.Starts);
        Assert.Empty(Directory.GetFiles(_dir, "req_v1_*"));
    }

    [Fact]
    public async Task Preflight_refuses_a_release_that_is_no_longer_the_latest_and_the_screen_can_read_the_new_one()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);
        // The screen last saw this release...
        _github.Tag = "v98.0.0";
        await client.PostAsync("/api/admin/upgrade/check", null);
        // ...and a newer one has been published since.
        _github.Tag = "v" + Latest;

        var response = await Preflight(client, "98.0.0");

        Assert.Equal("upgrade-nothing-newer", await CodeOf(response));
        // The refusal read the release, so the next status the screen asks for already shows it.
        Assert.Equal(Latest, (await StatusAsync(client)).Latest!.Version);
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task Preflight_and_start_name_the_missing_backup_passphrase_before_any_tap()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        Assert.Equal("backup-passphrase-not-set", await CodeOf(await Preflight(client)));
        Assert.Equal("backup-passphrase-not-set", await CodeOf(await Start(client)));
        var status = await StatusAsync(client);
        Assert.False(status.CanStart);
        Assert.Contains("backup passphrase", status.CannotStartReason, StringComparison.Ordinal);
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task A_host_that_reports_blocked_turns_the_button_off_and_says_how_to_clear_it()
    {
        await ResetAsync();
        HostHeartbeat("blocked");
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);
        await client.PostAsync("/api/admin/upgrade/check", null);

        var status = await StatusAsync(client);

        Assert.Equal("blocked", status.Host.State);
        Assert.False(status.CanStart);
        Assert.Contains("ack", status.CannotStartReason, StringComparison.Ordinal);
        Assert.Equal("upgrade-unavailable", await CodeOf(await Preflight(client)));
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task A_running_backup_or_a_waiting_request_is_refused_before_the_tap_and_an_expired_request_is_not()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);

        _backups.IsRunning = true;
        Assert.Equal("backup-in-progress", await CodeOf(await Preflight(client)));
        _backups.IsRunning = false;

        StaleRequest(TimeSpan.FromMinutes(1));
        Assert.Equal("upgrade-in-progress", await CodeOf(await Preflight(client)));

        // The host removes a request older than its expiry; until it does, it must not block a retry.
        foreach (var f in Directory.GetFiles(_dir, "req_v1_*")) File.Delete(f);
        StaleRequest(UpgradeMailbox.RequestExpiry + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await Preflight(client)).StatusCode);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_release_the_screen_already_knew_and_shows_the_failure_beside_it()
    {
        await ResetAsync();
        HostHeartbeat();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);
        await client.PostAsync("/api/admin/upgrade/check", null);

        _github.Down = true;
        var response = await client.PostAsync("/api/admin/upgrade/check", null);
        var status = await response.Content.ReadFromJsonAsync<UpgradeStatusResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(Latest, status!.Latest!.Version);
        Assert.True(status.UpdateAvailable);
        Assert.NotNull(status.CheckError);
        // Start never rests on a release it did not just read: with GitHub down it refuses.
        Assert.Equal("upgrade-unavailable", await CodeOf(await Start(client)));
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task An_install_that_tracks_the_latest_tag_is_told_to_upgrade_by_hand_before_any_tap()
    {
        await ResetAsync();
        HostHeartbeat("unpinned");
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SetPassphraseAsync(client);
        await client.PostAsync("/api/admin/upgrade/check", null);

        var status = await StatusAsync(client);

        Assert.Equal("unpinned", status.Host.State);
        Assert.False(status.CanStart);
        Assert.Contains("by hand", status.CannotStartReason, StringComparison.Ordinal);
        Assert.Equal("upgrade-unavailable", await CodeOf(await Preflight(client)));
        Assert.Equal(0, _backups.Starts);
    }

    [Fact]
    public async Task Every_refusal_that_needs_no_passkey_comes_before_the_tap_for_a_real_admin_session()
    {
        await ResetAsync();
        HostHeartbeat();
        var admin = await SyntheticLedger.CreateAsync(Db);
        await using (var db = Db.NewDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET is_admin = true WHERE id = {0}", admin.UserId);
        var cookie = await admin.IssueSessionCookieAsync();
        // The development principal never asks for a tap, so only a real session can tell whether a
        // refusal comes before it. This one has no step-up header at all.
        await using var factory = NewFactory(devAuth: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        // No backup passphrase yet.
        Assert.Equal("backup-passphrase-not-set", await CodeOf(await Preflight(client)));
        Assert.Equal("backup-passphrase-not-set", await CodeOf(await Start(client)));

        await using (var setup = NewFactory())
        {
            using var setupClient = setup.CreateClient();
            await SetPassphraseAsync(setupClient);
        }

        // A backup running.
        _backups.IsRunning = true;
        Assert.Equal("backup-in-progress", await CodeOf(await Preflight(client)));
        Assert.Equal("backup-in-progress", await CodeOf(await Start(client)));
        _backups.IsRunning = false;

        // A release that is not the latest.
        Assert.Equal("upgrade-nothing-newer", await CodeOf(await Preflight(client, "98.0.0")));
        Assert.Equal("upgrade-nothing-newer", await CodeOf(await Start(client, "98.0.0")));

        // Nothing to refuse: the preflight answers with no tap, and start is the first thing to ask for one.
        Assert.Equal(HttpStatusCode.NoContent, (await Preflight(client)).StatusCode);
        Assert.Equal("step-up-required", await CodeOf(await Start(client)));
        Assert.Equal(0, _backups.Starts);
    }
}
