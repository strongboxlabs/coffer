using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Ticking "Auto-post" on a reminder turns the ledger's auto-post job on.
/// </summary>
/// <remarks>
/// <para>
/// Requiring TWO switches — a per-reminder option and a per-ledger one — reproduces the
/// exact defect ADR-0097 sets out to remove: a user ticks Auto-post, saves, and nothing
/// happens, silently, because the other switch is off and nothing hints that it exists.
/// So the per-reminder tick IS the opt-in.
/// </para>
/// <para>
/// The per-ledger row still exists and still matters — it is what makes this a
/// <c>scheduled_jobs</c> job type rather than a tick-riding monitor, and so what buys
/// failure counting, the five-strike auto-disable and the monitor binding. It just is not
/// something a user has to go and find.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderAutoPostImplicitEnableTests : DatabaseTest
{
    public ReminderAutoPostImplicitEnableTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> CreateReminderAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId, int? acdays)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                AutoCommitDaysBefore = acdays,
                Payee = "Rent",
                SourceAccountId = bankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = catId, Amount = -1500m } },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private async Task<ScheduledJobRow?> AutoPostRowAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.ScheduledJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost);
    }

    private sealed record Setup(SyntheticLedger Ledger, HttpClient Client, Guid BankId, Guid CatId);

    private async Task<Setup> NewLedgerAsync(ApiFactory factory)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        return new Setup(ledger, await AuthedClientAsync(factory, ledger), bank.Id, cat.Id);
    }

    /// <summary>
    /// Creating a reminder that asks to be auto-posted configures the job, enabled.
    /// </summary>
    [Fact]
    public async Task Creating_an_auto_post_reminder_enables_the_job()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;

        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));

        await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: 2);

        var row = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.NotNull(row);
        Assert.True(row!.Enabled, "the job was configured but left disabled, so the tick still does nothing");
        Assert.True(row.NextRunAt.HasValue, "an enabled schedule with no next_run_at will never be picked up");
    }

    /// <summary>
    /// A reminder that does NOT ask for auto-posting configures nothing.
    /// </summary>
    /// <remarks>
    /// Without this, "always create the row" would pass the test above while turning a
    /// transaction-writing job on for every ledger that has ever held a reminder.
    /// </remarks>
    [Fact]
    public async Task Creating_an_ordinary_reminder_does_not_enable_the_job()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;

        await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: null);

        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));
    }

    /// <summary>
    /// Turning Auto-post on by EDITING an existing reminder enables the job too.
    /// </summary>
    /// <remarks>
    /// Every write that can turn Auto-post on must enable the job, not only the create
    /// endpoints: a reminder that gains auto-posting through an edit otherwise has no
    /// enabled job, and its tick never runs.
    /// </remarks>
    [Fact]
    public async Task Editing_a_reminder_to_add_auto_post_enables_the_job()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;

        var reminderId = await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: null);
        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{reminderId}",
            new EditReminderRequest { AutoCommitDaysBefore = 3 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var row = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.NotNull(row);
        Assert.True(row!.Enabled);
    }

    /// <summary>
    /// A reminder save that is refused writes nothing, the auto-post job included: the
    /// reminder that asked for auto-posting was never saved.
    /// </summary>
    /// <remarks>
    /// The job is written through the request's own database context, so enabling it
    /// after a refusal would also save whatever the refused edit had changed before it
    /// was refused; the bank cases assert the reminder is as it was.
    /// </remarks>
    [Theory]
    [InlineData("bank-edit-another-source")]
    [InlineData("bank-edit-end-before-start")]
    [InlineData("bank-edit-notes-from-another-side")]
    [InlineData("investment-edit-another-brokerage")]
    [InlineData("investment-create-invalid-shape")]
    public async Task A_refused_reminder_save_does_not_enable_the_job(string refusal)
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;
        var brokerage = await s.Ledger.AddInvestmentAccountAsync("brokerage");
        var otherBrokerage = await s.Ledger.AddInvestmentAccountAsync("other brokerage");
        var otherBank = await s.Ledger.AddBankAccountAsync("savings");
        CreateInvestmentTransactionRequest TransferFrom(Guid account, bool withCounterpart = true) => new()
        {
            BrokerageAccountId = account, Action = "transfer", TransferAccountId = withCounterpart ? s.BankId : null,
            Amount = 75m, PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        async Task<Guid> InvestmentReminderAsync()
        {
            var created = await client.PostAsJsonAsync($"/api/ledgers/{s.Ledger.LedgerId}/reminders/investment",
                new CreateInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                    Transaction = TransferFrom(brokerage.Id),
                });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        }

        var bankUrl = $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: null)}";
        var resp = refusal switch
        {
            "bank-edit-another-source" => await client.PatchAsJsonAsync(bankUrl, new EditReminderRequest
            {
                AutoCommitDaysBefore = 3, Payee = "Changed",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = otherBank.Id,
                    Items = [new TransactionPosting { CounterpartyAccountId = s.CatId, Amount = -1500m }],
                },
            }),
            "bank-edit-end-before-start" => await client.PatchAsJsonAsync(bankUrl, new EditReminderRequest
            {
                AutoCommitDaysBefore = 3, Payee = "Changed", EndDate = new DateOnly(2025, 12, 1),
            }),
            "bank-edit-notes-from-another-side" => await client.PatchAsJsonAsync(bankUrl, new EditReminderRequest
            {
                AutoCommitDaysBefore = 3, Payee = "Changed",
                Notes = new TransactionNotesRequest { Side = s.CatId, Memo = "changed" },
            }),
            "investment-edit-another-brokerage" => await client.PatchAsJsonAsync(
                $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{await InvestmentReminderAsync()}/investment",
                new EditInvestmentReminderRequest { AutoCommitDaysBefore = 3, Transaction = TransferFrom(otherBrokerage.Id) }),
            _ => await client.PostAsJsonAsync($"/api/ledgers/{s.Ledger.LedgerId}/reminders/investment",
                new CreateInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1), AutoCommitDaysBefore = 3,
                    Transaction = TransferFrom(brokerage.Id, withCounterpart: false),
                }),
        };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null(await AutoPostRowAsync(s.Ledger.LedgerId));
        var bank = (await client.GetFromJsonAsync<ReminderDetail>(bankUrl))!;
        Assert.Equal("Rent", bank.Payee);
        Assert.Null(bank.EndDate);
        Assert.Null(bank.AutoCommitDaysBefore);
    }

    /// <summary>
    /// A reminder edit does NOT restart a job the scheduler switched off after five
    /// failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE test in this file. The five-strike auto-disable is the entire reason auto-post
    /// is a job type, and "any reminder edit re-enables the job" would erase it: a job
    /// that gave up after five failed nights would come back the next time the user
    /// touched an unrelated reminder, fail again, and keep going round — with the streak
    /// reset each time so it could never be given up on twice.
    /// </para>
    /// <para>
    /// The disable reason is asserted too, not just <c>Enabled == false</c>. A row that
    /// stayed off but lost its reason reads as an operator's own pause, which is the
    /// distinction migration 216 exists to preserve.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_auto_disabled_job_is_not_re_enabled_by_editing_a_reminder()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;

        var reminderId = await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: 2);
        Assert.True((await AutoPostRowAsync(s.Ledger.LedgerId))!.Enabled);

        // The scheduler gives up on it.
        await using (var db = Db.NewServiceDbContext())
        {
            var row = await db.ScheduledJobs.SingleAsync(
                j => j.LedgerId == s.Ledger.LedgerId && j.JobType == JobTypes.ReminderAutoPost);
            row.Enabled = false;
            row.ConsecutiveFailures = SchedulerRunner.DisableAfterConsecutiveFailures;
            row.LastError = "the database rejected the operation";
            row.DisabledReason = ScheduleDisableReasons.ConsecutiveFailures;
            await db.SaveChangesAsync();
        }

        // The user edits the reminder — a perfectly ordinary act, and not a request to
        // restart a job that has been failing.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{reminderId}",
            new EditReminderRequest { AutoCommitDaysBefore = 4 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var after = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.NotNull(after);
        Assert.False(after!.Enabled, "editing a reminder restarted a job the scheduler had given up on");
        Assert.Equal(
            SchedulerRunner.DisableAfterConsecutiveFailures, after.ConsecutiveFailures);
        Assert.Equal(ScheduleDisableReasons.ConsecutiveFailures, after.DisabledReason);
    }

    /// <summary>
    /// Nor does it silently overwrite a time the user chose.
    /// </summary>
    /// <remarks>
    /// The reminders page owns WHEN the job runs. A create-if-absent that quietly reset
    /// the hour on every reminder save would move the run time back to the default behind
    /// the user's back, which is the same class of surprise as flipping Enabled.
    /// </remarks>
    [Fact]
    public async Task A_reminder_save_does_not_overwrite_a_chosen_run_time()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await NewLedgerAsync(factory);
        using var client = s.Client;

        var reminderId = await CreateReminderAsync(client, s.Ledger, s.BankId, s.CatId, acdays: 2);

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/schedules/{JobTypes.ReminderAutoPost}",
            new ScheduleDto(Enabled: true, HourLocal: 21, MinuteLocal: 45, Timezone: "America/New_York"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/{reminderId}",
            new EditReminderRequest { AutoCommitDaysBefore = 5 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var row = await AutoPostRowAsync(s.Ledger.LedgerId);
        Assert.Equal(21, row!.HourLocal);
        Assert.Equal(45, row.MinuteLocal);
        Assert.Equal("America/New_York", row.Timezone);
    }
}
