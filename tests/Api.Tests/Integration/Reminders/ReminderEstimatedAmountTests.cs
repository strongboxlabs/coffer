using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// A reminder that estimates its amount from its own recent history (migration 220).
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is asserted before anything is built on top of it, because the two
/// decisions most likely to be wrong are invisible from the outside: WHICH occurrences
/// fall in the sample window, and whether a deleted occurrence consumes a slot in it.
/// </para>
/// <para>
/// Amounts here are chosen so the mean of any wrong subset differs from the mean of the
/// right one. Round numbers that average to the same figure whichever three you pick
/// would make these tests pass against an off-by-one window.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderEstimatedAmountTests : DatabaseTest
{
    public ReminderEstimatedAmountTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private sealed record Setup(SyntheticLedger Ledger, HttpClient Client, Guid SeriesId, string BaseUrl, Guid BankId);

    /// <summary>A monthly bank reminder from 2026-01-01, optionally estimating, paid from an
    /// account in <paramref name="currencyCode"/>.</summary>
    private async Task<Setup> SeriesAsync(
        ApiFactory factory, int? sampleCount, decimal templateAmount = -100m, string currencyCode = "USD")
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking", currencyCode: currencyCode);
        var cat = await ledger.AddCategoryAsync("utilities");
        var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Electric",
                SourceAccountId = bank.Id,
                EstimateSampleCount = sampleCount,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = templateAmount },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        return new Setup(ledger, client, id, $"/api/ledgers/{ledger.LedgerId}/reminders/{id}", bank.Id);
    }

    /// <summary>
    /// Commit one occurrence at a chosen amount: fire it, then correct the amount.
    /// </summary>
    /// <remarks>
    /// Fired through the auto-post job's clone rather than seeded with SQL so the
    /// occurrence carries the real (series, date) stamp the sample window keys on — a raw
    /// insert would test the query against data the application never produces.
    /// </remarks>
    private async Task CommitOccurrenceAsync(Setup s, DateOnly date, decimal sourceNet)
    {
        await ReminderCloneFire.PostedAsync(Db, s.Ledger.LedgerId, s.SeriesId, date);

        await using var db = Db.NewServiceDbContext();
        var headerId = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == s.SeriesId
                        && h.OccurrenceDate == date
                        && !h.IsRecurringTemplate)
            .Select(h => h.Id)
            .SingleAsync();

        var srcAccount = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == s.SeriesId).Select(r => r.SourceAccountId!.Value).SingleAsync();

        var legs = await db.TxnLegs.Where(l => l.HeaderId == headerId).ToListAsync();
        foreach (var leg in legs)
            leg.Amount = leg.AccountId == srcAccount ? sourceNet : -sourceNet;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Turn estimation on for an existing series.
    /// </summary>
    /// <remarks>
    /// History has to be built BEFORE the estimate is enabled, because firing an
    /// estimating series with no history is refused — the feature's own safety rule.
    /// It is also the realistic order: the bill
    /// exists, and the user then asks for it to be estimated.
    /// </remarks>
    private static async Task EnableEstimateAsync(Setup s, int sampleCount)
    {
        var patch = await s.Client.PatchAsJsonAsync(
            s.BaseUrl, new EditReminderRequest { EstimateSampleCount = sampleCount });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }

    private async Task<UpcomingOccurrence?> SlotAsync(Setup s, DateOnly date)
    {
        var all = await s.Client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/upcoming"
            + $"?from={date.AddDays(-1):yyyy-MM-dd}&to={date.AddDays(1):yyyy-MM-dd}");
        return all?.SingleOrDefault(o => o.Date == date && o.ReminderId == s.SeriesId);
    }

    /// <summary>
    /// The estimate is the mean of the last N — not of all history, and not of the first N.
    /// </summary>
    [Fact]
    public async Task The_estimate_averages_the_last_n_occurrences()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        // Chosen so the mean of the last three differs from the mean of all four AND
        // from the mean of the FIRST three. Every wrong window gives a wrong number.
        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -40m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), -60m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 3, 1), -90m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 4, 1), -120m);

        await EnableEstimateAsync(s, 3);

        var slot = await SlotAsync(s, new DateOnly(2026, 5, 1));

        Assert.NotNull(slot);
        Assert.NotNull(slot!.Estimate);
        Assert.Equal(3, slot.Estimate!.AvailableSampleCount);
        Assert.Equal(-90.00m, slot.Estimate.Amount);
        Assert.Equal(-90.00m, slot.Amount);
        Assert.Null(slot.Estimate.UnavailableReason);
    }

    /// <summary>
    /// Fewer than N is fine: the average covers what exists.
    /// </summary>
    /// <remarks>
    /// The rule is "use N, or fewer if N are not available", which is what makes the
    /// estimate useful on a fresh ledger rather than only after N occurrences exist.
    /// </remarks>
    [Fact]
    public async Task Fewer_than_n_occurrences_still_produce_an_estimate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -30m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), -50m);

        await EnableEstimateAsync(s, 3);

        var slot = await SlotAsync(s, new DateOnly(2026, 3, 1));

        Assert.NotNull(slot!.Estimate);
        Assert.Equal(3, slot.Estimate!.RequestedSampleCount);
        Assert.Equal(2, slot.Estimate.AvailableSampleCount);
        Assert.Equal(-40.00m, slot.Estimate.Amount);
    }

    /// <summary>
    /// Zero history means NO estimate — never a guess from nothing.
    /// </summary>
    /// <remarks>
    /// The safety property. Auto-post commits whatever the estimate resolves to,
    /// unattended, so a null or an accidental zero reaching the write path is the worst
    /// outcome in the feature. The slot must fall back to the template amount and say why.
    /// </remarks>
    [Fact]
    public async Task No_history_means_no_estimate_and_the_template_amount_stands()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: 3, templateAmount: -100m);
        using var client = s.Client;

        var slot = await SlotAsync(s, new DateOnly(2026, 1, 1));

        Assert.NotNull(slot!.Estimate);
        Assert.Equal(0, slot.Estimate!.AvailableSampleCount);
        Assert.Null(slot.Estimate.Amount);
        Assert.Equal("no-history", slot.Estimate.UnavailableReason);
        Assert.Equal(-100.00m, slot.Amount);
    }

    /// <summary>
    /// A DELETED occurrence is excluded from the window, and does not consume a slot in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of exclusion and ranking is a real money difference and it is invisible
    /// from outside. Deleting a reminder occurrence SOFT-HIDES it (mig 218 made the
    /// (series, date) stamp an idempotency key, so the row has to survive), which means a
    /// deleted occurrence still carries its stamp.
    /// </para>
    /// <para>
    /// Ranking first and filtering after would let the deletion starve the window: the
    /// three most recent stamped rows would be the deleted one and the two before it, the deleted one would
    /// drop out, and the estimate would average only two. Filtering first keeps the
    /// window full from the real history behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_deleted_occurrence_leaves_the_sample_window_full()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -30m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), -60m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 3, 1), -90m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 4, 1), -900m);

        await EnableEstimateAsync(s, 3);

        // Delete the outlier. It keeps its stamp, so a rank-then-filter window would
        // still count it as one of the last three.
        Guid outlier;
        await using (var db = Db.NewServiceDbContext())
        {
            outlier = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.RecurringTransactionId == s.SeriesId
                            && h.OccurrenceDate == new DateOnly(2026, 4, 1))
                .Select(h => h.Id).SingleAsync();
        }
        var del = await client.DeleteAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{outlier}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var slot = await SlotAsync(s, new DateOnly(2026, 5, 1));

        Assert.NotNull(slot!.Estimate);
        // Three real samples (the three before the outlier), not two: filter-then-rank.
        Assert.Equal(3, slot.Estimate!.AvailableSampleCount);
        Assert.Equal(-60.00m, slot.Estimate.Amount);
    }

    /// <summary>
    /// A FIRED occurrence carries its real committed amount, with no estimate attached.
    /// </summary>
    /// <remarks>
    /// An estimate of a settled fact is noise, and worse — it would invite a reader to
    /// think the posted figure was a guess. The loan display override makes the same
    /// distinction.
    /// </remarks>
    [Fact]
    public async Task A_fired_occurrence_shows_its_real_amount_and_no_estimate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -45.67m);

        await EnableEstimateAsync(s, 3);

        var slot = await SlotAsync(s, new DateOnly(2026, 1, 1));

        Assert.NotNull(slot);
        Assert.Equal("scheduled", slot!.Kind);
        Assert.Null(slot.Estimate);
        Assert.Equal(-45.67m, slot.Amount);
    }

    /// <summary>
    /// A series that never opted in carries no estimate at all.
    /// </summary>
    [Fact]
    public async Task A_series_that_did_not_opt_in_has_no_estimate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -55m);

        var slot = await SlotAsync(s, new DateOnly(2026, 2, 1));

        Assert.NotNull(slot);
        Assert.Null(slot!.Estimate);
        Assert.Equal(-100.00m, slot.Amount);
    }

    /// <summary>
    /// Firing an estimated reminder COMMITS the estimate, not the template amount.
    /// </summary>
    /// <remarks>
    /// The display half is worth little on its own: a forecast that disagrees with what
    /// actually posts is worse than no forecast. The clone is what the auto-post job
    /// writes, so this is what a timer commits unattended.
    /// </remarks>
    [Fact]
    public async Task Firing_an_estimated_reminder_commits_the_estimate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null, templateAmount: -100m);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -20m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), -40m);

        await EnableEstimateAsync(s, 2);

        var slot = new DateOnly(2026, 3, 1);
        await ReminderCloneFire.PostedAsync(Db, s.Ledger.LedgerId, s.SeriesId, slot);

        await using var db = Db.NewServiceDbContext();
        var headerId = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == s.SeriesId
                        && h.OccurrenceDate == slot && !h.IsRecurringTemplate)
            .Select(h => h.Id).SingleAsync();
        var srcAccount = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == s.SeriesId).Select(r => r.SourceAccountId!.Value).SingleAsync();

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => new { l.AccountId, l.Amount })
            .ToListAsync();

        // The mean of the two committed amounts, not the template amount.
        Assert.Equal(-30.00m, legs.Where(l => l.AccountId == srcAccount).Sum(l => l.Amount));
        Assert.Equal(30.00m, legs.Where(l => l.AccountId != srcAccount).Sum(l => l.Amount));

        // And the posting still balances — the invariant the whole leg-pair mechanism
        // exists to preserve.
        Assert.Equal(0m, legs.Sum(l => l.Amount));
    }

    /// <summary>
    /// Firing is REFUSED when the series estimates and there is nothing to estimate from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative — committing the template amount — is the silent substitution this
    /// feature must not make. A user who asked for a computed figure has not asked for
    /// some other number to be posted when it cannot be computed, and on an unattended
    /// run nobody would see the swap.
    /// </para>
    /// <para>
    /// Asserts the template amount was NOT committed, not merely that the fire was
    /// refused: a refusal with a transaction written anyway would be the worst of both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Firing_is_refused_when_the_estimate_cannot_be_produced()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: 3, templateAmount: -100m);
        using var client = s.Client;

        var slot = new DateOnly(2026, 1, 1);
        var fire = await ReminderCloneFire.FireAsync(Db, s.Ledger.LedgerId, s.SeriesId, slot);

        Assert.Equal(
            Coffer.Api.Db.Repositories.RemindersRepository.FireOutcome.EstimateUnavailableForFire,
            fire.Outcome);

        await using var db = Db.NewServiceDbContext();
        Assert.Equal(
            0,
            await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.RecurringTransactionId == s.SeriesId
                                 && !h.IsRecurringTemplate));
    }

    /// <summary>
    /// A split reminder cannot be given an estimate — refused server-side, not just
    /// hidden in the UI.
    /// </summary>
    [Fact]
    public async Task A_split_reminder_cannot_estimate()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var a = await ledger.AddCategoryAsync("power");
        var b = await ledger.AddCategoryAsync("water");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Utilities",
                SourceAccountId = bank.Id,
                EstimateSampleCount = 3,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = a.Id, Amount = -60m },
                    new TransactionPosting { CounterpartyAccountId = b.Id, Amount = -40m },
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    /// <summary>
    /// Adding a split to a reminder that already estimates is refused too.
    /// </summary>
    /// <remarks>
    /// The create gate alone would leave the combination reachable by editing, which is
    /// the likelier route: the reminder exists, estimating works, and then someone splits
    /// it. Without this the series would be stored estimating-and-split, and the resolver
    /// would refuse forever with no way to tell why from the editor.
    /// </remarks>
    [Fact]
    public async Task An_estimating_reminder_cannot_be_given_a_split_by_editing()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: 3);
        using var client = s.Client;
        var second = await s.Ledger.AddCategoryAsync("water");

        var patch = await client.PatchAsJsonAsync(s.BaseUrl, new EditReminderRequest
        {
            EstimateSampleCount = 3,
            Postings = new PatchReminderPostings
            {
                SourceAccountId = (await FirstBankAccountIdAsync(s)),
                Items = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = second.Id, Amount = -60m },
                    new TransactionPosting { CounterpartyAccountId = second.Id, Amount = -40m },
                },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
    }

    /// <summary>N outside 1-24 is refused.</summary>
    [Fact]
    public async Task A_sample_count_outside_the_supported_range_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("power");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        foreach (var n in new[] { 0, 25 })
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/reminders",
                new CreateReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                    StartDate = new DateOnly(2026, 1, 1),
                    Payee = "Power",
                    SourceAccountId = bank.Id,
                    EstimateSampleCount = n,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -50m },
                    },
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
    }

    /// <summary>
    /// BULK-deleting an auto-posted occurrence does not bring it back either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deleted reminder occurrence soft-hides and keeps its (series, date) stamp — the
    /// idempotency key that stops auto-post re-posting it. That has to hold on the bulk
    /// path (<c>BulkTransactionsRepository</c>) as well as the single delete: a bulk delete
    /// that removed the stamp would let the timer resurrect every occurrence.
    /// </para>
    /// <para>
    /// Asserts on IDENTITY, not a count: hard-delete-then-re-post and soft-hide both leave
    /// exactly one row, so a count cannot tell the two apart.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Bulk_deleting_a_reminder_occurrence_soft_hides_it_and_keeps_the_stamp()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        var slot = new DateOnly(2026, 1, 1);
        await CommitOccurrenceAsync(s, slot, -55m);

        Guid headerId;
        await using (var db = Db.NewServiceDbContext())
        {
            headerId = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.RecurringTransactionId == s.SeriesId && !h.IsRecurringTemplate)
                .Select(h => h.Id).SingleAsync();
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit",
                    AccountId = s.BankId,
                    HeaderIds = new[] { headerId },
                },
            });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<BulkDeleteResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.HardDeleted);
        Assert.Equal(1, body.SoftHidden);

        await using var read = Db.NewServiceDbContext();
        var survivor = await read.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == s.SeriesId && !h.IsRecurringTemplate)
            .Select(h => new { h.Id, h.IsHidden, h.OccurrenceDate })
            .SingleAsync();

        Assert.Equal(headerId, survivor.Id);
        Assert.True(survivor.IsHidden, "the occurrence was not hidden, so the delete did nothing");
        Assert.Equal(slot, survivor.OccurrenceDate);
    }

    private async Task<Guid> FirstBankAccountIdAsync(Setup s)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == s.SeriesId)
            .Select(r => r.SourceAccountId!.Value)
            .SingleAsync();
    }

    /// <summary>
    /// The average rounds ONCE, at the source account's places.
    /// </summary>
    /// <remarks>
    /// Three amounts one minor unit apart whose mean is a repeating decimal, which must
    /// land on the nearest minor unit and not on a value the account's currency cannot
    /// hold.
    /// </remarks>
    [Fact]
    public async Task The_average_rounds_once_at_the_source_accounts_places()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -10.00m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), -10.01m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 3, 1), -10.01m);

        await EnableEstimateAsync(s, 3);

        var slot = await SlotAsync(s, new DateOnly(2026, 4, 1));

        Assert.Equal(-10.01m, slot!.Estimate!.Amount);
    }

    /// <summary>
    /// An average exactly on a half of the last minor unit rounds away from zero, at the
    /// source account's places (ADR-0114 D15): two places, none, and three.
    /// </summary>
    /// <remarks>
    /// Each pair of samples is one minor unit apart and averages to exactly half of one,
    /// with the even neighbour toward zero, so rounding half to even lands on a different
    /// figure, and rounding at two places for every currency lands on another again.
    /// </remarks>
    [Theory]
    [InlineData("USD", "-10.00", "-10.01", "-10.01")]
    [InlineData("JPY", "-100", "-101", "-101")]
    [InlineData("KWD", "-1.000", "-1.001", "-1.001")]
    public async Task An_average_on_a_half_rounds_away_from_zero_at_the_source_accounts_places(
        string currency, string firstSample, string secondSample, string expected)
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null, currencyCode: currency);
        using var client = s.Client;

        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), decimal.Parse(firstSample, CultureInfo.InvariantCulture));
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), decimal.Parse(secondSample, CultureInfo.InvariantCulture));

        await EnableEstimateAsync(s, 2);

        var slot = await SlotAsync(s, new DateOnly(2026, 3, 1));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), slot!.Estimate!.Amount);
        Assert.Null(slot.Estimate.UnavailableReason);
    }

    /// <summary>
    /// An average that rounds to zero at a whole-unit currency's places is no estimate
    /// (<c>zero-estimate</c>), though at two places it would be one.
    /// </summary>
    [Fact]
    public async Task An_average_that_rounds_to_zero_at_a_whole_unit_currency_is_a_zero_estimate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, sampleCount: null, currencyCode: "JPY");
        using var client = s.Client;

        // A mean of a third of one unit toward the template's direction.
        await CommitOccurrenceAsync(s, new DateOnly(2026, 1, 1), -1m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 2, 1), 1m);
        await CommitOccurrenceAsync(s, new DateOnly(2026, 3, 1), -1m);

        await EnableEstimateAsync(s, 3);

        var slot = await SlotAsync(s, new DateOnly(2026, 4, 1));

        Assert.Null(slot!.Estimate!.Amount);
        Assert.Equal("zero-estimate", slot.Estimate.UnavailableReason);
    }
}
