using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Provisioning;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The rest of what a ledger gathers over years, added to a seeded stress ledger beside
/// <see cref="LongLivedAccounts"/>: the starter category tree with rows on its nested
/// categories, a second currency, hidden and merged headers, a brokerage whose securities
/// were priced, sold out, and moved in without a price, and reminder series with estimates
/// and a loan.
/// </summary>
/// <remarks>
/// <para>The stress seed is dense and recent: every account busy, every category flat. A
/// read whose plan is bounded by how far a walk must go, or by what a ledger-wide scan
/// keeps, is cheap on it and slow on a ledger with years of history, many accounts that
/// went quiet, and rows the app hides. This set is that other shape. Compose it as
/// <c>StressLedger.SeedLedgerAsync</c>, then <see cref="LongLivedAccounts.AddAsync"/>,
/// then <see cref="AddAsync"/>.</para>
/// <para>Nothing here is written by hand into a derived table. Accounts, categories and
/// rows come from the <see cref="SyntheticLedger"/> helpers (each recomputes its running
/// balances as a save does), the tree from the product's own seeder, and reminders
/// through the API and the clone fire the auto-post job uses. The statistics are
/// refreshed at the end, as autovacuum refreshes them on a live install.</para>
/// </remarks>
public static class LongLivedLedger
{
    /// <summary>Rows on the starter tree's categories, nested and top level.</summary>
    public const int StarterRows = 300;

    /// <summary>Rows on each second-currency account.</summary>
    public const int EuroRows = 40;

    /// <summary>Reminder series, every third estimated from its last occurrences.</summary>
    public const int ReminderSeries = 30;

    /// <summary>Months each series has posted.</summary>
    public const int PostedMonths = 6;

    /// <summary>Loan payments posted.</summary>
    public const int LoanPayments = 12;

    /// <summary>Monthly prices of the priced security.</summary>
    public const int PricedMonths = 168;

    private static readonly DateTime FirstRow = new(2011, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The reading date the reminders and prices are laid out around.</summary>
    public static readonly DateOnly Today = new(2026, 6, 15);

    /// <summary>What <see cref="AddAsync"/> added.</summary>
    /// <param name="StarterCategories">Categories the starter seed created.</param>
    /// <param name="NestedCategoriesWithRows">Starter children that carry rows.</param>
    /// <param name="EuroAccounts">The second-currency accounts and categories.</param>
    /// <param name="HiddenHeaders">Headers set hidden.</param>
    /// <param name="MergedHeaders">Headers merged into another.</param>
    /// <param name="Brokerage">The brokerage the three securities below sit in.</param>
    /// <param name="PricedSecurity">Held, with a price every month for fourteen years.</param>
    /// <param name="SoldOutSecurity">Bought and sold out years back, last priced then.</param>
    /// <param name="MovedInSecurity">Moved in as shares with no price, ever.</param>
    /// <param name="Reminders">Series created, with their posted occurrences.</param>
    public sealed record Added(
        int StarterCategories,
        int NestedCategoriesWithRows,
        IReadOnlyList<Guid> EuroAccounts,
        int HiddenHeaders,
        int MergedHeaders,
        Guid Brokerage,
        Guid PricedSecurity,
        Guid SoldOutSecurity,
        Guid MovedInSecurity,
        int Reminders);

    /// <summary>Add the set to <paramref name="ledger"/>, then refresh the statistics.</summary>
    public static async Task<Added> AddAsync(
        TestDatabase database, SyntheticLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ledger);

        // ----- The starter tree, and rows on it ---------------------------------
        int starter;
        await using (var db = ledger.NewDbContext())
            starter = await new StarterCategoriesSeeder(TimeProvider.System)
                .SeedAsync(db, ledger.LedgerId, cancellationToken).ConfigureAwait(false);

        List<(Guid Id, Guid? ParentId)> expense;
        await using (var db = ledger.NewDbContext())
            expense = (await db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledger.LedgerId && a.AccountType == "category"
                    && a.CategoryKind == "expense" && a.Name != null
                    && EF.Functions.Like(a.Name, "stress-%") == false
                    && EF.Functions.Like(a.Name, "long-lived-%") == false)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.ParentId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(a => (a.Id, a.ParentId)).ToList();
        // Rows go mostly to children and some to a parent, on a handful of accounts that
        // are neither the busiest nor retired: a household's current accounts.
        var current = new List<Guid>();
        for (var i = 1; i <= 6; i++)
            current.Add((await ledger.AddBankAccountAsync($"long-lived-current-{i:D2}",
                openingBalance: 500m * i, cancellationToken: cancellationToken).ConfigureAwait(false)).Id);

        var nestedWithRows = new HashSet<Guid>();
        var rows = new List<(Guid From, Guid To, DateTime On)>();
        for (var i = 0; i < StarterRows; i++)
        {
            var category = expense[(i * 7) % expense.Count];
            if (category.ParentId is not null) nestedWithRows.Add(category.Id);
            // Spread over fifteen years: sparse early, denser late.
            var on = FirstRow.AddDays((int)(5600d * Math.Sqrt((i + 1d) / StarterRows)));
            rows.Add((current[i % current.Count], category.Id, on));
        }
        var written = new List<(Guid Leg, DateTime On)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (from, to, on) = rows[i];
            var pair = await ledger.AddTransactionPairAsync(from, to, -(5m + (i % 90)), on,
                payee: $"long-lived-payee-{i % 40:D2}", cancellationToken: cancellationToken).ConfigureAwait(false);
            written.Add((pair.FromTxnId, on));
        }

        // ----- Hidden and merged headers ----------------------------------------
        var hidden = 0;
        var merged = 0;
        for (var i = 0; i < written.Count; i++)
        {
            if (i % 25 == 3)
            {
                await ledger.HideTransactionAsync(written[i].Leg, cancellationToken).ConfigureAwait(false);
                hidden++;
            }
            else if (i % 40 == 7 && i > 0)
            {
                await ledger.MarkTransactionMergedAsync(written[i].Leg, written[i - 1].Leg, cancellationToken)
                    .ConfigureAwait(false);
                merged++;
            }
        }

        // The hide and merge helpers write past the recompute; the balances they leave are
        // brought to what a save derives, for every account the rows reached.
        await ledger.RecomputeBalancesAsync(
            current.Concat(rows.Select(r => r.To)).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        // ----- A second currency ------------------------------------------------
        var euro = new List<Guid>();
        var euroBank = await ledger.AddBankAccountAsync("long-lived-euro-bank", openingBalance: 2000m,
            cancellationToken: cancellationToken, currencyCode: "EUR").ConfigureAwait(false);
        var euroCategory = await ledger.AddCategoryAsync("long-lived-euro-spending",
            cancellationToken: cancellationToken, currencyCode: "EUR").ConfigureAwait(false);
        euro.Add(euroBank.Id);
        euro.Add(euroCategory.Id);
        for (var i = 0; i < EuroRows; i++)
            await ledger.AddTransactionPairAsync(euroBank.Id, euroCategory.Id, -(20m + i),
                FirstRow.AddDays(3000 + (i * 90)), payee: "long-lived-euro-payee",
                cancellationToken: cancellationToken).ConfigureAwait(false);

        // ----- Securities: priced, sold out, moved in without a price ------------
        var brokerage = await ledger.AddInvestmentAccountAsync("long-lived-active-brokerage",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var holdings = brokerage.HoldingsAccountId!.Value;
        var priced = await ledger.AddSecurityAsync("Long Lived Priced", "LLPR",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var soldOut = await ledger.AddSecurityAsync("Long Lived Sold Out", "LLSO",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var movedIn = await ledger.AddSecurityAsync("Long Lived Moved In", "LLMV",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var security in new[] { priced, soldOut, movedIn })
            await ledger.AddHoldingAsync(holdings, security, 0m, 0m, cancellationToken).ConfigureAwait(false);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, priced, 10m, 20m,
            FirstRow.AddDays(100), cancellationToken).ConfigureAwait(false);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, priced, 5m, 35m,
            FirstRow.AddDays(1800), cancellationToken).ConfigureAwait(false);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, soldOut, 8m, 12m,
            FirstRow.AddDays(200), cancellationToken).ConfigureAwait(false);
        await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, soldOut, 8m, 15m,
            FirstRow.AddDays(900), cancellationToken).ConfigureAwait(false);
        await ledger.AddSharesInAsync(brokerage.Id, holdings, current[0], movedIn, 4m, 80m,
            FirstRow.AddDays(2500), carryUnitPrice: true, cancellationToken).ConfigureAwait(false);
        await ledger.RecomputeHoldingsAsync(cancellationToken).ConfigureAwait(false);
        // The trade price each day's trade implies, for every trade in the ledger: the raw
        // stress seed and these helpers write trades past the interceptor that stores them.
        await ledger.RecomputeTradePricesAsync(cancellationToken).ConfigureAwait(false);

        // A price every month for the held security, on days its own trades did not price. The
        // sold-out security keeps only the price its sale stored: nothing after it, ever.
        HashSet<DateOnly> taken;
        await using (var db = ledger.NewDbContext())
            taken = (await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == priced)
                .Select(p => p.PriceDate).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var months = 0;
        for (var m = 0; months < PricedMonths; m++)
        {
            var on = FirstRow.AddMonths(m).AddDays(10);
            if (taken.Contains(DateOnly.FromDateTime(on))) continue;
            await ledger.AddSecurityPriceAsync(priced, 20m + (months * 0.4m), on,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            months++;
        }

        // ----- Reminders: estimates, posted occurrences and a loan --------------
        var reminders = await AddRemindersAsync(database, ledger, current, expense[0].Id, cancellationToken)
            .ConfigureAwait(false);

        await using (var db = ledger.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ANALYZE accounts; ANALYZE txn_headers; ANALYZE txn_legs; ANALYZE txn_header_account_balances; " +
                "ANALYZE securities; ANALYZE security_prices; ANALYZE holdings; ANALYZE lots; " +
                "ANALYZE recurring_transactions;",
                cancellationToken).ConfigureAwait(false);
        }

        return new Added(starter, nestedWithRows.Count, euro, hidden, merged, brokerage.Id,
            priced, soldOut, movedIn, reminders);
    }

    private static async Task<int> AddRemindersAsync(
        TestDatabase database, SyntheticLedger ledger, IReadOnlyList<Guid> banks, Guid category,
        CancellationToken cancellationToken)
    {
        await using var factory = new ApiFactory(database).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie",
            $"__Host-coffer.session={await ledger.IssueSessionCookieAsync(cancellationToken).ConfigureAwait(false)}");
        var start = Today.AddMonths(-PostedMonths);

        var created = 0;
        for (var i = 0; i < ReminderSeries; i++)
        {
            var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
                new CreateReminderRequest
                {
                    Rrule = $"FREQ=MONTHLY;BYMONTHDAY={1 + (i % 28)}",
                    StartDate = start,
                    AutoCommitDaysBefore = i % 2 == 0 ? 3 : null,
                    Payee = $"long-lived-series-{i:D2}",
                    SourceAccountId = banks[i % banks.Count],
                    Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -10m - i }],
                }, cancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<ReminderDetail>(cancellationToken).ConfigureAwait(false))!.Id;
            for (var m = 0; m < PostedMonths; m++)
                await ReminderCloneFire.PostedAsync(database, ledger.LedgerId, id,
                    start.AddMonths(m).AddDays(i % 28)).ConfigureAwait(false);
            // An estimate averages committed occurrences, so it is turned on once there are some.
            if (i % 3 == 0)
            {
                var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{id}",
                    new EditReminderRequest { EstimateSampleCount = 6 }, cancellationToken).ConfigureAwait(false);
                edit.EnsureSuccessStatusCode();
            }
            created++;
        }

        // A loan: its account and terms, and a reminder whose postings the loan override replaces.
        var loanId = Guid.NewGuid();
        var interest = await ledger.AddCategoryAsync("long-lived-loan-interest",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var escrow = await ledger.AddBankAccountAsync("long-lived-loan-escrow",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (var db = ledger.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = ledger.LedgerId, Name = "long-lived-mortgage",
                AccountType = "loan", CurrencyCode = "USD", OpeningBalance = -300_000m, IsActive = true,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 400_000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 400m, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        var loanStart = Today.AddMonths(-LoanPayments);
        var loan = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=10",
                StartDate = loanStart,
                SourceAccountId = banks[0],
                Payee = "Mortgage payment",
                Postings =
                [
                    new TransactionPosting { CounterpartyAccountId = loanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = interest.Id, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = escrow.Id, Amount = -400m },
                ],
            }, cancellationToken).ConfigureAwait(false);
        loan.EnsureSuccessStatusCode();
        var loanReminder = (await loan.Content.ReadFromJsonAsync<ReminderDetail>(cancellationToken).ConfigureAwait(false))!.Id;
        await using (var db = ledger.NewDbContext())
            await db.RecurringTransactions.Where(r => r.Id == loanReminder)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsLoanReminder, true), cancellationToken)
                .ConfigureAwait(false);
        for (var m = 0; m < LoanPayments; m++)
            await ReminderCloneFire.PostedAsync(database, ledger.LedgerId, loanReminder,
                loanStart.AddMonths(m).AddDays(9)).ConfigureAwait(false);

        return created + 1;
    }
}
