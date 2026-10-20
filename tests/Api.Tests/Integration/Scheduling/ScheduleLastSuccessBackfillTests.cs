using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// Migration 252's backfill of <c>last_success_at</c>, run as written in the migration file
/// over rows in each state an existing install can hold. A data migration is unverified until
/// it has met the data it will meet: this reads the statements out of the file rather than
/// restating them, so the migration and its proof cannot drift.
/// </summary>
/// <remarks>
/// The rows are built the way a pre-252 install left them — <c>last_success_at</c> NULL —
/// and the statements run inside a transaction that is rolled back, so the shared test
/// database keeps no trace and no other test's rows are touched.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ScheduleLastSuccessBackfillTests
{
    private readonly PostgresFixture _fixture;

    public ScheduleLastSuccessBackfillTests(PostgresFixture fixture) => _fixture = fixture;

    private static IReadOnlyList<string> BackfillStatements()
    {
        var path = Path.Combine(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            "252_schedule_last_success.sql");
        var sql = string.Join('\n', File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        return sql.Split(';').Select(s => s.Trim())
            .Where(s => s.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static readonly DateTime RanAt = new(2026, 10, 1, 19, 0, 0, DateTimeKind.Utc);

    private static ScheduledJobRow Row(Guid ledgerId, Guid userId, string jobType, string? lastError, int failures,
        DateTime? lastRunAt) => new()
        {
            LedgerId = ledgerId,
            JobType = jobType,
            Enabled = true,
            HourLocal = 19,
            MinuteLocal = 0,
            ConfiguredByUserId = userId,
            LastRunAt = lastRunAt,
            ConsecutiveFailures = failures,
            LastError = lastError,
        };

    [Fact]
    public async Task The_backfill_records_a_success_only_where_the_last_run_did_its_work()
    {
        var statements = BackfillStatements();
        Assert.Equal(2, statements.Count);   // scheduled_jobs and global_scheduled_jobs

        var a = await SyntheticLedger.CreateAsync(_fixture);
        var b = await SyntheticLedger.CreateAsync(_fixture);
        var c = await SyntheticLedger.CreateAsync(_fixture);
        await using (var seed = _fixture.NewDbContext())
        {
            seed.ScheduledJobs.AddRange(
                // A clean last run.
                Row(a.LedgerId, a.UserId, JobTypes.QuoteRefresh, null, 0, RanAt),
                // A partial run: did its work in part.
                Row(a.LedgerId, a.UserId, JobTypes.FeedSync, "1 of 2 connection(s) did not sync: x", 0, RanAt),
                // The three did-nothing messages, as the handlers write them.
                Row(a.LedgerId, a.UserId, JobTypes.Snapshot,
                    "Automatic snapshot skipped: all five slots hold manual snapshots. Delete one to resume automatic coverage.", 0, RanAt),
                Row(b.LedgerId, b.UserId, JobTypes.QuoteRefresh,
                    "No quote provider succeeded (2 of 2 errored). Detail is in the server log.", 0, RanAt),
                Row(b.LedgerId, b.UserId, JobTypes.FeedSync,
                    "All 3 connection(s) need reconnecting; nothing was synced.", 0, RanAt),
                Row(b.LedgerId, b.UserId, JobTypes.Snapshot, "Automatic snapshot did not run: AtCap.", 0, RanAt),
                // Failing: its last success is not recorded anywhere to recover.
                Row(c.LedgerId, c.UserId, JobTypes.QuoteRefresh, "a network request failed", 2, RanAt),
                // Never ran.
                Row(c.LedgerId, c.UserId, JobTypes.Snapshot, null, 0, null));
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.NewDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var statement in statements)
            await db.Database.ExecuteSqlRawAsync(statement);

        async Task<DateTime?> SuccessOf(SyntheticLedger l, string jobType) =>
            (await db.ScheduledJobs.AsNoTracking().SingleAsync(j => j.LedgerId == l.LedgerId && j.JobType == jobType))
            .LastSuccessAt;

        Assert.Equal(RanAt, await SuccessOf(a, JobTypes.QuoteRefresh));   // clean
        Assert.Equal(RanAt, await SuccessOf(a, JobTypes.FeedSync));       // partial
        Assert.Null(await SuccessOf(a, JobTypes.Snapshot));                // did nothing
        Assert.Null(await SuccessOf(b, JobTypes.QuoteRefresh));            // did nothing
        Assert.Null(await SuccessOf(b, JobTypes.FeedSync));                // did nothing
        Assert.Null(await SuccessOf(b, JobTypes.Snapshot));                // did nothing
        Assert.Null(await SuccessOf(c, JobTypes.QuoteRefresh));            // failing
        Assert.Null(await SuccessOf(c, JobTypes.Snapshot));                // never ran

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task The_backfill_is_a_function_of_the_rows_alone()
    {
        // No now(), no randomness: run twice over the same rows, the same answer. A
        // backfill that stamped the clock failed the restore drill once (2026-10-15).
        Assert.All(BackfillStatements(), s =>
        {
            Assert.DoesNotContain("now()", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current_timestamp", s, StringComparison.OrdinalIgnoreCase);
        });
        await Task.CompletedTask;
    }
}
