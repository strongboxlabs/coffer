using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Server-side fixes from the bank/investment parity audit (2026-09-22): the
/// investment delete path's missing reminder-occurrence carve-out, and the
/// in-kind convert reading raw dates while its own detector reads effective ones.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentParityBatchTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentParityBatchTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    /// <summary>
    /// Deleting a fired investment reminder occurrence soft-hides it, keeping the
    /// (series, slot) stamp — exactly as both bank delete paths already do.
    /// </summary>
    /// <remarks>
    /// The agenda decides "already fired" from that stamp on a live committed
    /// header. Hard-deleting the row removes it, so the slot reverts to un-acted
    /// and re-fireable — while the identical delete of a BANK occurrence leaves it
    /// consumed. Two different outcomes for the same gesture on the same kind of
    /// row.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_fired_investment_reminder_occurrence_soft_hides_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("RMND", ticker: "RMND");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Utc(2026, 4, 1),
                Action = "buy",
                SecurityId = securityId,
                Shares = 5m,
                Price = 20m,
                Amount = 100m,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content
            .ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;

        // A real series, so the FK is satisfied. Its own shape does not matter —
        // DeleteAsync reads only recurring_transaction_id, occurrence_date and
        // is_recurring_template.
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("rent");
        var reminder = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Rent",
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1500m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);
        var seriesId = (await reminder.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Stamp the investment row as a fired occurrence of it. Setting the two
        // columns directly keeps the test about DELETE rather than about the whole
        // reminders stack.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE txn_headers
                   SET recurring_transaction_id = {seriesId},
                       occurrence_date = DATE '2026-04-01'
                 WHERE id = {headerId}
                """);
        }

        var deleted = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}");
        Assert.True(deleted.IsSuccessStatusCode,
            $"expected 2xx, got {(int)deleted.StatusCode}");

        await using var check = _fixture.NewDbContext();
        var header = await check.TxnHeaders.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == headerId);

        Assert.True(header is not null,
            "the occurrence was hard-deleted, so its slot reads un-acted again");
        Assert.True(header!.IsHidden);
        // The stamp is what the agenda reads; it has to survive.
        Assert.NotNull(header.RecurringTransactionId);
        Assert.NotNull(header.OccurrenceDate);
    }

    /// <summary>
    /// The in-kind convert agrees with the detector about which dates count.
    /// </summary>
    /// <remarks>
    /// The detector reads <c>resolved_transactions</c>; the convert's own
    /// validation read raw <c>txn_headers</c>. So a sell/buy pair a user had dated
    /// onto the same day via the override layer was OFFERED by the detector and
    /// then refused by the convert as <c>NotAValidPair</c> — two surfaces
    /// disagreeing about one pair. A curated date is what the register shows, so
    /// it is what a same-day test has to mean.
    /// </remarks>
    [Fact]
    public async Task Convert_in_kind_honours_a_curated_date_like_its_detector_does()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content
                .ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
        }

        await Post(new()
        {
            BrokerageAccountId = source.Id, Action = "buy", SecurityId = security,
            Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1),
        });
        // RAW dates deliberately differ by a day...
        var sellId = await Post(new()
        {
            BrokerageAccountId = source.Id, Action = "sell", SecurityId = security,
            Shares = -10m, Price = 150m, PostedAt = Utc(2024, 6, 1),
        });
        var buyId = await Post(new()
        {
            BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security,
            Shares = 10m, Price = 150m, PostedAt = Utc(2024, 6, 2),
        });

        // ...and the user curates the buy onto the sell's day. That is what the
        // register shows from here on.
        await ledger.EditHeaderAsync(buyId, postedAt: Utc(2024, 6, 1));

        var convert = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sellId, BuyHeaderId = buyId });

        Assert.True(convert.StatusCode == HttpStatusCode.Created,
            "convert refused a pair whose effective dates match: "
            + $"{(int)convert.StatusCode} {await convert.Content.ReadAsStringAsync()}");
    }
}
