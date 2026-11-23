using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// <c>GET /api/ledgers/attention</c> — which of my ledgers needs me (FU-049).
/// </summary>
/// <remarks>
/// The one question the app could not answer. A problem was visible only if you were
/// already on the screen that shows it, and both of those screens live inside a ledger
/// you have to have chosen first.
/// <para>These tests exist as much for the SQL as for the rule: the endpoint groups by a
/// subject the DATABASE computes, and an untranslatable expression is a runtime failure
/// rather than a compile one.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerAttentionEndpointTests : DatabaseTest
{
    public LedgerAttentionEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Write one event at a chosen instant, bypassing the publisher.</summary>
    /// <remarks>
    /// Directly, because the point here is the READ rule over arbitrary arrangements of
    /// rows — including orderings no publisher would produce today. Going through
    /// <c>NotificationPublisher</c> would test the publishers instead, and could not
    /// place two events a minute apart without waiting a minute.
    /// </remarks>
    private async Task AddEventAsync(
        Guid ledgerId, string severity, string eventKey, DateTime at)
    {
        var summary = $"{eventKey} @ {at:O}";
        await using var db = Db.NewDbContext();
        db.LedgerEvents.Add(new LedgerEventRow
        {
            LedgerId = ledgerId,
            Severity = severity,
            Topic = NotificationTopics.Scheduler,
            EventKey = eventKey,
            Summary = summary,
            DetailJson = "{}",
        });
        await db.SaveChangesAsync();
        await db.LedgerEvents.Where(e => e.Summary == summary)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, at));
    }

    /// <summary>
    /// Wait out the monitor's boot pass, then clear what it wrote.
    /// </summary>
    /// <remarks>
    /// <para><c>SchedulerService</c> runs its first tick at process start with no
    /// initial delay, so building an <c>ApiFactory</c> publishes a real
    /// <c>consistency.ok</c> for every ledger, at a moment nothing controls. Dated now,
    /// it is later than any backdated <c>consistency.drift</c> a test seeds — so it
    /// resolves it, correctly, and the test is left asserting against an arrangement it
    /// did not make.</para>
    ///
    /// <para>Clearing FIRST and racing it would only move the problem. Waiting for the
    /// row the pass writes is the signal that it has finished; afterwards the ledger is
    /// inside the 12h cadence window, so nothing publishes again for the rest of the
    /// test.</para>
    ///
    /// <para>The alternative — dating fixtures in the future so the live event cannot
    /// outrank them — passes today and rots on a fixed date, silently, which is worse
    /// than a slow test.</para>
    /// </remarks>
    private async Task ResetEventsAsync(params SyntheticLedger[] ledgers)
    {
        foreach (var ledger in ledgers)
        {
            var settled = false;
            for (var attempt = 0; attempt < 100 && !settled; attempt++)
            {
                await using (var svc = Db.NewServiceDbContext())
                {
                    settled = await svc.LedgerConsistencyStates.AsNoTracking()
                        .AnyAsync(r => r.LedgerId == ledger.LedgerId
                                       && r.LastCheckedAt != null);
                }

                if (!settled) await Task.Delay(100);
            }

            Assert.True(settled, "The consistency monitor never completed its boot pass.");
        }

        await using var db = Db.NewServiceDbContext();
        foreach (var ledger in ledgers)
        {
            await db.LedgerEvents.Where(e => e.LedgerId == ledger.LedgerId)
                .ExecuteDeleteAsync();
        }
    }

    private static async Task<List<LedgerAttentionDto>> GetAsync(HttpClient client)
        => (await client.GetFromJsonAsync<List<LedgerAttentionDto>>(
            "/api/ledgers/attention"))!;

    private static async Task<List<OutstandingProblemDto>> OutstandingAsync(
        HttpClient client, SyntheticLedger ledger)
        => (await client.GetFromJsonAsync<List<OutstandingProblemDto>>(
            $"/api/ledgers/{ledger.LedgerId}/notifications/outstanding"))!;

    private static DateTime At(int day, int hour = 0) =>
        new(2026, 6, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_ledger_with_nothing_outstanding_is_absent()
    {
        // Absence is the answer, not a row saying "fine". The reader treats a missing
        // ledger as "nothing to say", which is also what it has before the request
        // finishes — so the quiet case and the loading case render identically and
        // nothing flashes on arrival.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        // An anchor: a ledger with a real problem, so "absent" below is a statement
        // about THIS ledger rather than about an endpoint that returns nothing at all.
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "consistency.ok", At(2));

        var rows = await GetAsync(client);
        Assert.DoesNotContain(rows, r => r.LedgerId == ledger.LedgerId);
    }

    [Fact]
    public async Task An_unresolved_warning_reports_the_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(3));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Warning, row.Severity);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task An_all_clear_only_resolves_its_own_subject()
    {
        // The rule the indicator inherits from the panel: a later info event clears the
        // SUBJECT it is about, and nothing else. Without this a routine
        // "consistency.ok" would silently switch off a failing backup's indicator,
        // which is the failure mode that makes an indicator worse than none.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(2));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "consistency.ok", At(3));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Critical, row.Severity);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task A_switched_off_job_clears_on_its_next_successful_run()
    {
        // ADR-0102 D1: a disable is `<job type>.disabled`, so the job's own next success
        // is its all-clear once someone turns it back on.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        // The anchor: an unrelated standing warning, so the count below is this ledger's.
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "feed-sync.disabled", At(2));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "feed-sync.succeeded", At(3));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Warning, row.Severity);
        Assert.Equal(1, row.Count);

        // The outstanding list agrees: the anchor alone.
        var outstanding = Assert.Single(await OutstandingAsync(client, ledger));
        Assert.Equal("consistency.", outstanding.SubjectKey);
    }

    [Fact]
    public async Task The_outstanding_list_is_the_attention_count_and_severity_row_by_row()
    {
        // One query behind both, so the rail's number is the rows a ledger lists.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(2));
        // A subject that went wrong, cleared, and went wrong again: it is outstanding
        // from the first problem after the all-clear, not from the first ever.
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "quote-refresh.failed", At(3));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "quote-refresh.succeeded", At(4));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "quote-refresh.failed", At(5));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "quote-refresh.failed", At(6));
        // Cleared, so on neither.
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "feed-sync.disabled", At(2));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "feed-sync.succeeded", At(7));

        var attention = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        var outstanding = await OutstandingAsync(client, ledger);

        Assert.Equal(attention.Count, outstanding.Count);
        Assert.Equal(attention.Severity, outstanding[0].Severity);
        Assert.Equal(
            ["backup.", "quote-refresh.", "consistency."],
            outstanding.Select(o => o.SubjectKey).ToArray());

        var quotes = outstanding.Single(o => o.SubjectKey == "quote-refresh.");
        Assert.Equal(At(5), quotes.FirstUnresolvedAt);
        Assert.Equal(At(6), quotes.LatestAt);
        Assert.Equal("quote-refresh.failed", quotes.EventKey);
        Assert.Equal(NotificationTopics.Scheduler, quotes.Topic);
    }

    [Fact]
    public async Task A_later_all_clear_takes_its_subject_off_the_outstanding_list()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        // The anchor: a subject that stays, so the absence below is this subject's.
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(2));
        Assert.Equal(2, (await OutstandingAsync(client, ledger)).Count);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            "consistency.ok", At(3));

        var row = Assert.Single(await OutstandingAsync(client, ledger));
        Assert.Equal("backup.", row.SubjectKey);
        Assert.Equal(1, Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId).Count);
    }

    [Fact]
    public async Task Two_switched_off_jobs_are_two_problems()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "feed-sync.disabled", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "quote-refresh.disabled", At(2));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Critical, row.Severity);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task The_highest_severity_wins_and_is_not_a_string_compare()
    {
        // "warning" sorts AFTER "critical" alphabetically, so a max over the raw column
        // would report a ledger with both as a warning — downgrading the irreversible
        // to the repairable, which is precisely backwards.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(2));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Critical, row.Severity);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task A_standing_problem_counts_once_however_often_it_is_announced()
    {
        // The count is of distinct SUBJECTS, not rows, and this is what that is for.
        // The consistency monitor publishes on every pass with no throttle, so a ledger
        // left drifting accumulates a row every twelve hours: after six months that is
        // hundreds of rows and exactly one problem. "364" would be a worse answer than
        // no number, and it would keep changing while nothing changed — which is how a
        // reader learns to stop reading it.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        for (var day = 1; day <= 10; day++)
        {
            await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
                "consistency.drift", At(day, 6));
            await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
                "consistency.drift", At(day, 18));
        }

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task A_subject_reports_the_severity_of_its_LATEST_event()
    {
        // A subject's current state is what the most recent event about it says. Taking
        // the max over every unresolved row instead would pin a ledger red forever on
        // the strength of one bad night months ago, and no later event short of a full
        // all-clear could bring it down.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ResetEventsAsync(ledger);

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(1));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            "backup.stale", At(2));

        var row = Assert.Single(await GetAsync(client), r => r.LedgerId == ledger.LedgerId);
        Assert.Equal(NotificationSeverity.Warning, row.Severity);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task Another_users_ledger_is_never_reported()
    {
        // The route has no ledgerId to gate on, which is exactly why this is asserted.
        // The grant join is the primary check and ledger_events' RLS the backstop; a
        // service-role context copied in from a neighbouring repository would hand
        // every install's problems to every caller with no error to notice.
        var mine = await SyntheticLedger.CreateAsync(Db);
        var theirs = await SyntheticLedger.CreateAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, mine);
        await ResetEventsAsync(mine, theirs);

        await AddEventAsync(theirs.LedgerId, NotificationSeverity.Critical,
            "backup.none", At(1));
        await AddEventAsync(mine.LedgerId, NotificationSeverity.Warning,
            "consistency.drift", At(1));

        var rows = await GetAsync(client);

        // Both halves matter. Seeing my own ledger proves the endpoint is working, so
        // the other's absence is a refusal rather than an empty response.
        Assert.Contains(rows, r => r.LedgerId == mine.LedgerId);
        Assert.DoesNotContain(rows, r => r.LedgerId == theirs.LedgerId);
    }

    [Fact]
    public async Task Anonymous_callers_are_refused()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.GetAsync("/api/ledgers/attention");
        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403, got {(int)resp.StatusCode}");
    }
}
