using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Meta;
using Coffer.Api.Upgrades;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The Upgrade tab's surface (ADR-0110). Every route is gated by <see cref="AuthPolicies.RequireAdmin"/>,
/// which is the cookie session and not an MCP bearer: an upgrade runs a new release on this install, so
/// no connected app reaches it.
///
///   * GET    /api/admin/upgrade          — everything the tab shows, including the attempt's progress
///   * PUT    /api/admin/upgrade/settings — the in-app switch, the automatic check, the private fork
///   * PUT    /api/admin/upgrade/token    — store the private-repository token (sealed)
///   * DELETE /api/admin/upgrade/token    — remove it
///   * POST   /api/admin/upgrade/check    — check for a release now
///   * POST   /api/admin/upgrade/preflight — every refusal that needs no passkey, so none is spent on one
///   * POST   /api/admin/upgrade          — start an upgrade: passkey tap, typed phrase, verified backup
/// </summary>
public static class AdminUpgradeEndpoints
{
    /// <summary>The word typed to confirm, checked here, trimmed and without regard to case.</summary>
    public const string ConfirmationPhrase = "upgrade";

    /// <summary>A host that has not written its state for this long (three ticks) is reported stale.</summary>
    public static readonly TimeSpan HostStaleAfter = TimeSpan.FromSeconds(180);

    private const int MaxTokenLength = 500;

    public static IEndpointRouteBuilder MapAdminUpgradeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/upgrade")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("", GetStatusAsync);
        group.MapPut("/settings", SetSettingsAsync);
        group.MapPut("/token", SetTokenAsync);
        group.MapDelete("/token", ClearTokenAsync);
        group.MapPost("/check", CheckNowAsync);
        group.MapPost("/preflight", PreflightAsync);
        group.MapPost("", StartAsync);
        return routes;
    }

    private static async Task<UpgradeStatusResponse> BuildAsync(
        SystemSettingsRepository settings, UpgradeRepoTokenStore token, ReleaseCheckService check,
        UpgradeCoordinator coordinator, UpgradeMailbox mailbox, IOptions<ApiOptions> options,
        BackupManager backups, TimeProvider clock, ReleaseCheckResult? result, CancellationToken ct)
    {
        var upgrade = options.Value.Upgrade;
        var inApp = await settings.GetBoolAsync(SystemSettingsRepository.UpgradeInAppEnabledKey, fallback: true, ct).ConfigureAwait(false);
        var auto = await settings.GetBoolAsync(SystemSettingsRepository.UpgradeAutoCheckKey, fallback: false, ct).ConfigureAwait(false);
        var privateRepo = await settings.GetBoolAsync(SystemSettingsRepository.UpgradePrivateRepoKey, fallback: false, ct).ConfigureAwait(false);
        var tokenStored = await token.HasAsync(ct).ConfigureAwait(false);

        var host = HostOf(mailbox, clock);
        var latest = result?.Latest;
        var installed = VersionInfo.Semver;
        var updateAvailable = latest is not null && ReleaseCheckService.IsNewer(latest.Version, installed);
        var attempt = await coordinator.ObserveAsync(ct).ConfigureAwait(false);
        var passphraseSet = await backups.IsPassphraseConfiguredAsync(ct).ConfigureAwait(false);
        var reason = CannotStartReason(inApp, upgrade.HostNote, host, attempt, updateAvailable, passphraseSet);

        return new UpgradeStatusResponse(
            Installed: installed,
            InAppEnabled: inApp, AutoCheck: auto, PrivateRepo: privateRepo, TokenStored: tokenStored,
            Repository: upgrade.Repository, InstallDir: upgrade.InstallDir, HostNote: upgrade.HostNote,
            Host: host,
            Latest: latest is null ? null : new UpgradeReleaseDto(latest.Version, latest.Notes, latest.Url, latest.PublishedAt),
            CheckedAt: result?.CheckedAt, CheckError: result?.Error,
            UpdateAvailable: updateAvailable,
            CanStart: reason is null, CannotStartReason: reason,
            Attempt: attempt is null ? null : new UpgradeAttemptDto(
                attempt.From, attempt.To, attempt.Phase, attempt.StartedAtUtc, attempt.Message, attempt.Changed));
    }

    private static UpgradeHostDto HostOf(UpgradeMailbox mailbox, TimeProvider clock)
    {
        var host = mailbox.Read().Host;
        if (host is null) return new UpgradeHostDto("absent", null);
        if (clock.GetUtcNow() - host.At > HostStaleAfter) return new UpgradeHostDto("stale", host.At);
        // "blocked" is the host's own word that an earlier run failed after it began changing the
        // install and nobody has run `ack` yet; "unpinned" that COFFER_IMAGE_TAG is not a version, so
        // it cannot tell what is installed. Any other name it writes is the ordinary state.
        return new UpgradeHostDto(host.State is "disabled" or "blocked" or "unpinned" ? host.State : "running", host.At);
    }

    /// <summary>Why the Upgrade button is off, or null when it is on. The server checks the same again on start.</summary>
    private static string? CannotStartReason(
        bool inApp, string hostNote, UpgradeHostDto host, UpgradeCoordinator.UpgradeAttempt? attempt,
        bool updateAvailable, bool passphraseSet)
    {
        if (!string.IsNullOrEmpty(hostNote)) return "This install cannot upgrade itself from the app.";
        if (!inApp) return "In-app upgrade is switched off in Settings.";
        if (host.State != "running")
            return host.State == "disabled" ? "The host updater is disabled on the host."
                 : host.State == "blocked" ? "An earlier upgrade failed after it began changing this install, so the host refuses new ones. Run ack on the host once you have looked."
                 : host.State == "unpinned" ? "This install tracks the latest tag, so the host cannot tell which version it runs. Upgrade it by hand on the host."
                 : host.State == "stale" ? "The host updater has not reported for a while."
                 : "The host updater has not reported yet.";
        if (attempt is { Phase: "backing-up" or "requested" or "started" }) return "An upgrade is already in progress.";
        if (!passphraseSet) return "Set a backup passphrase under Backups first. An upgrade takes a backup, and it is encrypted with it.";
        if (!updateAvailable) return "There is no newer release.";
        return null;
    }

    private static async Task<IResult> GetStatusAsync(
        SystemSettingsRepository settings, UpgradeRepoTokenStore token, ReleaseCheckService check,
        UpgradeCoordinator coordinator, UpgradeMailbox mailbox, IOptions<ApiOptions> options,
        BackupManager backups, TimeProvider clock, CancellationToken ct)
    {
        // Lazy: goes to GitHub only with the automatic check on and the last result stale.
        var result = await check.CurrentAsync(ct).ConfigureAwait(false);
        return Results.Ok(await BuildAsync(settings, token, check, coordinator, mailbox, options, backups, clock, result, ct)
            .ConfigureAwait(false));
    }

    private static async Task<IResult> CheckNowAsync(
        SystemSettingsRepository settings, UpgradeRepoTokenStore token, ReleaseCheckService check,
        UpgradeCoordinator coordinator, UpgradeMailbox mailbox, IOptions<ApiOptions> options,
        BackupManager backups, TimeProvider clock, CancellationToken ct)
    {
        await check.CheckNowAsync(ct).ConfigureAwait(false);
        // What the screen shows is the held result, which keeps a known release through a failed check.
        var result = await check.CurrentAsync(ct).ConfigureAwait(false);
        return Results.Ok(await BuildAsync(settings, token, check, coordinator, mailbox, options, backups, clock, result, ct)
            .ConfigureAwait(false));
    }

    private static async Task<IResult> SetSettingsAsync(
        UpgradeSettingsRequest request, SystemSettingsRepository settings, AdminAuditRepository audit,
        ICurrentUserAccessor currentUser, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow().UtcDateTime;
        await settings.SetBoolAsync(SystemSettingsRepository.UpgradeInAppEnabledKey, request.InAppEnabled, currentUser.UserId, now, ct).ConfigureAwait(false);
        await settings.SetBoolAsync(SystemSettingsRepository.UpgradeAutoCheckKey, request.AutoCheck, currentUser.UserId, now, ct).ConfigureAwait(false);
        await settings.SetBoolAsync(SystemSettingsRepository.UpgradePrivateRepoKey, request.PrivateRepo, currentUser.UserId, now, ct).ConfigureAwait(false);
        await audit.AppendAsync(AdminAuditActions.UpgradeSettingsChanged, currentUser.UserId,
            $"in-app={request.InAppEnabled}, auto-check={request.AutoCheck}, private-repo={request.PrivateRepo}", ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> SetTokenAsync(
        UpgradeTokenRequest request, UpgradeRepoTokenStore token, AdminAuditRepository audit,
        ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var value = request.Token?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Length > MaxTokenLength || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            return BusinessError.Problem(BusinessError.Codes.UpgradeTokenInvalid,
                "Enter the token as one value, without spaces, up to 500 characters.");
        await token.SetAsync(value, currentUser.UserId, ct).ConfigureAwait(false);
        await audit.AppendAsync(AdminAuditActions.UpgradeTokenChanged, currentUser.UserId, "stored", ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearTokenAsync(
        UpgradeRepoTokenStore token, AdminAuditRepository audit, ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        await token.ClearAsync(ct).ConfigureAwait(false);
        await audit.AppendAsync(AdminAuditActions.UpgradeTokenChanged, currentUser.UserId, "removed", ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>What a refusal check found: the refusal to return, or the release it read.</summary>
    private readonly record struct Gate(IResult? Refusal, ReleaseCheckResult? Result);

    /// <summary>
    /// Every refusal that needs neither a passkey nor a backup, in the order a person can fix them:
    /// the typed word, an unavailable host, no backup passphrase, something already running, and a
    /// release that is not the latest. The release is read fresh, not trusted from the screen, since
    /// the host refuses anything but the latest. Start and preflight both run it, so a refusal is
    /// the same at either, and the preflight lets the screen ask before the passkey does.
    /// </summary>
    private static async Task<Gate> CheckAsync(
        UpgradeStartRequest request, SystemSettingsRepository settings, ReleaseCheckService check,
        UpgradeCoordinator coordinator, UpgradeMailbox mailbox, IOptions<ApiOptions> options,
        BackupManager backups, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Phrase?.Trim(), ConfirmationPhrase, StringComparison.OrdinalIgnoreCase))
            return new Gate(BusinessError.Problem(BusinessError.Codes.UpgradePhraseWrong, $"Type {ConfirmationPhrase} to confirm."), null);

        var inApp = await settings.GetBoolAsync(SystemSettingsRepository.UpgradeInAppEnabledKey, fallback: true, ct).ConfigureAwait(false);
        var host = HostOf(mailbox, clock);
        var hostNote = options.Value.Upgrade.HostNote;
        if (!string.IsNullOrEmpty(hostNote) || !inApp || host.State != "running")
            return new Gate(BusinessError.Problem(BusinessError.Codes.UpgradeUnavailable,
                CannotStartReason(inApp, hostNote, host, null, updateAvailable: true, passphraseSet: true)
                    ?? "Upgrading from the app is not available."), null);

        if (!await backups.IsPassphraseConfiguredAsync(ct).ConfigureAwait(false))
            return new Gate(BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet,
                "Set a backup passphrase under Backups first. An upgrade takes a backup, and it is encrypted with it."), null);

        switch (coordinator.Blocker())
        {
            case UpgradeCoordinator.StartOutcome.UpgradeBusy:
                return new Gate(BusinessError.Problem(BusinessError.Codes.UpgradeInProgress, "An upgrade is already in progress."), null);
            case UpgradeCoordinator.StartOutcome.BackupBusy:
                return new Gate(BusinessError.Problem(BusinessError.Codes.BackupInProgress,
                    "A backup is running. Wait for it to finish, then start the upgrade."), null);
        }

        var result = await check.CheckNowAsync(ct).ConfigureAwait(false);
        if (result.Latest is null)
            return new Gate(BusinessError.Problem(BusinessError.Codes.UpgradeUnavailable,
                result.Error ?? "The latest release could not be read."), null);
        var installed = VersionInfo.Semver;
        if (request.Version != result.Latest.Version || !ReleaseCheckService.IsNewer(result.Latest.Version, installed))
            return new Gate(BusinessError.Problem(BusinessError.Codes.UpgradeNothingNewer,
                $"{installed} is installed and {result.Latest.Version} is the latest release; check again."), null);

        return new Gate(null, result);
    }

    /// <summary>
    /// Everything start would refuse for, answered without a passkey or a backup. The screen asks
    /// this first and only then asks for the tap, so a stale screen or a blocked host costs nothing.
    /// 204 means start would not refuse for any of these; start checks again, since time passes.
    /// </summary>
    private static async Task<IResult> PreflightAsync(
        UpgradeStartRequest request, SystemSettingsRepository settings, ReleaseCheckService check,
        UpgradeCoordinator coordinator, UpgradeMailbox mailbox, IOptions<ApiOptions> options,
        BackupManager backups, TimeProvider clock, CancellationToken ct)
    {
        var gate = await CheckAsync(request, settings, check, coordinator, mailbox, options, backups, clock, ct).ConfigureAwait(false);
        return gate.Refusal ?? Results.NoContent();
    }

    private static async Task<IResult> StartAsync(
        UpgradeStartRequest request, SystemSettingsRepository settings, UpgradeRepoTokenStore token,
        ReleaseCheckService check, UpgradeCoordinator coordinator, UpgradeMailbox mailbox,
        AdminAuditRepository audit, ICurrentUserAccessor currentUser, IOptions<ApiOptions> options,
        BackupManager backups, StepUpGuard stepUp, TimeProvider clock, CancellationToken ct)
    {
        var gate = await CheckAsync(request, settings, check, coordinator, mailbox, options, backups, clock, ct).ConfigureAwait(false);
        if (gate.Refusal is not null) return gate.Refusal;
        var result = gate.Result!;
        var latest = result.Latest!;
        var installed = VersionInfo.Semver;

        var approval = await stepUp.RequireAsync(StepUpActions.Upgrade, ct).ConfigureAwait(false);
        if (approval.Failure is not null) return approval.Failure;

        // Checked again by the start itself: another request can take the slot while the tap is asked for.
        var outcome = coordinator.TryStart(installed, latest.Version, currentUser.UserId);
        if (outcome == UpgradeCoordinator.StartOutcome.UpgradeBusy)
            return BusinessError.Problem(BusinessError.Codes.UpgradeInProgress, "An upgrade is already in progress.");
        if (outcome == UpgradeCoordinator.StartOutcome.BackupBusy)
            return BusinessError.Problem(BusinessError.Codes.BackupInProgress,
                "A backup is running. Wait for it to finish, then start the upgrade.");

        await audit.AppendAsync(AdminAuditActions.UpgradeStarted, currentUser.UserId,
            $"{installed} to {latest.Version}", ct).ConfigureAwait(false);
        return Results.Json(await BuildAsync(settings, token, check, coordinator, mailbox, options, backups, clock, result, ct)
            .ConfigureAwait(false), statusCode: StatusCodes.Status202Accepted);
    }
}
