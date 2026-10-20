using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Snapshots;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// The generic scheduler core (mig 136): the runner dispatches each due
/// <c>scheduled_jobs</c> row to the handler for its job_type and advances
/// <c>next_run_at</c>; a job_type with no handler is skipped but still advanced.
/// Plus a concrete check that the snapshot handler creates an auto snapshot.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SchedulerRunnerTests
{
    private readonly PostgresFixture _fixture;

    public SchedulerRunnerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Dispatches_due_jobs_by_type_and_advances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5));
        await SeedJobAsync(ledger, JobTypes.Snapshot, DateTime.UtcNow.AddMinutes(-5));

        var quote = new SpyHandler(JobTypes.QuoteRefresh);
        var snapshot = new SpyHandler(JobTypes.Snapshot);
        var handlers = new Dictionary<string, IScheduledJobHandler>(StringComparer.Ordinal)
        {
            [quote.JobType] = quote,
            [snapshot.JobType] = snapshot,
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        var count = await new SchedulerRunner()
            .RunDueAsync(db, handlers, now, NullLogger.Instance, default);

        Assert.Equal(2, count);
        Assert.Equal((ledger.LedgerId, ledger.UserId), Assert.Single(quote.Calls));
        Assert.Equal((ledger.LedgerId, ledger.UserId), Assert.Single(snapshot.Calls));

        await using var read = _fixture.NewDbContext();
        var jobs = await read.ScheduledJobs.AsNoTracking()
            .Where(j => j.LedgerId == ledger.LedgerId).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.True(j.NextRunAt > now));
        Assert.All(jobs, j => Assert.NotNull(j.LastRunAt));
    }

    [Fact]
    public async Task Job_with_no_handler_is_skipped_but_advanced()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5));

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        // No handlers registered → the due row is skipped, not run, but advanced.
        var count = await new SchedulerRunner().RunDueAsync(
            db, new Dictionary<string, IScheduledJobHandler>(), now, NullLogger.Instance, default);

        Assert.Equal(1, count);
        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.NextRunAt > now);
    }

    [Fact]
    public async Task Snapshot_handler_creates_an_auto_snapshot()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();

        await new SnapshotJobHandler(NullLoggerFactory.Instance, TimeProvider.System)
            .RunAsync(db, ledger.LedgerId, ledger.UserId, new JobRunClock(DateTime.UtcNow, "UTC"), default);

        await using var read = _fixture.NewDbContext();
        var autos = await read.LedgerSnapshots.AsNoTracking()
            .Where(s => s.LedgerId == ledger.LedgerId && s.Kind == "auto")
            .ToListAsync();
        Assert.Single(autos);
    }

    [Fact]
    public async Task Dispatches_due_global_jobs_and_advances()
    {
        await ResetGlobalRowAsync();
        await SeedGlobalJobAsync(GlobalJobTypes.Backup, DateTime.UtcNow.AddMinutes(-5));

        var backup = new SpyGlobalHandler(GlobalJobTypes.Backup);
        var handlers = new Dictionary<string, IGlobalScheduledJobHandler>(StringComparer.Ordinal)
        {
            [backup.JobType] = backup,
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        var count = await new SchedulerRunner()
            .RunDueGlobalAsync(db, handlers, now, NullLogger.Instance, default);

        Assert.Equal(1, count);
        Assert.Equal(1, backup.Calls);

        await using var read = _fixture.NewDbContext();
        var job = await read.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.True(job.NextRunAt > now);
        Assert.NotNull(job.LastRunAt);
    }

    [Fact]
    public async Task Global_job_with_no_handler_is_skipped_but_advanced()
    {
        await ResetGlobalRowAsync();
        await SeedGlobalJobAsync(GlobalJobTypes.Backup, DateTime.UtcNow.AddMinutes(-5));

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        var count = await new SchedulerRunner().RunDueGlobalAsync(
            db, new Dictionary<string, IGlobalScheduledJobHandler>(), now, NullLogger.Instance, default);

        Assert.Equal(1, count);
        await using var read = _fixture.NewDbContext();
        var job = await read.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.True(job.NextRunAt > now);
    }

    [Fact]
    public async Task Failing_job_advances_and_records_the_failure()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5));
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new ThrowingHandler(JobTypes.QuoteRefresh, "quotes exploded"),
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(db, handlers, now, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.NextRunAt > now);              // failure does not mean "retry next tick"
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Contains("quotes exploded", job.LastError);
        Assert.NotNull(job.LastFailureAt);
        Assert.True(job.Enabled);                      // one failure is not enough to disable
    }

    /// <summary>
    /// The mig-194 regression test. A handler that leaves the connection unusable
    /// used to cost us the advance — the runner applied next_run_at in memory and
    /// persisted it after the loop, over the context the handler had just wrecked,
    /// so the row stayed due and re-fired every tick. The advance is now committed
    /// before dispatch, so it survives even though the post-run bookkeeping cannot
    /// be written.
    /// </summary>
    [Fact]
    public async Task Job_that_poisons_the_connection_still_advances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5));
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new ConnectionPoisoningHandler(JobTypes.QuoteRefresh),
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        // Must not throw: the runner absorbs the unwritable outcome and gives up
        // on the tick rather than propagating.
        await new SchedulerRunner().RunDueAsync(db, handlers, now, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.NextRunAt > now);
        // Without a fresh connection to write it on, the failure bookkeeping is lost —
        // the next test is the same run given one, as the scheduler service now does.
    }

    [Fact]
    public async Task A_job_that_kills_its_own_connection_still_has_its_failure_recorded()
    {
        // FU-070. The outcome was saved on the context the handler had just wrecked, so
        // last_error and consecutive_failures stayed unwritten and the schedule showed a
        // neutral "last attempted" for exactly the job whose failure mattered.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5));
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new ConnectionPoisoningHandler(JobTypes.QuoteRefresh),
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, now, NullLogger.Instance, default, publisher: null,
            freshContext: _fixture.NewDbContext);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.NextRunAt > now);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Contains("handler died", job.LastError);
        Assert.NotNull(job.LastFailureAt);
        Assert.Null(job.LastSuccessAt);
    }

    // --- last_success_at (FU-070, FU-071) ------------------------------------

    private sealed class OutcomeHandler(string jobType, JobRunOutcome outcome) : IScheduledJobHandler
    {
        public string JobType => jobType;
        public Task<JobRunOutcome> RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken ct)
            => Task.FromResult(outcome);
    }

    private async Task<ScheduledJobRow> RunOnceAsync(JobRunOutcome? outcome, string? throwing = null)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.Snapshot, DateTime.UtcNow.AddMinutes(-5));
        IScheduledJobHandler handler = throwing is not null
            ? new ThrowingHandler(JobTypes.Snapshot, throwing)
            : new OutcomeHandler(JobTypes.Snapshot, outcome!.Value);
        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, new Dictionary<string, IScheduledJobHandler> { [JobTypes.Snapshot] = handler },
            DateTime.UtcNow, NullLogger.Instance, default);
        await using var read = _fixture.NewDbContext();
        return await read.ScheduledJobs.AsNoTracking().SingleAsync(j => j.LedgerId == ledger.LedgerId);
    }

    [Fact]
    public async Task A_run_that_did_its_work_records_its_success()
    {
        var job = await RunOnceAsync(JobRunOutcome.Ok());

        Assert.NotNull(job.LastSuccessAt);
        Assert.Equal(job.LastRunAt, job.LastSuccessAt);
        Assert.Null(ScheduleHealthRules.NothingDoneSince(
            job.LastError, job.ConsecutiveFailures, job.LastRunAt, job.LastSuccessAt, job.CreatedAt));
    }

    [Fact]
    public async Task A_run_that_did_part_of_its_work_counts_as_a_success_with_its_warning()
    {
        var job = await RunOnceAsync(JobRunOutcome.Degraded("one of two banks did not sync"));

        Assert.Equal(job.LastRunAt, job.LastSuccessAt);
        Assert.Equal(0, job.ConsecutiveFailures);
        Assert.Contains("one of two banks", job.LastError);
        Assert.Null(ScheduleHealthRules.NothingDoneSince(
            job.LastError, job.ConsecutiveFailures, job.LastRunAt, job.LastSuccessAt, job.CreatedAt));
    }

    [Fact]
    public async Task A_run_that_did_nothing_does_not_move_the_last_success_and_reads_as_doing_nothing()
    {
        var job = await RunOnceAsync(JobRunOutcome.DidNothing("Automatic snapshot skipped: all five slots hold manual snapshots."));

        Assert.Null(job.LastSuccessAt);
        Assert.Equal(0, job.ConsecutiveFailures);   // still not a failure: no auto-disable
        // Never succeeded, so "nothing since" is when the schedule was set up.
        Assert.Equal(job.CreatedAt, ScheduleHealthRules.NothingDoneSince(
            job.LastError, job.ConsecutiveFailures, job.LastRunAt, job.LastSuccessAt, job.CreatedAt));
    }

    [Fact]
    public async Task A_failed_run_does_not_record_a_success()
    {
        var job = await RunOnceAsync(outcome: null, throwing: "boom");

        Assert.Null(job.LastSuccessAt);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Null(ScheduleHealthRules.NothingDoneSince(   // a failure, shown as one
            job.LastError, job.ConsecutiveFailures, job.LastRunAt, job.LastSuccessAt, job.CreatedAt));
    }

    [Fact]
    public async Task Failure_at_the_threshold_disables_the_job()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5),
            consecutiveFailures: SchedulerRunner.DisableAfterConsecutiveFailures - 1);
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new ThrowingHandler(JobTypes.QuoteRefresh, "again"),
        };

        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.Equal(SchedulerRunner.DisableAfterConsecutiveFailures, job.ConsecutiveFailures);
        Assert.False(job.Enabled);

        // WHO switched it off. Without this, the row is indistinguishable from one an
        // operator turned off, which is the whole of mig 216.
        Assert.Equal(ScheduleDisableReasons.ConsecutiveFailures, job.DisabledReason);
    }

    /// <summary>
    /// A failure BELOW the threshold must not claim the job was disabled.
    /// </summary>
    /// <remarks>
    /// The paired negative for the assertion above. A reason stamped on every failure
    /// would satisfy that test and label a job that is still running as switched off.
    /// </remarks>
    [Fact]
    public async Task A_failure_below_the_threshold_leaves_the_job_enabled_and_unlabelled()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5),
            consecutiveFailures: SchedulerRunner.DisableAfterConsecutiveFailures - 2);
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new ThrowingHandler(JobTypes.QuoteRefresh, "not yet"),
        };

        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.Enabled);
        Assert.Null(job.DisabledReason);
    }

    [Fact]
    public async Task Success_clears_earlier_failures()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5),
            consecutiveFailures: 3);
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new SpyHandler(JobTypes.QuoteRefresh),
        };

        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.Equal(0, job.ConsecutiveFailures);
        Assert.Null(job.LastError);
        Assert.True(job.Enabled);
        Assert.Null(job.DisabledReason);
    }

    [Fact]
    public async Task A_degraded_run_records_the_reason_without_counting_as_a_failure()
    {
        // The middle state, and the reason it exists. A job that RAN but did not do its
        // work is neither a failure nor a success: counting it as a failure would
        // auto-disable a daily refresh after five flaky days (losing more than it
        // protects), and counting it as a success is what let a total provider outage
        // look healthy in the first place.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5),
            consecutiveFailures: 3);
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new SpyHandler(JobTypes.QuoteRefresh)
            {
                Outcome = JobRunOutcome.Degraded("No quote provider succeeded: yahoo timed out"),
            },
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, now, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);

        // It ran: the streak resets and the job stays enabled...
        Assert.Equal(0, job.ConsecutiveFailures);
        Assert.True(job.Enabled);
        Assert.True(job.NextRunAt > now);

        // ...but the reason survives, because "it ran" is not the same as "it worked"
        // and the difference has to be visible somewhere other than a container log.
        Assert.Contains("yahoo timed out", job.LastError);
        Assert.NotNull(job.LastFailureAt);
    }

    [Fact]
    public async Task A_degraded_run_never_reaches_the_auto_disable_threshold()
    {
        // Directly pins the rule the auto-disable would otherwise break: a ledger holding
        // one delisted ticker degrades every single day, and must never lose the job for
        // it. Seeded one short of the threshold so a single miscount would trip it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedJobAsync(ledger, JobTypes.QuoteRefresh, DateTime.UtcNow.AddMinutes(-5),
            consecutiveFailures: SchedulerRunner.DisableAfterConsecutiveFailures - 1);
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [JobTypes.QuoteRefresh] = new SpyHandler(JobTypes.QuoteRefresh)
            {
                Outcome = JobRunOutcome.Degraded("a provider failed"),
            },
        };

        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.Enabled);
        Assert.Equal(0, job.ConsecutiveFailures);
    }

    [Fact]
    public async Task Failing_global_job_advances_and_records_the_failure()
    {
        await ResetGlobalRowAsync();
        await SeedGlobalJobAsync(GlobalJobTypes.Backup, DateTime.UtcNow.AddMinutes(-5));
        var handlers = new Dictionary<string, IGlobalScheduledJobHandler>
        {
            [GlobalJobTypes.Backup] = new ThrowingGlobalHandler(GlobalJobTypes.Backup, "pg_dump died"),
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueGlobalAsync(db, handlers, now, NullLogger.Instance, default);

        await using var read = _fixture.NewDbContext();
        var job = await read.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.True(job.NextRunAt > now);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Contains("pg_dump died", job.LastError);
    }

    [Fact]
    public async Task A_global_job_cut_short_by_a_stop_is_recorded_as_interrupted()
    {
        // FU-065: the run was claimed (last_run_at advanced) and then the process stopped
        // under it. The cancellation was rethrown with nothing recorded, so the row showed a
        // run that started and never an outcome; only the 48-hour age alert ever noticed.
        await ResetGlobalRowAsync();
        await SeedGlobalJobAsync(GlobalJobTypes.Backup, DateTime.UtcNow.AddMinutes(-5));
        using var stopping = new CancellationTokenSource();
        var handlers = new Dictionary<string, IGlobalScheduledJobHandler>
        {
            [GlobalJobTypes.Backup] = new CancelledByStopHandler(stopping),
        };

        var now = DateTime.UtcNow;
        await using var db = _fixture.NewDbContext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SchedulerRunner().RunDueGlobalAsync(db, handlers, now, NullLogger.Instance, stopping.Token));

        await using var read = _fixture.NewDbContext();
        var job = await read.GlobalScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.JobType == GlobalJobTypes.Backup);
        Assert.Equal(1, job.ConsecutiveFailures);
        Assert.Contains("Interrupted", job.LastError);
        Assert.NotNull(job.LastFailureAt);
        Assert.True(job.NextRunAt > now);   // still advanced: tomorrow's run is not lost
    }

    private sealed class CancelledByStopHandler(CancellationTokenSource stopping) : IGlobalScheduledJobHandler
    {
        public string JobType => GlobalJobTypes.Backup;

        public Task RunAsync(AppDbContext db, CancellationToken ct)
        {
            stopping.Cancel();                       // the process is stopping mid-run
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private async Task ResetGlobalRowAsync()
    {
        await using var db = _fixture.NewDbContext();   // service role
        await db.Database.ExecuteSqlRawAsync("DELETE FROM global_scheduled_jobs;");
    }

    private async Task SeedGlobalJobAsync(string jobType, DateTime nextRunAt)
    {
        await using var db = _fixture.NewDbContext();   // service-role only table
        db.GlobalScheduledJobs.Add(new GlobalScheduledJobRow
        {
            JobType = jobType,
            Enabled = true,
            HourLocal = 3,
            MinuteLocal = 0,
            NextRunAt = DateTime.SpecifyKind(nextRunAt, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedJobAsync(
        SyntheticLedger ledger, string jobType, DateTime nextRunAt, int consecutiveFailures = 0)
    {
        await using var db = ledger.NewDbContext();
        db.ScheduledJobs.Add(new ScheduledJobRow
        {
            LedgerId = ledger.LedgerId,
            JobType = jobType,
            Enabled = true,
            HourLocal = 19,
            MinuteLocal = 0,
            ConfiguredByUserId = ledger.UserId,
            NextRunAt = DateTime.SpecifyKind(nextRunAt, DateTimeKind.Utc),
            ConsecutiveFailures = consecutiveFailures,
        });
        await db.SaveChangesAsync();
    }

    private sealed class SpyHandler : IScheduledJobHandler
    {
        public SpyHandler(string jobType) => JobType = jobType;
        public string JobType { get; }
        public List<(Guid Ledger, Guid User)> Calls { get; } = new();

        /// <summary>What this spy reports; Ok unless a test says otherwise.</summary>
        public JobRunOutcome Outcome { get; init; } = JobRunOutcome.Ok();

        public Task<JobRunOutcome> RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken ct)
        {
            Calls.Add((ledgerId, configuredByUserId));
            return Task.FromResult(Outcome);
        }
    }

    private sealed class SpyGlobalHandler : IGlobalScheduledJobHandler
    {
        public SpyGlobalHandler(string jobType) => JobType = jobType;
        public string JobType { get; }
        public int Calls { get; private set; }

        public Task RunAsync(AppDbContext db, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IScheduledJobHandler
    {
        private readonly string _message;
        public ThrowingHandler(string jobType, string message)
            => (JobType, _message) = (jobType, message);
        public string JobType { get; }

        public Task<JobRunOutcome> RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken ct)
            => throw new InvalidOperationException(_message);
    }

    private sealed class ThrowingGlobalHandler : IGlobalScheduledJobHandler
    {
        private readonly string _message;
        public ThrowingGlobalHandler(string jobType, string message)
            => (JobType, _message) = (jobType, message);
        public string JobType { get; }

        public Task RunAsync(AppDbContext db, CancellationToken ct)
            => throw new InvalidOperationException(_message);
    }

    /// <summary>
    /// Stands in for the real failure: a handler that leaves the context unable to
    /// write. It opens a transaction, fails a statement inside it (so Postgres marks
    /// the transaction aborted), and leaves it open — every later command on that
    /// connection then errors, exactly as they did against a database in crash
    /// recovery. Any subsequent SaveChanges must fail for this test to mean anything.
    /// </summary>
    private sealed class ConnectionPoisoningHandler : IScheduledJobHandler
    {
        public ConnectionPoisoningHandler(string jobType) => JobType = jobType;
        public string JobType { get; }

        public async Task<JobRunOutcome> RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken ct)
        {
            await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1/0", ct);
            }
            catch
            {
                // Intentionally swallowed: the aborted transaction is the point.
            }
            throw new InvalidOperationException("handler died with the connection aborted");
        }
    }
}
