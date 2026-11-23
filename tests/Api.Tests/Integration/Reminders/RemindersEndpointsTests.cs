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
public sealed class RemindersEndpointsTests : DatabaseTest
{
    public RemindersEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static Task InsertLeglessHeaderAsync(
        Coffer.Api.Db.AppDbContext db, Guid id, Guid ledgerId, string? externalId, string payee,
        string? memo, DateTime postedAt, bool isTemplate, Guid? seriesId = null, DateOnly? occurrenceDate = null) =>
        TxnHeaderSeed.InsertLeglessHeaderAsync(
            db, id, ledgerId, externalId, payee, memo, postedAt, isTemplate, seriesId, occurrenceDate);

    [Fact]
    public async Task Get_reminders_lists_materialized_series_and_excludes_dormant_ones()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var templateId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            // A materialized series: a template header + its recurring row. The
            // template has no legs, so it names no originating account (NULL, mig
            // 263) — a state no factory builds, hence SQL.
            await InsertLeglessHeaderAsync(db, templateId, ledger.LedgerId, "mdreminder:t1", "Rent",
                "to landlord", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), isTemplate: true);
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

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        // No source account, so its amount is in no currency, and none is named.
        Assert.Null(summary.CurrencyCode);

        // A template with no legs has no side whose register presents it, so the agenda
        // and the editor show the transaction's own memo, as the list does.
        var agenda = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-03-01");
        Assert.NotEmpty(agenda!);
        Assert.All(agenda!, o => Assert.Equal("to landlord", o.Memo));
        var detail = await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{summary.Id}");
        Assert.Equal("to landlord", detail!.Memo);
    }

    [Fact]
    public async Task The_list_names_the_currency_of_each_series_amount_as_the_agenda_does()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var category = await ledger.AddCategoryAsync("category-a");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A source account in a currency other than the default, so a currency read off
        // anything but the source fails.
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Account A", AccountType = "bank", CurrencyCode = "EUR" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var sourceId = (await made.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Payee A",
                SourceAccountId = sourceId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -40m } },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var summary = Assert.Single((await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders"))!);
        var agenda = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-01-31");

        // Anchor: the amount is the source side's.
        Assert.Equal(-40m, summary.Amount);
        Assert.Equal("EUR", summary.CurrencyCode);
        Assert.Equal("EUR", Assert.Single(agenda!).CurrencyCode);
    }

    [Fact]
    public async Task Upcoming_mixes_fired_scheduled_with_unfired_reminder_slots_in_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var fired = new DateOnly(2026, 3, 1);
        await using (var db = Db.NewDbContext())
        {
            // Legless template and occurrence: NULL originating account (mig 263), as SQL.
            await InsertLeglessHeaderAsync(db, templateId, ledger.LedgerId, $"mdreminder:{ledger.LedgerId}",
                "Rent", memo: null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), isTemplate: true);
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
            // A committed occurrence (as if fired) for 2026-03-01.
            await InsertLeglessHeaderAsync(db, Guid.NewGuid(), ledger.LedgerId, externalId: null, "Rent",
                memo: null, fired.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), isTemplate: false,
                seriesId: seriesId, occurrenceDate: fired);
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

    // ---- the second-amount rule on a bank reminder's writes (ADR-0114 D13) --------------
    //
    // A template line between real accounts in different currencies carries both amounts,
    // nonzero and opposite in sign, on create, edit and Post or skip alike; a refused write
    // leaves the series as it was. A line with a category needs no second amount, whatever
    // its account's currency.

    private static async Task<string?> RefusedCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static Task<HttpResponseMessage> CreateTransferSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid other, decimal amount, decimal? second) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = new DateOnly(2026, 1, 1),
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = new[] { new TransactionPosting { CounterpartyAccountId = other, Amount = amount, CounterpartyAmount = second } },
        });

    private async Task<IReadOnlyList<TxnLegRow>> TemplateLegsAsync(Guid seriesId)
    {
        await using var db = Db.NewServiceDbContext();
        var templateId = (await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId)).TemplateHeaderId!.Value;
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == templateId).ToListAsync();
    }

    [Fact]
    public async Task A_reminder_line_across_currencies_with_a_zero_or_same_signed_second_amount_is_refused_on_create_edit_and_fire()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal("transaction-posting-two-amounts-nonzero",
            await RefusedCodeAsync(await CreateTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 0m)));
        Assert.Equal("transaction-posting-two-amounts-nonzero",
            await RefusedCodeAsync(await CreateTransferSeriesAsync(client, ledger, usd.Id, eur.Id, 0m, 183.4m)));
        Assert.Equal("transaction-posting-two-amounts-sign",
            await RefusedCodeAsync(await CreateTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, -183.4m)));
        await using (var db = Db.NewServiceDbContext())
            Assert.Equal(0, await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledger.LedgerId));

        var created = await CreateTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 183.4m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var sourceLeg = Assert.Single(await TemplateLegsAsync(seriesId), l => l.AccountId == usd.Id);

        Assert.Equal("transaction-posting-two-amounts-sign", await RefusedCodeAsync(await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}",
            new EditReminderRequest
            {
                Payee = "Payee B",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = usd.Id,
                    Items = [new TransactionPosting { LegId = sourceLeg.Id, CounterpartyAccountId = eur.Id, Amount = -250m, CounterpartyAmount = -229m }],
                },
            })));
        var template = await TemplateLegsAsync(seriesId);
        Assert.Equal((-200m, 183.4m),
            (Assert.Single(template, l => l.AccountId == usd.Id).Amount, Assert.Single(template, l => l.AccountId == eur.Id).Amount));

        Assert.Equal("transaction-posting-two-amounts-nonzero", await RefusedCodeAsync(await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1),
                SourceAccountId = usd.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -200m, CounterpartyAmount = 0m } },
            })));
        await using (var db = Db.NewServiceDbContext())
            Assert.Equal(0, await db.TxnHeaders.AsNoTracking().CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate));
    }

    [Fact]
    public async Task A_reminder_expense_from_an_account_in_another_currency_than_the_reporting_one_needs_no_second_amount()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);   // reported in USD
        var eur = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await CreateTransferSeriesAsync(client, ledger, eur.Id, category.Id, -40m, null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var fired = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1),
                SourceAccountId = eur.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -41m } },
            });
        Assert.Equal(HttpStatusCode.OK, fired.StatusCode);
    }
}
