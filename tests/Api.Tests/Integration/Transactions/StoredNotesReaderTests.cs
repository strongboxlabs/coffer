using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The readers outside the register, reports and listing read the stored memo-and-tags
/// presentation: reminders from the template's (or the occurrence's) originating
/// account, and the cross-account <c>/legs</c> read from each leg's own account. Each
/// case is one the raw header and leg values answer differently, so a reader that went
/// back to them fails here.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesReaderTests : DatabaseTest
{
    private static readonly DateTime March5 = new(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc);

    public StoredNotesReaderTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // =====================================================================
    // Reminders
    // =====================================================================

    [Fact]
    public async Task A_reminder_lists_and_details_the_notes_its_source_register_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var (seriesId, templateId) = await AddTemplateAsync(ledger, bank.Id, (category.Id, -1500m));
        // The bank side's own memo; the category side inherits it.
        await ledger.WriteNotesAsync(templateId, new AnnotationIntent<string>(bank.Id,
            FieldEdit<string>.Set("source memo"), FieldEdit<IReadOnlyList<string>>.Set(["tag-source"])), bank.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var summary = Assert.Single((await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders"))!);
        Assert.Equal("source memo", summary.Memo);

        var upcoming = (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-03-31"))!;
        Assert.NotEmpty(upcoming);
        Assert.All(upcoming, o => Assert.Equal("source memo", o.Memo));

        // The detail is the editor's prefill: each leg's own value, never an inherited
        // one, and no transaction memo on a one-posting template.
        var detail = (await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}"))!;
        Assert.Null(detail.Memo);
        Assert.Equal("source memo", detail.Legs.Single(l => l.AccountId == bank.Id).LegMemo);
        Assert.Null(detail.Legs.Single(l => l.AccountId == category.Id).LegMemo);

        // The category side overrides: that is its own value now, and the bank keeps its own.
        await ledger.SetSideMemoAsync(templateId, category.Id, "category memo", bank.Id);
        detail = (await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}"))!;
        Assert.Equal("category memo", detail.Legs.Single(l => l.AccountId == category.Id).LegMemo);
        Assert.Equal("source memo", detail.Legs.Single(l => l.AccountId == bank.Id).LegMemo);
    }

    [Fact]
    public async Task A_split_reminder_lists_and_details_the_transactions_memo_and_each_lines_own()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var c1 = await ledger.AddCategoryAsync("Category A");
        var c2 = await ledger.AddCategoryAsync("Category B");
        var (seriesId, templateId) = await AddTemplateAsync(ledger, bank.Id, (c1.Id, -100m), (c2.Id, -50m));
        await ledger.WriteNotesAsync(templateId, new AnnotationIntent<string>(bank.Id,
                FieldEdit<string>.Set("whole"), FieldEdit<IReadOnlyList<string>>.Leave,
                [new LineIntent<string>(1, FieldEdit<string>.Set("second line"), FieldEdit<IReadOnlyList<string>>.Leave)]),
            bank.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var summary = Assert.Single((await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders"))!);
        Assert.Equal("whole", summary.Memo);

        var detail = (await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}"))!;
        Assert.Equal("whole", detail.Memo);
        var bankLines = detail.Legs.Where(l => l.AccountId == bank.Id).OrderBy(l => l.PostingIndex).ToList();
        Assert.Equal([null, "second line"], bankLines.Select(l => l.LegMemo));
        // A line's other side shows it inherited, which is not its own.
        Assert.All(detail.Legs.Where(l => l.AccountId != bank.Id), l => Assert.Null(l.LegMemo));
    }

    [Fact]
    public async Task An_investment_reminder_details_the_events_memo_and_no_leg_memo_of_its_own()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var fee = await ledger.AddCategoryAsync("Category A");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A buy with a fee: several postings, so the event's memo is the header's.
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = LedgerActions.Buy, SecurityId = security,
                    Shares = 1m, Price = 10m, FeeAccountId = fee.Id, FeeAmount = 1m, Memo = "event memo",
                },
            });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var seriesId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var summary = Assert.Single((await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders"))!);
        Assert.Equal("event memo", summary.Memo);
        var detail = (await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}"))!;
        Assert.Equal("event memo", detail.Memo);
        // The event's side shows the transaction's memo and the fee's row inherits it:
        // neither is a leg's own.
        Assert.True(detail.Legs.Count >= 4, $"{detail.Legs.Count} legs");
        Assert.All(detail.Legs, l => Assert.Null(l.LegMemo));
    }

    [Fact]
    public async Task A_fired_occurrence_and_the_mcp_agenda_show_the_stored_memo()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var (seriesId, templateId) = await AddTemplateAsync(ledger, bank.Id, (category.Id, -40m));
        await ledger.SetSideMemoAsync(templateId, bank.Id, "source memo", bank.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        // Posted as the auto-post job posts it: a verbatim clone of the template.
        var occurrenceId = await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, seriesId, new DateOnly(2026, 2, 1));
        // The occurrence then gets a memo of its own on the category side, which the
        // bank's register does not show over the bank's own.
        await ledger.SetSideMemoAsync(occurrenceId, category.Id, "category memo", bank.Id);
        await ledger.SetSideMemoAsync(occurrenceId, bank.Id, "occurrence memo", bank.Id);

        var upcoming = (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-03-31"))!;
        Assert.Equal("occurrence memo", Assert.Single(upcoming, o => o.Kind == "scheduled").Memo);
        Assert.All(upcoming.Where(o => o.Kind != "scheduled"), o => Assert.Equal("source memo", o.Memo));

        // The MCP agenda is the same read. The agenda reads through neither of the
        // repositories a fire writes through, so they are left out.
        await using var db = Db.NewDbContext();
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new RemindersRepository(db, new Coffer.Domain.Reminders.RecurrenceExpander(), null!, null!, clock);
        var agenda = await RemindersTools.ListUpcomingReminders(
            repository, clock, ledger.LedgerId, daysAhead: 89);
        Assert.Equal("occurrence memo", Assert.Single(agenda, o => o.Kind == "scheduled").Memo);
        Assert.Contains(agenda, o => o.Kind == "reminder" && o.Memo == "source memo");
    }

    // =====================================================================
    // /legs
    // =====================================================================

    [Fact]
    public async Task The_legs_read_carries_each_legs_stored_presentation_from_its_own_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var fee = await ledger.AddCategoryAsync("Category A");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, Action = LedgerActions.Buy, SecurityId = security,
                Shares = 10m, Price = 20m, FeeAccountId = fee.Id, FeeAmount = 3m, PostedAt = March5,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var buy = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        await ledger.WriteNotesAsync(buy, new AnnotationIntent<string>(brokerage.Id,
            FieldEdit<string>.Set("event memo"), FieldEdit<IReadOnlyList<string>>.Set(["tag-event"])), brokerage.Id);

        var legs = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{buy}/legs")).EnumerateArray().ToList();
        Assert.NotEmpty(legs);
        foreach (var leg in legs)
        {
            var account = leg.GetProperty("accountId").GetGuid();
            Assert.Equal("event memo", leg.GetProperty("memo").GetString());
            Assert.Equal(["tag-event"], leg.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
            if (account == fee.Id)
            {
                // The fee's row is the event's counterpart: read-only, and it inherits.
                Assert.Equal(NotesKinds.EventOpposite, leg.GetProperty("notesKind").GetString());
                Assert.True(leg.GetProperty("isReadOnly").GetBoolean());
                Assert.True(leg.GetProperty("memoInherited").GetBoolean());
                Assert.Equal(brokerage.Id, leg.GetProperty("inheritFromAccountId").GetGuid());
            }
            else
            {
                Assert.Equal(NotesKinds.EventSide, leg.GetProperty("notesKind").GetString());
                Assert.False(leg.GetProperty("memoInherited").GetBoolean());
                Assert.Equal("event memo", leg.GetProperty("headerMemo").GetString());
            }
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<(Guid SeriesId, Guid TemplateId)> AddTemplateAsync(
        SyntheticLedger ledger, Guid sourceId, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var template = TxnHeaderRow.NewTemplate(templateId, ledger.LedgerId, sourceId, externalId: $"tpl-{templateId:N}");
            template.Payee = "Payee A";
            template.PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            template.TransactedAt = template.PostedAt;
            db.TxnHeaders.Add(template);
            for (var i = 0; i < postings.Length; i++)
            {
                db.TxnLegs.Add(new TxnLegRow
                {
                    Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                    AccountId = sourceId, PostingIndex = i, Amount = postings[i].Amount,
                });
                db.TxnLegs.Add(new TxnLegRow
                {
                    Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                    AccountId = postings[i].Counterparty, PostingIndex = i, Amount = -postings[i].Amount,
                });
            }
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{seriesId:N}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId, SourceAccountId = sourceId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "manual",
            });
            await db.SaveChangesAsync();
        }
        // The posting counts and the stored presentation, as the writers leave them.
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeNotesAsync(templateId);
        return (seriesId, templateId);
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
