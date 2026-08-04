using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Reminders read surface (ADR-0047). <c>GET /reminders</c> lists
/// fully-materialized series (those with a template header) and excludes
/// dormant legacy rows (reshaped-but-not-yet-re-imported, no
/// template_header_id). Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RemindersEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public RemindersEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Get_reminders_lists_materialized_series_and_excludes_dormant_ones()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var templateId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            // A materialized series: a template header + its recurring row.
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = templateId,
                LedgerId = ledger.LedgerId,
                Origin = "manual",
                ExternalId = "mdreminder:t1",
                Payee = "Rent",
                Memo = "to landlord",
                PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),                IsRecurringTemplate = true,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                // external_id is GLOBALLY unique (mig 013) — scope to the ledger.
                ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                AutoCommitDaysBefore = 2,
                TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1),
                IsActive = true,
                Origin = "moneydance_import",
            });
            // A dormant legacy row (no template yet) — must NOT be listed.
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                ExternalId = $"rem-legacy-{ledger.LedgerId}",
                Rrule = null,
                TemplateHeaderId = null,
                StartDate = new DateOnly(2026, 1, 1),
                IsActive = true,
                Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/reminders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var list = await resp.Content.ReadFromJsonAsync<List<ReminderSummary>>();
        var summary = Assert.Single(list!);   // only the materialized series
        Assert.Equal("Rent", summary.Payee);
        Assert.Equal("to landlord", summary.Memo);
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=1", summary.Rrule);
        Assert.Equal(2, summary.AutoCommitDaysBefore);
        Assert.True(summary.IsActive);
        Assert.Equal(new DateOnly(2026, 1, 1), summary.StartDate);
        Assert.Equal("moneydance_import", summary.Origin);
    }

    [Fact]
    public async Task Fire_materializes_a_committed_occurrence_that_hits_balances_and_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = templateId, LedgerId = ledger.LedgerId, Origin = "manual",
                ExternalId = "mdreminder:t1", Payee = "Rent",
                PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsRecurringTemplate = true,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                AccountId = bank.Id, PostingIndex = 0, Amount = -1500m,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                AccountId = cat.Id, PostingIndex = 0, Amount = 1500m,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var occurrence = new DateOnly(2026, 3, 1);
        var fireResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire",
            new FireReminderRequest { OccurrenceDate = occurrence });
        Assert.Equal(HttpStatusCode.OK, fireResp.StatusCode);
        var fired = (await fireResp.Content.ReadFromJsonAsync<FireReminderResponse>())!;
        var committedId = fired.HeaderId;
        // Catch-up (ADR-0047 §9.2): firing Mar 1 out of order also marks the
        // earlier un-acted Jan 1 + Feb 1 skipped — reported on the response.
        Assert.Equal(2, fired.SkippedEarlierCount);
        Assert.Equal(new DateOnly(2026, 1, 1), fired.SkippedEarlierFrom);

        await using (var db = _fixture.NewDbContext())
        {
            // The committed occurrence is a LIVE header, stamped + dated.
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == committedId);
            Assert.False(header.IsRecurringTemplate);
            Assert.Equal(seriesId, header.RecurringTransactionId);
            Assert.Equal(occurrence, header.OccurrenceDate);
            Assert.Equal(occurrence, DateOnly.FromDateTime(header.PostedAt));

            // Legs cloned.
            Assert.Equal(2, await db.TxnLegs.AsNoTracking().CountAsync(l => l.HeaderId == committedId));

            // KEYSTONE inverse: the fired occurrence DOES get a balance row
            // (it's live now), unlike its template.
            var bal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(b => b.HeaderId == committedId && b.AccountId == bank.Id);
            Assert.Equal(-1500m, bal.BalanceAfter);
            Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(b => b.HeaderId == templateId));   // template still has none

            // Catch-up cursor (ADR-0047 §9.2): firing Mar 1 marked the earlier
            // un-acted Jan 1 + Feb 1 skipped, so the cursor advances to the first
            // occurrence AFTER the acted slot — Apr 1 — rather than stranding the
            // overdue Jan/Feb. The two cascade exception rows exist.
            var series = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId);
            Assert.Equal(new DateOnly(2026, 4, 1), series.NextDueDate);
            var cascadeSkips = await db.RecurringOccurrenceExceptions.AsNoTracking()
                .Where(e => e.RecurringTransactionId == seriesId)
                .Select(e => e.OccurrenceDate).OrderBy(d => d).ToListAsync();
            Assert.Equal(new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1) }, cascadeSkips);
        }

        // Idempotent: firing the same occurrence again returns the same header.
        var fireAgain = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire",
            new FireReminderRequest { OccurrenceDate = occurrence });
        Assert.Equal(HttpStatusCode.OK, fireAgain.StatusCode);
        Assert.Equal(committedId, (await fireAgain.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1, await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate));
        }
    }

    [Fact]
    public async Task Upcoming_mixes_fired_scheduled_with_unfired_reminder_slots_in_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var fired = new DateOnly(2026, 3, 1);
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = templateId, LedgerId = ledger.LedgerId, Origin = "manual",
                ExternalId = $"mdreminder:{ledger.LedgerId}", Payee = "Rent",
                PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsRecurringTemplate = true,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "moneydance_import",
            });
            // A committed occurrence (as if fired) for 2026-03-01.
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Origin = "manual",
                Payee = "Rent",
                PostedAt = fired.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                IsRecurringTemplate = false,
                RecurringTransactionId = seriesId, OccurrenceDate = fired,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var list = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-02-01&to=2026-05-31");

        // Monthly-on-the-1st across [Feb 1, May 31]: Feb/Apr/May un-fired, Mar fired.
        Assert.Equal(
            new[]
            {
                (new DateOnly(2026, 2, 1), "reminder"),
                (new DateOnly(2026, 3, 1), "scheduled"),
                (new DateOnly(2026, 4, 1), "reminder"),
                (new DateOnly(2026, 5, 1), "reminder"),
            },
            list!.Select(x => (x.Date, x.Kind)).ToArray());

        var marchScheduled = Assert.Single(list!, x => x.Date == fired);
        Assert.NotNull(marchScheduled.HeaderId);            // the materialized header
        Assert.All(list!.Where(x => x.Kind == "reminder"), x => Assert.Null(x.HeaderId));
    }
}
