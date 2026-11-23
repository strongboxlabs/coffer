using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The consistency check lists, beside its verdict and outside it, the stored figures with
/// more places than their currency now allows and the stored codes an account or a ledger
/// can no longer take (ADR-0114 D15, as ADR-0084 D6's unbacked-price advisory is): no
/// consistency.drift, no repair, and an edit that makes a figure fit takes it off the list.
/// </summary>
/// <remarks>
/// <para>Each figure that does not fit is seeded directly, as the released writers, which
/// held every currency to two places and took any three-letter code, could store it. The
/// check, the stored verdict and the events are read through the API.</para>
/// <para>Every <see cref="ApiFactory"/> runs one monitor pass at start; each test waits for
/// that pass's announcement before seeding, so the pass cannot see a seed half written.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyMinorUnitsAdvisoryTests : DatabaseTest
{
    public ConsistencyMinorUnitsAdvisoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Block until the start pass has announced its verdict for this ledger.</summary>
    private async Task WaitForStartPassAsync(Guid ledgerId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using (var db = Db.NewDbContext())
            {
                if (await db.LedgerEvents.AsNoTracking()
                        .AnyAsync(e => e.LedgerId == ledgerId && e.EventKey == ConsistencyVerdicts.OkKey))
                    return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The monitor's start pass never announced a verdict for this ledger.");
    }

    private static async Task<LedgerConsistencyReport> CheckAsync(HttpClient client, Guid ledgerId)
    {
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!;
    }

    private async Task<List<string>> ConsistencyEventKeysAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && e.Topic == NotificationTopics.Consistency)
            .Select(e => e.EventKey)
            .ToListAsync();
    }

    private static string Unhealthy(LedgerConsistencyReport report) =>
        string.Join(", ", report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + "=" + p.MismatchedCount));

    /// <summary>
    /// A line finer than its account's currency, its category's mirror, the account's opening
    /// balance and a budget target stamped with a whole-unit currency are listed, each once,
    /// beside a healthy verdict that records and announces no drift. A three-place line in a
    /// three-place currency and a whole line in a whole-unit one are not. An edit that makes
    /// the line fit takes it off the list and leaves the other figures on it.
    /// </summary>
    [Fact]
    public async Task Figures_finer_than_their_currency_are_listed_beside_a_healthy_verdict_until_an_edit_makes_one_fit()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        var jpy = await ledger.AddBankAccountAsync("Account A", openingBalance: 12.34m, currencyCode: "JPY");
        var kwd = await ledger.AddBankAccountAsync("Account B", currencyCode: "KWD");
        var category = await ledger.AddCategoryAsync("Category A");
        var budgeted = await ledger.AddCategoryAsync("Category B");
        var (unfitLeg, _) = await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -12.34m, PostedAt, payee: "Payee A");
        var unfitHeader = await ledger.ResolveHeaderIdAsync(unfitLeg);
        var wholeHeader = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -5m, PostedAt, payee: "Payee B")).FromTxnId);
        var kwdHeader = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(kwd.Id, category.Id, -1.234m, PostedAt, payee: "Payee C")).FromTxnId);
        await using (var seed = Db.NewServiceDbContext())
        {
            seed.BudgetTargets.Add(new BudgetTargetRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                CategoryId = budgeted.Id,
                TargetMonth = new DateOnly(2026, 3, 1),
                Amount = 10.50m,
                CurrencyCode = "JPY",
                CreatedAt = PostedAt,
                UpdatedAt = PostedAt,
            });
            await seed.SaveChangesAsync();
        }

        var report = await CheckAsync(client, ledger.LedgerId);

        // Anchored on the rows being listed in the same response, so the verdict below is
        // not read off a report that found nothing.
        var line = Assert.Single(report.UnfitAmounts, r => r.HeaderId == unfitHeader);
        Assert.Equal((UnfitAmountKinds.Transaction, jpy.Id, "Account A", "JPY", 0, new DateOnly(2026, 3, 2), "Payee A", (string?)null),
            (line.Kind, line.AccountId, line.Account, line.CurrencyCode, line.Places, line.Date, line.Payee, line.Reason));
        var opening = Assert.Single(report.UnfitAmounts, r => r.Kind == UnfitAmountKinds.OpeningBalance);
        Assert.Equal((jpy.Id, "JPY", 0), (opening.AccountId, opening.CurrencyCode, opening.Places));
        var target = Assert.Single(report.UnfitAmounts, r => r.Kind == UnfitAmountKinds.BudgetTarget);
        Assert.Equal((budgeted.Id, "Category B", "JPY", 0, new DateOnly(2026, 3, 1)),
            (target.AccountId, target.Account, target.CurrencyCode, target.Places, target.Month));
        Assert.Equal(3, report.UnfitAmounts.Count);
        Assert.DoesNotContain(report.UnfitAmounts, r => r.HeaderId == wholeHeader || r.HeaderId == kwdHeader);
        Assert.DoesNotContain(report.UnfitAmounts, r => r.AccountId == kwd.Id);

        // Not counted: the verdict is healthy, stored as healthy, and nothing announced drift.
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));
        Assert.Empty(report.RefusedCurrencies);
        var state = (await client.GetFromJsonAsync<LedgerConsistencyStateDto>(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/state"))!;
        Assert.True(state.Healthy);
        Assert.Empty(state.FailingProjections);
        Assert.DoesNotContain(ConsistencyVerdicts.DriftKey, await ConsistencyEventKeysAsync(ledger.LedgerId));

        // Edited to a whole figure: the line leaves the list, the account-level figures stay.
        var edited = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/transactions/{unfitHeader}")
        {
            Content = JsonContent.Create(new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = jpy.Id,
                    Items = [new TransactionPosting { LegId = unfitLeg, CounterpartyAccountId = category.Id, Amount = -12m }],
                },
            }),
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await CheckAsync(client, ledger.LedgerId);
        Assert.Equal(new[] { UnfitAmountKinds.BudgetTarget, UnfitAmountKinds.OpeningBalance },
            after.UnfitAmounts.Select(r => r.Kind).Order(StringComparer.Ordinal));
        Assert.True(after.Healthy, "unhealthy: " + Unhealthy(after));
    }

    /// <summary>
    /// A hidden transaction keeps its stored figure and comes back on unhide (ADR-0072 D2),
    /// so a line of one that does not fit is listed, and says it is hidden: its register shows
    /// it only in the Hidden view (ADR-0072 D1), where its link opens. A visible line beside
    /// it does not.
    /// </summary>
    [Fact]
    public async Task A_hidden_transaction_that_does_not_fit_is_listed_as_hidden()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        var hiddenHeader = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -12.34m, PostedAt, payee: "Payee A")).FromTxnId);
        var visibleHeader = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -5.67m, PostedAt, payee: "Payee B")).FromTxnId);
        // A row with an external id is hidden by its delete, not removed (ADR-0072 D1).
        await using (var seed = Db.NewServiceDbContext())
        {
            await seed.TxnHeaders.Where(h => h.Id == hiddenHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExternalId, "feed-a"));
        }
        var hidden = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{hiddenHeader}");
        Assert.True(hidden.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"hide failed: {(int)hidden.StatusCode} {await hidden.Content.ReadAsStringAsync()}");
        await using (var db = Db.NewDbContext())
            Assert.True((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == hiddenHeader)).IsHidden);

        var report = await CheckAsync(client, ledger.LedgerId);

        var hiddenRow = Assert.Single(report.UnfitAmounts, r => r.HeaderId == hiddenHeader);
        Assert.Equal((UnfitAmountKinds.Transaction, jpy.Id, "Payee A", true),
            (hiddenRow.Kind, hiddenRow.AccountId, hiddenRow.Payee, hiddenRow.Hidden));
        var visibleRow = Assert.Single(report.UnfitAmounts, r => r.HeaderId == visibleHeader);
        Assert.Equal((UnfitAmountKinds.Transaction, jpy.Id, "Payee B", false),
            (visibleRow.Kind, visibleRow.AccountId, visibleRow.Payee, visibleRow.Hidden));
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));
    }

    /// <summary>
    /// No screen shows or opens a transaction merged into another, so a line of one that
    /// does not fit is not listed, while its winner's line is (decided by the owner,
    /// 2026-11-17; ADR-0114 D15). Deleting the winner un-merges it (<c>is_merged_into</c> ON
    /// DELETE SET NULL), and it is then listed like any other row.
    /// </summary>
    /// <remarks>The loser is a row awaiting review, as a feed or an import leaves one: only
    /// such a row folds into another. The merge and the delete run through the API.</remarks>
    [Fact]
    public async Task A_merged_away_row_that_does_not_fit_is_left_out_until_deleting_its_winner_un_merges_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        var winner = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -5.67m, PostedAt, payee: "Payee A")).FromTxnId);
        var loser = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(jpy.Id, category.Id, -12.34m, PostedAt, payee: "Payee B")).FromTxnId);
        await using (var seed = Db.NewServiceDbContext())
        {
            await seed.TxnHeaders.Where(h => h.Id == loser)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true));
        }
        // Before the merge the loser's line is one the advisory lists.
        Assert.Contains((await CheckAsync(client, ledger.LedgerId)).UnfitAmounts, r => r.HeaderId == loser);

        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
                   new MergeTransactionRequest { FromHeaderId = winner }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
        await using (var db = Db.NewDbContext())
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);

        var report = await CheckAsync(client, ledger.LedgerId);

        // Anchored on the winner's line, listed in the same response.
        var winnerRow = Assert.Single(report.UnfitAmounts, r => r.HeaderId == winner);
        Assert.Equal((UnfitAmountKinds.Transaction, jpy.Id, "Payee A"), (winnerRow.Kind, winnerRow.AccountId, winnerRow.Payee));
        Assert.DoesNotContain(report.UnfitAmounts, r => r.HeaderId == loser);
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));

        // A row entered by hand is removed by its delete, which un-merges the loser.
        var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{winner}");
        Assert.True(deleted.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"delete failed: {(int)deleted.StatusCode} {await deleted.Content.ReadAsStringAsync()}");
        await using (var db = Db.NewDbContext())
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);

        var restored = await CheckAsync(client, ledger.LedgerId);

        var loserRow = Assert.Single(restored.UnfitAmounts, r => r.HeaderId == loser);
        Assert.Equal((UnfitAmountKinds.Transaction, jpy.Id, "JPY", 0, "Payee B", false),
            (loserRow.Kind, loserRow.AccountId, loserRow.CurrencyCode, loserRow.Places, loserRow.Payee, loserRow.Hidden));
        Assert.DoesNotContain(restored.UnfitAmounts, r => r.HeaderId == winner);
        Assert.True(restored.Healthy, "unhealthy: " + Unhealthy(restored));
    }

    /// <summary>
    /// A series with no source account, as a released import could leave one, whose template
    /// line does not fit its account's currency is listed as the reminder, on that template
    /// line's account, once for the line and its category's mirror.
    /// </summary>
    [Fact]
    public async Task A_reminder_with_no_source_account_is_listed_on_its_template_lines_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        var jpy = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = new DateOnly(2026, 1, 1),
            Payee = "Payee A",
            SourceAccountId = jpy.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12m }],
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        await using (var seed = Db.NewServiceDbContext())
        {
            var templateId = (await seed.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId))
                .TemplateHeaderId!.Value;
            await seed.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == jpy.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, -12.34m));
            await seed.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == category.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, 12.34m));
            await seed.RecurringTransactions.Where(r => r.Id == seriesId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceAccountId, (Guid?)null));
        }

        var report = await CheckAsync(client, ledger.LedgerId);

        var row = Assert.Single(report.UnfitAmounts);
        Assert.Equal((UnfitAmountKinds.Reminder, seriesId, jpy.Id, "Account A", "JPY", 0, "Payee A", (Guid?)null),
            (row.Kind, row.ReminderId, row.AccountId, row.Account, row.CurrencyCode, row.Places, row.Payee, row.HeaderId));
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));
    }

    /// <summary>
    /// A stored reporting currency and an account currency that a create or a change would
    /// now refuse (a fund unit with more places than the ceiling, an entry with no exponent)
    /// are listed, beside a healthy verdict; an accepted code is not. A category, which takes
    /// the reporting currency and no other, is covered by the reporting row and not listed on
    /// its own, though it carries the refused code.
    /// </summary>
    [Fact]
    public async Task Stored_codes_an_account_or_a_ledger_can_no_longer_take_are_listed_beside_a_healthy_verdict()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        var metal = await ledger.AddBankAccountAsync("Account A", currencyCode: "XAU");
        await ledger.AddBankAccountAsync("Account B", currencyCode: "JPY");
        await using (var seed = Db.NewServiceDbContext())
        {
            await seed.Ledgers.Where(l => l.Id == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.CurrencyCode, "CLF"));
        }
        // A category made now is stamped with the ledger's stored code, the refused one.
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = "Category A", AccountType = "category", CategoryKind = "expense",
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal("CLF", (await made.Content.ReadFromJsonAsync<AccountSummary>())!.CurrencyCode);

        var report = await CheckAsync(client, ledger.LedgerId);

        Assert.Equal(
            new[]
            {
                new RefusedCurrencyAdvisory(RefusedCurrencyKinds.Reporting, null, null, "CLF"),
                new RefusedCurrencyAdvisory(RefusedCurrencyKinds.Account, metal.Id, "Account A", "XAU"),
            },
            report.RefusedCurrencies);
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));
    }
}
