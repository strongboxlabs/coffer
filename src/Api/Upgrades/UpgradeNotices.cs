using System.Text.Json;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;

namespace Coffer.Api.Upgrades;

/// <summary>
/// An available release, said as an ordinary deployment-scope notification (ADR-0110): the same
/// dot, the same list, the same delivery rules as every other problem (ADR-0096, ADR-0102).
/// </summary>
/// <remarks>
/// <para>Subject <c>upgrade.</c>. <c>upgrade.available</c> is a warning, because the in-app badge
/// takes warning and above and a third visual state is ruled out (ADR-0102 D5);
/// <c>upgrade.current</c> is its info all-clear. Nothing is dismissible, as with every subject.</para>
/// <para>One setting remembers the version last announced, so a newer release is announced once and
/// an all-clear follows only for something announced. A manual check and the automatic one reach the
/// same code, which is what makes the indicator independent of how a release was found. The all-clear
/// does not wait for a check: it runs when the installed version has caught up, from the scheduler
/// tick and from the admin read behind the navigation, so an install that upgraded with the automatic
/// check off does not keep a stale dot.</para>
/// <para>Publishing is best-effort and a failure is logged, never thrown: a notice that cannot be
/// recorded must not fail the check that found it. The announced version is written only after the
/// publish succeeded, so the next call tries again.</para>
/// </remarks>
public sealed class UpgradeNotices
{
    public const string AvailableKey = "upgrade.available";
    public const string CurrentKey = "upgrade.current";

    private readonly NotificationPublisher _publisher;
    private readonly SystemSettingsRepository _settings;
    private readonly ReleaseCheckState _state;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgradeNotices> _logger;

    public UpgradeNotices(
        NotificationPublisher publisher, SystemSettingsRepository settings, ReleaseCheckState state,
        TimeProvider clock, ILogger<UpgradeNotices> logger)
    {
        _publisher = publisher;
        _settings = settings;
        _state = state;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The version last announced and not yet cleared, or null.</summary>
    public async Task<string?> AnnouncedAsync(CancellationToken ct = default)
    {
        var json = await _settings.GetValueJsonAsync(SystemSettingsRepository.UpgradeAnnouncedKey, ct)
            .ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<string>(json);
            return UpgradeMailbox.IsVersion(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clear what was announced once the installed version has caught up with it. Takes the check's
    /// gate, so it cannot interleave with a check's own announcement.
    /// </summary>
    public async Task ClearIfInstalledAsync(string installed, CancellationToken ct = default)
    {
        await _state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReconcileLockedAsync(latest: null, installed, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    /// <summary>
    /// Announce <paramref name="latest"/> if it is newer than <paramref name="installed"/> and not yet
    /// announced; clear what was announced if it is no longer ahead. The caller holds the gate
    /// (<see cref="ReleaseCheckService.CheckNowAsync"/> does).
    /// </summary>
    /// <param name="latest">The newest release a check found, or null when none is known.</param>
    internal async Task ReconcileLockedAsync(string? latest, string installed, CancellationToken ct)
    {
        try
        {
            var announced = await AnnouncedAsync(ct).ConfigureAwait(false);

            if (latest is not null && ReleaseCheckService.IsNewer(latest, installed))
            {
                if (string.Equals(announced, latest, StringComparison.Ordinal)) return;
                await _publisher.PublishAsync(new NotificationEvent(
                    NotificationSeverity.Warning, NotificationTopics.Upgrade, AvailableKey,
                    $"Update available: {latest}",
                    new Dictionary<string, string> { ["version"] = latest, ["installed"] = installed }), ct)
                    .ConfigureAwait(false);
                await _settings.SetValueJsonAsync(
                    SystemSettingsRepository.UpgradeAnnouncedKey, JsonSerializer.Serialize(latest),
                    updatedBy: null, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                return;
            }

            if (announced is null) return;

            // Something was announced. It stands unless the install has reached it, or the newest
            // release a check can see is no longer ahead (a withdrawn release must not pin a dot).
            var gone = latest is not null || !ReleaseCheckService.IsNewer(announced, installed);
            if (!gone) return;

            await _publisher.PublishAsync(new NotificationEvent(
                NotificationSeverity.Info, NotificationTopics.Upgrade, CurrentKey,
                $"Up to date: {installed}",
                new Dictionary<string, string> { ["installed"] = installed }), ct).ConfigureAwait(false);
            await _settings.RemoveAsync(SystemSettingsRepository.UpgradeAnnouncedKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record the upgrade notice; the next check or tick tries again.");
        }
    }
}
