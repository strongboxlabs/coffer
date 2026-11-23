using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Backup;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Api.Upgrades;

namespace Coffer.Api.Tests.Integration.Upgrades;

/// <summary>
/// One upgrade at a time, from the click to the host's word (ADR-0110): the order (verified
/// backup, then pin, then request), what each host outcome does to the pin, and what survives
/// the API restart an upgrade causes.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UpgradeCoordinatorTests : DatabaseTest, IDisposable
{
    private const string From = "0.128.1";
    private const string To = "0.129.0";
    private const string BackupId = "b-up-new";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coffer-upg-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
    private readonly FakeBackups _backups = new();
    private IServiceProvider? _servicesInstance;

    public UpgradeCoordinatorTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // Built on first use: the database is only ready once the test has started.
    private IServiceProvider _services => _servicesInstance ??= new ServiceCollection()
        .AddSingleton(new BackupPinsRepository(Db.NewServiceFactory()))
        .BuildServiceProvider();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeBackups : IUpgradeBackups
    {
        public BackupRunCoordinator.BackupRunState? Next { get; set; }
        public int Starts { get; private set; }
        public bool IsRunning { get; set; }

        public BackupRunCoordinator.BackupRunState? TryStart()
        {
            Starts++;
            return Next;
        }

        public BackupRunCoordinator.BackupRunState? Get(Guid runId) => Next is { } n && n.RunId == runId ? n : null;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private UpgradeMailbox NewMailbox() => new(_dir, _clock);

    private UpgradeCoordinator NewCoordinator() => new(
        _backups, NewMailbox(), _services.GetRequiredService<IServiceScopeFactory>(),
        new FakeLifetime(), _clock, NullLogger<UpgradeCoordinator>.Instance);

    private BackupPinsRepository Pins => _services.GetRequiredService<BackupPinsRepository>();

    private static BackupRunCoordinator.BackupRunState Succeeded(string backupId = BackupId) =>
        new(Guid.NewGuid(), "succeeded", DateTime.UtcNow, DateTime.UtcNow, backupId, 1, null);

    private static BackupRunCoordinator.BackupRunState Failed() =>
        new(Guid.NewGuid(), "failed", DateTime.UtcNow, DateTime.UtcNow, null, null, "Backup failed: no space. Check the server logs.");

    private async Task ClearPinsAsync()
    {
        await using var db = Db.NewDbContext();
        await db.BackupPins.ExecuteDeleteAsync();
    }

    private string Epoch() => _clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void HostSays(string id, string state, string from = From, string to = To)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, $"st_v1_{id}_{state}_{from}_{to}_{Epoch()}"), []);
        // The host consumes the request it answers.
        foreach (var req in Directory.GetFiles(_dir, $"req_v1_{id}_*")) File.Delete(req);
    }

    private static async Task<UpgradeCoordinator.UpgradeAttempt> UntilAsync(
        UpgradeCoordinator coordinator, Func<UpgradeCoordinator.UpgradeAttempt, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var attempt = await coordinator.ObserveAsync();
            if (attempt is not null && done(attempt)) return attempt;
            await Task.Delay(25);
        }
        throw new TimeoutException("The attempt never reached the expected phase.");
    }

    private async Task<(UpgradeCoordinator Coordinator, string RequestId)> RequestedAsync()
    {
        await ClearPinsAsync();
        _backups.Next = Succeeded();
        var coordinator = NewCoordinator();
        Assert.Equal(UpgradeCoordinator.StartOutcome.Started, coordinator.TryStart(From, To, null));
        var attempt = await UntilAsync(coordinator, a => a.Phase == "requested");
        return (coordinator, attempt.RequestId!);
    }

    [Fact]
    public async Task A_verified_backup_is_pinned_then_the_request_is_written()
    {
        var (coordinator, requestId) = await RequestedAsync();

        var request = Assert.Single(NewMailbox().Read().Requests);
        Assert.Equal((requestId, To), (request.Id, request.Version));
        Assert.Equal($"before upgrade {From} to {To}", (await Pins.GetUpgradeLabelsAsync())[BackupId]);
        Assert.DoesNotContain(BackupId, await Pins.GetAdminPinnedIdsAsync());
        Assert.Equal(BackupId, (await coordinator.ObserveAsync())!.BackupId);
    }

    [Fact]
    public async Task A_failed_backup_writes_no_request_and_pins_nothing()
    {
        await ClearPinsAsync();
        _backups.Next = Failed();
        var coordinator = NewCoordinator();

        Assert.Equal(UpgradeCoordinator.StartOutcome.Started, coordinator.TryStart(From, To, null));
        var attempt = await UntilAsync(coordinator, a => a.Phase == "failed");

        Assert.False(attempt.Changed);
        Assert.Contains("Backup failed", attempt.Message);
        Assert.Empty(NewMailbox().Read().Requests);
        Assert.Empty(await Pins.GetPinnedIdsAsync());
    }

    [Fact]
    public async Task A_running_backup_refuses_the_start_and_leaves_nothing_behind()
    {
        _backups.Next = null;
        var coordinator = NewCoordinator();

        Assert.Equal(UpgradeCoordinator.StartOutcome.BackupBusy, coordinator.TryStart(From, To, null));
        Assert.Null(await coordinator.ObserveAsync());
    }

    [Fact]
    public async Task A_request_the_host_has_not_answered_blocks_another_start_even_after_a_restart()
    {
        var (coordinator, _) = await RequestedAsync();

        Assert.Equal(UpgradeCoordinator.StartOutcome.UpgradeBusy, coordinator.TryStart(From, "0.130.0", null));
        var afterRestart = NewCoordinator();
        Assert.Equal(UpgradeCoordinator.StartOutcome.UpgradeBusy, afterRestart.TryStart(From, "0.130.0", null));
        Assert.Equal(1, _backups.Starts);   // neither asked for a second backup
    }

    [Theory]
    [InlineData("exit10")]
    [InlineData("exit11")]
    [InlineData("exit13")]
    [InlineData("notlatest")]
    [InlineData("expired")]
    public async Task An_attempt_that_changed_nothing_releases_its_pin_at_once(string state)
    {
        var (coordinator, requestId) = await RequestedAsync();

        HostSays(requestId, state);
        var attempt = await UntilAsync(coordinator, a => a.Phase == "failed");

        Assert.False(attempt.Changed);
        Assert.Empty(await Pins.GetPinnedIdsAsync());
    }

    [Theory]
    [InlineData("exit12")]
    [InlineData("exit14")]
    [InlineData("exit99")]
    public async Task An_attempt_that_got_past_the_first_change_or_is_unknown_keeps_its_pin(string state)
    {
        var (coordinator, requestId) = await RequestedAsync();

        HostSays(requestId, state);
        var attempt = await UntilAsync(coordinator, a => a.Phase == "failed");

        Assert.True(attempt.Changed);
        Assert.Contains(BackupId, await Pins.GetPinnedIdsAsync());
    }

    [Fact]
    public async Task Started_shows_as_started_and_done_trims_the_pins_to_the_newest_two_once()
    {
        await ClearPinsAsync();
        await Pins.PinForUpgradeAsync("b-old-1", "one", null); await Task.Delay(15);
        await Pins.PinForUpgradeAsync("b-old-2", "two", null); await Task.Delay(15);
        _backups.Next = Succeeded();
        var coordinator = NewCoordinator();
        coordinator.TryStart(From, To, null);
        var requestId = (await UntilAsync(coordinator, a => a.Phase == "requested")).RequestId!;

        HostSays(requestId, "started");
        Assert.Equal("started", (await UntilAsync(coordinator, a => a.Phase == "started")).Phase);
        Assert.Equal(3, (await Pins.GetPinnedIdsAsync()).Count);

        HostSays(requestId, "done");
        var done = await UntilAsync(coordinator, a => a.Phase == "done");

        Assert.Equal($"Coffer is now on {To}.", done.Message);
        Assert.Equal(["b-old-2", BackupId], (await Pins.GetPinnedIdsAsync()).Order(StringComparer.Ordinal).ToArray());

        await Pins.PinForUpgradeAsync("b-later", "later", null);
        await coordinator.ObserveAsync();
        Assert.Contains("b-later", await Pins.GetPinnedIdsAsync());   // not trimmed again for the same request
    }

    [Fact]
    public async Task A_request_nobody_answers_is_called_unanswered_and_its_pin_released()
    {
        var (coordinator, _) = await RequestedAsync();

        _clock.Advance(UpgradeCoordinator.HostPickupWindow - TimeSpan.FromSeconds(1));
        Assert.Equal("requested", (await coordinator.ObserveAsync())!.Phase);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var attempt = (await coordinator.ObserveAsync())!;

        Assert.Equal("failed", attempt.Phase);
        Assert.False(attempt.Changed);
        Assert.Contains("host", attempt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await Pins.GetPinnedIdsAsync());
    }

    [Fact]
    public async Task After_a_restart_the_hosts_last_word_is_the_answer_and_it_trims_once()
    {
        await ClearPinsAsync();
        await Pins.PinForUpgradeAsync("b-1", "one", null); await Task.Delay(15);
        await Pins.PinForUpgradeAsync("b-2", "two", null); await Task.Delay(15);
        await Pins.PinForUpgradeAsync("b-3", "three", null);
        HostSays("0123456789abcdef0123456789abcdef", "done");
        var restarted = NewCoordinator();   // the upgrade recreated the API: nothing in memory

        var attempt = await restarted.ObserveAsync();

        Assert.Equal(("done", From, To), (attempt!.Phase, attempt.From, attempt.To));
        Assert.Equal(["b-2", "b-3"], (await Pins.GetPinnedIdsAsync()).Order(StringComparer.Ordinal).ToArray());

        await Pins.PinForUpgradeAsync("b-4", "four", null);
        await restarted.ObserveAsync();
        Assert.Contains("b-4", await Pins.GetPinnedIdsAsync());   // the same status does not trim again
    }

    [Fact]
    public async Task After_a_restart_a_failure_past_the_change_is_shown_and_an_old_status_is_not()
    {
        HostSays("0123456789abcdef0123456789abcdef", "exit12");
        var restarted = NewCoordinator();

        var attempt = await restarted.ObserveAsync();
        Assert.Equal(("failed", true), (attempt!.Phase, attempt.Changed));

        _clock.Advance(UpgradeCoordinator.RecentWindow + TimeSpan.FromMinutes(1));
        Assert.Null(await NewCoordinator().ObserveAsync());
    }

    [Fact]
    public async Task After_a_restart_a_run_the_host_has_only_started_is_still_under_way()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        HostSays(id, "started");
        var restarted = NewCoordinator();   // nothing in memory

        var attempt = await restarted.ObserveAsync();

        Assert.Equal(("started", From, To, id), (attempt!.Phase, attempt.From, attempt.To, attempt.RequestId));
        Assert.False(attempt.Changed);
    }

    [Fact]
    public async Task After_a_restart_an_older_requests_result_does_not_stand_in_for_the_newest_one()
    {
        const string older = "11111111111111111111111111111111";
        const string newest = "22222222222222222222222222222222";
        HostSays(older, "done", from: "0.127.0", to: "0.128.1");
        _clock.Advance(TimeSpan.FromMinutes(5));
        HostSays(newest, "started");
        var restarted = NewCoordinator();

        var attempt = await restarted.ObserveAsync();

        // Not the green card for the previous upgrade: the newest request has not finished.
        Assert.Equal(("started", newest), (attempt!.Phase, attempt.RequestId));

        _clock.Advance(TimeSpan.FromMinutes(1));
        HostSays(newest, "exit12");
        var settled = await NewCoordinator().ObserveAsync();
        Assert.Equal(("failed", true), (settled!.Phase, settled.Changed));
    }

    [Fact]
    public async Task A_request_older_than_the_hosts_expiry_no_longer_blocks_another_start()
    {
        var (_, requestId) = await RequestedAsync();
        var coordinator = NewCoordinator();
        Assert.Equal(UpgradeCoordinator.StartOutcome.UpgradeBusy, coordinator.Blocker());

        _clock.Advance(UpgradeMailbox.RequestExpiry + TimeSpan.FromSeconds(1));

        // The file is still there, because the host has not looked yet, but it is dead.
        Assert.Single(NewMailbox().Read().Requests);
        Assert.Null(coordinator.Blocker());
        _backups.Next = Succeeded("b-up-retry");
        Assert.Equal(UpgradeCoordinator.StartOutcome.Started, coordinator.TryStart(From, To, null));
        Assert.NotEmpty(requestId);
    }

    [Fact]
    public async Task The_blocker_names_a_running_backup_without_starting_anything()
    {
        var coordinator = NewCoordinator();
        Assert.Null(coordinator.Blocker());

        _backups.IsRunning = true;

        Assert.Equal(UpgradeCoordinator.StartOutcome.BackupBusy, coordinator.Blocker());
        Assert.Equal(0, _backups.Starts);
        Assert.Null(await coordinator.ObserveAsync());
    }

    [Theory]
    [InlineData("done", "done", true)]
    [InlineData("exit10", "failed", false)]
    [InlineData("exit11", "failed", false)]
    [InlineData("exit12", "failed", true)]
    [InlineData("exit13", "failed", false)]
    [InlineData("exit14", "failed", true)]
    [InlineData("exit0", "failed", true)]
    [InlineData("exit255", "failed", true)]
    [InlineData("notlatest", "failed", false)]
    public void Each_status_name_means_one_thing(string state, string phase, bool changed)
    {
        var status = new MailboxStatus("0123456789abcdef0123456789abcdef", state, From, To, _clock.GetUtcNow());

        var result = UpgradeCoordinator.Classify(status);

        Assert.Equal((phase, changed), (result.Phase, result.Changed));
    }
}
