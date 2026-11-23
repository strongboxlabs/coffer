using System.Globalization;

using Microsoft.Extensions.Hosting;

using Coffer.Api.Backup;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Upgrades;

/// <summary>
/// The backup an upgrade waits for: the manual backup run, which verifies the artifact before it
/// counts. A seam so the order (verified backup, then pin, then request) is testable without pg_dump.
/// </summary>
public interface IUpgradeBackups
{
    /// <summary>A backup is running now, so a second cannot start.</summary>
    bool IsRunning { get; }

    /// <summary>Start a backup off the request, or null when one is already running.</summary>
    BackupRunCoordinator.BackupRunState? TryStart();

    /// <summary>The state of a run this process started.</summary>
    BackupRunCoordinator.BackupRunState? Get(Guid runId);
}

/// <summary>The real backup run, through <see cref="BackupRunCoordinator"/>.</summary>
public sealed class ManualBackupRuns(BackupRunCoordinator coordinator) : IUpgradeBackups
{
    public bool IsRunning => coordinator.IsRunning;

    public BackupRunCoordinator.BackupRunState? TryStart() => coordinator.TryStartManual();

    public BackupRunCoordinator.BackupRunState? Get(Guid runId) => coordinator.Get(runId);
}

/// <summary>
/// One upgrade at a time, from the click to the host's word (ADR-0110): a fresh verified backup,
/// held with an upgrade pin, then a request in the mailbox, then whatever the host writes back.
/// </summary>
/// <remarks>
/// <para><b>What survives the upgrade.</b> The upgrade recreates this very container, so the
/// in-memory attempt is gone exactly when it matters most. The screen therefore reads the host's
/// status names from the mailbox (a volume that outlives the container) and the pins from the
/// database, and the in-memory attempt only adds what the host cannot know: the backup phase and
/// which backup the pin holds. A process that restarted cannot name the pin of an attempt it never
/// saw, so it keeps it: the next success's trim bounds it, and holding one backup too long is the
/// safe direction.</para>
/// <para><b>The pin.</b> Released at once when the attempt ended before anything changed (the host's
/// exits 10, 11 and 13, or a refusal), kept when it ended after the first change or unknown, and
/// trimmed to the newest two when an upgrade succeeds.</para>
/// </remarks>
public sealed class UpgradeCoordinator
{
    /// <summary>
    /// How long a request may sit in the mailbox before the attempt is called unanswered: the host's
    /// 15-minute expiry and two ticks. It refuses an older request when it does look.
    /// </summary>
    public static readonly TimeSpan HostPickupWindow = TimeSpan.FromMinutes(17);

    /// <summary>How long the screen shows the host's last word after the attempt is gone from memory.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly HashSet<string> _trimmed = new(StringComparer.Ordinal);
    private readonly IUpgradeBackups _backups;
    private readonly UpgradeMailbox _mailbox;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgradeCoordinator> _logger;
    private UpgradeAttempt? _attempt;

    public UpgradeCoordinator(
        IUpgradeBackups backups, UpgradeMailbox mailbox, IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime, TimeProvider clock, ILogger<UpgradeCoordinator> logger)
    {
        _backups = backups;
        _mailbox = mailbox;
        _scopes = scopes;
        _lifetime = lifetime;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Phases: backing-up, requested (waiting for the host), started, done, failed.</summary>
    public sealed record UpgradeAttempt(
        string From, string To, string Phase, DateTime StartedAtUtc, DateTime? RequestedAtUtc,
        string? BackupId, string? RequestId, string? Message, bool Changed);

    public enum StartOutcome { Started, UpgradeBusy, BackupBusy }

    private static bool IsActive(UpgradeAttempt a) => a.Phase is "backing-up" or "requested" or "started";

    /// <summary>
    /// Whether an upgrade could start right now, without starting one: the same two answers
    /// <see cref="TryStart"/> gives, for the check that runs before the passkey is asked for.
    /// </summary>
    public StartOutcome? Blocker()
    {
        lock (_lock)
        {
            if (HoldsMailbox()) return StartOutcome.UpgradeBusy;
            return _backups.IsRunning ? StartOutcome.BackupBusy : null;
        }
    }

    /// <summary>An attempt is under way, or a request is waiting for the host and has not expired.</summary>
    private bool HoldsMailbox()
    {
        if (_attempt is { } current && IsActive(current)) return true;
        // A request still in the mailbox is one the host has not answered, ours or from before a
        // restart. One older than the host's expiry is dead: the host removes it the next time it
        // looks, and until then it must not turn every retry into "already in progress".
        var now = _clock.GetUtcNow();
        return _mailbox.Read().Requests.Any(r => now - r.At <= UpgradeMailbox.RequestExpiry);
    }

    /// <summary>Begin an attempt for <paramref name="to"/>, or say why not. The backup runs off the request.</summary>
    public StartOutcome TryStart(string from, string to, Guid? actor)
    {
        lock (_lock)
        {
            if (HoldsMailbox()) return StartOutcome.UpgradeBusy;
            var run = _backups.TryStart();
            if (run is null) return StartOutcome.BackupBusy;

            var attempt = new UpgradeAttempt(from, to, "backing-up", _clock.GetUtcNow().UtcDateTime,
                null, null, null, null, Changed: false);
            _attempt = attempt;
            _ = Task.Run(() => RunAsync(attempt, run.RunId, actor, _lifetime.ApplicationStopping));
            return StartOutcome.Started;
        }
    }

    private async Task RunAsync(UpgradeAttempt started, Guid runId, Guid? actor, CancellationToken stopping)
    {
        string? pinned = null;
        try
        {
            BackupRunCoordinator.BackupRunState? run;
            while ((run = _backups.Get(runId)) is { State: "running" })
                await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, stopping).ConfigureAwait(false);

            if (run is not { State: "succeeded", BackupId: { } backupId })
            {
                Fail(started, run?.Error ?? "The backup did not finish, so the upgrade was not started.", changed: false);
                return;
            }

            using var scope = _scopes.CreateScope();
            var label = $"before upgrade {started.From} to {started.To}";
            await scope.ServiceProvider.GetRequiredService<BackupPinsRepository>()
                .PinForUpgradeAsync(backupId, label, actor, stopping).ConfigureAwait(false);
            pinned = backupId;

            var requestId = _mailbox.WriteRequest(started.To);
            lock (_lock)
            {
                _attempt = started with
                {
                    Phase = "requested", BackupId = backupId, RequestId = requestId,
                    RequestedAtUtc = _clock.GetUtcNow().UtcDateTime,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the upgrade to {Version} failed.", started.To);
            if (pinned is not null) await ReleasePinAsync(pinned).ConfigureAwait(false);
            Fail(started, "The upgrade could not be handed to the host. Check the server logs.", changed: false);
        }
    }

    private void Fail(UpgradeAttempt started, string message, bool changed)
    {
        lock (_lock)
        {
            _attempt = started with { Phase = "failed", Message = message, Changed = changed };
        }
    }

    /// <summary>The attempt as the screen shows it now, with the host's status folded in. Null when there is none to show.</summary>
    public async Task<UpgradeAttempt?> ObserveAsync(CancellationToken ct = default)
    {
        var view = _mailbox.Read();
        var now = _clock.GetUtcNow();
        UpgradeAttempt? attempt;
        lock (_lock) attempt = _attempt;

        if (attempt is null)
        {
            // Nothing in memory: a restart, which an upgrade causes. The host's last word on the NEWEST
            // request is the answer. Not the newest settled word of any request: while the new run
            // has only said "started", an older request's result must not stand in for it.
            var recent = view.Statuses.Where(s => now - s.At < RecentWindow).ToList();
            var newest = recent.OrderByDescending(s => s.At).FirstOrDefault();
            if (newest is null) return null;
            var ofNewest = recent.Where(s => s.Id == newest.Id).ToList();
            var last = Settled(ofNewest);
            if (last is null)
            {
                // Started and not yet settled: still under way, so the screen keeps its progress card
                // and keeps asking. The host settles every run, a dead one included.
                return new UpgradeAttempt(newest.From, newest.To, "started", newest.At.UtcDateTime, null, null,
                    newest.Id, null, Changed: false);
            }
            var derived = Classify(last);
            if (derived.Phase == "done") await TrimOnceAsync(last.Id, ct).ConfigureAwait(false);
            return new UpgradeAttempt(last.From, last.To, derived.Phase, last.At.UtcDateTime, null, null, last.Id,
                derived.Message, derived.Changed);
        }

        if (attempt.RequestId is null || !IsActive(attempt)) return attempt;

        var mine = view.Statuses.Where(s => s.Id == attempt.RequestId).ToList();
        if (Settled(mine) is { } settled)
        {
            var c = Classify(settled);
            if (c.Phase == "done") await TrimOnceAsync(settled.Id, ct).ConfigureAwait(false);
            else if (!c.Changed && attempt.BackupId is not null) await ReleasePinAsync(attempt.BackupId).ConfigureAwait(false);
            return Update(attempt, a => a with { Phase = c.Phase, Message = c.Message, Changed = c.Changed });
        }
        if (mine.Count > 0) return Update(attempt, a => a with { Phase = "started" });

        if (attempt.RequestedAtUtc is { } at && now.UtcDateTime - at > HostPickupWindow)
        {
            if (attempt.BackupId is not null) await ReleasePinAsync(attempt.BackupId).ConfigureAwait(false);
            return Update(attempt, a => a with
            {
                Phase = "failed", Changed = false,
                Message = "The host did not pick the request up. Check that the host updater is running; nothing was changed.",
            });
        }
        return attempt;
    }

    /// <summary>The host's last word on a request: a settled status if there is one, else null.</summary>
    private static MailboxStatus? Settled(IEnumerable<MailboxStatus> statuses) =>
        statuses.Where(s => s.State != "started").OrderByDescending(s => s.At).FirstOrDefault();

    private UpgradeAttempt Update(UpgradeAttempt seen, Func<UpgradeAttempt, UpgradeAttempt> change)
    {
        lock (_lock)
        {
            // Another poll may have settled it already; only replace the attempt this poll saw.
            if (!ReferenceEquals(_attempt, seen)) return _attempt ?? change(seen);
            _attempt = change(seen);
            return _attempt;
        }
    }

    /// <summary>What a status name means for the screen and for the pin.</summary>
    internal static (string Phase, string Message, bool Changed) Classify(MailboxStatus status)
    {
        var state = status.State;
        if (state == "done") return ("done", $"Coffer is now on {status.To}.", true);
        if (state.StartsWith("exit", StringComparison.Ordinal)
            && int.TryParse(state.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return code switch
            {
                10 => ("failed", "A check before any change failed, so nothing was changed.", false),
                11 => ("failed", "The upgrade stopped before it changed anything.", false),
                13 => ("failed", "The upgrade script on the host is too old for this. Nothing was changed; run the manual command once.", false),
                12 => ("failed", "The upgrade failed after it began changing this install, and it needs attention. Run the manual command on the host.", true),
                14 => ("failed", "The new version did not become ready in time. It may still be starting; check the host.", true),
                _ => ("failed", $"The upgrade ended with status {code}. Treat the install as changed and check the host.", true),
            };
        }
        // A refusal tag: the host did not start anything.
        return ("failed", state switch
        {
            "expired" => "The host did not act on the request within 15 minutes. It may be blocked after an earlier failure (run ack on the host), disabled, busy, or unable to read the release with its own token. Nothing was changed.",
            "notlatest" => "The host found a different latest release than the app did. Check again. Nothing was changed.",
            "notnewer" => "The host found nothing newer than what is installed. Nothing was changed.",
            "unknowninst" => "The host cannot tell which version is installed (COFFER_IMAGE_TAG is not a version), so it did not act. Nothing was changed.",
            _ => $"The host refused the request ({state}). Nothing was changed.",
        }, false);
    }

    private async Task TrimOnceAsync(string requestId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_trimmed.Add(requestId)) return;
        }
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BackupPinsRepository>()
                .TrimUpgradePinsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock) _trimmed.Remove(requestId);   // try again on the next poll
            _logger.LogWarning(ex, "Trimming the upgrade pins failed.");
        }
    }

    private async Task ReleasePinAsync(string backupId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BackupPinsRepository>()
                .ReleaseUpgradePinAsync(backupId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Releasing the upgrade pin failed.");
        }
    }
}
