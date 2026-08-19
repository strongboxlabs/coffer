using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

        await new SnapshotJobHandler(NullLoggerFactory.Instance)
            .RunAsync(db, ledger.LedgerId, ledger.UserId, default);

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
        // The failure bookkeeping is the part that is legitimately lost here — the
        // connection was gone. The advance is what matters, and it held.
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

        public Task RunAsync(AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken ct)
        {
            Calls.Add((ledgerId, configuredByUserId));
            return Task.CompletedTask;
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

        public Task RunAsync(AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken ct)
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

        public async Task RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken ct)
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
