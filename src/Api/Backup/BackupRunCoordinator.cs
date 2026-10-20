using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Coffer.Api.Notifications;

namespace Coffer.Api.Backup;

/// <summary>
/// The one place that decides whether a backup may run now, and runs a manual one off the
/// request that asked for it (FU-065, FU-067).
/// </summary>
/// <remarks>
/// <para>
/// <b>One at a time.</b> Nothing serialised a manual backup and the scheduled one, so two
/// could run together: two pg_dumps, two encryptions, a retention pass racing a write. Every
/// backup, whatever started it, goes through <see cref="BackupManager.CreateBackupAsync"/>,
/// which takes this gate. A scheduled run WAITS for it, because the manual one in front of
/// it ends in minutes and skipping would lose that day; a manual start while another runs is
/// refused, because a click that quietly queues behind a long job reads as a hang.
/// </para>
/// <para>
/// <b>Off the request.</b> A manual backup used to run inside its HTTP request, so it died
/// with it: the request is cancelled at a proxy's timeout (Cloudflare's is 100 seconds), and
/// a backup of a growing ledger crosses that. It is now accepted (202), runs on the host's
/// own lifetime, and the browser polls <see cref="Get"/> for the outcome, the way a restore
/// reports through a recorded outcome rather than through the connection that asked.
/// </para>
/// <para>
/// A run's state is kept in memory, so it does not outlive the process. That is by design
/// and not a gap: a run the process dies in is exactly the one nothing can report, which is
/// why an interrupted run publishes <c>backup.failed</c> on its way out (<see cref="RunAsync"/>)
/// and why the age monitor still watches for the backup that never arrived.
/// </para>
/// </remarks>
public sealed class BackupRunCoordinator
{
    /// <summary>How many finished runs stay readable. A browser asks about the one it
    /// started within seconds; this only has to outlast that.</summary>
    private const int KeepRuns = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, BackupRunState> _runs = new();
    private readonly ConcurrentQueue<Guid> _order = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackupRunCoordinator> _logger;

    public BackupRunCoordinator(
        IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime,
        TimeProvider clock,
        ILogger<BackupRunCoordinator> logger)
    {
        _scopes = scopes;
        _lifetime = lifetime;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>One backup run: <see cref="State"/> is "running", "succeeded" or "failed".</summary>
    public sealed record BackupRunState(
        Guid RunId, string State, DateTime StartedAtUtc, DateTime? FinishedAtUtc,
        string? BackupId, long? SizeBytes, string? Error);

    /// <summary>Wait for the gate. Scheduled backups take it here, via the manager.</summary>
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_gate);
    }

    /// <summary>True while a backup holds the gate.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// Start a manual backup that outlives the request, or return null when one is already
    /// running. The caller answers 202 with the returned run's id.
    /// </summary>
    public BackupRunState? TryStartManual()
    {
        if (!_gate.Wait(0)) return null;

        var run = new BackupRunState(
            Guid.NewGuid(), "running", _clock.GetUtcNow().UtcDateTime, null, null, null, null);
        Remember(run);

        // Deliberately not tied to the request's token: surviving it is the point. The
        // host's stopping token still ends it, and that is recorded as an interruption.
        _ = Task.Run(() => RunAsync(run, _lifetime.ApplicationStopping));
        return run;
    }

    /// <summary>The state of a run this process started, or null for an id it does not
    /// know (never issued, aged out of the last <see cref="KeepRuns"/>, or the process restarted).</summary>
    public BackupRunState? Get(Guid runId) => _runs.TryGetValue(runId, out var run) ? run : null;

    private async Task RunAsync(BackupRunState started, CancellationToken stopping)
    {
        BackupRunState finished;
        try
        {
            using var scope = _scopes.CreateScope();
            try
            {
                // Resolved inside the try: a container that cannot build the manager is a
                // failed run to report, not an unobserved exception on a background task.
                var manager = scope.ServiceProvider.GetRequiredService<BackupManager>();
                // The gate is already held by TryStartManual, so the un-gated core.
                var info = await manager.CreateBackupCoreAsync(stopping).ConfigureAwait(false);
                finished = started with
                {
                    State = "succeeded", FinishedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    BackupId = info.Id, SizeBytes = info.SizeBytes,
                };
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                finished = started with
                {
                    State = "failed", FinishedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    Error = "Interrupted: the server was stopping while the backup was running.",
                };
                await AnnounceInterruptionAsync(scope.ServiceProvider).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The detail is the server's to log, not the browser's to read (it can name
                // hosts, paths and database names); the browser gets the kind of failure.
                _logger.LogError(ex, "Manual backup failed.");
                finished = started with
                {
                    State = "failed", FinishedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    Error = "Backup failed: " + PublishedFailure.Describe(ex) + ". Check the server logs.",
                };
            }
        }
        finally
        {
            _gate.Release();
        }

        // Recorded AFTER the gate is released, never before. A browser that sees "finished"
        // and immediately starts another backup must find the gate free; the other order
        // refused it with "already running" for the instant between the two.
        Remember(finished);
    }

    /// <summary>A run cut short by a stop leaves no other record: say so to whoever is
    /// listening, best-effort and not tied to the token that just fired.</summary>
    private async Task AnnounceInterruptionAsync(IServiceProvider services)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
            await services.GetRequiredService<NotificationPublisher>().PublishAsync(
                new NotificationEvent(
                    Severity: NotificationSeverity.Critical,
                    Topic: NotificationTopics.Backup,
                    EventKey: "backup.failed",
                    Summary: "Backup FAILED: it was interrupted because the server stopped while it was running.",
                    Detail: null,
                    Monitor: NotificationMonitors.Backup,
                    Signal: MonitorSignal.Failure),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not announce an interrupted manual backup.");
        }
    }

    private void Remember(BackupRunState run)
    {
        if (_runs.TryAdd(run.RunId, run)) _order.Enqueue(run.RunId);
        else _runs[run.RunId] = run;
        while (_order.Count > KeepRuns && _order.TryDequeue(out var old)) _runs.TryRemove(old, out _);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
