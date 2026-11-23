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
public sealed class ReminderAutoPostJobHandlerTests : DatabaseTest
{
    public ReminderAutoPostJobHandlerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly SeriesStart = new(2026, 1, 1);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A monthly bank reminder, optionally asking to be auto-posted.</summary>
    private static async Task<Guid> NewSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId, int? acdays,
        string? payee = "Rent", string rrule = "FREQ=MONTHLY;BYMONTHDAY=1", int? estimateSampleCount = null,
        decimal? counterpartyAmount = null)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = rrule,
                StartDate = SeriesStart,
                AutoCommitDaysBefore = acdays,
                Payee = payee,
                SourceAccountId = bankId,
                EstimateSampleCount = estimateSampleCount,
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        CounterpartyAccountId = catId, Amount = -1500m, CounterpartyAmount = counterpartyAmount,
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    /// <summary>
    /// A reminder asking to be auto-posted whose template has no lines, as an imported
    /// Moneydance reminder with no field values leaves it. No API path builds one.
    /// </summary>
    private async Task<Guid> NewLinelessSeriesAsync(
        SyntheticLedger ledger, string payee, string rrule, int acdays)
    {
        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        await using var db = Db.NewDbContext();
        await TxnHeaderSeed.InsertLeglessHeaderAsync(db, templateId, ledger.LedgerId,
            $"mdreminder:{seriesId:N}", payee, null,
            SeriesStart.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), isTemplate: true);
        db.RecurringTransactions.Add(new Coffer.Api.Db.Entities.RecurringTransactionRow
        {
            Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{seriesId:N}",
            Rrule = rrule, TemplateHeaderId = templateId, StartDate = SeriesStart,
            AutoCommitDaysBefore = acdays, IsActive = true, Origin = "moneydance_import",
        });
        await db.SaveChangesAsync();
        return seriesId;
    }

    private async Task<Coffer.Api.Db.Entities.LedgerEventRow> SingleBacklogWarningAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return Assert.Single(await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && e.EventKey == "reminder-backlog.deferred")
            .ToListAsync());
    }

    private ReminderAutoPostJobHandler NewHandler(
        ReminderAutoPostLimits? limits = null,
        INotificationSubscriber? spy = null,
        int? commandTimeoutSeconds = null)
    {
        var factory = commandTimeoutSeconds is null
            ? Db.NewServiceFactory()
            : PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
            {
                ConnectionString = Db.AppConnectionString,
                // A short command timeout turns "blocked on a lock somebody else holds"
                // into a deterministic, fast failure — the only fault injection available
                // for a handler that builds its own repositories internally.
                ServiceConnectionString =
                    $"{Db.ServiceConnectionString};Command Timeout={commandTimeoutSeconds}",
            }));

        return new ReminderAutoPostJobHandler(
            factory,
            new RecurrenceExpander(),
            new NotificationPublisher(
                Db.NewServiceFactory(),
                Db.NewLedgerKeyService(),
                spy is null ? Array.Empty<INotificationSubscriber>() : new[] { spy },
                NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
            NullLogger<ReminderAutoPostJobHandler>.Instance,
            TimeProvider.System,
            limits);
    }

    private static JobRunClock At(DateOnly today) =>
        new(today.ToDateTime(new TimeOnly(5, 0), DateTimeKind.Utc), "UTC");

    /// <summary>
    /// Limits with catch-up switched OFF, so exactly one occurrence is in play.
    /// </summary>
    /// <remarks>
    /// A series starting 2026-01-01 has un-acted January, February and March slots, and
    /// the default 45-day catch-up window posts the recent ones — correctly. A test
    /// that assumes a single candidate would fail against that backlog. Pinning
    /// CatchUpDays to 0 isolates the acdays window, which is what those tests are
    /// actually about; the catch-up window has its own tests.
    /// </remarks>
    private static ReminderAutoPostLimits ForwardWindowOnly => new(CatchUpDays: 0);

    private async Task<int> CommittedOccurrenceCountAsync(Guid seriesId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate);
    }

    /// <summary>
    /// The core case: an occurrence inside its series' acdays window gets posted.
    /// </summary>
    [Fact]
    public async Task An_occurrence_within_its_window_is_posted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        await using var db = Db.NewServiceDbContext();

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        await using var db = Db.NewServiceDbContext();

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: null);

        await using var db = Db.NewServiceDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/skip",
            new SkipReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        await using var db = Db.NewServiceDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Accepted by the API: validation only refuses a negative.
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 500);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(
                CatchUpDays: 0, MaxAutoCommitDaysBefore: 90)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 1, 5)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);

        await using var read = Db.NewServiceDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/skip",
            new SkipReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        await using var db = Db.NewServiceDbContext();
        var repo = new Coffer.Api.Db.Repositories.RemindersRepository(
            db,
            new RecurrenceExpander(),
            new Coffer.Api.Db.Repositories.InvestmentTransactionsRepository(db, TimeProvider.System),
            new Coffer.Api.Db.Repositories.TransactionsRepository(db, TimeProvider.System),
            TimeProvider.System);

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
        var a = await SyntheticLedger.CreateAsync(Db);
        var aBank = await a.AddBankAccountAsync("a-checking");
        var aCat = await a.AddCategoryAsync("a-rent");

        var b = await SyntheticLedger.CreateAsync(Db);
        var bBank = await b.AddBankAccountAsync("b-checking");
        var bCat = await b.AddCategoryAsync("b-rent");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var clientA = await AuthedClientAsync(factory, a);
        using var clientB = await AuthedClientAsync(factory, b);

        var seriesA = await NewSeriesAsync(clientA, a, aBank.Id, aCat.Id, acdays: 3);
        var seriesB = await NewSeriesAsync(clientB, b, bBank.Id, bCat.Id, acdays: 3);

        await using var db = Db.NewServiceDbContext();

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
        var a = await SyntheticLedger.CreateAsync(Db);
        var aBank = await a.AddBankAccountAsync("a-checking");
        var aCat = await a.AddCategoryAsync("a-rent");

        var b = await SyntheticLedger.CreateAsync(Db);
        var bBank = await b.AddBankAccountAsync("b-checking");
        var bCat = await b.AddCategoryAsync("b-rent");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var clientA = await AuthedClientAsync(factory, a);
        using var clientB = await AuthedClientAsync(factory, b);

        var seriesA = await NewSeriesAsync(clientA, a, aBank.Id, aCat.Id, acdays: 3);
        var seriesB = await NewSeriesAsync(clientB, b, bBank.Id, bCat.Id, acdays: 3);

        await using var db = Db.NewServiceDbContext();
        var repo = new Coffer.Api.Db.Repositories.RemindersRepository(
            db,
            new RecurrenceExpander(),
            new Coffer.Api.Db.Repositories.InvestmentTransactionsRepository(db, TimeProvider.System),
            new Coffer.Api.Db.Repositories.TransactionsRepository(db, TimeProvider.System),
            TimeProvider.System);

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 0);

        await using var db = Db.NewServiceDbContext();

        // 2026-04-01 is due; Jan/Feb/Mar are earlier and un-acted. A catch-up window of
        // one day means only April is postable, so the earlier three must survive
        // untouched rather than being swept.
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 1)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 4, 1)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);

        await using var read = Db.NewServiceDbContext();
        var exceptions = await read.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == seriesId);
        Assert.Equal(0, exceptions);
    }

    /// <summary>
    /// Deleting an auto-posted transaction does not cause it to be posted again.
    /// </summary>
    /// <remarks>
    /// The loop this closes: the (series, date) stamp on the committed header IS the
    /// slot's idempotency key, and every fired occurrence lacks an <c>external_id</c>.
    /// A <c>DeleteAsync</c> that HARD-deleted such headers would take the stamp with
    /// the row, the slot would read un-acted, and the next tick would post it back —
    /// delete again, it returns. Soft-hiding keeps the stamp, so the slot stays consumed.
    /// </remarks>
    [Fact]
    public async Task Deleting_an_auto_posted_occurrence_does_not_bring_it_back()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var clock = At(new DateOnly(2026, 3, 30));

        await using (var db = Db.NewServiceDbContext())
        {
            await NewHandler(ForwardWindowOnly)
                .RunAsync(db, ledger.LedgerId, ledger.UserId, clock, default);
        }

        Guid postedHeaderId;
        await using (var read = Db.NewServiceDbContext())
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
        await using (var db = Db.NewServiceDbContext())
        {
            var again = await NewHandler(ForwardWindowOnly).RunAsync(
                db, ledger.LedgerId, ledger.UserId, clock, default);
            Assert.Equal(JobRunResult.Ok, again.Result);
        }

        // COUNT ALONE CANNOT TELL THE TWO WORLDS APART: with a hard delete the header
        // vanishes (0) and is re-posted (1), while soft-hiding leaves the original in place (1).
        // Identical totals. So this asserts IDENTITY — the surviving row is the SAME
        // header, hidden, and no replacement was written.
        await using var final = Db.NewServiceDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);

        var slot = new DateOnly(2026, 4, 1);

        await using var holder = new Npgsql.NpgsqlConnection(Db.ServiceConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var lockCmd = new Npgsql.NpgsqlCommand(
            "SELECT locked FROM reminder_occurrence_lock(@s, @d)", holder, holderTx))
        {
            lockCmd.Parameters.AddWithValue("s", seriesId);
            lockCmd.Parameters.AddWithValue("d", slot);
            await lockCmd.ExecuteScalarAsync();
        }

        await using var db = Db.NewServiceDbContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewHandler(ForwardWindowOnly, commandTimeoutSeconds: 1).RunAsync(
                db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default));

        Assert.Contains("posted nothing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// A reminder whose template has no lines cannot be posted. The run skips it, says so
    /// by name, and is not a failed run.
    /// </summary>
    /// <remarks>
    /// Firing it is refused, and a refusal on every run would otherwise either fail the
    /// job each time or vanish into the log. The user is told which reminder, so they can
    /// give it its lines or delete it.
    /// </remarks>
    [Fact]
    public async Task A_reminder_with_no_lines_is_skipped_and_announced_rather_than_failing_the_run()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seriesId = await NewLinelessSeriesAsync(
            ledger, "Payee A", "FREQ=MONTHLY;BYMONTHDAY=1", acdays: 3);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));

        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        Assert.Contains("Payee A", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("no lines", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("delete it", warning.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(seriesId.ToString(), warning.Summary, StringComparison.OrdinalIgnoreCase);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("1", detail.RootElement.GetProperty("template_has_no_lines").GetString());
    }

    /// <summary>
    /// A reminder with no lines does not take a slot in the per-run cap from one that can
    /// be posted.
    /// </summary>
    [Fact]
    public async Task A_reminder_with_no_lines_does_not_consume_the_per_run_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var postable = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);
        // Due on the 31st, so it sorts ahead of the postable series' slot on the 1st.
        var lineless = await NewLinelessSeriesAsync(
            ledger, "Payee A", "FREQ=MONTHLY;BYMONTHDAY=31", acdays: 3);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 0, MaxPostsPerRun: 1))
            .RunAsync(db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(lineless));
        Assert.Equal(1, await CommittedOccurrenceCountAsync(postable));
    }

    /// <summary>Retire an account through the endpoint a user uses.</summary>
    private static async Task DeactivateAsync(HttpClient client, Guid ledgerId, Guid accountId)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/accounts/{accountId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    /// <summary>
    /// A reminder whose template uses an inactive account is not posted. The run sets it
    /// aside, names it, and is not a failed run.
    /// </summary>
    /// <remarks>
    /// Posting it would put a new transaction on an account the user retired, which every
    /// by-hand post refuses. Only the user can resolve it, by reactivating the account or
    /// editing the reminder, so the notice names the reminder rather than only counting.
    /// </remarks>
    [Fact]
    public async Task A_reminder_using_an_inactive_account_is_skipped_and_announced_rather_than_failing_the_run()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var retired = await ledger.AddCategoryAsync("category-a");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var named = await NewSeriesAsync(client, ledger, bank.Id, retired.Id, acdays: 3, payee: "Payee A");
        // No payee: named as the reminders list names it.
        var untitled = await NewSeriesAsync(client, ledger, bank.Id, retired.Id, acdays: 3, payee: null);
        await DeactivateAsync(client, ledger.LedgerId, retired.Id);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(named));
        Assert.Equal(0, await CommittedOccurrenceCountAsync(untitled));

        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        Assert.Contains("\"Payee A\"", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("\"Untitled reminder\"", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("inactive account", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("reactivate the account", warning.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edit", warning.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(named.ToString(), warning.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no lines", warning.Summary, StringComparison.Ordinal);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("2", detail.RootElement.GetProperty("template_uses_inactive_account").GetString());
        Assert.Equal("0", detail.RootElement.GetProperty("template_has_no_lines").GetString());
    }

    /// <summary>
    /// A reminder using an inactive account does not take a slot in the per-run cap from
    /// one that can be posted.
    /// </summary>
    [Fact]
    public async Task A_reminder_using_an_inactive_account_does_not_consume_the_per_run_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var live = await ledger.AddCategoryAsync("category-a");
        var retiredSource = await ledger.AddBankAccountAsync("account-b");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var postable = await NewSeriesAsync(client, ledger, bank.Id, live.Id, acdays: 3);
        // Due on the 31st, so it sorts ahead of the postable series' slot on the 1st; its
        // SOURCE is the account retired.
        var retiredSeries = await NewSeriesAsync(
            client, ledger, retiredSource.Id, live.Id, acdays: 3, payee: "Payee B",
            rrule: "FREQ=MONTHLY;BYMONTHDAY=31");
        await DeactivateAsync(client, ledger.LedgerId, retiredSource.Id);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 0, MaxPostsPerRun: 1))
            .RunAsync(db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(retiredSeries));
        Assert.Equal(1, await CommittedOccurrenceCountAsync(postable));
    }

    /// <summary>
    /// A reminder whose template line has more places than its currency uses is set aside
    /// before the per-run cap, counted under its own key and named in the summary, and the
    /// agenda says so by the job's own rule (ADR-0114 D15).
    /// </summary>
    /// <remarks>
    /// The fire refuses such a template, so trying it would spend the cap's slot run after
    /// run on a series that can never post. The template line is seeded by rewriting a
    /// created template's two legs, as the released writers, which held every currency to
    /// two places, could store it.
    /// </remarks>
    [Fact]
    public async Task A_reminder_whose_template_amount_does_not_fit_its_currency_does_not_consume_the_per_run_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-a");
        var wholeUnit = await ledger.AddBankAccountAsync("account-b", currencyCode: "JPY");
        var cat = await ledger.AddCategoryAsync("category-a");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var postable = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);
        // Due on the 31st, so it sorts ahead of the postable series' slot on the 1st.
        var unfit = await NewSeriesAsync(
            client, ledger, wholeUnit.Id, cat.Id, acdays: 3, payee: "Payee B",
            rrule: "FREQ=MONTHLY;BYMONTHDAY=31");
        await SeedUnfitTemplateAsync(unfit, wholeUnit.Id, cat.Id);

        var upcoming = (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-03-31&to=2026-04-01"))!;
        Assert.Equal(AutoPostExclusion.TemplateAmountDoesNotFit,
            Assert.Single(upcoming, o => o.ReminderId == unfit).AutoPost!.ExcludedReason);
        Assert.Null(Assert.Single(upcoming, o => o.ReminderId == postable).AutoPost!.ExcludedReason);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 0, MaxPostsPerRun: 1))
            .RunAsync(db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        // Set aside like every other series that can never post: not the run's health.
        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(unfit));
        Assert.Equal(1, await CommittedOccurrenceCountAsync(postable));

        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("1", detail.RootElement.GetProperty("template_amount_does_not_fit").GetString());
        Assert.Equal("0", detail.RootElement.GetProperty("over_per_run_cap").GetString());
        // Named, as the other reasons only the user can fix are, after the opening sentence.
        Assert.Contains(
            "on the calendar. 1 reminder(s) cannot be posted because a figure has more decimal places than "
            + "its currency uses (\"Payee B\"). Edit each reminder, or its loan's terms, so every figure fits "
            + "its currency.",
            warning.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(unfit.ToString(), warning.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Give a created template, whose two legs are on <paramref name="wholeUnitId"/> and
    /// <paramref name="catId"/>, a figure finer than the whole-unit currency, as the released
    /// writers, which held every currency to two places, could store it.
    /// </summary>
    private async Task SeedUnfitTemplateAsync(Guid seriesId, Guid wholeUnitId, Guid catId)
    {
        await using var seed = Db.NewServiceDbContext();
        var templateId = (await seed.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId))
            .TemplateHeaderId!.Value;
        await seed.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == wholeUnitId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, -1500.5m));
        await seed.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == catId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, 1500.5m));
    }

    /// <summary>
    /// The reminders held back because a figure does not fit are named last, after the
    /// other named reasons, so the loan-across-currencies sentence, itself after the no-lines
    /// and inactive-account ones, comes first (ADR-0114 D15, placement settled by Claude).
    /// </summary>
    [Fact]
    public async Task The_reminders_whose_figure_does_not_fit_are_named_after_the_other_named_reasons()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var paying = await ledger.AddBankAccountAsync("account-b");
        var wholeUnit = await ledger.AddBankAccountAsync("account-c", currencyCode: "JPY");
        var cat = await ledger.AddCategoryAsync("category-a");
        var interest = await ledger.AddCategoryAsync("category-b");
        var escrow = await ledger.AddCategoryAsync("category-c");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var unfit = await NewSeriesAsync(
            client, ledger, wholeUnit.Id, cat.Id, acdays: 3, payee: "Payee B",
            rrule: "FREQ=MONTHLY;BYMONTHDAY=31");
        await SeedUnfitTemplateAsync(unfit, wholeUnit.Id, cat.Id);
        await NewLoanPaymentAcrossCurrenciesAsync(client, ledger, paying.Id, interest.Id, escrow.Id);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        Assert.EndsWith(
            "Use accounts in the loan's currency, or enter each payment as a transfer with both amounts. "
            + "1 reminder(s) cannot be posted because a figure has more decimal places than its currency uses "
            + "(\"Payee B\"). Edit each reminder, or its loan's terms, so every figure fits its currency.",
            warning.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A managed loan series whose paying account was moved to another currency than the
    /// loan's after setup can never post, so it is set aside before the per-run cap, counted
    /// under its own key and named in the summary, and the agenda says so by the job's own
    /// rule (ADR-0114 D13, ADR-0097).
    /// </summary>
    /// <remarks>
    /// Every step is an API write: the loan and its terms, the managed payment's setup, its
    /// auto-post days, and the paying account's currency edit. The fire refuses such a
    /// series, so trying it would spend the cap's slot run after run.
    /// </remarks>
    [Fact]
    public async Task A_loan_payment_across_currencies_is_set_aside_before_the_per_run_cap_and_named()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-a");
        var paying = await ledger.AddBankAccountAsync("account-b");
        var cat = await ledger.AddCategoryAsync("category-a");
        var interest = await ledger.AddCategoryAsync("category-b");
        var escrow = await ledger.AddCategoryAsync("category-c");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var postable = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3);
        // Due on the 31st, so it sorts ahead of the postable series' slot on the 1st.
        var loanSeries = await NewLoanPaymentAcrossCurrenciesAsync(
            client, ledger, paying.Id, interest.Id, escrow.Id);

        var upcoming = (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-03-31&to=2026-04-01"))!;
        Assert.Equal(AutoPostExclusion.LoanAcrossCurrencies,
            Assert.Single(upcoming, o => o.ReminderId == loanSeries).AutoPost!.ExcludedReason);
        Assert.Null(Assert.Single(upcoming, o => o.ReminderId == postable).AutoPost!.ExcludedReason);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 0, MaxPostsPerRun: 1))
            .RunAsync(db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        // Set aside like every other series that can never post: not the run's health.
        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(loanSeries));
        Assert.Equal(1, await CommittedOccurrenceCountAsync(postable));

        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("1", detail.RootElement.GetProperty("managed_loan_across_currencies").GetString());
        Assert.Equal("0", detail.RootElement.GetProperty("over_per_run_cap").GetString());
        Assert.Contains("\"Loan payment\"", warning.Summary, StringComparison.Ordinal);
        Assert.Contains("another currency than their loan's", warning.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loan in USD whose managed payment, due on the 31st and auto-posted three days
    /// before, is paid from <paramref name="payingId"/>, then that account moved to EUR, so
    /// the payment would cross currencies. Every step is an API write.
    /// </summary>
    private static async Task<Guid> NewLoanPaymentAcrossCurrenciesAsync(
        HttpClient client, SyntheticLedger ledger, Guid payingId, Guid interestId, Guid escrowId)
    {
        var loan = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = "Account L",
            AccountType = "loan",
            CurrencyCode = "USD",
            OpeningBalance = -100_000m,
            LoanTerms = new LoanTermsDto
            {
                OriginalPrincipal = 100_000m, AnnualInterestRate = 4.00m, PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 100m, InterestAccountId = interestId, EscrowAccountId = escrowId,
                PaymentIsComputed = true,
            },
        });
        Assert.True(loan.StatusCode == HttpStatusCode.Created, await loan.Content.ReadAsStringAsync());
        var loanId = (await loan.Content.ReadFromJsonAsync<AccountSummary>())!.Id;
        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(payingId, new DateOnly(2026, 1, 31)));
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        var loanSeries = (await setup.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("reminderId").GetGuid();
        var acdays = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/reminders/{loanSeries}")
        {
            Content = JsonContent.Create(new EditReminderRequest { AutoCommitDaysBefore = 3 }),
        });
        Assert.True(acdays.IsSuccessStatusCode, await acdays.Content.ReadAsStringAsync());
        var edit = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{payingId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = "EUR" }),
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        return loanSeries;
    }

    /// <summary>
    /// The fire's backstop for a managed loan payment across currencies, reached only by a
    /// series whose account changed currency between the scan and the fire, leaves the run
    /// not Ok, as the places backstop does (ADR-0114 D13).
    /// </summary>
    [Fact]
    public void A_run_whose_fire_was_refused_for_a_loan_payment_across_currencies_is_not_reported_ok()
    {
        var nothingPosted = ReminderAutoPostJobHandler.SettledOutcome(
            posted: 0, refusedBusy: 0, refusedUnfit: 0, refusedLoanAcross: 1, due: 1);
        Assert.Equal(JobRunResult.Degraded, nothingPosted.Result);
        Assert.True(nothingPosted.NothingDone);
        Assert.Contains("another currency than the loan's", nothingPosted.Message, StringComparison.Ordinal);

        var somePosted = ReminderAutoPostJobHandler.SettledOutcome(
            posted: 1, refusedBusy: 0, refusedUnfit: 0, refusedLoanAcross: 1, due: 2);
        Assert.Equal(JobRunResult.Degraded, somePosted.Result);
        Assert.False(somePosted.NothingDone);
    }

    /// <summary>
    /// A run whose only refusal is the fire's backstop for a figure that does not fit its
    /// currency is not Ok: nothing posted is DidNothing, which leaves last_success_at where
    /// it was, and some posted is Degraded. Neither is a throw toward the auto-disable.
    /// </summary>
    /// <remarks>
    /// The scan sets such a series aside by the fire's own predicate, so the backstop is
    /// reached only by a series that changed between the scan and the fire; the outcome is
    /// pinned on its own for that reason.
    /// </remarks>
    [Fact]
    public void A_run_whose_fires_were_refused_for_a_figure_that_does_not_fit_is_not_reported_ok()
    {
        var nothingPosted = ReminderAutoPostJobHandler.SettledOutcome(
            posted: 0, refusedBusy: 0, refusedUnfit: 1, refusedLoanAcross: 0, due: 1);
        Assert.Equal(JobRunResult.Degraded, nothingPosted.Result);
        Assert.True(nothingPosted.NothingDone);
        Assert.Contains("decimal places", nothingPosted.Message, StringComparison.Ordinal);

        var somePosted = ReminderAutoPostJobHandler.SettledOutcome(
            posted: 1, refusedBusy: 0, refusedUnfit: 1, refusedLoanAcross: 0, due: 2);
        Assert.Equal(JobRunResult.Degraded, somePosted.Result);
        Assert.False(somePosted.NothingDone);

        // The anchor: a run with no refusal is Ok.
        Assert.Equal(JobRunResult.Ok, ReminderAutoPostJobHandler.SettledOutcome(1, 0, 0, 0, 1).Result);
    }

    /// <summary>The auto-post scan, over a service context of its own as the job's is.</summary>
    private async Task<AutoPostScanResult> ScanAsync(Guid ledgerId, DateOnly today)
    {
        await using var db = Db.NewServiceFactory().Create();
        var repository = new Coffer.Api.Db.Repositories.RemindersRepository(
            db, new RecurrenceExpander(), new Coffer.Api.Db.Repositories.InvestmentTransactionsRepository(db, TimeProvider.System),
            new Coffer.Api.Db.Repositories.TransactionsRepository(db, TimeProvider.System),
            TimeProvider.System);
        return await repository.ScanForAutoPostAsync(
            ledgerId, today, ForwardWindowOnly.CatchUpDays, ReminderAutoPostLimits.Default.MaxAutoCommitDaysBefore);
    }

    /// <summary>
    /// An estimating reminder with no history yet has no estimate, so its fire is refused:
    /// the run counts it under <c>estimate_unavailable</c>, it stays due for the next run,
    /// and the run is not a failure (ADR-0097, ADR-0114 D13).
    /// </summary>
    [Fact]
    public async Task An_estimate_with_no_history_is_counted_as_unavailable_and_stays_due()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-a");
        var cat = await ledger.AddCategoryAsync("category-a");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3, estimateSampleCount: 3);
        var today = new DateOnly(2026, 3, 30);

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(db, ledger.LedgerId, ledger.UserId, At(today), default);

        // Not a failure, so never a step toward the auto-disable.
        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("1", detail.RootElement.GetProperty("estimate_unavailable").GetString());
        Assert.Contains("1 reminder occurrence(s) were not posted", warning.Summary, StringComparison.Ordinal);

        // Still due on the next scan.
        var next = await ScanAsync(ledger.LedgerId, today);
        Assert.Contains(next.Due, c => c.ReminderId == seriesId && c.OccurrenceDate == new DateOnly(2026, 4, 1));
    }

    /// <summary>
    /// An estimating transfer between currencies whose due date has no derived rate has no
    /// estimate: its fire is refused, nothing is written, and the run counts it under
    /// <c>estimate_unavailable</c> (ADR-0097, ADR-0114 D13).
    /// </summary>
    [Fact]
    public async Task An_estimate_between_currencies_with_no_rate_for_its_due_date_is_counted_as_unavailable()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "USD");
        var bank = await ledger.AddBankAccountAsync("account-a", currencyCode: "USD");
        var other = await ledger.AddBankAccountAsync("account-b", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, other.Id, acdays: 3, counterpartyAmount: 1400m);
        // A history to estimate from, posted by hand with both amounts, then the estimate.
        foreach (var slot in new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1) })
        {
            var fired = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
                new FireBankReminderRequest
                {
                    OccurrenceDate = slot,
                    SourceAccountId = bank.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = other.Id, Amount = -1500m, CounterpartyAmount = 1400m }],
                });
            Assert.True(fired.IsSuccessStatusCode, await fired.Content.ReadAsStringAsync());
        }
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}", new EditReminderRequest { EstimateSampleCount = 2 });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        await using var db = Db.NewServiceDbContext();
        var outcome = await NewHandler(ForwardWindowOnly).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(2, await CommittedOccurrenceCountAsync(seriesId));
        var warning = await SingleBacklogWarningAsync(ledger.LedgerId);
        using var detail = System.Text.Json.JsonDocument.Parse(warning.DetailJson);
        Assert.Equal("1", detail.RootElement.GetProperty("estimate_unavailable").GetString());
    }

    /// <summary>
    /// The anchor: an occurrence skipped by hand between the scan and the fire is a person
    /// acting on the slot, not an estimate that is unavailable, so it is not counted under
    /// <c>estimate_unavailable</c>, though its series estimates with no history.
    /// </summary>
    /// <remarks>
    /// The skip lands between the two deterministically: a second session holds the slot's
    /// lock and writes the skip in its open transaction, which the scan cannot see; the
    /// fire then waits on the lock, and only once it is seen waiting is the skip committed.
    /// The skip row is the one the skip route writes for the slot.
    /// </remarks>
    [Fact]
    public async Task An_occurrence_skipped_between_the_scan_and_the_fire_is_not_counted_as_an_unavailable_estimate()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-a");
        var cat = await ledger.AddCategoryAsync("category-a");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3, estimateSampleCount: 3);
        var slot = new DateOnly(2026, 4, 1);

        await using var holder = new Npgsql.NpgsqlConnection(Db.ServiceConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var lockCmd = new Npgsql.NpgsqlCommand(
            "SELECT locked FROM reminder_occurrence_lock(@s, @d)", holder, holderTx))
        {
            lockCmd.Parameters.AddWithValue("s", seriesId);
            lockCmd.Parameters.AddWithValue("d", slot);
            await lockCmd.ExecuteScalarAsync();
        }
        await using (var skipCmd = new Npgsql.NpgsqlCommand(
            "INSERT INTO recurring_occurrence_exceptions (id, ledger_id, recurring_transaction_id, occurrence_date, created_by_user_id) "
            + "VALUES (@id, @l, @s, @d, @u)", holder, holderTx))
        {
            skipCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            skipCmd.Parameters.AddWithValue("l", ledger.LedgerId);
            skipCmd.Parameters.AddWithValue("s", seriesId);
            skipCmd.Parameters.AddWithValue("d", slot);
            skipCmd.Parameters.AddWithValue("u", ledger.UserId);
            await skipCmd.ExecuteNonQueryAsync();
        }

        await using var db = Db.NewServiceDbContext();
        var run = NewHandler(ForwardWindowOnly).RunAsync(db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);

        // The fire is waiting on the slot's lock, so the scan has already run.
        await using (var watcher = new Npgsql.NpgsqlConnection(Db.ServiceConnectionString))
        {
            await watcher.OpenAsync();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Assert.False(run.IsCompleted, "the run finished before its fire waited on the slot's lock");
                await using var probe = new Npgsql.NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted", watcher);
                if ((long)(await probe.ExecuteScalarAsync())! > 0) break;
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), "the fire never waited on the slot's lock");
                await Task.Delay(50);
            }
        }
        await holderTx.CommitAsync();

        var outcome = await run;

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
        // Nothing was left behind for the backlog to count: no warning of an unavailable
        // estimate, or of anything else.
        await using var read = Db.NewServiceDbContext();
        Assert.False(await read.LedgerEvents.AsNoTracking()
            .AnyAsync(e => e.LedgerId == ledger.LedgerId && e.EventKey == "reminder-backlog.deferred"));
    }

    /// <summary>
    /// The agenda says, per occurrence, whether the job will post it and from which
    /// account, by the job's own predicate: what it says is what the job then does.
    /// </summary>
    [Fact]
    public async Task The_agenda_says_whether_and_from_where_each_occurrence_posts_by_the_jobs_own_rule()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-a");
        var cat = await ledger.AddCategoryAsync("category-a");
        var retiredSource = await ledger.AddBankAccountAsync("account-b");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postable = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 3, payee: "Payee A");
        var manual = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: null, payee: "Payee B");
        var retired = await NewSeriesAsync(client, ledger, retiredSource.Id, cat.Id, acdays: 3, payee: "Payee C");
        var lineless = await NewLinelessSeriesAsync(ledger, "Payee D", "FREQ=MONTHLY;BYMONTHDAY=1", acdays: 3);
        await DeactivateAsync(client, ledger.LedgerId, retiredSource.Id);

        await using (var db = Db.NewServiceDbContext())
        {
            await NewHandler(ForwardWindowOnly).RunAsync(
                db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 3, 30)), default);
        }

        var upcoming = (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-04-01&to=2026-04-01"))!
            .ToDictionary(o => o.ReminderId);

        // Posted by the job, so a committed occurrence, carrying its source as well.
        var posted = upcoming[postable];
        Assert.Equal("scheduled", posted.Kind);
        Assert.Equal(bank.Id, posted.SourceAccountId);
        Assert.Equal("account-a", posted.SourceAccountName);
        Assert.Equal("USD", posted.CurrencyCode);
        Assert.Null(Assert.IsType<UpcomingAutoPost>(posted.AutoPost).ExcludedReason);

        // Never asked to be auto-posted.
        Assert.Equal("reminder", upcoming[manual].Kind);
        Assert.Null(upcoming[manual].AutoPost);
        Assert.Equal(bank.Id, upcoming[manual].SourceAccountId);

        // Left for a person, each with the job's reason; the retired source keeps its
        // name and currency.
        Assert.Equal("reminder", upcoming[retired].Kind);
        Assert.Equal(AutoPostExclusion.InactiveAccount, upcoming[retired].AutoPost!.ExcludedReason);
        Assert.Equal("account-b", upcoming[retired].SourceAccountName);
        Assert.Equal("USD", upcoming[retired].CurrencyCode);

        Assert.Equal(AutoPostExclusion.NoLines, upcoming[lineless].AutoPost!.ExcludedReason);
        Assert.Null(upcoming[lineless].SourceAccountId);
        Assert.Null(upcoming[lineless].SourceAccountName);
        Assert.Null(upcoming[lineless].CurrencyCode);

        // And the job did exactly what the agenda says.
        Assert.Equal(0, await CommittedOccurrenceCountAsync(retired));
        Assert.Equal(0, await CommittedOccurrenceCountAsync(lineless));
        Assert.Equal(0, await CommittedOccurrenceCountAsync(manual));
    }

    /// <summary>
    /// Occurrences older than the catch-up window are counted, not posted — and not
    /// touched.
    /// </summary>
    [Fact]
    public async Task Occurrences_older_than_the_catch_up_window_are_left_open_and_unposted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id, acdays: 0);

        await using var db = Db.NewServiceDbContext();

        // Today is well past several monthly slots, but the window reaches back one day,
        // so only an occurrence dated today could post — and none is.
        var outcome = await NewHandler(new ReminderAutoPostLimits(CatchUpDays: 1)).RunAsync(
            db, ledger.LedgerId, ledger.UserId, At(new DateOnly(2026, 4, 15)), default);

        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));

        // Left OPEN, not swept: no exception rows were written for them either.
        await using var read = Db.NewServiceDbContext();
        Assert.Equal(
            0,
            await read.RecurringOccurrenceExceptions.AsNoTracking()
                .CountAsync(e => e.RecurringTransactionId == seriesId));
    }
}
