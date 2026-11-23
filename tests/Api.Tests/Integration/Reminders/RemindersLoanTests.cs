using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Reminders;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Loan-aware reminder occurrences (ADR-0050 slice 4): a loan reminder's
/// template carries mostly-zero principal/interest legs (MD computes them live),
/// so every surface that shows or materializes the amount must override it with
/// the computed full payment (principal + interest + escrow) derived from
/// <c>loan_terms</c> (its split, from the loan's balance as of the payment's own date,
/// ADR-0078). These tests pin all
/// three readers/writers: the agenda, the manage-page list, and the verbatim
/// clone-fire. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RemindersLoanTests : DatabaseTest
{
    public RemindersLoanTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // A fixed-rate loan amortized over PaymentCount monthly payments (see
    // SeedLoanAsync), with OwedBalance still owed and Escrow on top. Interest =
    // round(OwedBalance × AnnualInterestRate / 12); principal is the amortized
    // payment less that interest; the full payment is principal + interest +
    // Escrow. The InRange bounds below bracket the principal and the full payment.
    private const decimal OwedBalance = 400000.00m;
    private const decimal Escrow = 500.00m;
    private const decimal Interest = 1333.33m;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <param name="currencyCode">The loan's currency, and by default its paying and escrow
    /// accounts'.</param>
    /// <param name="checkingCurrency">The paying account's, when it differs.</param>
    /// <param name="escrowCurrency">The real escrow account's, when it differs.</param>
    /// <param name="escrowIsCategory">The escrow is a category, which holds the ledger's
    /// reporting currency, rather than a real account.</param>
    private async Task<(SyntheticLedger Ledger, Guid LoanId, Guid InterestId, Guid EscrowId, Guid CheckingId)>
        SeedLoanAsync(
            string currencyCode = "USD", string? checkingCurrency = null, string? escrowCurrency = null,
            bool escrowIsCategory = false)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("checking", currencyCode: checkingCurrency ?? currencyCode);
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        var escrow = escrowIsCategory
            ? await ledger.AddCategoryAsync("escrow", "expense")
            : await ledger.AddBankAccountAsync("escrow", currencyCode: escrowCurrency ?? currencyCode);

        var loanId = Guid.NewGuid();
        await using var db = Db.NewDbContext();
        // Loan account whose negative balance (liability) is the amount owed.
        db.Accounts.Add(new AccountRow
        {
            Id = loanId, LedgerId = ledger.LedgerId, Name = "Mortgage",
            AccountType = "loan", CurrencyCode = currencyCode,
            OpeningBalance = -OwedBalance, IsActive = true,
        });
        await db.SaveChangesAsync();

        db.LoanTerms.Add(new LoanTermsRow
        {
            AccountId = loanId, LedgerId = ledger.LedgerId,
            OriginalPrincipal = 500000m, AnnualInterestRate = 4.00m, Points = 0m,
            PaymentCount = 360, PaymentsPerYear = 12,
            EscrowAmount = Escrow, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
            PaymentIsComputed = true, FixedPayment = null,
        });
        await db.SaveChangesAsync();

        return (ledger, loanId, interest.Id, escrow.Id, checking.Id);
    }

    private async Task<Guid> CreateLoanReminderAsync(
        HttpClient client, SyntheticLedger ledger,
        Guid checkingId, Guid loanId, Guid interestId, Guid escrowId, DateOnly start,
        TransactionNotesRequest? notes = null)
    {
        // A bank reminder whose postings target loan / interest / escrow (the
        // loan-payment shape). Non-zero placeholders pass posting validation; the
        // loan override replaces them with the computed split.
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=10",
                StartDate = start,
                SourceAccountId = checkingId,
                Payee = "Mortgage payment",
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = loanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = interestId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = escrowId, Amount = -500.00m },
                },
                Notes = notes,
            });
        created.EnsureSuccessStatusCode();
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Mark it a loan reminder (the importer would set this from MD).
        await using var db = Db.NewDbContext();
        await db.RecurringTransactions
            .Where(r => r.Id == reminderId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsLoanReminder, true));
        return reminderId;
    }

    [Fact]
    public async Task Agenda_amount_is_the_computed_full_payment()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2026, 1, 10));

        var upcoming = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-01-31");

        var occ = Assert.Single(upcoming!, x => x.ReminderId == reminderId);
        // The full payment, as a source-side outflow — NOT the near-escrow-only
        // figure the template legs would net.
        Assert.InRange(occ.Amount, -2895m, -2880m);
    }

    [Fact]
    public async Task The_priced_detail_keeps_each_legs_own_tags_and_the_transactions()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2026, 1, 10),
            new TransactionNotesRequest
            {
                Side = seed.CheckingId, Tags = ["house"],
                Lines =
                [
                    new TransactionLineNotesRequest { Index = 0, Tags = ["principal"] },
                    new TransactionLineNotesRequest { Index = 1, Tags = ["interest"] },
                ],
            });

        var detail = (await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}"))!;

        IReadOnlyList<string> SourceTags(int posting) =>
            detail.Legs.Single(l => l.PostingIndex == posting && l.AccountId == seed.CheckingId).LegTags;
        // The split is priced: the interest line is not the template's placeholder.
        Assert.NotEqual(1m, Math.Abs(detail.Legs.Single(l => l.AccountId == seed.InterestId).Amount));
        Assert.Equal(["house"], detail.Tags);
        Assert.Equal(["principal"], SourceTags(0));
        Assert.Equal(["interest"], SourceTags(1));
        Assert.Empty(SourceTags(2));
    }

    [Fact]
    public async Task Manage_list_amount_is_the_computed_full_payment()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2026, 1, 10));

        var list = await client.GetFromJsonAsync<List<ReminderSummary>>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders");

        var summary = Assert.Single(list!, x => x.Id == reminderId);
        Assert.InRange(summary.Amount, -2895m, -2880m);
    }

    [Fact]
    public async Task Clone_fire_commits_the_computed_split_not_the_template_zeros()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var occurrence = new DateOnly(2026, 1, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);

        // The verbatim clone the auto-post job writes (NOT /fire/bank): must still
        // commit the computed split, so the job cannot post the near-zero template legs.
        var headerId = await ReminderCloneFire.PostedAsync(Db, seed.Ledger.LedgerId, reminderId, occurrence);

        await using var db = Db.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToListAsync();
        decimal NetOn(Guid accountId) => legs.Where(l => l.AccountId == accountId).Sum(l => l.Amount);

        Assert.Equal(Interest, NetOn(seed.InterestId));            // interest leg, exact
        Assert.Equal(Escrow, NetOn(seed.EscrowId));                // escrow leg, exact
        Assert.InRange(NetOn(seed.LoanId), 1050m, 1058m);          // principal leg
        Assert.InRange(NetOn(seed.CheckingId), -2895m, -2880m);    // source outflow = -(full payment)
    }

    // ADR-0114 D15: the payment rounds up to one minor unit of the loan's currency and the
    // interest rounds at its places, so a whole-unit loan posts whole figures. At two places
    // the same terms give an interest and a payment with a fractional part.
    [Fact]
    public async Task A_whole_unit_loans_fire_posts_whole_interest_and_principal_summing_to_the_payment()
    {
        var seed = await SeedLoanAsync("JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var occurrence = new DateOnly(2026, 1, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);

        var headerId = await ReminderCloneFire.PostedAsync(Db, seed.Ledger.LedgerId, reminderId, occurrence);

        await using var db = Db.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
        decimal NetOn(Guid accountId) => legs.Where(l => l.AccountId == accountId).Sum(l => l.Amount);
        var interest = NetOn(seed.InterestId);
        var principal = NetOn(seed.LoanId);

        Assert.Equal(decimal.Truncate(interest), interest);
        Assert.Equal(decimal.Truncate(principal), principal);
        Assert.Equal(decimal.Round(Interest), interest);
        var payment = Coffer.Api.Loans.LoanAmortization.PeriodicPayment(500000m, 4.00m, 360, 12, "JPY");
        Assert.Equal(payment, interest + principal);
        Assert.Equal(-(payment + Escrow), NetOn(seed.CheckingId));
    }

    [Fact]
    public async Task Split_owed_ignores_merged_away_duplicate_payments()
    {
        // Owed must come from the register's canonical balance
        // (account_current_balances), which excludes is_merged_into duplicates —
        // not a raw txn_legs re-sum, under which a merged-away duplicate payment
        // double-counts principal, under-stating owed and so under-charging
        // interest / over-paying principal on every split.
        var seed = await SeedLoanAsync();   // opening balance is -OwedBalance
        var utc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        // A real payment credits principal → owed drops by that payment…
        var (winnerLoanLeg, _) = await seed.Ledger.AddTransactionPairAsync(
            seed.LoanId, seed.CheckingId, 10000m, utc);
        // …and an imported duplicate of it, merged into the winner, must NOT
        // count toward owed.
        var (dupLoanLeg, _) = await seed.Ledger.AddTransactionPairAsync(
            seed.LoanId, seed.CheckingId, 10000m, utc);
        await seed.Ledger.MarkTransactionMergedAsync(dupLoanLeg, winnerLoanLeg);
        // The harness merge is a raw flag flip; recompute so balance_after
        // (→ account_current_balances) drops the merged duplicate, as the real
        // merge endpoint would.
        await using (var rc = Db.NewDbContext())
            await rc.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_recompute_balances_for_account({seed.LoanId}, '0001-01-01'::timestamptz);");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var occurrence = new DateOnly(2026, 2, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);

        var headerId = await ReminderCloneFire.PostedAsync(Db, seed.Ledger.LedgerId, reminderId, occurrence);

        await using var db = Db.NewDbContext();
        var interestLeg = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == seed.InterestId)
            .SumAsync(l => l.Amount);
        // owed = OwedBalance less ONE payment → interest = round(owed × rate / 12).
        // A raw re-sum would count the merged duplicate → owed less TWO payments
        // → too little interest.
        Assert.Equal(1300.00m, interestLeg);
    }

    // ----- The split is priced as of the payment's own date (ADR-0078, 2026-10-21) ----
    //
    // Owed is the loan's balance just before the payment in the register's order: every
    // row dated before the payment's posted_at, plus a row on that same instant that
    // precedes it by seq, which at posting time is every such row already entered (the
    // payment takes the next seq). Nothing dated after it counts. No "today" enters it,
    // in any time zone, so neither the caller's date, nor when the job runs, nor who
    // fires the occurrence can move it. Each test below sends a caller's date (`today`)
    // the branch before this ruling honoured, to prove that nothing reads it now.

    /// <summary>round(owed × rate / periods), the interest LoanAmortization charges at
    /// the seeded rate, to the cent.</summary>
    private static decimal InterestOn(decimal owed) =>
        Math.Round(owed * 4.00m / 100m / 12m, 2, MidpointRounding.AwayFromZero);

    /// <summary>The clone the auto-post job writes for one occurrence, outside a run, so
    /// no run's date is in play either.</summary>
    private Task<Guid> CloneFireAsync(SyntheticLedger ledger, Guid reminderId, DateOnly occurrence) =>
        ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, reminderId, occurrence);

    /// <summary>The interest the occurrence dialog previews for one occurrence.</summary>
    private static async Task<decimal> PreviewInterestAsync(
        HttpClient client, SyntheticLedger ledger, Guid reminderId, Guid interestId,
        DateOnly occurrence, DateOnly callerToday)
    {
        var detail = await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}"
            + $"?occurrenceDate={occurrence:yyyy-MM-dd}&today={callerToday:yyyy-MM-dd}");
        return Math.Abs(detail!.Legs.Single(l => l.AccountId == interestId).Amount);
    }

    private async Task<decimal> NetOnAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .SumAsync(l => l.Amount);
    }

    [Fact]
    public async Task A_payment_fired_ahead_and_dated_before_the_occurrence_is_in_what_it_is_priced_on()
    {
        var seed = await SeedLoanAsync();   // opening balance is -OwedBalance
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var march = new DateOnly(2026, 3, 10);
        var april = new DateOnly(2026, 4, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, march);

        // Both occurrences are posted ahead, and previewed by a caller whose date is
        // before either: the register shows both as Scheduled for that caller.
        var ahead = new DateOnly(2026, 3, 1);

        // Nothing is dated before March: owed is the opening balance.
        var marchHeader = await CloneFireAsync(seed.Ledger, reminderId, march);
        Assert.Equal(Interest, await NetOnAsync(marchHeader, seed.InterestId));
        var marchPrincipal = await NetOnAsync(marchHeader, seed.LoanId);

        // April is priced net of March's principal, because March is dated before it.
        var expected = InterestOn(OwedBalance - marchPrincipal);
        Assert.NotEqual(Interest, expected);   // the case discriminates

        Assert.Equal(expected, await PreviewInterestAsync(
            client, seed.Ledger, reminderId, seed.InterestId, april, ahead));
        var aprilHeader = await CloneFireAsync(seed.Ledger, reminderId, april);
        Assert.Equal(expected, await NetOnAsync(aprilHeader, seed.InterestId));
    }

    [Fact]
    public async Task The_preview_prices_the_requested_occurrence_not_the_next_due_one()
    {
        // The series' next due occurrence is March, still un-acted; the dialog opens
        // April. A row dated between them counts for April and not for March, so a
        // preview that priced the next due occurrence instead of the requested one would
        // show March's split in April's dialog.
        var seed = await SeedLoanAsync();   // opening balance is -OwedBalance
        var march = new DateOnly(2026, 3, 10);
        var april = new DateOnly(2026, 4, 10);
        await seed.Ledger.AddTransactionPairAsync(
            seed.LoanId, seed.CheckingId, 10000m,
            march.AddDays(10).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, march);

        var unrequested = await client.GetFromJsonAsync<ReminderDetail>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}");
        Assert.Equal(march, unrequested!.NextDueDate);
        // Without a requested occurrence it is the next due one, March: the opening balance.
        Assert.Equal(Interest, Math.Abs(unrequested.Legs.Single(l => l.AccountId == seed.InterestId).Amount));

        // April, requested: net of the row dated before it.
        var expected = InterestOn(OwedBalance - 10000m);
        Assert.NotEqual(Interest, expected);   // the case discriminates
        Assert.Equal(expected, await PreviewInterestAsync(
            client, seed.Ledger, reminderId, seed.InterestId, april, march));
    }

    [Fact]
    public async Task A_payment_dated_after_the_occurrence_is_not_counted_against_it()
    {
        var seed = await SeedLoanAsync();   // opening balance is -OwedBalance
        var april = new DateOnly(2026, 4, 10);
        var aprilInstant = april.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // On the occurrence's own date and entered before it is posted: it precedes the
        // payment in the register (same posted_at, lower seq), so it counts.
        await seed.Ledger.AddTransactionPairAsync(seed.LoanId, seed.CheckingId, 10000m, aprilInstant);
        // Dated after the occurrence: it follows the payment in the register, so it does
        // not count, although the caller's date below is past it.
        await seed.Ledger.AddTransactionPairAsync(
            seed.LoanId, seed.CheckingId, 10000m, aprilInstant.AddDays(10));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        // The managed reminder (linked to the loan), which the adjusted fire re-splits.
        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.LoanId}/payment-reminder",
            new SetupPaymentReminderRequest(seed.CheckingId, april));
        setup.EnsureSuccessStatusCode();
        var reminderId = (await setup.Content.ReadFromJsonAsync<ReminderIdResponse>())!.ReminderId;
        var late = new DateOnly(2026, 5, 1);

        // owed = OwedBalance less the same-day payment only → round(owed × rate / 12).
        // Counting the later one too would give InterestOn(OwedBalance - 20000).
        var expected = InterestOn(OwedBalance - 10000m);
        Assert.Equal(1300.00m, expected);
        Assert.Equal(expected, await PreviewInterestAsync(
            client, seed.Ledger, reminderId, seed.InterestId, april, late));

        // The adjusted fire (the occurrence dialog's Post) prices it the same way.
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire/bank?today={late:yyyy-MM-dd}",
            new FireBankReminderRequest
            {
                OccurrenceDate = april,
                SourceAccountId = seed.CheckingId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.LoanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.InterestId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.EscrowId, Amount = -1m },
                },
            });
        fire.EnsureSuccessStatusCode();
        var headerId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;
        Assert.Equal(expected, await NetOnAsync(headerId, seed.InterestId));

        // And owed is exactly the register's running balance just before the payment.
        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(b => b.HeaderId == headerId && b.AccountId == seed.LoanId);
        Assert.Equal(-(OwedBalance - 10000m), row.BalanceAfter - row.NetAmount);
    }

    [Fact]
    public async Task The_auto_post_job_prices_the_split_as_of_the_payments_date_whatever_its_run_time_or_zone()
    {
        // When the job runs, and in which zone, decides only what is due. Three ledgers
        // with the same loan and the same rows: one posted by the job on the previous
        // evening west of UTC, one by the job on the due date in UTC, one by the job's
        // clone outside any run, after a preview by a caller whose date is before both
        // rows. All three split alike, and the preview agrees with them.
        var occurrence = new DateOnly(2026, 3, 10);
        var occurrenceInstant = occurrence.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();

        async Task<(SyntheticLedger Ledger, Guid InterestId, Guid ReminderId, HttpClient Client)> ArrangeAsync()
        {
            var seed = await SeedLoanAsync();   // opening balance is -OwedBalance
            // Dated the day before the occurrence: counts.
            await seed.Ledger.AddTransactionPairAsync(
                seed.LoanId, seed.CheckingId, 10000m, occurrenceInstant.AddDays(-1));
            // Dated the day after: does not.
            await seed.Ledger.AddTransactionPairAsync(
                seed.LoanId, seed.CheckingId, 10000m, occurrenceInstant.AddDays(1));
            var client = await AuthedClientAsync(factory, seed.Ledger);
            var reminderId = await CreateLoanReminderAsync(
                client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);
            await using var setup = Db.NewDbContext();
            await setup.RecurringTransactions
                .Where(r => r.Id == reminderId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.AutoCommitDaysBefore, 3));
            return (seed.Ledger, seed.InterestId, reminderId, client);
        }

        async Task<decimal> RunJobAsync(
            SyntheticLedger ledger, Guid reminderId, Guid interestId, DateTimeOffset instant, string zone,
            DateOnly expectedLocalToday)
        {
            var run = new JobRunClock(instant.UtcDateTime, zone);
            Assert.Equal(expectedLocalToday, run.LocalToday());
            var handler = new ReminderAutoPostJobHandler(
                Db.NewServiceFactory(),
                new RecurrenceExpander(),
                new NotificationPublisher(
                    Db.NewServiceFactory(),
                    Db.NewLedgerKeyService(),
                    Array.Empty<INotificationSubscriber>(),
                    NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
                NullLogger<ReminderAutoPostJobHandler>.Instance,
                new TestClock(instant),
                new ReminderAutoPostLimits(CatchUpDays: 0));
            await using (var serviceDb = Db.NewServiceDbContext())
            {
                var outcome = await handler.RunAsync(serviceDb, ledger.LedgerId, ledger.UserId, run, default);
                Assert.Equal(JobRunResult.Ok, outcome.Result);
            }
            await using var db = Db.NewDbContext();
            var headerId = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.RecurringTransactionId == reminderId && !h.IsRecurringTemplate)
                .Select(h => h.Id)
                .SingleAsync();
            return await NetOnAsync(headerId, interestId);
        }

        // owed = OwedBalance less the earlier row only → round(owed × rate / 12).
        var expected = InterestOn(OwedBalance - 10000m);

        // 03:00 UTC two days before the due date: the previous evening in a zone west of
        // UTC, and a UTC date on which the earlier row is still in the future.
        var west = await ArrangeAsync();
        Assert.Equal(expected, await RunJobAsync(
            west.Ledger, west.ReminderId, west.InterestId,
            new DateTimeOffset(occurrenceInstant.AddDays(-2).AddHours(3), TimeSpan.Zero),
            "America/New_York", occurrence.AddDays(-3)));

        // Midday on the due date in UTC.
        var utc = await ArrangeAsync();
        Assert.Equal(expected, await RunJobAsync(
            utc.Ledger, utc.ReminderId, utc.InterestId,
            new DateTimeOffset(occurrenceInstant.AddHours(12), TimeSpan.Zero),
            "UTC", occurrence));

        // Outside a run, previewed first by a caller whose date is before both rows.
        var hand = await ArrangeAsync();
        var ahead = occurrence.AddDays(-5);
        Assert.Equal(expected, await PreviewInterestAsync(
            hand.Client, hand.Ledger, hand.ReminderId, hand.InterestId, occurrence, ahead));
        var handHeader = await CloneFireAsync(hand.Ledger, hand.ReminderId, occurrence);
        Assert.Equal(expected, await NetOnAsync(handHeader, hand.InterestId));

        foreach (var c in new[] { west.Client, utc.Client, hand.Client }) c.Dispose();
    }

    private sealed record ReminderIdResponse(Guid ReminderId);

    [Fact]
    public async Task Fire_bank_recomputes_a_managed_loan_split_and_advances_the_cursor()
    {
        // A managed loan reminder's split is server-authoritative: /fire/bank must
        // recompute it from the loan terms + balance (ignoring whatever amounts
        // the client sends) AND advance the cursor past the fired slot.
        var seed = await SeedLoanAsync();   // owed OwedBalance → interest Interest
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var start = new DateOnly(2026, 1, 10);

        // Set up the managed reminder via the account editor endpoint (links
        // loan_account_id + is_loan_reminder), paying from checking.
        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.LoanId}/payment-reminder",
            new SetupPaymentReminderRequest(seed.CheckingId, start));
        setup.EnsureSuccessStatusCode();
        var reminderId = (await setup.Content.ReadFromJsonAsync<ReminderIdResponse>())!.ReminderId;

        // Fire the first occurrence with DELIBERATELY WRONG placeholder amounts.
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = start,
                SourceAccountId = seed.CheckingId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.LoanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.InterestId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.EscrowId, Amount = -1m },
                },
            });
        fire.EnsureSuccessStatusCode();
        var headerId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        await using var db = Db.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
        decimal NetOn(Guid accountId) => legs.Where(l => l.AccountId == accountId).Sum(l => l.Amount);
        // Server recomputed despite the placeholder client amounts.
        Assert.Equal(Interest, NetOn(seed.InterestId));            // interest leg, exact
        Assert.Equal(Escrow, NetOn(seed.EscrowId));                // escrow leg, exact
        Assert.InRange(NetOn(seed.LoanId), 1050m, 1058m);          // principal

        // Cursor advanced past the fired slot (no stale next-due → no phantom backlog).
        var nextDue = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.NextDueDate).FirstAsync();
        Assert.Equal(new DateOnly(2026, 2, 10), nextDue);
    }

    // ------------------------------------------------------------------
    // A managed loan payment stays in the loan's currency (ADR-0114 D13):
    // setup is refused across currencies, and a series an account's later
    // currency change left across them is refused by both fires.
    // ------------------------------------------------------------------

    private static Task<HttpResponseMessage> SetUpPaymentAsync(
        HttpClient client, SyntheticLedger ledger, Guid loanId, Guid payingId, DateOnly start) =>
        client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(payingId, start));

    private static async Task AssertLoanAcrossCurrenciesAsync(
        HttpResponseMessage response, string loanCurrency, string otherCurrency)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("payment-reminder-currency-mismatch", doc.RootElement.GetProperty("code").GetString());
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains($"loan is in {loanCurrency}", detail, StringComparison.Ordinal);
        Assert.Contains($"account in {otherCurrency}", detail, StringComparison.Ordinal);
    }

    private async Task<int> SeriesCountAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        return await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledger.LedgerId);
    }

    [Fact]
    public async Task Setting_up_a_loan_payment_from_an_account_in_another_currency_is_refused()
    {
        var seed = await SeedLoanAsync(checkingCurrency: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        await AssertLoanAcrossCurrenciesAsync(
            await SetUpPaymentAsync(client, seed.Ledger, seed.LoanId, seed.CheckingId, new DateOnly(2026, 1, 10)),
            "USD", "EUR");
        Assert.Equal(0, await SeriesCountAsync(seed.Ledger));
    }

    [Fact]
    public async Task Setting_up_a_loan_payment_whose_real_escrow_account_is_in_another_currency_is_refused()
    {
        // The paying account is in the loan's currency; only the escrow is not.
        var seed = await SeedLoanAsync(escrowCurrency: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        await AssertLoanAcrossCurrenciesAsync(
            await SetUpPaymentAsync(client, seed.Ledger, seed.LoanId, seed.CheckingId, new DateOnly(2026, 1, 10)),
            "USD", "EUR");
        Assert.Equal(0, await SeriesCountAsync(seed.Ledger));
    }

    /// <summary>
    /// A category escrow and interest hold the ledger's reporting currency, but their
    /// lines mirror the paying account's, so a loan kept outside the reporting currency
    /// and paid from an account in its own sets up (ADR-0114 D13).
    /// </summary>
    [Fact]
    public async Task A_loan_outside_the_reporting_currency_with_category_escrow_and_interest_sets_up()
    {
        var seed = await SeedLoanAsync(currencyCode: "CAD", escrowIsCategory: true);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var setup = await SetUpPaymentAsync(client, seed.Ledger, seed.LoanId, seed.CheckingId, new DateOnly(2026, 1, 10));
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        Assert.Equal(1, await SeriesCountAsync(seed.Ledger));
    }

    /// <summary>
    /// A series set up in one currency whose paying account is then moved to another can
    /// never post as it stands: the clone fire the auto-post job writes and the occurrence
    /// dialog's fire both refuse it, naming both currencies, and nothing is written
    /// (ADR-0114 D13). The same series posted before the edit.
    /// </summary>
    [Fact]
    public async Task A_loan_payment_a_currency_edit_left_across_currencies_is_refused_by_both_fires()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var first = new DateOnly(2026, 1, 10);
        var second = new DateOnly(2026, 2, 10);
        var setup = await SetUpPaymentAsync(client, seed.Ledger, seed.LoanId, seed.CheckingId, first);
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        var reminderId = (await setup.Content.ReadFromJsonAsync<ReminderIdResponse>())!.ReminderId;

        // The anchor: in one currency the series posts.
        await ReminderCloneFire.PostedAsync(Db, seed.Ledger.LedgerId, reminderId, first);

        var edit = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.CheckingId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = "EUR" }),
        });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var clone = await ReminderCloneFire.FireAsync(Db, seed.Ledger.LedgerId, reminderId, second);
        Assert.Equal(RemindersRepository.FireOutcome.LoanAcrossCurrencies, clone.Outcome);
        Assert.Equal(new RemindersRepository.LoanPaymentCurrencies("USD", "EUR"), clone.LoanCurrencies);

        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = second,
                SourceAccountId = seed.CheckingId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.LoanId, Amount = -1m, CounterpartyAmount = 1m },
                    new TransactionPosting { CounterpartyAccountId = seed.InterestId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.EscrowId, Amount = -1m, CounterpartyAmount = 1m },
                },
            });
        await AssertLoanAcrossCurrenciesAsync(fire, "USD", "EUR");

        await using var db = Db.NewDbContext();
        Assert.False(await db.TxnHeaders.AsNoTracking()
            .AnyAsync(h => h.RecurringTransactionId == reminderId && h.OccurrenceDate == second && !h.IsRecurringTemplate));
    }

    [Fact]
    public async Task Agenda_hides_occurrences_on_or_before_the_ack_floor()
    {
        // A long-running imported series: the agenda window can span dates before
        // the acknowledged floor, but those were handled pre-import and must NOT
        // surface — as a reminder OR as a "skipped" chip. GetUpcomingAsync now
        // applies the same ADR-0051 floor ComputeNextDueAsync uses for the cursor.
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2020, 1, 10));

        var floor = new DateOnly(2024, 6, 10);
        await using (var setup = Db.NewDbContext())
        {
            await setup.RecurringTransactions
                .Where(r => r.Id == reminderId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastAcknowledgedDate, floor));
        }

        // Window straddles the floor (Jan–Dec 2024).
        var upcoming = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/upcoming?from=2024-01-01&to=2024-12-31");

        var mine = upcoming!.Where(x => x.ReminderId == reminderId).ToList();
        Assert.NotEmpty(mine);
        // Nothing on/before the floor; the post-floor slots (Jul–Dec) are present.
        Assert.All(mine, x => Assert.True(x.Date > floor,
            $"agenda returned {x.Date}, which is on/before the ack floor {floor}"));
        Assert.Contains(mine, x => x.Date == new DateOnly(2024, 7, 10));
    }

    [Fact]
    public async Task Fire_advances_cursor_past_the_slot_and_never_skips_below_the_ack_floor()
    {
        // A loan reminder started years ago, whose acknowledged floor sits one month
        // before the next slot (the shape an imported reminder carries). Firing
        // that slot must (1) advance the cursor to the FOLLOWING occurrence — not
        // strand it on the just-fired slot — and (2) not retro-skip the pre-floor
        // history (which would litter the agenda with phantom "skipped" chips).
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2012, 3, 4));

        // Simulate the imported state: cursor on the first slot after the floor,
        // acknowledged through the prior month (Moneydance's ack date).
        var floor = new DateOnly(2025, 2, 10);
        var firedSlot = new DateOnly(2025, 3, 10);
        await using (var setup = Db.NewDbContext())
        {
            await setup.RecurringTransactions
                .Where(r => r.Id == reminderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.NextDueDate, firedSlot)
                    .SetProperty(r => r.LastAcknowledgedDate, floor));
        }

        // Posted by hand, which is the path that cascades.
        var fired = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = firedSlot,
                SourceAccountId = seed.CheckingId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.LoanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.InterestId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = seed.EscrowId, Amount = -500.00m },
                },
            });
        fired.EnsureSuccessStatusCode();

        await using var db = Db.NewDbContext();
        var nextDue = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.NextDueDate).FirstAsync();
        Assert.Equal(new DateOnly(2025, 4, 10), nextDue);   // advanced past the fired slot

        // No slot exists strictly between the floor and the fired slot, so the
        // catch-up cascade skips nothing — no years of pre-floor phantom backlog.
        var skips = await db.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == reminderId);
        Assert.Equal(0, skips);
    }
}
