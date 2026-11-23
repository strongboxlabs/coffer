using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The consistency check's <c>reminder_auto_post</c> projection: a ledger whose reminders
/// ask to be auto-posted and that has no auto-post job is reported, and its Repair turns
/// the job on (ADR-0097, 2026-11-18 amendment).
/// </summary>
/// <remarks>
/// <para>
/// Each ledger's reminders are saved through the reminders endpoint, which creates the job
/// for one set to auto-post. The flagged state is then made by removing the job's row: that
/// is the state a Moneydance import made before the import turned the job on leaves, and no
/// write the app has today can reach it otherwise.
/// </para>
/// <para>
/// A job row that is there and switched off is the user's choice, or the scheduler's after
/// repeated failures, and is never reported: a Repair that turned it back on would undo it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyReminderAutoPostTests : DatabaseTest
{
    public ConsistencyReminderAutoPostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record Setup(SyntheticLedger Ledger, HttpClient Client, Guid BankId, Guid CategoryId);

    private async Task<Setup> NewLedgerAsync(ApiFactory factory)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category a");
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new Setup(ledger, client, bank.Id, category.Id);
    }

    /// <summary>A monthly reminder saved through the endpoint; <paramref name="acdays"/> null
    /// is a manual one, a number one set to auto-post that many days before.</summary>
    private static async Task CreateReminderAsync(Setup s, string payee, int? acdays)
    {
        var made = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                AutoCommitDaysBefore = acdays,
                Payee = payee,
                SourceAccountId = s.BankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.CategoryId, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
    }

    private async Task<ScheduledJobRow?> AutoPostRowAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.ScheduledJobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost);
    }

    /// <summary>The ledger's auto-post job removed, as a ledger imported before the import
    /// turned it on was left: there was one to remove, so the state is the one under test.</summary>
    private async Task RemoveAutoPostRowAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        Assert.Equal(1, await db.ScheduledJobs
            .Where(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost)
            .ExecuteDeleteAsync());
    }

    private static LedgerConsistencyRepository RepoFor(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

    /// <summary>The whole check, and its reminder_auto_post entry.</summary>
    private async Task<(LedgerConsistencyReport Report, ProjectionConsistency AutoPost)> CheckAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledgerId);
        return (report, Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.ReminderAutoPost));
    }

    private static Task<HttpResponseMessage> RepairAsync(Setup s) =>
        s.Client.PostAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/balances/consistency/{ConsistencyProjections.ReminderAutoPost}/repair",
            content: null);

    private async Task<List<string>> RepairedSummariesAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && e.EventKey == "consistency.repaired")
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Summary)
            .ToListAsync();
    }

    [Fact]
    public async Task Reminders_set_to_auto_post_with_no_job_are_reported()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        await CreateReminderAsync(s, "Payee A", acdays: 2);
        await CreateReminderAsync(s, "Payee B", acdays: 0);
        await CreateReminderAsync(s, "Payee C", acdays: null);
        await RemoveAutoPostRowAsync(s.Ledger.LedgerId);

        var (report, autoPost) = await CheckAsync(s.Ledger.LedgerId);

        Assert.False(report.Healthy);
        Assert.False(autoPost.Healthy);
        Assert.Equal(1, autoPost.Checked);
        Assert.Equal(1, autoPost.MismatchedCount);
        var finding = Assert.Single(autoPost.Mismatches);
        Assert.Equal("Auto-post job", finding.Scope);
        // The two that ask, not the manual one.
        Assert.Equal("not set up, so the 2 reminders set to auto-post are never posted", finding.Field);
        Assert.Equal(0m, finding.Stored);
        Assert.Equal(0m, finding.Expected);
        // The only finding: the reminders' own writes left every other projection agreeing.
        Assert.All(report.Projections.Where(p => p.Projection != ConsistencyProjections.ReminderAutoPost),
            p => Assert.True(p.Healthy, p.Projection + " disagrees"));

        // The check writes nothing.
        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));

        // A person's check records the verdict, and its notice says what is wrong in the
        // words the Maintenance panel uses, not the transactions' count.
        var checkedByPerson = await client.PostAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/balances/consistency", content: null);
        Assert.Equal(HttpStatusCode.OK, checkedByPerson.StatusCode);
        await using (var read = Db.NewDbContext())
        {
            var drift = await read.LedgerEvents.AsNoTracking()
                .SingleAsync(e => e.LedgerId == s.Ledger.LedgerId && e.EventKey == "consistency.drift");
            Assert.Equal(
                "reminder_auto_post: the auto-post job is not set up, so reminders set to auto-post are never posted.",
                drift.Summary);
        }
        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));
    }

    [Fact]
    public async Task An_inactive_reminder_set_to_auto_post_still_asks_for_the_job()
    {
        // Reactivating a reminder turns no job on, so an inactive reminder that keeps its
        // Auto-post needs the job already, for when it is active again: the rule a reminder
        // save applies, whatever the reminder's state.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        var made = await client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                AutoCommitDaysBefore = 2,
                Payee = "Payee A",
                SourceAccountId = s.BankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.CategoryId, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var reminderId = (await made.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var inactive = await client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{reminderId}/active",
            new SetReminderActiveRequest { Active = false });
        Assert.True(inactive.IsSuccessStatusCode, "deactivating answered " + inactive.StatusCode);
        await RemoveAutoPostRowAsync(s.Ledger.LedgerId);

        var (_, autoPost) = await CheckAsync(s.Ledger.LedgerId);

        Assert.False(autoPost.Healthy);
        Assert.Equal("not set up, so the reminder set to auto-post is never posted",
            Assert.Single(autoPost.Mismatches).Field);
    }

    [Fact]
    public async Task An_enabled_job_is_not_reported()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        await CreateReminderAsync(s, "Payee A", acdays: 2);
        Assert.True((await AutoPostRowAsync(s.Ledger.LedgerId))!.Enabled);

        var (report, autoPost) = await CheckAsync(s.Ledger.LedgerId);

        Assert.True(autoPost.Healthy);
        Assert.Equal(1, autoPost.Checked);
        Assert.Empty(autoPost.Mismatches);
        Assert.True(report.Healthy);
    }

    [Fact]
    public async Task A_job_switched_off_is_the_users_choice_and_is_not_reported_or_repaired()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        await CreateReminderAsync(s, "Payee A", acdays: 2);
        var off = await client.PutAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/schedules/{JobTypes.ReminderAutoPost}",
            new ScheduleDto(Enabled: false, HourLocal: 5, MinuteLocal: 0, Timezone: null));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var before = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.False(before!.Enabled);

        var (_, autoPost) = await CheckAsync(s.Ledger.LedgerId);
        Assert.True(autoPost.Healthy, "a job the user switched off was reported");

        // Repair has nothing to do, so it leaves the job off.
        var repaired = await RepairAsync(s);
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        var after = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.False(after!.Enabled, "the repair turned on a job the user had switched off");
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Reminders_none_of_which_auto_post_are_not_reported()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        await CreateReminderAsync(s, "Payee A", acdays: null);
        await CreateReminderAsync(s, "Payee B", acdays: null);

        // Anchored on the reminders being there and the job not: the verdict below is the
        // rule's, not an empty ledger's.
        await using (var db = Db.NewServiceDbContext())
        {
            Assert.Equal(2, await db.RecurringTransactions.AsNoTracking()
                .CountAsync(r => r.LedgerId == s.Ledger.LedgerId && r.AutoCommitDaysBefore == null));
        }
        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));

        var (_, autoPost) = await CheckAsync(s.Ledger.LedgerId);

        Assert.True(autoPost.Healthy, "a ledger with only manual reminders was reported");
        Assert.Equal(1, autoPost.Checked);
    }

    [Fact]
    public async Task A_ledger_with_no_reminders_is_not_reported()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var (report, autoPost) = await CheckAsync(ledger.LedgerId);

        Assert.True(autoPost.Healthy);
        Assert.Equal(1, autoPost.Checked);
        Assert.True(report.Healthy);
    }

    /// <summary>Wait until <paramref name="pid"/> waits on a lock, or <paramref name="running"/>
    /// is done. Asked as the superuser, who can read every role's wait events.</summary>
    private async Task WaitForLockWaitOrDoneAsync(int pid, Task running)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 400 && !running.IsCompleted; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", probe);
            cmd.Parameters.AddWithValue("pid", pid);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Two writers that both find the job absent, as a reminder save and a Repair can: the
    /// one whose insert loses to the other's reports that it created nothing, rather than
    /// failing, and leaves the winner's row as it is. Inside a transaction, as the Repair
    /// runs it under the ledger's write lock, that transaction is still usable and commits.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_created_by_another_writer_after_the_read_is_not_an_error(bool insideATransaction)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        const int winnersHour = 7;

        // The winner: its row inserted, not yet committed, so the loser's read misses it.
        await using var winnerDb = Db.NewServiceDbContext();
        await using var winning = await winnerDb.Database.BeginTransactionAsync();
        Assert.True(await new SchedulesRepository(winnerDb).EnsureCreatedEnabledAsync(
            ledger.LedgerId, JobTypes.ReminderAutoPost, winnersHour, ledger.UserId, now));

        await using var loserDb = Db.NewServiceDbContext();
        await using var write = insideATransaction
            ? await loserDb.BeginLedgerWriteAsync(ledger.LedgerId)
            : null;
        await loserDb.Database.OpenConnectionAsync();
        var running = new SchedulesRepository(loserDb).EnsureCreatedEnabledAsync(
            ledger.LedgerId, JobTypes.ReminderAutoPost, ScheduleDefaults.ReminderAutoPostHour,
            ledger.UserId, now);
        await WaitForLockWaitOrDoneAsync(
            ((NpgsqlConnection)loserDb.Database.GetDbConnection()).ProcessID, running);
        // Staged as claimed: the loser read no row and is waiting on the winner's insert.
        Assert.False(running.IsCompleted, "the second writer did not reach its insert while the first was open");
        await winning.CommitAsync();

        Assert.False(await running);
        if (write is not null)
        {
            // The failed insert was rolled back alone: the transaction still reads and commits.
            Assert.Equal(1, await loserDb.ScheduledJobs.AsNoTracking()
                .CountAsync(j => j.LedgerId == ledger.LedgerId && j.JobType == JobTypes.ReminderAutoPost));
            await write.CommitAsync();
        }

        var row = await AutoPostRowAsync(ledger.LedgerId);
        Assert.NotNull(row);
        Assert.Equal(winnersHour, row.HourLocal);
    }

    [Fact]
    public async Task Repair_turns_the_job_on_and_is_recorded_and_a_second_repair_changes_nothing()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        await CreateReminderAsync(s, "Payee A", acdays: 2);
        await RemoveAutoPostRowAsync(s.Ledger.LedgerId);
        Assert.False((await CheckAsync(s.Ledger.LedgerId)).AutoPost.Healthy);

        var first = await RepairAsync(s);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // What it rewrote, and the check after it, which agrees.
        var repair = (await first.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.Equal(ConsistencyProjections.ReminderAutoPost, repair.Projection);
        Assert.Equal(1, repair.Repaired.MismatchedCount);
        Assert.True(repair.ProjectionAfter().Healthy);
        Assert.True(repair.After!.Healthy);

        // The job a reminder save creates: enabled, at the default hour, configured by the
        // person who repaired, and due.
        var row = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.NotNull(row);
        Assert.True(row.Enabled);
        Assert.Equal(ScheduleDefaults.ReminderAutoPostHour, row.HourLocal);
        Assert.Equal(0, row.MinuteLocal);
        Assert.Null(row.Timezone);
        Assert.NotNull(row.NextRunAt);
        Assert.Equal(s.Ledger.UserId, row.ConfiguredByUserId);
        Assert.True((await CheckAsync(s.Ledger.LedgerId)).AutoPost.Healthy);

        // Recorded as every repair is: its event, and the ledger's state.
        var summary = Assert.Single(await RepairedSummariesAsync(s.Ledger.LedgerId));
        Assert.StartsWith("Repaired reminder_auto_post: turned the auto-post job on.", summary, StringComparison.Ordinal);
        await using (var svc = Db.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == s.Ledger.LedgerId);
            Assert.NotNull(state.LastRepairedAt);
            Assert.True(state.Healthy);
            Assert.Empty(state.FailingProjections);
        }

        // Again: nothing to do, the job exactly as the first left it, and the no-op recorded.
        var second = await RepairAsync(s);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var again = (await second.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.Equal(0, again.Repaired.MismatchedCount);
        var unchanged = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.Equal(row.Enabled, unchanged!.Enabled);
        Assert.Equal(row.HourLocal, unchanged.HourLocal);
        Assert.Equal(row.NextRunAt, unchanged.NextRunAt);
        Assert.Equal(row.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(row.ConfiguredByUserId, unchanged.ConfiguredByUserId);
        var summaries = await RepairedSummariesAsync(s.Ledger.LedgerId);
        Assert.Equal(2, summaries.Count);
        Assert.Contains("nothing needed changing", summaries[1], StringComparison.Ordinal);
    }
}
