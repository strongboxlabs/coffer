using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// The gate that serialises backups and the run that outlives its request (FU-065, FU-067).
/// The coordinator resolves the manager from a scope, so these use a scope whose provider
/// blocks and then fails: what is under test is the gate, the recorded state and what is
/// reported, none of which needs a pg_dump.
/// </summary>
public sealed class BackupRunCoordinatorTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopping.Cancel();
    }

    /// <summary>A provider that holds the run open until released, then throws what it is
    /// told to. Stands in for "the manager was resolved and the backup then failed".</summary>
    private sealed class HeldProvider(Func<CancellationToken, Exception> onRelease, CancellationToken stopping)
        : IServiceProvider, IServiceScope, IServiceScopeFactory
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(BackupManager))
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
                throw onRelease(stopping);
            }
            // The publisher, asked for after an interruption: absent here, and the
            // announcement is best-effort, so its absence must not fail anything.
            return null;
        }

        public IServiceProvider ServiceProvider => this;
        public IServiceScope CreateScope() => this;
        public void Dispose() { }
    }

    private static (BackupRunCoordinator Coordinator, HeldProvider Held, FakeLifetime Lifetime) Build(
        Func<CancellationToken, Exception>? onRelease = null)
    {
        var lifetime = new FakeLifetime();
        var held = new HeldProvider(
            onRelease ?? (_ => new InvalidOperationException("pg_dump failed against db.internal:5432")),
            lifetime.ApplicationStopping);
        return (new BackupRunCoordinator(
            held, lifetime, TimeProvider.System, NullLogger<BackupRunCoordinator>.Instance), held, lifetime);
    }

    private static async Task<BackupRunCoordinator.BackupRunState> UntilFinishedAsync(
        BackupRunCoordinator c, Guid id)
    {
        for (var i = 0; i < 400; i++)
        {
            var run = c.Get(id)!;
            if (run.State != "running") return run;
            await Task.Delay(25);
        }
        throw new TimeoutException("the run never finished");
    }

    [Fact]
    public async Task A_manual_start_is_refused_while_a_backup_is_running()
    {
        var (c, _, _) = Build();
        await using var lease = await c.AcquireAsync(default);   // a scheduled run holds the gate

        Assert.True(c.IsRunning);
        Assert.Null(c.TryStartManual());
    }

    [Fact]
    public async Task A_manual_start_is_refused_while_another_manual_one_runs_and_accepted_after()
    {
        var (c, held, _) = Build();

        var first = c.TryStartManual();
        Assert.NotNull(first);
        held.Entered.Wait(TimeSpan.FromSeconds(5));
        Assert.Null(c.TryStartManual());                // the second would race the first's dump

        held.Release.Set();
        await UntilFinishedAsync(c, first!.RunId);
        Assert.False(c.IsRunning);
        // Rejected, not wedged: once the first run is over the gate is free again.
        var third = c.TryStartManual();
        Assert.NotNull(third);
        held.Release.Set();
        await UntilFinishedAsync(c, third!.RunId);
    }

    [Fact]
    public async Task A_scheduled_backup_waits_for_a_running_manual_one_instead_of_skipping()
    {
        var (c, held, _) = Build();
        var manual = c.TryStartManual()!;
        held.Entered.Wait(TimeSpan.FromSeconds(5));

        var scheduled = c.AcquireAsync(default);
        await Task.Delay(150);
        Assert.False(scheduled.IsCompleted);            // behind the manual run, not skipped

        held.Release.Set();
        await using var lease = await scheduled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("failed", (await UntilFinishedAsync(c, manual.RunId)).State);
    }

    [Fact]
    public async Task A_failed_run_is_recorded_without_the_servers_detail()
    {
        var (c, held, _) = Build();
        var run = c.TryStartManual()!;
        held.Release.Set();

        var done = await UntilFinishedAsync(c, run.RunId);

        Assert.Equal("failed", done.State);
        Assert.NotNull(done.Error);
        // The exception named an internal host and port; the browser is told the kind of
        // failure and that the log has the rest.
        Assert.DoesNotContain("db.internal", done.Error);
        Assert.DoesNotContain("5432", done.Error);
        Assert.Contains("server logs", done.Error);
    }

    [Fact]
    public async Task A_run_cut_short_by_a_stop_is_recorded_as_interrupted()
    {
        var (c, held, lifetime) = Build(stopping => new OperationCanceledException(stopping));
        var run = c.TryStartManual()!;
        held.Entered.Wait(TimeSpan.FromSeconds(5));

        lifetime.StopApplication();
        held.Release.Set();
        var done = await UntilFinishedAsync(c, run.RunId);

        Assert.Equal("failed", done.State);
        Assert.Contains("Interrupted", done.Error);
        Assert.False(c.IsRunning);                      // the gate was released on the way out
    }

    [Fact]
    public void A_run_the_process_does_not_know_is_null()
    {
        var (c, _, _) = Build();

        Assert.Null(c.Get(Guid.NewGuid()));
    }

    [Fact]
    public async Task Only_the_most_recent_runs_are_kept()
    {
        var (c, held, _) = Build();
        held.Release.Set();
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var run = c.TryStartManual();
            while (run is null) { await Task.Delay(10); run = c.TryStartManual(); }
            ids.Add(run.RunId);
            await UntilFinishedAsync(c, run.RunId);
        }

        Assert.Null(c.Get(ids[0]));                     // aged out
        Assert.NotNull(c.Get(ids[^1]));                 // the one just finished is there
    }
}
