using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Import;

/// <summary>
/// A Moneydance import that brings a reminder set to auto-commit turns the new ledger's
/// auto-post job on, as saving such a reminder in the app does.
/// </summary>
/// <remarks>
/// Through the real background runner (<c>POST /api/imports/moneydance</c>), because the
/// schedule is written by the runner's own pre-commit step inside the import's
/// transaction, which no lower-level import call composes. Without it the reminders page
/// shows the imported reminder as auto-posting while nothing ever posts it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ImportAutoPostScheduleTests : DatabaseTest
{
    public ImportAutoPostScheduleTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task An_import_with_an_auto_commit_reminder_turns_auto_post_on_for_its_importer()
    {
        var ledgerId = await ImportAsync("Auto-post on " + Guid.NewGuid().ToString("N")[..8], Export(acdays: "3"));

        await using var db = Db.NewServiceDbContext();
        var owner = await db.UserLedgerGrants.AsNoTracking()
            .Where(g => g.LedgerId == ledgerId && g.Role == "owner")
            .Select(g => g.UserId).SingleAsync();
        var row = await AutoPostRowAsync(ledgerId);

        Assert.NotNull(row);
        Assert.True(row.Enabled);
        Assert.Equal(owner, row.ConfiguredByUserId);
        Assert.Equal(ScheduleDefaults.ReminderAutoPostHour, row.HourLocal);
        Assert.Equal(0, row.MinuteLocal);
        Assert.NotNull(row.NextRunAt);
    }

    [Fact]
    public async Task An_import_with_only_manual_reminders_leaves_auto_post_unconfigured()
    {
        var ledgerId = await ImportAsync("Auto-post off " + Guid.NewGuid().ToString("N")[..8], Export(acdays: "-1"));

        // Anchored on the reminder being there, so the absence below is about the rule and
        // not about an import that brought nothing.
        await using var db = Db.NewServiceDbContext();
        var reminder = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.LedgerId == ledgerId);
        Assert.Null(reminder.AutoCommitDaysBefore);
        Assert.Null(await AutoPostRowAsync(ledgerId));
    }

    /// <summary>
    /// The import and the consistency check read one rule (<c>AutoPostRequests</c>): a
    /// ledger the import leaves is one the check agrees with, with the job and without it,
    /// and the import's ledger without its job is the one the check reports.
    /// </summary>
    [Fact]
    public async Task The_consistency_check_agrees_with_what_an_import_leaves()
    {
        var auto = await ImportAsync("Auto-post check " + Guid.NewGuid().ToString("N")[..8], Export(acdays: "3"));
        var manual = await ImportAsync("Manual check " + Guid.NewGuid().ToString("N")[..8], Export(acdays: "-1"));
        Assert.NotNull(await AutoPostRowAsync(auto));
        Assert.Null(await AutoPostRowAsync(manual));

        Assert.True((await AutoPostCheckAsync(auto)).Healthy, "the check reported the job the import created");
        Assert.True((await AutoPostCheckAsync(manual)).Healthy, "the check asked for a job the import did not need");

        // The import's ledger as an import before the fix left it.
        await using (var db = Db.NewServiceDbContext())
        {
            Assert.Equal(1, await db.ScheduledJobs
                .Where(j => j.LedgerId == auto && j.JobType == JobTypes.ReminderAutoPost)
                .ExecuteDeleteAsync());
        }
        var flagged = await AutoPostCheckAsync(auto);
        Assert.False(flagged.Healthy);
        Assert.Equal("not set up, so the reminder set to auto-post is never posted",
            Assert.Single(flagged.Mismatches).Field);
    }

    private async Task<ProjectionConsistency> AutoPostCheckAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var report = await new LedgerConsistencyRepository(
                db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
                TimeProvider.System)
            .CheckAsync(ledgerId);
        return Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.ReminderAutoPost);
    }

    private async Task<ScheduledJobRow?> AutoPostRowAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.ScheduledJobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost);
    }

    /// <summary>A bank account, a category and one monthly reminder between them, whose
    /// <c>acdays</c> is <paramref name="acdays"/>: -1 is manual, 0 or more auto-commits. Every
    /// account names the file's one currency, its base (ADR-0114).</summary>
    private static string Export(string acdays) => $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-bill","type":"e","name":"Bills","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"reminder","id":"rem-1","desc":"Bill","type":"monthly","sdt":"20260101",
             "acdays":"{{acdays}}","daily":"0","monthlydays":"1","monthlymod":"1","weeklydays":"",
             "yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-bank","txn.desc":"Bill","txn.dt":"20260101",
             "txn.0.id":"rs-1","txn.0.acctid":"c-bill","txn.0.samt":"1000","txn.0.pamt":"-1000"}
          ]
        }
        """;

    /// <summary>Import <paramref name="json"/> through the background runner and return the
    /// new ledger; a run that does not succeed fails with the errors it logged.</summary>
    private async Task<Guid> ImportAsync(string ledgerName, string json)
    {
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(Db)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "export.json");
        content.Add(new StringContent(ledgerName), "ledgerName");

        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString()!;

        // A genuine wait: the import is a background task. The ceiling only turns a hang
        // into a failure.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var response = await client.GetAsync($"/api/imports/moneydance/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = body.RootElement.GetProperty("state").GetString();
            if (state == "running")
            {
                await Task.Delay(250);
                continue;
            }

            Assert.True(state == "succeeded", $"The import ended '{state}': "
                + string.Join(" || ", logs.Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message)));
            // The fixture is clean, so a failed check would mean the reminder did not import.
            Assert.Empty(body.RootElement.GetProperty("warnings").EnumerateArray());
            return body.RootElement.GetProperty("ledgerId").GetGuid();
        }

        Assert.Fail("The import job never left the 'running' state.");
        return default;
    }
}
