using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Api.Upgrades;

namespace Coffer.Api.Tests.Integration.Upgrades;

/// <summary>
/// The release check (ADR-0110): lazy and opt-in, "Check now" always available, notes capped,
/// the token sent to GitHub only and never echoed in an error.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReleaseCheckServiceTests : DatabaseTest
{
    private const string Token = "synthetic-repo-token";

    private readonly TestClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
    private readonly StubHandler _handler = new();
    private readonly ReleaseCheckState _state = new();

    public ReleaseCheckServiceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Release(string tag = "v0.129.0", string body = "notes", string url = "https://github.com/o/r/releases/tag/v0.129.0") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = tag, body, html_url = url, published_at = "2026-10-30T12:00:00Z",
        });

    private UpgradeRepoTokenStore NewTokens() => new(Db.NewServiceFactory(), PostgresFixture.FixtureKeys(), _clock);

    private SystemSettingsRepository NewSettings() => new(Db.NewServiceFactory());

    private ReleaseCheckService NewService(string repository = "o/r") => new(
        new HttpClient(_handler), NewSettings(), NewTokens(),
        Options.Create(new ApiOptions { Upgrade = new UpgradeOptions { Repository = repository } }),
        _state, _clock);

    /// <summary>The service as the app builds it: a check that finds a newer release announces it.</summary>
    private ReleaseCheckService NewAnnouncingService(ReleaseCheckState? state = null)
    {
        state ??= _state;
        var notices = new UpgradeNotices(
            new NotificationPublisher(
                Db.NewServiceFactory(), Db.NewLedgerKeyService(), [],
                NullLogger<NotificationPublisher>.Instance, _clock),
            NewSettings(), state, _clock, NullLogger<UpgradeNotices>.Instance);
        return new ReleaseCheckService(
            new HttpClient(_handler), NewSettings(), NewTokens(),
            Options.Create(new ApiOptions { Upgrade = new UpgradeOptions { Repository = "o/r" } }),
            state, _clock, notices);
    }

    private async Task<List<string>> UpgradeEventKeysAsync()
    {
        await using var db = Db.NewServiceDbContext();
        return await db.SystemEvents.AsNoTracking()
            .Where(e => e.Topic == NotificationTopics.Upgrade)
            .Select(e => e.EventKey)
            .ToListAsync();
    }

    private async Task SetAsync(string key, bool value) =>
        await NewSettings().SetBoolAsync(key, value, null, _clock.GetUtcNow().UtcDateTime);

    private async Task ResetAsync()
    {
        await NewTokens().ClearAsync();
        await SetAsync(SystemSettingsRepository.UpgradeAutoCheckKey, false);
        await SetAsync(SystemSettingsRepository.UpgradePrivateRepoKey, false);
    }

    [Fact]
    public async Task Check_now_reads_the_latest_release_anonymously_from_a_public_repository()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release());

        var result = await NewService().CheckNowAsync();

        Assert.Null(result.Error);
        Assert.Equal("0.129.0", result.Latest!.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v0.129.0", result.Latest.Url);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.github.com/repos/o/r/releases/latest", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task A_manual_check_that_finds_a_newer_release_announces_it()
    {
        // The automatic check is OFF here: only an explicit check goes to the network. The dot must
        // not care how the release was found, so this path announces exactly as the automatic one does.
        await ResetAsync();
        _handler.Respond = _ => Json(Release(tag: "v999.0.0"));

        await NewAnnouncingService().CheckNowAsync();

        Assert.Equal(new[] { UpgradeNotices.AvailableKey }, await UpgradeEventKeysAsync());
    }

    [Fact]
    public async Task The_automatic_check_announces_the_same_way()
    {
        await ResetAsync();
        await SetAsync(SystemSettingsRepository.UpgradeAutoCheckKey, true);
        _handler.Respond = _ => Json(Release(tag: "v999.0.0"));

        // A screen load, not a button: the lazy path.
        await NewAnnouncingService().CurrentAsync();

        Assert.Equal(new[] { UpgradeNotices.AvailableKey }, await UpgradeEventKeysAsync());
    }

    [Fact]
    public async Task A_check_that_finds_nothing_newer_announces_nothing()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release(tag: "v0.0.1"));

        await NewAnnouncingService().CheckNowAsync();

        Assert.Empty(await UpgradeEventKeysAsync());
    }

    [Fact]
    public async Task A_failed_check_announces_nothing_and_stores_nothing()
    {
        await ResetAsync();
        _handler.Respond = _ => Json("{}", HttpStatusCode.InternalServerError);

        var result = await NewAnnouncingService().CheckNowAsync();

        Assert.NotNull(result.Error);
        Assert.Empty(await UpgradeEventKeysAsync());
        Assert.Null(await NewSettings().GetValueJsonAsync(SystemSettingsRepository.UpgradeLastCheckKey));
    }

    [Fact]
    public async Task A_failed_check_keeps_the_release_last_found_for_the_screen_but_not_for_a_decision()
    {
        await ResetAsync();
        var service = NewAnnouncingService();
        _handler.Respond = _ => Json(Release(tag: "v999.0.0"));
        await service.CheckNowAsync();

        _handler.Respond = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        var raw = await service.CheckNowAsync();

        // The caller of a decision (the upgrade start) gets what this check read: nothing.
        Assert.Null(raw.Latest);
        Assert.NotNull(raw.Error);
        // What the screen holds keeps the known release, with the failure beside it.
        var held = await service.CurrentAsync();
        Assert.Equal("999.0.0", held!.Latest!.Version);
        Assert.Equal(raw.Error, held.Error);
    }

    [Fact]
    public async Task The_lazy_automatic_check_that_fails_also_keeps_the_known_release()
    {
        await ResetAsync();
        await SetAsync(SystemSettingsRepository.UpgradeAutoCheckKey, true);
        var service = NewAnnouncingService();
        _handler.Respond = _ => Json(Release(tag: "v999.0.0"));
        await service.CurrentAsync();

        // Past the staleness window, with GitHub failing: a screen load does the lazy check.
        _clock.Advance(ReleaseCheckService.Staleness + TimeSpan.FromMinutes(1));
        _handler.Respond = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        var shown = await service.CurrentAsync();

        Assert.Equal("999.0.0", shown!.Latest!.Version);
        Assert.NotNull(shown.Error);
    }

    [Fact]
    public async Task A_failed_check_after_a_restart_keeps_the_release_that_was_stored()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release(tag: "v999.0.0"));
        await NewAnnouncingService().CheckNowAsync();

        // A new process: nothing in memory, and the first thing to happen is a check that fails.
        _handler.Respond = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        var restarted = NewAnnouncingService(new ReleaseCheckState());
        var raw = await restarted.CheckNowAsync();

        Assert.Null(raw.Latest);
        var shown = await restarted.CurrentAsync();
        Assert.Equal("999.0.0", shown!.Latest!.Version);
        Assert.Equal(raw.Error, shown.Error);
    }

    [Fact]
    public async Task A_failed_check_with_nothing_known_before_holds_just_the_failure()
    {
        await ResetAsync();
        _handler.Respond = _ => Json("{}", HttpStatusCode.ServiceUnavailable);
        var service = NewAnnouncingService();

        await service.CheckNowAsync();

        var held = await service.CurrentAsync();
        Assert.Null(held!.Latest);
        Assert.NotNull(held.Error);
    }

    [Fact]
    public async Task The_last_good_result_survives_a_restart_without_a_check()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release(tag: "v0.131.0", body: "what changed"));
        var first = await NewAnnouncingService().CheckNowAsync();
        var requestsBefore = _handler.Requests.Count;

        // A new process: nothing in memory, and the network is off the table.
        _handler.Respond = _ => throw new HttpRequestException("no network after a restart");
        var restarted = await NewAnnouncingService(new ReleaseCheckState()).CurrentAsync();

        Assert.NotNull(restarted);
        Assert.Equal("0.131.0", restarted!.Latest!.Version);
        Assert.Equal("what changed", restarted.Latest.Notes);
        Assert.Equal(first.CheckedAt, restarted.CheckedAt);
        Assert.Equal(requestsBefore, _handler.Requests.Count);
    }

    [Fact]
    public async Task A_stored_result_that_does_not_parse_is_ignored()
    {
        await ResetAsync();
        await NewSettings().SetValueJsonAsync(
            SystemSettingsRepository.UpgradeLastCheckKey, "\"not an object\"", null, _clock.GetUtcNow().UtcDateTime);

        Assert.Null(await NewAnnouncingService(new ReleaseCheckState()).CurrentAsync());
    }

    [Fact]
    public async Task Notes_are_capped_and_only_a_github_link_is_kept()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release(body: new string('x', 9000), url: "https://evil.example/a"));

        var result = await NewService().CheckNowAsync();

        Assert.Equal(ReleaseCheckService.MaxNotesLength, result.Latest!.Notes.Length);
        Assert.Equal("", result.Latest.Url);
    }

    [Fact]
    public async Task A_private_repository_sends_the_token_and_without_one_makes_no_request()
    {
        await ResetAsync();
        await SetAsync(SystemSettingsRepository.UpgradePrivateRepoKey, true);

        var without = await NewService().CheckNowAsync();
        Assert.Empty(_handler.Requests);
        Assert.Contains("token", without.Error!, StringComparison.OrdinalIgnoreCase);

        await NewTokens().SetAsync(Token, null);
        _handler.Respond = _ => Json(Release());
        var with = await NewService().CheckNowAsync();

        Assert.Null(with.Error);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A_public_repository_never_sends_a_stored_token()
    {
        await ResetAsync();
        await NewTokens().SetAsync(Token, null);
        _handler.Respond = _ => Json(Release());

        await NewService().CheckNowAsync();

        Assert.Null(Assert.Single(_handler.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("release-1")]
    [InlineData("1.2.3.4")]
    public async Task A_latest_release_not_tagged_as_a_version_is_an_error(string tag)
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release(tag: tag));

        var result = await NewService().CheckNowAsync();

        Assert.Null(result.Latest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Failures_become_an_error_that_never_names_the_token()
    {
        await ResetAsync();
        await SetAsync(SystemSettingsRepository.UpgradePrivateRepoKey, true);
        await NewTokens().SetAsync(Token, null);

        foreach (var respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
                 {
                     _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                     _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                     _ => Json("not json"),
                     _ => throw new HttpRequestException(Token),
                 })
        {
            _handler.Respond = respond;
            var result = await NewService().CheckNowAsync();
            Assert.Null(result.Latest);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.DoesNotContain(Token, result.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("a/b/c")]
    [InlineData("../x/y")]
    [InlineData("a b/c")]
    public async Task A_repository_that_is_not_owner_slash_name_makes_no_request(string repository)
    {
        await ResetAsync();

        var result = await NewService(repository).CheckNowAsync();

        Assert.NotNull(result.Error);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task With_the_automatic_check_off_the_screen_load_never_goes_to_the_network()
    {
        await ResetAsync();
        _handler.Respond = _ => Json(Release());
        var service = NewService();

        Assert.Null(await service.CurrentAsync());
        Assert.Empty(_handler.Requests);

        await service.CheckNowAsync();   // an explicit check is always available
        Assert.Equal("0.129.0", (await service.CurrentAsync())!.Latest!.Version);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task With_the_automatic_check_on_a_result_is_reused_until_it_is_stale()
    {
        await ResetAsync();
        await SetAsync(SystemSettingsRepository.UpgradeAutoCheckKey, true);
        _handler.Respond = _ => Json(Release());
        var service = NewService();

        await service.CurrentAsync();
        await service.CurrentAsync();
        Assert.Single(_handler.Requests);

        _clock.Advance(ReleaseCheckService.Staleness + TimeSpan.FromMinutes(1));
        await service.CurrentAsync();
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Theory]
    [InlineData("0.129.0", "0.128.1", true)]
    [InlineData("0.128.1", "0.128.1", false)]
    [InlineData("0.128.0", "0.128.1", false)]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.129", "0.128.1", false)]
    [InlineData("0.129.0", "latest", false)]
    public void Newer_is_a_numeric_comparison_of_x_y_z(string candidate, string installed, bool expected)
    {
        Assert.Equal(expected, ReleaseCheckService.IsNewer(candidate, installed));
    }
}
