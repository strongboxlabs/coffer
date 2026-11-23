using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// An estimating reminder between real accounts in different currencies estimates both
/// sides (ADR-0114 D13): the source from its own history, as before, and the other side
/// per occurrence, from the source estimate at the derived rate its due date reads, rounded
/// once at the other account's places (ADR-0114 D15). With no rate for the due date the
/// occurrence has no estimate and its fire is refused; a rate is never written.
/// </summary>
/// <remarks>
/// <para>Every write is an API write on a synthetic ledger: the reminder and its two
/// amounts, the posted history (Post or skip…, with both amounts typed), the estimate, and
/// each rate, typed through the hand-entry route. The ledger reports in the source's
/// currency, so the derived rate is the inverse of the one stored leg, the other account's
/// currency into the reporting one.</para>
/// <para>The rate rule reads the server's UTC today (ADR-0114 D9), so the series is laid on
/// days counted back from it: one occurrence every ten days, the first two posted. Of the
/// un-posted ones, the first falls before any stored rate, the next two each read a
/// different stored rate, and one is due today and reads the latest rate.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderEstimateAcrossCurrenciesTests : DatabaseTest
{
    public ReminderEstimateAcrossCurrenciesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const string Rrule = "FREQ=DAILY;INTERVAL=10";

    private static DateOnly Start => Today.AddDays(-50);

    /// <summary>The template's source amount; the estimate differs from it.</summary>
    private const decimal TemplateAmount = -100m;

    /// <summary>The mean of the two posted occurrences' source amounts.</summary>
    private const decimal Estimate = -50m;

    /// <summary>The stored rates of one unit of the other account's currency in the
    /// reporting currency: the first two each read by one past occurrence, the last the
    /// latest rate, typed for yesterday so the pair is covered through it.</summary>
    private static (DateOnly Day, decimal Rate)[] Rates =>
    [
        (Today.AddDays(-25), 1.25m),
        (Today.AddDays(-15), 1.6m),
        (Today.AddDays(-1), 2m),
    ];

    /// <summary>The source estimate negated, at one over <paramref name="storedRate"/>,
    /// rounded once at the other account's places.</summary>
    private static decimal OtherSideAt(decimal storedRate) =>
        Math.Round(-Estimate / storedRate, 2, MidpointRounding.AwayFromZero);

    private sealed record Setup(SyntheticLedger Ledger, HttpClient Client, Guid SeriesId, Guid SourceId, Guid OtherId)
    {
        public string Url => $"/api/ledgers/{Ledger.LedgerId}/reminders/{SeriesId}";
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>
    /// A ledger reporting in the source's currency, a source account in it and the other in
    /// another, and the reminder between them, its history posted and its estimate on.
    /// </summary>
    /// <param name="otherCurrency">The other account's currency; the source's for the
    /// one-currency case.</param>
    /// <param name="typeRates">Whether to type <see cref="Rates"/>.</param>
    private async Task<Setup> SeriesAsync(ApiFactory factory, string otherCurrency = "EUR", bool typeRates = true)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "USD");
        var source = await ledger.AddBankAccountAsync("account-a", currencyCode: "USD");
        var other = await ledger.AddBankAccountAsync("account-b", currencyCode: otherCurrency);
        var client = await AuthedClientAsync(factory, ledger);
        var across = otherCurrency != "USD";

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = Rrule,
                StartDate = Start,
                Payee = "Payee A",
                SourceAccountId = source.Id,
                Postings =
                [
                    new TransactionPosting
                    {
                        CounterpartyAccountId = other.Id, Amount = TemplateAmount,
                        CounterpartyAmount = across ? 90m : null,
                    },
                ],
            });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        // Posted by hand, both amounts typed, at source amounts whose mean is the estimate.
        foreach (var (back, amount) in new[] { (50, -40m), (40, -60m) })
        {
            var fired = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/reminders/{id}/fire/bank",
                new FireBankReminderRequest
                {
                    OccurrenceDate = Today.AddDays(-back),
                    SourceAccountId = source.Id,
                    Payee = "Payee A",
                    Postings =
                    [
                        new TransactionPosting
                        {
                            CounterpartyAccountId = other.Id, Amount = amount,
                            CounterpartyAmount = across ? -amount : null,
                        },
                    ],
                });
            Assert.True(fired.IsSuccessStatusCode, await fired.Content.ReadAsStringAsync());
        }

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{id}", new EditReminderRequest { EstimateSampleCount = 2 });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        if (typeRates)
            foreach (var (day, rate) in Rates)
            {
                var typed = await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/fx-rates",
                    new AddFxRateRequest { FromCurrency = otherCurrency, ToCurrency = "USD", Date = Iso(day), Rate = rate });
                Assert.True(typed.StatusCode == HttpStatusCode.Created, await typed.Content.ReadAsStringAsync());
            }

        return new Setup(ledger, client, id, source.Id, other.Id);
    }

    private static async Task<IReadOnlyDictionary<DateOnly, UpcomingOccurrence>> AgendaAsync(Setup s)
    {
        var all = (await s.Client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{s.Ledger.LedgerId}/reminders/upcoming?from={Iso(Today.AddDays(-35))}&to={Iso(Today)}"))!;
        return all.Where(o => o.ReminderId == s.SeriesId && o.Kind == "reminder").ToDictionary(o => o.Date);
    }

    /// <summary>The committed legs of the occurrence posted for <paramref name="slot"/>, by
    /// account.</summary>
    private async Task<Dictionary<Guid, decimal>> PostedLegsAsync(Setup s, DateOnly slot)
    {
        await using var db = Db.NewServiceDbContext();
        var headerId = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.RecurringTransactionId == s.SeriesId && h.OccurrenceDate == slot && !h.IsRecurringTemplate)
            .Select(h => h.Id)
            .SingleAsync();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToDictionaryAsync(l => l.AccountId, l => l.Amount);
    }

    [Fact]
    public async Task The_agenda_estimates_the_other_side_of_each_occurrence_at_its_own_due_dates_rate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory);
        using var client = s.Client;

        var agenda = await AgendaAsync(s);

        // Two past occurrences, each at the stored rate on or before its own day: the
        // other sides differ, and the source estimate is the same on both.
        var first = agenda[Today.AddDays(-20)].Estimate!;
        var second = agenda[Today.AddDays(-10)].Estimate!;
        Assert.Equal(Estimate, first.Amount);
        Assert.Equal(OtherSideAt(1.25m), first.CounterpartyAmount);
        Assert.Equal(Estimate, second.Amount);
        Assert.Equal(OtherSideAt(1.6m), second.CounterpartyAmount);
        Assert.NotEqual(first.CounterpartyAmount, second.CounterpartyAmount);
        Assert.Null(first.UnavailableReason);
        Assert.Equal(Estimate, agenda[Today.AddDays(-20)].Amount);

        // Due today: the latest rate.
        Assert.Equal(OtherSideAt(2m), agenda[Today].Estimate!.CounterpartyAmount);
    }

    [Fact]
    public async Task An_occurrence_with_no_rate_for_its_due_date_has_no_estimate_while_one_of_the_same_series_with_a_rate_has()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory);
        using var client = s.Client;

        var agenda = await AgendaAsync(s);

        // Before any stored rate: no estimate on either side, the missing stored leg named,
        // and the template's figure standing in, as for any estimate that cannot be made.
        var unrated = agenda[Today.AddDays(-30)];
        Assert.Null(unrated.Estimate!.Amount);
        Assert.Null(unrated.Estimate.CounterpartyAmount);
        Assert.Equal("rate-unavailable", unrated.Estimate.UnavailableReason);
        Assert.Equal(
            new RateUnavailable("EUR", "USD", Today.AddDays(-30)), Assert.Single(unrated.Estimate.RateUnavailable!));
        Assert.Equal(TemplateAmount, unrated.Amount);

        // Per occurrence, not per series: the next occurrence has a rate and an estimate.
        Assert.Equal(Estimate, agenda[Today.AddDays(-20)].Estimate!.Amount);

        // The fire of the unrated occurrence is refused, and nothing is written for it.
        var fire = await ReminderCloneFire.FireAsync(Db, s.Ledger.LedgerId, s.SeriesId, Today.AddDays(-30));
        Assert.Equal(RemindersRepository.FireOutcome.EstimateUnavailableForFire, fire.Outcome);
        await using var db = Db.NewServiceDbContext();
        Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(
            h => h.RecurringTransactionId == s.SeriesId && h.OccurrenceDate == Today.AddDays(-30) && !h.IsRecurringTemplate));
    }

    [Fact]
    public async Task The_reminder_detail_estimates_the_occurrence_it_is_asked_for()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory);
        using var client = s.Client;

        var first = (await client.GetFromJsonAsync<ReminderDetail>($"{s.Url}?occurrenceDate={Iso(Today.AddDays(-20))}"))!;
        var second = (await client.GetFromJsonAsync<ReminderDetail>($"{s.Url}?occurrenceDate={Iso(Today.AddDays(-10))}"))!;

        Assert.Equal(Estimate, first.Estimate!.Amount);
        Assert.Equal(OtherSideAt(1.25m), first.Estimate.CounterpartyAmount);
        Assert.Equal(OtherSideAt(1.6m), second.Estimate!.CounterpartyAmount);
    }

    [Fact]
    public async Task The_fire_posts_both_estimated_figures_and_writes_no_rate()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory);
        using var client = s.Client;
        var slot = Today.AddDays(-20);

        await ReminderCloneFire.PostedAsync(Db, s.Ledger.LedgerId, s.SeriesId, slot);

        var legs = await PostedLegsAsync(s, slot);
        Assert.Equal(Estimate, legs[s.SourceId]);
        Assert.Equal(OtherSideAt(1.25m), legs[s.OtherId]);

        // The rate was only read: the one needed pair still holds exactly the rates typed,
        // and no pair between the two account currencies is stored.
        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{s.Ledger.LedgerId}/fx-rates"))!;
        var pair = Assert.Single(view.Pairs);
        Assert.Equal(("EUR", "USD"), (pair.FromCurrency, pair.ToCurrency));
        Assert.Equal(Rates.Length, pair.RateCount);
    }

    [Fact]
    public async Task A_one_currency_estimating_transfer_still_posts_the_negation()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var s = await SeriesAsync(factory, otherCurrency: "USD", typeRates: false);
        using var client = s.Client;
        var slot = Today.AddDays(-30);

        var agenda = await AgendaAsync(s);
        Assert.Equal(Estimate, agenda[slot].Estimate!.Amount);
        Assert.Null(agenda[slot].Estimate!.CounterpartyAmount);

        await ReminderCloneFire.PostedAsync(Db, s.Ledger.LedgerId, s.SeriesId, slot);

        var legs = await PostedLegsAsync(s, slot);
        Assert.Equal(Estimate, legs[s.SourceId]);
        Assert.Equal(-Estimate, legs[s.OtherId]);
    }
}
