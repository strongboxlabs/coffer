using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Snapshots;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Reminders MUTATION surface (ADR-0047 slice — manual authoring): create/edit
/// per transaction shape (bank + investment), disable/enable, and skip. The
/// load-bearing assertions are the two KEYSTONE tests — a manually-created
/// template (bank OR investment) must NEVER produce a balance row, and the
/// investment template must never auto-create a holdings/lots row (the
/// HoldingsRecomputeInterceptor skips template legs). Atomic per-test ledger;
/// every shared-table read is scoped by the test's ledger/series ids.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RemindersMutationTests
{
    private readonly PostgresFixture _fixture;

    public RemindersMutationTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // -----------------------------------------------------------------------
    // BANK create + keystone
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_bank_reminder_materializes_template_series_and_never_hits_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var req = new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = new DateOnly(2026, 1, 1),
            AutoCommitDaysBefore = 2,
            Payee = "Rent",
            Memo = "to landlord",
            SourceAccountId = bank.Id,
            Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
        };
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var detail = await resp.Content.ReadFromJsonAsync<ReminderDetail>();
        Assert.Equal("bank", detail!.Kind);
        Assert.Null(detail.Action);
        Assert.Equal("Rent", detail.Payee);
        Assert.Equal(2, detail.AutoCommitDaysBefore);
        Assert.Equal(new DateOnly(2026, 1, 1), detail.NextDueDate);   // first monthly occurrence
        Assert.Equal(2, detail.Legs.Count);
        Assert.Equal(0m, detail.Legs.Sum(l => l.Amount));             // posting sums to zero

        // MD-parity: the management list surfaces the source-side net (-1500),
        // not the per-posting zero - the figure the agenda shows.
        var list = await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders");
        Assert.Equal(-1500m, Assert.Single(list!).Amount);

        await using var db = _fixture.NewDbContext();
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        Assert.Equal("manual", template.Origin);
        Assert.Null(template.ExternalId);
        Assert.Null(template.ProviderKey);
        Assert.Null(template.Action);

        var series = await db.RecurringTransactions.AsNoTracking()
            .SingleAsync(r => r.LedgerId == ledger.LedgerId);
        Assert.Equal("manual", series.Origin);
        Assert.Null(series.ExternalId);
        Assert.Equal(template.Id, series.TemplateHeaderId);

        // KEYSTONE: the template produces NO balance row, and the source
        // account balance is untouched (the recompute ran via the interceptor
        // but the template is excluded from live_txn_headers).
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
            .CountAsync(b => b.HeaderId == template.Id));
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
            .CountAsync(b => b.AccountId == bank.Id));   // no live activity at all yet
    }

    // -----------------------------------------------------------------------
    // INVESTMENT create + keystone (guards the HoldingsRecomputeInterceptor fix)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_investment_reminder_template_never_touches_holdings_lots_or_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "TESTX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var req = new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
            StartDate = new DateOnly(2026, 1, 15),
            Transaction = new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Payee = "Auto-invest",
            },
        };
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var detail = await resp.Content.ReadFromJsonAsync<ReminderDetail>();
        Assert.Equal("investment", detail!.Kind);
        Assert.Equal("buy", detail.Action);
        // A buy is one sec posting: cash leg (brokerage) + holdings leg
        // (security/qty/price). The holdings-side leg carries the metadata.
        Assert.Contains(detail.Legs, l => l.SecurityId == securityId && l.Quantity == 10m && l.UnitPrice == 100m);

        await using var db = _fixture.NewDbContext();
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        Assert.Equal("buy", template.Action);

        // KEYSTONE (investment): the template auto-creates NO holdings row,
        // NO lot, and NO balance row. Before the interceptor fix, the buy's
        // holdings-side leg would have been enqueued and the recompute's
        // auto-create branch would have left a zero-qty holdings row.
        Assert.Equal(0, await db.Holdings.AsNoTracking()
            .CountAsync(h => h.AccountId == brokerage.HoldingsAccountId && h.SecurityId == securityId));
        Assert.Equal(0, await db.Holdings.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId));
        Assert.Equal(0, await db.Lots.AsNoTracking().CountAsync(l => l.LedgerId == ledger.LedgerId));
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
            .CountAsync(b => b.HeaderId == template.Id));
        // Symmetry with the bank keystone: the brokerage cash leg also produces
        // no balance row (the template is excluded from the balance recompute).
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
            .CountAsync(b => b.AccountId == brokerage.Id));
    }

    [Fact]
    public async Task Create_investment_reminder_reuses_action_field_validation()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "TESTX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // buy without shares → the SHARED investment validator rejects it.
        var req = new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
            StartDate = new DateOnly(2026, 1, 15),
            Transaction = new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = securityId,
                Price = 100m,   // no Shares
            },
        };
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment", req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.InvestmentTxnSharesRequired, await ErrorCodeAsync(resp));

        // and nothing was written.
        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId));
        Assert.Equal(0, await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledger.LedgerId));
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_bank_rejects_invalid_rrule_and_investment_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var badRule = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=NONSENSE;;",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRule.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderRruleInvalid, await ErrorCodeAsync(badRule));

        // investment account on the bank route → cross-shape 422.
        var wrongShape = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = brokerage.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongShape.StatusCode);
        Assert.Equal(BusinessError.Codes.TransactionAccountIsInvestment, await ErrorCodeAsync(wrongShape));
    }

    [Fact]
    public async Task Create_bank_rejects_counterparty_in_another_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var foreignCat = await other.AddCategoryAsync("foreign");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = foreignCat.Id, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotInLedger, await ErrorCodeAsync(resp));
    }

    // -----------------------------------------------------------------------
    // Edit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Edit_bank_updates_schedule_and_postings_keeping_template_off_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        var cat2 = await ledger.AddCategoryAsync("utilities");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Change the rule to the 5th + replace the single posting with a 2-split.
        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}",
            new EditReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=5",
                StartDate = new DateOnly(2026, 1, 5),
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1200m },
                        new TransactionPosting { CounterpartyAccountId = cat2.Id, Amount = -300m },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var detail = await edit.Content.ReadFromJsonAsync<ReminderDetail>();
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=5", detail!.Rrule);
        Assert.Equal(new DateOnly(2026, 1, 5), detail.NextDueDate);
        Assert.Equal(4, detail.Legs.Count);                 // two postings × two legs
        Assert.Equal(0m, detail.Legs.Sum(l => l.Amount));   // still balances

        await using var db = _fixture.NewDbContext();
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        Assert.Equal(4, await db.TxnLegs.AsNoTracking().CountAsync(l => l.HeaderId == template.Id));
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking().CountAsync(b => b.HeaderId == template.Id));
    }

    [Fact]
    public async Task Edit_rejects_empty_patch_and_shape_mismatch()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var empty = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}", new EditReminderRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderPatchEmpty, await ErrorCodeAsync(empty));

        // Editing a bank series via the INVESTMENT route → shape mismatch.
        var mismatch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/investment",
            new EditInvestmentReminderRequest { Rrule = "FREQ=WEEKLY;BYDAY=MO" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderShapeMismatch, await ErrorCodeAsync(mismatch));
    }

    // -----------------------------------------------------------------------
    // Disable / enable
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Disable_drops_from_upcoming_but_stays_listed_then_enable_restores()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        string Window() => $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-02-01&to=2026-04-30";

        var disable = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/active",
            new SetReminderActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        var afterDisable = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(Window());
        Assert.Empty(afterDisable!);   // disabled series drops from the agenda
        var listed = await client.GetFromJsonAsync<List<ReminderSummary>>($"/api/ledgers/{ledger.LedgerId}/reminders");
        Assert.Single(listed!);        // but still appears in the management list
        Assert.False(listed![0].IsActive);

        var enable = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/active",
            new SetReminderActiveRequest { Active = true });
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        var afterEnable = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(Window());
        Assert.NotEmpty(afterEnable!);
    }

    // -----------------------------------------------------------------------
    // Skip mechanics
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Skip_marks_occurrence_skipped_advances_cursor_and_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var skipDate = new DateOnly(2026, 1, 1);   // the first/next-due occurrence
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip",
            new SkipReminderRequest { OccurrenceDate = skipDate });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        var skipResp = await skip.Content.ReadFromJsonAsync<SkipReminderResponse>();
        Assert.Equal(new DateOnly(2026, 2, 1), skipResp!.NextDueDate);   // cursor advanced past the skip

        // The skipped slot stays in the agenda as a read-only "skipped" chip
        // (ADR-0049 D11) — a visible trail, not a gap.
        var upcoming = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-03-31");
        Assert.Contains(upcoming!, x => x.Date == skipDate && x.Kind == "skipped");
        // MD-parity: occurrences carry the source-side net amount (-1500).
        Assert.Contains(upcoming!, x => x.Date == new DateOnly(2026, 2, 1) && x.Amount == -1500m);

        // Idempotent: one exception row regardless of repeat skips.
        var skipAgain = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip",
            new SkipReminderRequest { OccurrenceDate = skipDate });
        Assert.Equal(HttpStatusCode.OK, skipAgain.StatusCode);

        await using var db = _fixture.NewDbContext();
        Assert.Equal(1, await db.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == reminderId && e.OccurrenceDate == skipDate));
    }

    [Fact]
    public async Task Skip_with_catch_up_on_old_series_lands_cursor_past_the_backlog()
    {
        // Regression (ADR-0049 D11): a series that started years ago, skipped at a
        // recent occurrence, must land the next-due cursor on the next FUTURE slot
        // — not NULL. ComputeNextDueAsync once anchored its expansion horizon to
        // start+2y, which a multi-year catch-up backlog exhausted, stranding the
        // cursor at null. The horizon now anchors to the latest consumed slot.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2019, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Skip a 2026 occurrence -> catch-up skips every 2019-01..2026-05 slot.
        var skipDate = new DateOnly(2026, 6, 1);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip",
            new SkipReminderRequest { OccurrenceDate = skipDate });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        var skipResp = await skip.Content.ReadFromJsonAsync<SkipReminderResponse>();

        // Cursor advances to the first slot after the skip — not stranded at NULL.
        Assert.Equal(new DateOnly(2026, 7, 1), skipResp!.NextDueDate);
        Assert.True(skipResp.SkippedEarlierCount > 0);   // the overdue backlog was cleared
    }

    [Fact]
    public async Task Skip_and_fire_are_mutually_exclusive_per_slot()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}";

        // Skip Feb, then firing Feb is refused.
        var feb = new DateOnly(2026, 2, 1);
        await client.PostAsJsonAsync($"{baseUrl}/skip", new SkipReminderRequest { OccurrenceDate = feb });
        var fireSkipped = await client.PostAsJsonAsync($"{baseUrl}/fire", new FireReminderRequest { OccurrenceDate = feb });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fireSkipped.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderOccurrenceSkipped, await ErrorCodeAsync(fireSkipped));

        // Fire Mar, then skipping Mar is refused.
        var mar = new DateOnly(2026, 3, 1);
        var fireMar = await client.PostAsJsonAsync($"{baseUrl}/fire", new FireReminderRequest { OccurrenceDate = mar });
        Assert.Equal(HttpStatusCode.OK, fireMar.StatusCode);
        var skipFired = await client.PostAsJsonAsync($"{baseUrl}/skip", new SkipReminderRequest { OccurrenceDate = mar });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, skipFired.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderOccurrenceAlreadyFired, await ErrorCodeAsync(skipFired));

        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == reminderId && e.OccurrenceDate == mar));
    }

    // -----------------------------------------------------------------------
    // Cross-ledger authorization (API-layer gate, not just UI)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mutations_reject_when_ledger_not_visible_to_caller()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var stranger = await SyntheticLedger.CreateAsync(_fixture);   // different user, no grant on `ledger`
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var strangerClient = await AuthedClientAsync(factory, stranger);

        var resp = await strangerClient.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledger.LedgerId));
    }

    // -----------------------------------------------------------------------
    // Catch-up (ADR-0047 §9.2 / ADR-0049): acting on an occurrence marks every
    // earlier UN-ACTED slot skipped (clearing the overdue backlog), but never
    // re-skips an earlier FIRED one (it carries real cash). The cursor lands on
    // the first occurrence after the acted slot.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Acting_catches_up_earlier_unacted_occurrences_but_preserves_a_fired_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}";

        // Fire the EARLIEST occurrence (Jan 1): real committed cash, nothing
        // earlier to catch up.
        var fireJan = await client.PostAsJsonAsync($"{baseUrl}/fire",
            new FireReminderRequest { OccurrenceDate = new DateOnly(2026, 1, 1) });
        Assert.Equal(HttpStatusCode.OK, fireJan.StatusCode);
        Assert.Equal(0, (await fireJan.Content.ReadFromJsonAsync<FireReminderResponse>())!.SkippedEarlierCount);

        // Skip Apr 1 out ahead: catch-up marks the earlier un-acted Feb 1 + Mar 1
        // skipped (2), but the already-FIRED Jan 1 is preserved.
        var skipApr = await client.PostAsJsonAsync($"{baseUrl}/skip",
            new SkipReminderRequest { OccurrenceDate = new DateOnly(2026, 4, 1) });
        Assert.Equal(HttpStatusCode.OK, skipApr.StatusCode);
        var skipResp = (await skipApr.Content.ReadFromJsonAsync<SkipReminderResponse>())!;
        Assert.Equal(2, skipResp.SkippedEarlierCount);
        Assert.Equal(new DateOnly(2026, 2, 1), skipResp.SkippedEarlierFrom);
        Assert.Equal(new DateOnly(2026, 5, 1), skipResp.NextDueDate);   // cursor past the acted Apr

        await using var db = _fixture.NewDbContext();
        // Jan stays a committed (fired) header — NOT converted to a skip.
        Assert.Equal(1, await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == reminderId && !h.IsRecurringTemplate
                             && h.OccurrenceDate == new DateOnly(2026, 1, 1)));
        var skips = await db.RecurringOccurrenceExceptions.AsNoTracking()
            .Where(e => e.RecurringTransactionId == reminderId)
            .Select(e => e.OccurrenceDate).OrderBy(d => d).ToListAsync();
        // Feb + Mar (cascade) + Apr (the acted skip); Jan absent (it was fired).
        Assert.Equal(
            new[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1) },
            skips);
    }

    // -----------------------------------------------------------------------
    // Investment-EDIT keystone (replace-all leg path: Deleted + Added template
    // legs must still never auto-create a holdings/lots/balance row)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Edit_investment_replace_all_keeps_template_off_holdings_lots_and_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var sec1 = await ledger.AddSecurityAsync("Fund A", "AAAX");
        var sec2 = await ledger.AddSecurityAsync("Fund B", "BBBX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy",
                    SecurityId = sec1, Shares = 10m, Price = 100m,
                },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Replace-all the transaction shape (different security/shares/price) —
        // exercises ReplaceTemplateLegsAsync's Deleted-leg + Added-leg path.
        var edit = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/investment",
            new EditInvestmentReminderRequest
            {
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy",
                    SecurityId = sec2, Shares = 5m, Price = 200m,
                },
            });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var detail = await edit.Content.ReadFromJsonAsync<ReminderDetail>();
        Assert.Contains(detail!.Legs, l => l.SecurityId == sec2 && l.Quantity == 5m);

        await using var db = _fixture.NewDbContext();
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        Assert.Equal(0, await db.Holdings.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId));
        Assert.Equal(0, await db.Lots.AsNoTracking().CountAsync(l => l.LedgerId == ledger.LedgerId));
        Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
            .CountAsync(b => b.HeaderId == template.Id));
    }

    // -----------------------------------------------------------------------
    // Inverted-range guard (single-sided edit must not persist end < start)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Edit_rejects_single_sided_end_date_before_existing_start()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 6, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Patch ONLY endDate, to before the EXISTING startDate (2026-06-01) — the
        // endpoint's both-present check can't catch this; the repo's effective-
        // range guard does. Must 422 and persist nothing.
        var resp = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}",
            new EditReminderRequest { EndDate = new DateOnly(2026, 3, 1) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderEndBeforeStart, await ErrorCodeAsync(resp));

        await using var db = _fixture.NewDbContext();
        var series = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == reminderId);
        Assert.Null(series.EndDate);   // rolled back
    }

    // -----------------------------------------------------------------------
    // Snapshot round-trip (mig 125 — the new exception table must be in BOTH
    // fn_ledger_snapshot_payload AND fn_ledger_snapshot_restore, else a
    // restore silently drops every skip — the mig 111->112 failure mode)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_round_trip_preserves_reminder_series_template_and_skips()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -1500m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var skipA = new DateOnly(2026, 1, 1);
        await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip",
            new SkipReminderRequest { OccurrenceDate = skipA });

        // Snapshot captures series + template + the skip-A exception.
        var snapResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("with-reminder"));
        Assert.Equal(HttpStatusCode.OK, snapResp.StatusCode);
        var snapId = (await snapResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!.Id;

        // Post-snapshot mutation: skip a SECOND occurrence.
        var skipB = new DateOnly(2026, 2, 1);
        await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip",
            new SkipReminderRequest { OccurrenceDate = skipB });
        await using (var db = _fixture.NewDbContext())
            Assert.Equal(2, await db.RecurringOccurrenceExceptions.AsNoTracking()
                .CountAsync(e => e.RecurringTransactionId == reminderId));

        // Restore → only the pre-snapshot skip-A survives (proves the exception
        // table is captured in the payload AND re-inserted on restore); the
        // series + its template survive intact.
        var restore = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snapId}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1, await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.Id == reminderId));
            Assert.Equal(1, await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate));
            var skips = await db.RecurringOccurrenceExceptions.AsNoTracking()
                .Where(e => e.RecurringTransactionId == reminderId)
                .Select(e => e.OccurrenceDate)
                .ToListAsync();
            Assert.Equal(new[] { skipA }, skips);
        }
    }

    // -----------------------------------------------------------------------
    // Adjust-at-post: fire with an EDITED transaction (ADR-0049). Bank reuses
    // the bank leg builder; investment REUSES the live investment create path
    // (holdings + lots), stamped to the occurrence.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Fire_with_bank_override_commits_the_edited_amount_and_payee()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("utilities");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Payee = "Utility",
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -84.99m } },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Fire the first occurrence with an EDITED amount + payee (the varying bill).
        var occ = new DateOnly(2026, 1, 1);
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = occ,
                SourceAccountId = bank.Id,
                Payee = "Utility (Jan)",
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -90.50m } },
            });
        Assert.Equal(HttpStatusCode.OK, fire.StatusCode);
        var committedId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == committedId);
        Assert.False(header.IsRecurringTemplate);
        Assert.Equal(reminderId, header.RecurringTransactionId);
        Assert.Equal(occ, header.OccurrenceDate);
        Assert.Equal("Utility (Jan)", header.Payee);                 // edited
        // The committed balance reflects the EDITED amount (-90.50), not -84.99.
        var bal = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(b => b.HeaderId == committedId && b.AccountId == bank.Id);
        Assert.Equal(-90.50m, bal.BalanceAfter);
        // The template is untouched (still -84.99 on its source leg).
        var template = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        Assert.Equal(-84.99m, await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == template.Id && l.AccountId == bank.Id)
            .SumAsync(l => l.Amount));
    }

    [Fact]
    public async Task Fire_investment_with_override_commits_edited_shares_with_holdings_and_lots()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "TESTX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = new DateOnly(2026, 1, 15),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy",
                    SecurityId = securityId, Shares = 10m, Price = 100m,
                },
            });
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Fire the first occurrence with EDITED shares (12 instead of the template's 10).
        var occ = new DateOnly(2026, 1, 15);
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/fire/investment",
            new FireInvestmentReminderRequest
            {
                OccurrenceDate = occ,
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy",
                    SecurityId = securityId, Shares = 12m, Price = 100m,
                    PostedAt = occ.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                },
            });
        Assert.Equal(HttpStatusCode.OK, fire.StatusCode);
        var committedId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == committedId);
        Assert.Equal(reminderId, header.RecurringTransactionId);
        Assert.Equal(occ, header.OccurrenceDate);
        Assert.False(header.IsRecurringTemplate);

        // REUSE proof: the live investment create path built holdings + a lot for
        // the EDITED 12 shares (a template builds neither).
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == brokerage.HoldingsAccountId && h.SecurityId == securityId);
        Assert.Equal(12m, holding.Quantity);
        var lot = await db.Lots.AsNoTracking().SingleAsync(l => l.LedgerId == ledger.LedgerId);
        Assert.Equal(12m, lot.Quantity);
    }

    [Fact]
    public async Task Fire_override_routes_reject_cross_shape()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("Fund", "FUNDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var bankRem = (await (await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -10m } },
            })).Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var invRem = (await (await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15", StartDate = new DateOnly(2026, 1, 15),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = securityId, Shares = 1m, Price = 10m,
                },
            })).Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Bank override on the INVESTMENT series -> ShapeMismatch.
        var bankOnInv = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{invRem}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 15),
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -10m } },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bankOnInv.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderShapeMismatch, await ErrorCodeAsync(bankOnInv));

        // Investment fire on the BANK series -> ShapeMismatch.
        var invOnBank = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{bankRem}/fire/investment",
            new FireInvestmentReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = securityId, Shares = 1m, Price = 10m,
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invOnBank.StatusCode);
        Assert.Equal(BusinessError.Codes.ReminderShapeMismatch, await ErrorCodeAsync(invOnBank));
    }
}
