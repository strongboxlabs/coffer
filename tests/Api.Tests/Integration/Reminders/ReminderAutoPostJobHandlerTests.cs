using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Notifications;
using Coffer.Api.Reminders;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// The reminder auto-post handler: what it posts, what it refuses to post, and how it
/// reports a run that did not fully land.
/// </summary>
/// <remarks>
/// <para>
/// Every test places "today" explicitly through <see cref="JobRunClock"/>. That is the
/// whole reason the clock is a parameter: a handler reading <c>DateTime.UtcNow</c> could
/// only be tested against whatever today happens to be, which is how a due-ness
/// assertion becomes either flaky or vacuous.
/// </para>
/// <para>
/// Limits are shrunk per test rather than using production values, because a cap test
/// that had to seed fifty occurrences would not get written — and an untested cap is a
/// number that silently stops being true.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderAutoPostJobHandlerTests
{
    private readonly PostgresFixture _fixture;

    public ReminderAutoPostJobHandlerTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateOnly SeriesStart = new(2026, 1, 1);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A monthly bank reminder, optionally asking to be auto-posted.</summary>
    private static async Task<Guid> NewSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId, int? acdays)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = SeriesStart,
                AutoCommitDaysBefore = acdays,
                Payee = "Rent",
                SourceAccountId = bankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = catId, Amount = -1500m } },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private ReminderAutoPostJobHandler NewHandler(
        ReminderAutoPostLimits? limits = null,
        INotificationSubscriber? spy = null,
        int? commandTimeoutSeconds = null)
    {
        var factory = commandTimeoutSeconds is null
            ? _fixture.NewServiceFactory()
            : PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
            {
                ConnectionString = _fixture.AppConnectionString,
                // A short command timeout turns "blocked on a lock somebody else holds"
                // into a deterministic, fast failure — the only fault injection available
                // for a handler that builds its own repositories internally.
                ServiceConnectionString =
                    $"{_fixture.ServiceConnectionString};Command Timeout={commandTimeoutSeconds}",
            }));

        return new ReminderAutoPostJobHandler(
            factory,
            new RecurrenceExpander(),
            new NotificationPublisher(
                _fixture.NewServiceFactory(),
                _fixture.NewLedgerKeyService(),
                spy is null ? Array.Empty<INotificationSubscriber>() : new[] { spy },
                NullLogger<NotificationPublisher>.Instance),
            NullLogger<ReminderAutoPostJobHandler>.Instance,
            limits);
    }

    private static JobRunClock At(DateOnly today) =>
        new(today.ToDateTime(new TimeOnly(5, 0), DateTimeKind.Utc), "UTC");

    /// <summary>
    /// Limits with catch-up switched OFF, so exactly one occurrence is in play.
    /// </summary>
    /// <remarks>
    /// A series starting 2026-01-01 has un-acted January, February and March slots, and
    /// the production 45-day catch-up window posts the recent ones — correctly. Three
    /// tests here were written assuming a single candidate and failed against that
    /// backlog: the code was right and the fixture was wrong. Pinning CatchUpDays to 0
    /// isolates the acdays window, which is what those tests are actually about; the
    /// catch-up window has its own tests.
    /// </remarks>
    private static ReminderAutoPostLimits ForwardWindowOnly => new(CatchUpDays: 0);

    private async Task<int> CommittedOccurrenceCountAsync(Guid seriesId)
    {
        await using var db = _fixture.NewServiceDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate);
    }

    /// <summary>
    /// The core case: an occurrence inside its series' acdays window gets posted.
    /// </summary>
    [Fact]
    public async Task An_occurrence_within_its_window_is_posted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        await using var db = _fixture.NewServiceDbContext();

        // 2026-03-30 is two days before the 2026-04-01 slot, inside a 3-day window.
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        // Exactly one, not "at least one": a handler ignoring the window would post the
        // whole backlog and satisfy a >0 assertion.
        Assert.Equal(1, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// ...and one still outside its window is left alone. Without this, a handler that
    /// ignored acdays entirely would pass the test above.
    /// </summary>
    [Fact]
    public async Task An_occurrence_beyond_its_window_is_not_posted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        await using var db = _fixture.NewServiceDbContext();

        // Ten days out, window is three.
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 22)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// A series with no acdays is never auto-posted, however overdue it is.
    /// </summary>
    [Fact]
    public async Task A_series_that_never_asked_to_be_auto_posted_is_left_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: null);

        await using var db = _fixture.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 4, 2)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// A SKIPPED occurrence is never posted by the timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asserts the PROPERTY, and the property is doubly guarded.</b> The scan
    /// filters on <c>Kind == "reminder"</c>, and <c>FireAsync</c> independently refuses a
    /// slot with a skip exception. Verified by mutation that removing EITHER one alone
    /// leaves this test green — the guards mask each other. That is good for safety and
    /// bad for a test that claims to pin one of them, so the scan's filter has its own
    /// test below and this one stays as the end-to-end statement.
    /// </para>
    /// <para>
    /// The trap being guarded is real: <c>GetUpcomingAsync</c> EMITS a skipped slot with
    /// <c>Kind == "skipped"</c> rather than omitting it, because the calendar renders it
    /// as a read-only trail. A scan filtering on anything looser — "not scheduled", the
    /// obvious phrasing — would hand suppressed occurrences to the fire path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_skipped_occurrence_is_never_posted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/skip",
            new SkipReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        await using var db = _fixture.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// A series whose acdays exceeds the cap BEHAVES as the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cap is enforced by clamping at read, not by rejecting on write: the column
    /// predates the cap, so an existing row can hold a larger value, and rejecting it
    /// would make a reminder the user can still see configured impossible to save. The
    /// SPA mirrors the ceiling with a <c>max</c> attribute; the clamp is what actually
    /// bounds the behaviour.
    /// </para>
    /// <para>
    /// So this is the test that makes the cap real. Asserting the <c>max</c> attribute
    /// would only prove an attribute exists; asserting the clamp proves a 500-day window
    /// does not write transactions sixteen months ahead of their due date.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_acdays_beyond_the_cap_is_clamped_rather_than_honoured()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Accepted by the API: validation only refuses a negative.
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 500);

        await using var db = _fixture.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(
                CatchUpDays: 0, MaxAutoCommitDaysBefore: 90)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 1, 5)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);

        await using var read = _fixture.NewServiceDbContext();
        var postedDates = await read.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate)
            .Select(h => h.OccurrenceDate!.Value)
            .ToListAsync();

        // 27 days out: inside the clamped 90-day window, so it posts. Without this the
        // absence below could hold because nothing posted at all.
        Assert.Contains(new DateOnly(2026, 2, 1), postedDates);

        // 116 days out: inside the series' declared 500 and OUTSIDE the cap. Honouring
        // the raw value would write this transaction now.
        Assert.DoesNotContain(new DateOnly(2026, 5, 1), postedDates);
    }

    /// <summary>
    /// The scan's own filter: a skipped slot is not even a candidate.
    /// </summary>
    /// <remarks>
    /// Pins the <c>Kind == "reminder"</c> filter by itself, which the end-to-end test
    /// above cannot — <c>FireAsync</c>'s independent skip check masks it. Loosening the
    /// filter to <c>!= "scheduled"</c> fails HERE and nowhere else.
    /// </remarks>
    [Fact]
    public async Task A_skipped_occurrence_is_not_a_scan_candidate()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/skip",
            new SkipReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        await using var db = _fixture.NewServiceDbContext();
        var repo = new Coffer.Api.Db.Repositories.RemindersRepository(
            db,
            new RecurrenceExpander(),
            new Coffer.Api.Db.Repositories.InvestmentTransactionsRepository(db),
            new Coffer.Api.Db.Repositories.TransactionsRepository(db));

        var scan = await repo.ScanForAutoPostAsync(
            ledger.LedgerId, new DateOnly(2026, 3, 30), catchUpDays: 0,
            maxAutoCommitDaysBefore: 90);

        Assert.DoesNotContain(scan.Due, c => c.OccurrenceDate == slot);
    }

    /// <summary>
    /// THE ledger-scoping test. The handler runs over a BYPASSRLS context, so the
    /// ledgerId predicate is the only thing standing between it and every other ledger's
    /// reminders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately arranged so the OTHER ledger's occurrence is due too. A two-ledger
    /// test where only one has work proves nothing — the handler would pass by having
    /// nothing to find.
    /// </para>
    /// <para>
    /// Asserted from ledger B's side: B's occurrence must have NO committed header. An
    /// assertion that only counted A's postings would pass a handler that posted both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_handler_never_posts_another_ledgers_occurrence()
    {
        var a = await SyntheticLedger.CreateAsync(_fixture);
        var aBank = await a.AddBankAccountAsync("a-checking");
        var aCat = await a.AddCategoryAsync("a-rent");

        var b = await SyntheticLedger.CreateAsync(_fixture);
        var bBank = await b.AddBankAccountAsync("b-checking");
        var bCat = await b.AddCategoryAsync("b-rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var clientA = await AuthedClientAsync(factory, a);
        using var clientB = await AuthedClientAsync(factory, b);

        var seriesA = await NewSeriesAsync(clientA, a, aBank.Id, aCat.Id, acdays: 3);
        var seriesB = await NewSeriesAsync(clientB, b, bBank.Id, bCat.Id, acdays: 3);

        await using var db = _fixture.NewServiceDbContext();

        // Run for ledger A only, at a moment when BOTH ledgers' occurrences are due.
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, a.LedgerId, a.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.True(
            await CommittedOccurrenceCountAsync(seriesA) > 0,
            "the run posted nothing for its own ledger, so the cross-ledger assertion "
            + "below would hold for the wrong reason");
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesB));
    }

    /// <summary>
    /// The scan's own ledger filter: another ledger's series is not a candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ledger scoping here is defended three times over, and NO single-layer mutation
    /// is observable from outside.</b> Established by mutation, not assumed:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   Drop the scan's own <c>r.LedgerId == ledgerId</c> predicate — every test stays
    ///   green, because <c>GetUpcomingAsync</c> is called with the ledger id and never
    ///   emits another ledger's occurrence, so the foreign series cannot reach the
    ///   intersection. That predicate is an EFFICIENCY filter (do not load every
    ///   ledger's reminder config on a per-ledger job), not the safety boundary. Worth
    ///   knowing before someone "simplifies" it away believing it is load-bearing.
    ///   </description></item>
    ///   <item><description>
    ///   Drop <c>GetUpcomingAsync</c>'s predicate too — STILL green, because
    ///   <c>FireAsync</c> resolves the series by <c>(id, ledgerId)</c> and answers
    ///   <c>NotFound</c> for a foreign one. That is the last-resort boundary and the one
    ///   that actually cannot be bypassed from here.
    ///   </description></item>
    /// </list>
    /// <para>
    /// So this test states the property at the scan boundary rather than isolating a
    /// layer, and the note above is the honest account of what it can and cannot catch.
    /// The alternative — deleting two of three guards so a mutation becomes visible —
    /// would trade real safety for test theatre.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_scan_returns_only_this_ledgers_candidates()
    {
        var a = await SyntheticLedger.CreateAsync(_fixture);
        var aBank = await a.AddBankAccountAsync("a-checking");
        var aCat = await a.AddCategoryAsync("a-rent");

        var b = await SyntheticLedger.CreateAsync(_fixture);
        var bBank = await b.AddBankAccountAsync("b-checking");
        var bCat = await b.AddCategoryAsync("b-rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var clientA = await AuthedClientAsync(factory, a);
        using var clientB = await AuthedClientAsync(factory, b);

        var seriesA = await NewSeriesAsync(clientA, a, aBank.Id, aCat.Id, acdays: 3);
        var seriesB = await NewSeriesAsync(clientB, b, bBank.Id, bCat.Id, acdays: 3);

        await using var db = _fixture.NewServiceDbContext();
        var repo = new Coffer.Api.Db.Repositories.RemindersRepository(
            db,
            new RecurrenceExpander(),
            new Coffer.Api.Db.Repositories.InvestmentTransactionsRepository(db),
            new Coffer.Api.Db.Repositories.TransactionsRepository(db));

        var scan = await repo.ScanForAutoPostAsync(
            a.LedgerId, new DateOnly(2026, 3, 30), catchUpDays: 0, maxAutoCommitDaysBefore: 90);

        // A's own candidate is present, so the absence below is not vacuous.
        Assert.Contains(scan.Due, c => c.ReminderId == seriesA);
        Assert.DoesNotContain(scan.Due, c => c.ReminderId == seriesB);
    }

    /// <summary>
    /// Earlier un-acted occurrences are left OPEN — the timer must not cascade-skip.
    /// </summary>
    /// <remarks>
    /// <c>CascadeSkipEarlierAsync</c> runs unconditionally on the manual path, which is
    /// right for a person: the SPA shows them the count before they confirm. A timer has
    /// no dialog, so cascading would mark occurrences skipped that nobody decided to
    /// skip — turning a bounded catch-up window into permanent data loss, where the bound
    /// stops the job posting them and the cascade stops anyone else from seeing them.
    /// </remarks>
    [Fact]
    public async Task Earlier_un_acted_occurrences_are_left_open_rather_than_cascaded()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 0);

        await using var db = _fixture.NewServiceDbContext();

        // 2026-04-01 is due; Jan/Feb/Mar are earlier and un-acted. A catch-up window of
        // one day means only April is postable, so the earlier three must survive
        // untouched rather than being swept.
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 1)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 4, 1)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);

        await using var read = _fixture.NewServiceDbContext();
        var exceptions = await read.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == seriesId);
        Assert.Equal(0, exceptions);
    }

    /// <summary>
    /// Deleting an auto-posted transaction does not cause it to be posted again.
    /// </summary>
    /// <remarks>
    /// The loop this closes: the (series, date) stamp on the committed header IS the
    /// slot's idempotency key, and <c>DeleteAsync</c> used to HARD-delete any header
    /// without an <c>external_id</c> — which every fired occurrence is. The stamp died
    /// with the row, the slot read un-acted, and the next tick posted it back. Delete
    /// again, it returns. Soft-hiding keeps the stamp, so the slot stays consumed.
    /// </remarks>
    [Fact]
    public async Task Deleting_an_auto_posted_occurrence_does_not_bring_it_back()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var clock = At(new DateOnly(2026, 3, 30));

        await using (var db = _fixture.NewServiceDbContext())
        {
            await NewHandler(ForwardWindowOnly)
                .RunAsync(db, ledger.LedgerId, ledger.UserId, clock, default);
        }

        Guid postedHeaderId;
        await using (var read = _fixture.NewServiceDbContext())
        {
            postedHeaderId = await read.TxnHeaders.AsNoTracking()
                .Where(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate)
                .Select(h => h.Id)
                .SingleAsync();
        }

        var delete = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{postedHeaderId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        // The next tick must NOT re-post it.
        await using (var db = _fixture.NewServiceDbContext())
        {
            var again = await NewHandler(ForwardWindowOnly).RunAsync(
                db, ledger.LedgerId, ledger.UserId, clock, default);
            Assert.Equal(JobRunResult.Ok, again.Result);
        }

        // COUNT ALONE CANNOT TELL THE TWO WORLDS APART, and asserting it did was a real
        // mistake caught by mutation: with the hard delete restored the header vanishes
        // (0) and is re-posted (1), while soft-hiding leaves the original in place (1).
        // Identical totals. So this asserts IDENTITY — the surviving row is the SAME
        // header, hidden, and no replacement was written.
        await using var final = _fixture.NewServiceDbContext();
        var survivors = await final.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate)
            .Select(h => new { h.Id, h.IsHidden })
            .ToListAsync();

        var survivor = Assert.Single(survivors);
        Assert.Equal(postedHeaderId, survivor.Id);
        Assert.True(
            survivor.IsHidden,
            "the occurrence header is present and NOT hidden, so the delete did not take "
            + "effect at all");
    }

    /// <summary>
    /// A run where every due occurrence fails THROWS — the only path to the five-strike
    /// auto-disable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that makes the job-type decision in ADR-0097 mean anything.
    /// <c>SchedulerRunner.ApplyOutcome</c> sets <c>consecutive_failures</c> to ZERO on a
    /// <c>Degraded</c> outcome, so a handler that reported failure as Degraded could
    /// never be switched off by its own failures however long it stayed broken.
    /// </para>
    /// <para>
    /// The failure is injected by holding the occurrence's advisory lock (mig 218) from
    /// the test's own connection while the handler runs with a one-second command
    /// timeout. Deterministic: the lock is held for the whole run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_run_where_every_occurrence_fails_throws_so_the_disable_is_reachable()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);

        await using var holder = new Npgsql.NpgsqlConnection(_fixture.ServiceConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var lockCmd = new Npgsql.NpgsqlCommand(
            "SELECT locked FROM reminder_occurrence_lock(@s, @d)", holder, holderTx))
        {
            lockCmd.Parameters.AddWithValue("s", seriesId);
            lockCmd.Parameters.AddWithValue("d", slot);
            await lockCmd.ExecuteScalarAsync();
        }

        await using var db = _fixture.NewServiceDbContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewHandler(ForwardWindowOnly, commandTimeoutSeconds: 1).RunAsync(
                db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default));

        Assert.Contains("posted nothing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// Occurrences older than the catch-up window are counted, not posted — and not
    /// touched.
    /// </summary>
    [Fact]
    public async Task Occurrences_older_than_the_catch_up_window_are_left_open_and_unposted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 0);

        await using var db = _fixture.NewServiceDbContext();

        // Today is well past several monthly slots, but the window reaches back one day,
        // so only an occurrence dated today could post — and none is.
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 1)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 4, 15)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));

        // Left OPEN, not swept: no exception rows were written for them either.
        await using var read = _fixture.NewServiceDbContext();
        Assert.Equal(
            0,
            await read.RecurringOccurrenceExceptions.AsNoTracking()
                .CountAsync(e => e.RecurringTransactionId == seriesId));
    }
}
