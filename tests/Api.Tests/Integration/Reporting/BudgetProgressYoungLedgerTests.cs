using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The usual figures of a ledger younger than the trailing window.
///
/// <para>A month before the ledger's first transaction is no history at all, not a
/// month of no spending. So each day of the month divides by the window's months in
/// which the ledger already existed on that day, counting from its first transaction
/// of any kind, at least one and at most the window's length; and every figure that
/// averages the window uses that count: each row's typical, the total's, the daily
/// profile the band and the projection read, and the mark an untargeted row is judged
/// by. A ledger older than the window, or with no transactions, divides by the window
/// as before.</para>
///
/// <para>The history is written through the API, as past-dated transactions.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BudgetProgressYoungLedgerTests : DatabaseTest
{
    public BudgetProgressYoungLedgerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // The reported month and its three-month window: June, July and August.
    private const string Month = "2026-09";
    private const int Window = 3;
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static DateTime Utc(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>One transaction from <paramref name="account"/>, through the API: a
    /// negative amount leaves the account for the category, a positive one comes in.</summary>
    private static async Task PostAsync(
        HttpClient client, SyntheticLedger ledger, Guid account, Guid counterparty, decimal amount, DateTime postedAt)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = "Payee A",
                SourceAccountId = account,
                Postings = [new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount }],
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<BudgetProgressDto> ProgressAsync(HttpClient client, SyntheticLedger ledger)
    {
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month={Month}&window={Window}&today={Today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BudgetProgressDto>())!;
    }

    private static decimal NormalOn(BudgetProgressDto dto, Guid category, int day) =>
        Assert.Single(dto.DailyNormal, c => c.CategoryId == category && c.Day == day).Amount!.Value;

    [Fact]
    public async Task A_ledger_with_one_month_of_history_reads_that_month_as_its_usual_not_a_share_of_the_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var parent = await ledger.AddCategoryAsync("Category A");
        var child = await ledger.AddCategoryAsync("Category B", parentId: parent.Id);
        var other = await ledger.AddCategoryAsync("Category C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The ledger's first transaction is on August's first day: one whole month of
        // history in a three-month window.
        const decimal ParentOwn = 90m, ChildSpend = 300m, OtherSpend = 120m, ChildTarget = 200m;
        const int ChildDay = 10, OtherDay = 20;
        await PostAsync(client, ledger, bank.Id, parent.Id, -ParentOwn, Utc(8, 1));
        await PostAsync(client, ledger, bank.Id, child.Id, -ChildSpend, Utc(8, ChildDay));
        await PostAsync(client, ledger, bank.Id, other.Id, -OtherSpend, Utc(8, OtherDay));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(child.Id, Month, ChildTarget))).StatusCode);

        var dto = await ProgressAsync(client, ledger);

        Assert.Equal(new DateOnly(2026, 8, 1), dto.HistoryFrom);
        Assert.Equal(Window, dto.WindowMonths);
        var parentRow = Assert.Single(dto.Rows, r => r.CategoryId == parent.Id);
        var childRow = Assert.Single(dto.Rows, r => r.CategoryId == child.Id);
        var otherRow = Assert.Single(dto.Rows, r => r.CategoryId == other.Id);
        Assert.Equal(ParentOwn + ChildSpend, parentRow.Typical);
        Assert.Equal(ChildSpend, childRow.Typical);
        Assert.Equal(OtherSpend, otherRow.Typical);
        // An untargeted row's mark is its usual; a parent's swaps its targeted child's
        // usual for the child's target.
        Assert.Equal(OtherSpend, otherRow.Mark);
        Assert.Equal(ParentOwn + ChildTarget, parentRow.Mark);
        Assert.Equal(ParentOwn + ChildSpend + OtherSpend, dto.TypicalTotal);
        // The daily profile the band and the projection read.
        Assert.Equal(ParentOwn, NormalOn(dto, parent.Id, 1));
        Assert.Equal(ChildSpend, NormalOn(dto, child.Id, ChildDay));
        Assert.Equal(OtherSpend, NormalOn(dto, other.Id, OtherDay));
    }

    [Fact]
    public async Task A_ledger_whose_first_transaction_falls_mid_month_counts_that_month_only_from_that_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var spend = await ledger.AddCategoryAsync("Category A");
        var income = await ledger.AddCategoryAsync("Category B", "income");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The first transaction is money in, on FirstDay of July: a transaction of any
        // kind starts the ledger. Spending follows in July and August.
        const int FirstDay = 15, EarlyDay = 5, LateDay = 20;
        const decimal Deposit = 1_000m, JulyLate = 80m, AugustEarly = 60m, AugustLate = 140m;
        await PostAsync(client, ledger, bank.Id, income.Id, Deposit, Utc(7, FirstDay));
        await PostAsync(client, ledger, bank.Id, spend.Id, -JulyLate, Utc(7, LateDay));
        await PostAsync(client, ledger, bank.Id, spend.Id, -AugustEarly, Utc(8, EarlyDay));
        await PostAsync(client, ledger, bank.Id, spend.Id, -AugustLate, Utc(8, LateDay));

        var dto = await ProgressAsync(client, ledger);

        Assert.Equal(new DateOnly(2026, 7, FirstDay), dto.HistoryFrom);
        // A day before FirstDay existed in August only; a day from it on, in July and August.
        const int MonthsBefore = 1, MonthsFrom = 2;
        Assert.Equal(AugustEarly / MonthsBefore, NormalOn(dto, spend.Id, EarlyDay));
        Assert.Equal((JulyLate + AugustLate) / MonthsFrom, NormalOn(dto, spend.Id, LateDay));
        var usual = AugustEarly / MonthsBefore + (JulyLate + AugustLate) / MonthsFrom;
        Assert.Equal(usual, Assert.Single(dto.Rows, r => r.CategoryId == spend.Id).Typical);
        Assert.Equal(usual, dto.TypicalTotal);
    }

    [Fact]
    public async Task A_ledger_older_than_the_window_divides_by_the_window_as_before()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var parent = await ledger.AddCategoryAsync("Category A");
        var child = await ledger.AddCategoryAsync("Category B", parentId: parent.Id);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A transaction before the window, then spending in its first and last months.
        const decimal Before = 50m, ParentOwn = 900m, ChildSpend = 100m;
        const int ChildDay = 10;
        await PostAsync(client, ledger, bank.Id, child.Id, -Before, Utc(5, 20));
        await PostAsync(client, ledger, bank.Id, parent.Id, -ParentOwn, Utc(6, 1));
        await PostAsync(client, ledger, bank.Id, child.Id, -ChildSpend, Utc(8, ChildDay));

        var dto = await ProgressAsync(client, ledger);

        Assert.Null(dto.HistoryFrom);
        Assert.Equal((ParentOwn + ChildSpend) / Window, Assert.Single(dto.Rows, r => r.CategoryId == parent.Id).Typical);
        Assert.Equal(ChildSpend / Window, Assert.Single(dto.Rows, r => r.CategoryId == child.Id).Typical);
        Assert.Equal((ParentOwn + ChildSpend) / Window, dto.TypicalTotal);
        Assert.Equal(ParentOwn / Window, NormalOn(dto, parent.Id, 1));
        Assert.Equal(ChildSpend / Window, NormalOn(dto, child.Id, ChildDay));
    }

    [Fact]
    public async Task A_ledger_whose_first_transaction_is_the_windows_first_day_averages_the_whole_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var spend = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        const decimal Spend = 300m;
        await PostAsync(client, ledger, bank.Id, spend.Id, -Spend, Utc(6, 1));

        var dto = await ProgressAsync(client, ledger);

        Assert.Null(dto.HistoryFrom);
        Assert.Equal(Spend / Window, Assert.Single(dto.Rows, r => r.CategoryId == spend.Id).Typical);
    }

    /// <summary>
    /// Only a live transaction starts the ledger: a hidden one, one merged away and a
    /// reminder's template are no history, so each is dated before the first live one
    /// here, and dropping any one of those filters moves the start.
    /// </summary>
    [Fact]
    public async Task A_hidden_a_merged_away_and_a_template_transaction_dated_earlier_do_not_start_the_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var spend = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Every one of these falls inside the window and before the first live day.
        var templateDay = new DateOnly(2026, 6, 10);
        var hiddenDay = new DateOnly(2026, 6, 20);
        var mergedDay = new DateOnly(2026, 7, 5);
        var firstLiveDay = new DateOnly(2026, 8, 1);
        const decimal Amount = 25m;

        // Two rows imported for review: one to hide, one to merge away.
        var qif = string.Join('\n',
            "!Type:Bank",
            $"D{hiddenDay:MM'/'dd'/'yyyy}", $"T-{Amount.ToString(CultureInfo.InvariantCulture)}", "PPayee A", "^",
            $"D{mergedDay:MM'/'dd'/'yyyy}", $"T-{Amount.ToString(CultureInfo.InvariantCulture)}", "PPayee B", "^");
        using (var upload = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(qif));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            upload.Add(file, "file", "statement.qif");
            upload.Add(new StringContent(bank.Id.ToString()), "accountId");
            upload.Add(new StringContent("qif"), "providerAccountId");
            var imported = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/qif/import", upload);
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }
        Guid hiddenId, mergedId;
        await using (var db = Db.NewDbContext())
        {
            var rows = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId && h.ExternalId != null)
                .Select(h => new { h.Id, h.PostedAt })
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            hiddenId = rows.Single(r => DateOnly.FromDateTime(r.PostedAt) == hiddenDay).Id;
            mergedId = rows.Single(r => DateOnly.FromDateTime(r.PostedAt) == mergedDay).Id;
        }

        // Deleting an imported row hides it.
        var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{hiddenId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("soft-hidden", (await deleted.Content.ReadFromJsonAsync<DeleteTransactionResponse>())!.Kind);

        // A hand-entered row absorbs the other imported row, takes its date, and is then
        // moved to the first live day.
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Utc(firstLiveDay.Month, firstLiveDay.Day),
                Payee = "Payee B",
                SourceAccountId = bank.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = spend.Id, Amount = -Amount }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var survivorId = (await created.Content.ReadFromJsonAsync<CreatedHeader>())!.HeaderId;
        var merged = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{mergedId}/merge",
            new MergeTransactionRequest { FromHeaderId = survivorId });
        Assert.Equal(HttpStatusCode.NoContent, merged.StatusCode);
        var redated = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/transactions/{survivorId}")
        {
            Content = JsonContent.Create(new PatchTransactionRequest { PostedAt = Utc(firstLiveDay.Month, firstLiveDay.Day) }),
        });
        Assert.True(redated.IsSuccessStatusCode, $"re-dating the survivor failed: {(int)redated.StatusCode}");

        // A reminder's template is dated the reminder's start.
        var reminder = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = $"FREQ=MONTHLY;BYMONTHDAY={templateDay.Day}",
                StartDate = templateDay,
                EndDate = templateDay,
                Payee = "Payee C",
                SourceAccountId = bank.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = spend.Id, Amount = -Amount }],
            });
        Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);

        // Each excluded kind is really there, dated where this test put it.
        await using (var db = Db.NewDbContext())
        {
            var headers = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => new { h.Id, h.PostedAt, h.IsHidden, h.IsMergedInto, h.IsRecurringTemplate })
                .ToListAsync();
            Assert.Contains(headers, h => h.Id == hiddenId && h.IsHidden && DateOnly.FromDateTime(h.PostedAt) == hiddenDay);
            Assert.Contains(headers, h => h.Id == mergedId && h.IsMergedInto == survivorId && DateOnly.FromDateTime(h.PostedAt) == mergedDay);
            Assert.Contains(headers, h => h.IsRecurringTemplate && DateOnly.FromDateTime(h.PostedAt) == templateDay);
            Assert.Contains(headers, h => h.Id == survivorId && DateOnly.FromDateTime(h.PostedAt) == firstLiveDay);
        }

        var dto = await ProgressAsync(client, ledger);

        Assert.Equal(firstLiveDay, dto.HistoryFrom);
    }

    private sealed record CreatedHeader(Guid HeaderId);

    [Fact]
    public async Task A_ledger_with_no_transactions_names_the_whole_window_as_before()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var spend = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A target makes the category a row with nothing behind it.
        const decimal Target = 100m;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(spend.Id, Month, Target))).StatusCode);

        var dto = await ProgressAsync(client, ledger);

        Assert.Null(dto.HistoryFrom);
        Assert.Equal(Window, dto.WindowMonths);
        Assert.Null(dto.TypicalTotal);
        Assert.Empty(dto.DailyNormal);
        var row = Assert.Single(dto.Rows, r => r.CategoryId == spend.Id);
        Assert.Null(row.Typical);
        Assert.Equal(Target, row.Mark);
    }
}
