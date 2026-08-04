using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Loan-aware reminder occurrences (ADR-0050 slice 4): a loan reminder's
/// template carries mostly-zero principal/interest legs (MD computes them live),
/// so every surface that shows or materializes the amount must override it with
/// the computed full payment (principal + interest + escrow) derived from
/// <c>loan_terms</c> + the loan account's current balance. These tests pin all
/// three readers/writers: the agenda, the manage-page list, and the verbatim
/// clone-fire. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RemindersLoanTests
{
    private readonly PostgresFixture _fixture;

    public RemindersLoanTests(PostgresFixture fixture) => _fixture = fixture;

    // $500,000 @ 4.00% / 360 monthly payments, $400,000.00 still owed, escrow
    // $500.00. Payment ≈ $2,387; interest = round(400000 × 4.00%/12) =
    // $1,333.33; principal ≈ $1,054; full payment ≈ $2,887.
    private const decimal OwedBalance = 400000.00m;
    private const decimal Escrow = 500.00m;
    private const decimal Interest = 1333.33m;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private async Task<(SyntheticLedger Ledger, Guid LoanId, Guid InterestId, Guid EscrowId, Guid CheckingId)>
        SeedLoanAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var interest = await ledger.AddCategoryAsync("mortgage-interest", "expense");
        var escrow = await ledger.AddBankAccountAsync("escrow");

        var loanId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        // Loan account whose negative balance (liability) is the amount owed.
        db.Accounts.Add(new AccountRow
        {
            Id = loanId, LedgerId = ledger.LedgerId, Name = "Mortgage",
            AccountType = "loan", CurrencyCode = "USD",
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
        Guid checkingId, Guid loanId, Guid interestId, Guid escrowId, DateOnly start)
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
            });
        created.EnsureSuccessStatusCode();
        var reminderId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Mark it a loan reminder (the importer would set this from MD).
        await using var db = _fixture.NewDbContext();
        await db.RecurringTransactions
            .Where(r => r.Id == reminderId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsLoanReminder, true));
        return reminderId;
    }

    [Fact]
    public async Task Agenda_amount_is_the_computed_full_payment()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2026, 1, 10));

        var upcoming = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-01-31");

        var occ = Assert.Single(upcoming!, x => x.ReminderId == reminderId);
        // Full payment ≈ -$2,887 (a source-side outflow) — NOT the escrow-only
        // -500.00 the template legs would net.
        Assert.InRange(occ.Amount, -2895m, -2880m);
    }

    [Fact]
    public async Task Manage_list_amount_is_the_computed_full_payment()
    {
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var occurrence = new DateOnly(2026, 1, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);

        // The verbatim-clone fire route (NOT /fire/bank): must still commit the
        // computed split, so a future auto-commit worker / direct API call can't
        // post the near-zero template legs.
        var fired = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire",
            new FireReminderRequest { OccurrenceDate = occurrence });
        fired.EnsureSuccessStatusCode();
        var headerId = (await fired.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToListAsync();
        decimal NetOn(Guid accountId) => legs.Where(l => l.AccountId == accountId).Sum(l => l.Amount);

        Assert.Equal(Interest, NetOn(seed.InterestId));            // interest leg, exact
        Assert.Equal(Escrow, NetOn(seed.EscrowId));                // escrow leg, exact
        Assert.InRange(NetOn(seed.LoanId), 1050m, 1058m);          // principal leg
        Assert.InRange(NetOn(seed.CheckingId), -2895m, -2880m);    // source outflow = -(full payment)
    }

    [Fact]
    public async Task Split_owed_ignores_merged_away_duplicate_payments()
    {
        // Regression (mortgage-split drift): owed must come from the register's
        // canonical balance (account_current_balances), which excludes
        // is_merged_into duplicates — not a raw txn_legs re-sum. A merged-away
        // duplicate payment used to double-count principal, under-stating owed
        // and so under-charging interest / over-paying principal on every split.
        var seed = await SeedLoanAsync();   // opening −400,000
        var utc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        // A real payment credits $10,000 principal → owed 390,000…
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
        await using (var rc = _fixture.NewDbContext())
            await rc.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_recompute_balances_for_account({seed.LoanId}, '0001-01-01'::timestamptz);");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var occurrence = new DateOnly(2026, 2, 10);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId, occurrence);

        var fired = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire",
            new FireReminderRequest { OccurrenceDate = occurrence });
        fired.EnsureSuccessStatusCode();
        var headerId = (await fired.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        await using var db = _fixture.NewDbContext();
        var interestLeg = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == seed.InterestId)
            .SumAsync(l => l.Amount);
        // owed 390,000 → interest round(390000 × 4%/12) = 1300.00. The old raw
        // re-sum counted the merged duplicate → owed 380,000 → 1266.67.
        Assert.Equal(1300.00m, interestLeg);
    }

    private sealed record ReminderIdResponse(Guid ReminderId);

    [Fact]
    public async Task Fire_bank_recomputes_a_managed_loan_split_and_advances_the_cursor()
    {
        // A managed loan reminder's split is server-authoritative: /fire/bank must
        // recompute it from the loan terms + balance (ignoring whatever amounts
        // the client sends) AND advance the cursor past the fired slot.
        var seed = await SeedLoanAsync();   // owed 400,000 → interest 1,333.33
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var start = new DateOnly(2026, 1, 10);

        // Set up the managed reminder via the account editor endpoint (links
        // loan_account_id + is_loan_reminder), paying from checking.
        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.LoanId}/payment-reminder",
            new SetupPaymentReminderRequest(seed.CheckingId, start));
        setup.EnsureSuccessStatusCode();
        var reminderId = (await setup.Content.ReadFromJsonAsync<ReminderIdResponse>())!.ReminderId;

        // Fire the first occurrence with DELIBERATELY WRONG amounts (-1 each).
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

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
        decimal NetOn(Guid accountId) => legs.Where(l => l.AccountId == accountId).Sum(l => l.Amount);
        // Server recomputed despite the -1 client amounts.
        Assert.Equal(Interest, NetOn(seed.InterestId));            // 1,333.33
        Assert.Equal(Escrow, NetOn(seed.EscrowId));                // 500.00
        Assert.InRange(NetOn(seed.LoanId), 1050m, 1058m);          // principal

        // Cursor advanced past the fired slot (no stale next-due → no phantom backlog).
        var nextDue = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.NextDueDate).FirstAsync();
        Assert.Equal(new DateOnly(2026, 2, 10), nextDue);
    }

    [Fact]
    public async Task Agenda_hides_occurrences_on_or_before_the_ack_floor()
    {
        // A long-running imported series: the agenda window can span dates before
        // the acknowledged floor, but those were handled pre-import and must NOT
        // surface — as a reminder OR as a "skipped" chip. GetUpcomingAsync now
        // applies the same ADR-0051 floor ComputeNextDueAsync uses for the cursor.
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2020, 1, 10));

        var floor = new DateOnly(2024, 6, 10);
        await using (var setup = _fixture.NewDbContext())
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
        // Regression for the imported-mortgage bug: a decade-old loan reminder
        // whose acknowledged floor sits one month before the next slot. Firing
        // that slot must (1) advance the cursor to the FOLLOWING occurrence — not
        // strand it on the just-fired slot — and (2) not retro-skip the pre-floor
        // history (which would litter the agenda with phantom "skipped" chips).
        var seed = await SeedLoanAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var reminderId = await CreateLoanReminderAsync(
            client, seed.Ledger, seed.CheckingId, seed.LoanId, seed.InterestId, seed.EscrowId,
            new DateOnly(2015, 6, 9));

        // Simulate the imported state: cursor on the first slot after the floor,
        // acknowledged through the prior month (Moneydance's ack date).
        var floor = new DateOnly(2026, 6, 10);
        var firedSlot = new DateOnly(2026, 7, 10);
        await using (var setup = _fixture.NewDbContext())
        {
            await setup.RecurringTransactions
                .Where(r => r.Id == reminderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.NextDueDate, firedSlot)
                    .SetProperty(r => r.LastAcknowledgedDate, floor));
        }

        var fired = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reminders/{reminderId}/fire",
            new FireReminderRequest { OccurrenceDate = firedSlot });
        fired.EnsureSuccessStatusCode();

        await using var db = _fixture.NewDbContext();
        var nextDue = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.NextDueDate).FirstAsync();
        Assert.Equal(new DateOnly(2026, 8, 10), nextDue);   // advanced past the fired slot

        // No slot exists strictly between the floor and the fired slot, so the
        // catch-up cascade skips nothing — no decade of pre-floor phantom backlog.
        var skips = await db.RecurringOccurrenceExceptions.AsNoTracking()
            .CountAsync(e => e.RecurringTransactionId == reminderId);
        Assert.Equal(0, skips);
    }
}
