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
/// The in-app read over <c>ledger_events</c> — ADR-0096 D5's promised surface.
/// </summary>
/// <remarks>
/// D5 justified ledger scope having no external absence detection on the grounds that it
/// is "DB-backed and pull-based, surfaced in-app". Nothing read the table: migration 208
/// installed <c>ledger_events_read</c> and it had never had a single caller. These are the
/// first tests to exercise it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerEventsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public LedgerEventsEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    private async Task AddEventAsync(
        Guid ledgerId, string severity, string summary, DateTime? at = null,
        string eventKey = "test.event")
    {
        await using var db = _fixture.NewDbContext();
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

        if (at is not null)
        {
            await db.LedgerEvents.Where(e => e.Summary == summary)
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurredAt, at.Value));
        }
    }

    private static async Task<List<SystemEventDto>> GetAsync(HttpClient client, Guid ledgerId)
        => (await client.GetFromJsonAsync<List<SystemEventDto>>(
            $"/api/ledgers/{ledgerId}/notifications/events"))!;

    [Fact]
    public async Task Warnings_are_returned_and_routine_successes_are_not()
    {
        // The whole design of the endpoint. Every scheduled run of every enabled job
        // publishes an info success, so returning all severities would mean a newest-20
        // of "completed" lines with the one failure pushed off the end — a surface that
        // looks answered while hiding the answer.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info, $"routine-{tag}");
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning, $"trouble-{tag}");
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Critical, $"disaster-{tag}");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var events = await GetAsync(client, ledger.LedgerId);
        var summaries = events.Select(e => e.Summary).ToList();

        Assert.Contains($"trouble-{tag}", summaries);
        Assert.Contains($"disaster-{tag}", summaries);
        Assert.DoesNotContain($"routine-{tag}", summaries);
    }

    [Fact]
    public async Task The_severity_filter_runs_in_the_database_not_after_the_limit()
    {
        // The bug this endpoint exists to avoid, pinned directly. Filtering in memory
        // would take the newest N rows and THEN drop the successes — so a ledger with a
        // month of daily successes would return an empty list while a real warning sat
        // just below the cut. Bury the warning under more than one page of noise and
        // assert it still comes back.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning, $"buried-{tag}",
            at: DateTime.UtcNow.AddDays(-40));
        for (var i = 0; i < 30; i++)
        {
            await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info, $"noise-{tag}-{i}",
                at: DateTime.UtcNow.AddDays(-i));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var events = await GetAsync(client, ledger.LedgerId);
        Assert.Contains($"buried-{tag}", events.Select(e => e.Summary));
    }

    [Fact]
    public async Task A_warning_a_later_success_supersedes_is_marked_resolved()
    {
        // The failure this exists for: the first real row this list ever showed was a
        // six-day-old drift warning that had already been resolved, indistinguishable
        // from one raised a minute ago.
        //
        // Event keys are "<subject>.<outcome>", so a later INFO event with the same
        // SUBJECT is the all-clear. Every scheduled job already publishes one on its next
        // good run, which makes job warnings self-resolving without a new event type.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            $"broke-{tag}", at: DateTime.UtcNow.AddHours(-3), eventKey: "quote-refresh.failed");
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            $"fixed-{tag}", at: DateTime.UtcNow.AddHours(-1), eventKey: "quote-refresh.succeeded");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var broke = (await GetAsync(client, ledger.LedgerId))
            .Single(e => e.Summary == $"broke-{tag}");

        // Still listed — silently vanishing is its own kind of lie — but marked.
        Assert.NotNull(broke.ResolvedAt);
    }

    [Fact]
    public async Task A_warning_with_no_later_success_stays_unresolved()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            $"still-{tag}", at: DateTime.UtcNow.AddHours(-2), eventKey: "snapshot.failed");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var still = (await GetAsync(client, ledger.LedgerId))
            .Single(e => e.Summary == $"still-{tag}");
        Assert.Null(still.ResolvedAt);
    }

    [Fact]
    public async Task A_success_for_a_DIFFERENT_subject_resolves_nothing()
    {
        // The rule is per subject, not per ledger. A snapshot succeeding says nothing
        // about the quote refresh, and treating it as an all-clear would silently mark
        // real problems fixed — worse than never marking them at all.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            $"quotes-broke-{tag}", at: DateTime.UtcNow.AddHours(-3), eventKey: "quote-refresh.failed");
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            $"snap-ok-{tag}", at: DateTime.UtcNow.AddHours(-1), eventKey: "snapshot.succeeded");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var broke = (await GetAsync(client, ledger.LedgerId))
            .Single(e => e.Summary == $"quotes-broke-{tag}");
        Assert.Null(broke.ResolvedAt);
    }

    [Fact]
    public async Task An_all_clear_BEFORE_the_problem_does_not_resolve_it()
    {
        // Ordering matters or the mark is meaningless: yesterday's success cannot fix
        // today's failure. A naive "does a success exist for this subject" would call
        // every recurring problem resolved forever after its first good run.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Info,
            $"early-ok-{tag}", at: DateTime.UtcNow.AddHours(-5), eventKey: "snapshot.succeeded");
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning,
            $"later-broke-{tag}", at: DateTime.UtcNow.AddHours(-2), eventKey: "snapshot.failed");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var broke = (await GetAsync(client, ledger.LedgerId))
            .Single(e => e.Summary == $"later-broke-{tag}");
        Assert.Null(broke.ResolvedAt);
    }

    [Fact]
    public async Task Newest_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning, $"older-{tag}",
            at: DateTime.UtcNow.AddDays(-5));
        await AddEventAsync(ledger.LedgerId, NotificationSeverity.Warning, $"newer-{tag}",
            at: DateTime.UtcNow.AddMinutes(-1));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var mine = (await GetAsync(client, ledger.LedgerId))
            .Where(e => e.Summary.EndsWith(tag, StringComparison.Ordinal))
            .ToList();

        Assert.Equal($"newer-{tag}", mine[0].Summary);
        Assert.Equal($"older-{tag}", mine[1].Summary);
    }

    [Fact]
    public async Task Another_ledgers_events_are_not_visible()
    {
        // Two defences and this test cannot tell which one fired, deliberately: the
        // endpoint's explicit ledger_id predicate is the primary check, and
        // ledger_events_read (mig 208) is the backstop for the day someone forgets it.
        // What matters is that a caller with no grant gets nothing.
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var theirs = await SyntheticLedger.CreateAsync(_fixture);
        var tag = Guid.NewGuid().ToString("N")[..6];
        await AddEventAsync(theirs.LedgerId, NotificationSeverity.Critical, $"secret-{tag}");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, mine);

        // Asking for my own ledger must not leak theirs...
        Assert.DoesNotContain($"secret-{tag}",
            (await GetAsync(client, mine.LedgerId)).Select(e => e.Summary));

        // ...and asking for theirs directly is refused before any row is read.
        var direct = await client.GetAsync(
            $"/api/ledgers/{theirs.LedgerId}/notifications/events");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, direct.StatusCode);
    }

    [Fact]
    public async Task The_limit_is_clamped_rather_than_trusted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        // A caller-supplied limit reaches a Take(). Unclamped, limit=0 or a negative
        // throws and a huge one is an invitation to pull the whole table.
        foreach (var limit in new[] { "0", "-5", "100000" })
        {
            var response = await client.GetAsync(
                $"/api/ledgers/{ledger.LedgerId}/notifications/events?limit={limit}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
