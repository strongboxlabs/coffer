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

    private async Task SeedJobAsync(SyntheticLedger ledger, string jobType, DateTime nextRunAt)
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
}
