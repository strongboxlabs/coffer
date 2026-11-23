using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.MarketSeries;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The rate between two account currencies (<see cref="ExchangeRateTable.Derived(string, string, string, RateDay)"/>,
/// ADR-0114 D13): the stored rate into the reporting currency or its inverse when R is one of
/// them, the cross of the two legs otherwise; each leg read by the one rate rule; good for
/// the older of its legs' days; and each missing stored leg named in the direction it is
/// stored (ADR-0114 D9).
/// </summary>
/// <remarks>
/// Every rate is typed through <c>POST /fx-rates</c>, the hand-entry path a person uses, on a
/// synthetic ledger, so no stored rate is one the API could not have written. A hand-entered
/// day extends its pair's coverage (ADR-0114 D9), which is what makes a pair covered or
/// lapsed here. The server's clock is the real one, as the endpoint's is.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DerivedRateTests : DatabaseTest
{
    public DerivedRateTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private readonly MarketDataFake _fake = new(TimeProvider.System);

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The past day most cases read: before the server's UTC today, so it reads
    /// its own rate.</summary>
    private static DateOnly Day => Today.AddDays(-10);

    /// <summary>A rate typed after <see cref="Day"/>, so the pair's coverage, which runs to
    /// its latest hand-entered day, reaches past it. Its value is never the one read.</summary>
    private static (string From, string To, DateOnly Day, decimal Rate) Later(string from, string to) =>
        (from, to, Day.AddDays(2), 7m);

    /// <summary>A ledger in <paramref name="reporting"/> with one bank account in each of
    /// <paramref name="accountCurrencies"/>, so each one's pair into R is needed.</summary>
    private async Task<SyntheticLedger> LedgerAsync(string reporting, params string[] accountCurrencies)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: reporting);
        foreach (var code in accountCurrencies)
            await ledger.AddBankAccountAsync($"Bank {code}", currencyCode: code);
        return ledger;
    }

    private async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var cookie = await user.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Types each rate through the API, as a person would.</summary>
    private async Task TypeRatesAsync(SyntheticLedger ledger, params (string From, string To, DateOnly Day, decimal Rate)[] rates)
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        foreach (var (from, to, day, rate) in rates)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/fx-rates",
                new { fromCurrency = from, toCurrency = to, date = Iso(day), rate });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    /// <summary>Declares and loads the derived rate's legs for <paramref name="day"/>, and
    /// reads it, on the request role as the ledger's owner.</summary>
    private async Task<DerivedRate> DerivedAsync(
        SyntheticLedger ledger, string from, string to, string reporting, DateOnly day)
    {
        await using var db = Db.NewRequestDbContextAsUser(ledger.UserId);
        var lookup = new ExchangeRateLookup(db, TimeProvider.System);
        var needs = lookup.NewNeeds().AddDerived(from, to, reporting, day);
        var table = await lookup.LoadAsync(ledger.LedgerId, needs);
        return table.Derived(from, to, reporting, day);
    }

    [Fact]
    public async Task When_the_reporting_currency_is_the_destination_the_rate_is_the_stored_leg()
    {
        var ledger = await LedgerAsync("USD", "EUR");
        var stored = Day.AddDays(-5);
        await TypeRatesAsync(ledger, ("EUR", "USD", stored, 1.1m), Later("EUR", "USD"));

        var derived = await DerivedAsync(ledger, "EUR", "USD", "USD", Day);

        Assert.Equal(1.1m, derived.Rate);
        Assert.Equal(stored, derived.RateDate);
        Assert.Equal(new DerivedRateLeg("EUR", "USD", stored, 1.1m), Assert.Single(derived.Legs));
        Assert.Empty(derived.Unavailable);
        // Rounded once at the destination's places, with the amount's sign.
        Assert.Equal(-11.01m, derived.Convert(-10.01m));
    }

    [Fact]
    public async Task When_the_reporting_currency_is_the_source_the_rate_is_the_inverse_of_the_stored_leg()
    {
        var ledger = await LedgerAsync("USD", "USD", "EUR");
        var stored = Day.AddDays(-5);
        await TypeRatesAsync(ledger, ("EUR", "USD", stored, 1.1m), Later("EUR", "USD"));

        var derived = await DerivedAsync(ledger, "USD", "EUR", "USD", Day);

        Assert.Equal(1m / 1.1m, derived.Rate);
        Assert.Equal(stored, derived.RateDate);
        Assert.Equal(new DerivedRateLeg("EUR", "USD", stored, 1.1m), Assert.Single(derived.Legs));
        // 1/1.1 is a repeating decimal; the figure divides once, after multiplying.
        Assert.Equal(-100m, derived.Convert(-110m));
        Assert.Equal(9.09m, derived.Convert(10m));
    }

    [Fact]
    public async Task Between_two_currencies_other_than_the_reporting_one_the_rate_is_the_cross_good_for_the_older_day()
    {
        var ledger = await LedgerAsync("USD", "EUR", "GBP");
        var older = Day.AddDays(-3);
        var newer = Day.AddDays(-1);
        await TypeRatesAsync(ledger,
            ("EUR", "USD", older, 1.1m), Later("EUR", "USD"),
            ("GBP", "USD", newer, 1.3m), Later("GBP", "USD"));

        var derived = await DerivedAsync(ledger, "EUR", "GBP", "USD", Day);

        Assert.Equal(1.1m / 1.3m, derived.Rate);
        Assert.Equal(older, derived.RateDate);
        Assert.Equal(
            [new DerivedRateLeg("EUR", "USD", older, 1.1m), new DerivedRateLeg("GBP", "USD", newer, 1.3m)],
            derived.Legs);
        // The amount times X→R over Y→R, at full precision, rounded once: the inverse
        // ratio would give a figure larger than the amount.
        Assert.Equal(84.62m, derived.Convert(100m));

        // The other way round is the inverse cross, and still the older day.
        var back = await DerivedAsync(ledger, "GBP", "EUR", "USD", Day);
        Assert.Equal(1.3m / 1.1m, back.Rate);
        Assert.Equal(older, back.RateDate);
        Assert.Equal(118.18m, back.Convert(100m));
    }

    [Fact]
    public async Task A_day_before_today_reads_its_own_rate_and_today_reads_the_latest()
    {
        var ledger = await LedgerAsync("USD", "EUR");
        var past = Today.AddDays(-3);
        await TypeRatesAsync(ledger, ("EUR", "USD", past, 1.1m), ("EUR", "USD", Today, 1.2m));

        var dated = await DerivedAsync(ledger, "EUR", "USD", "USD", Today.AddDays(-2));
        Assert.Equal((1.1m, past), (dated.Rate, dated.RateDate));

        var current = await DerivedAsync(ledger, "EUR", "USD", "USD", Today);
        Assert.Equal((1.2m, Today), (current.Rate, current.RateDate));

        // A later day is a forecast day, and reads the latest too.
        var forecast = await DerivedAsync(ledger, "EUR", "USD", "USD", Today.AddDays(30));
        Assert.Equal((1.2m, Today), (forecast.Rate, forecast.RateDate));
    }

    [Fact]
    public async Task A_missing_leg_is_named_in_its_stored_direction_with_the_day()
    {
        var ledger = await LedgerAsync("USD", "EUR", "GBP");
        await TypeRatesAsync(ledger, ("EUR", "USD", Day.AddDays(-5), 1.1m), Later("EUR", "USD"));
        var day = Day;

        var derived = await DerivedAsync(ledger, "EUR", "GBP", "USD", day);

        Assert.Null(derived.Rate);
        Assert.Null(derived.RateDate);
        Assert.Empty(derived.Legs);
        Assert.Null(derived.Convert(100m));
        // Only the leg with no rate, from the account currency into R.
        Assert.Equal([new RateUnavailable("GBP", "USD", day)], derived.Unavailable);

        // From the other side it is the same stored leg that is missing.
        var back = await DerivedAsync(ledger, "GBP", "EUR", "USD", day);
        Assert.Equal([new RateUnavailable("GBP", "USD", day)], back.Unavailable);
    }

    [Fact]
    public async Task Both_missing_legs_are_named()
    {
        var ledger = await LedgerAsync("USD", "EUR", "GBP");
        var day = Today.AddDays(-5);

        var derived = await DerivedAsync(ledger, "EUR", "GBP", "USD", day);

        Assert.Null(derived.Rate);
        Assert.Equal(
            [new RateUnavailable("EUR", "USD", day), new RateUnavailable("GBP", "USD", day)],
            derived.Unavailable);
    }

    [Fact]
    public async Task A_pair_whose_coverage_has_lapsed_has_no_rate()
    {
        var ledger = await LedgerAsync("USD", "EUR");
        var last = Today.AddDays(-5);
        await TypeRatesAsync(ledger, ("EUR", "USD", last, 1.1m));

        // Today reads the latest, which a coverage ending before yesterday does not give;
        // the reason names the rate it would have used, as every latest-rate reason does.
        var current = await DerivedAsync(ledger, "EUR", "USD", "USD", Today);
        Assert.Null(current.Rate);
        Assert.Equal([new RateUnavailable("EUR", "USD", last)], current.Unavailable);

        // A past day after the coverage is missing too, and names that day.
        var after = Today.AddDays(-3);
        var dated = await DerivedAsync(ledger, "EUR", "USD", "USD", after);
        Assert.Null(dated.Rate);
        Assert.Equal([new RateUnavailable("EUR", "USD", after)], dated.Unavailable);
    }

    // A rate stored from R into an account currency is the opposite direction to the leg a
    // derived rate reads. It is never inverted to stand in for the missing leg.
    [Fact]
    public async Task A_rate_stored_from_the_reporting_currency_into_an_account_currency_supplies_no_leg()
    {
        // While R is EUR, the ledger needs, and takes, a USD into EUR rate.
        var ledger = await LedgerAsync("EUR", "EUR", "USD");
        var stored = Today.AddDays(-10);
        await TypeRatesAsync(ledger, ("USD", "EUR", stored, 0.9m));

        await using (var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth()))
        {
            using var client = await ClientAsync(factory, ledger);
            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}")
            {
                Content = JsonContent.Create(new { currencyCode = "USD" }),
            };
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(patch)).StatusCode);
        }

        // Now R is USD: the leg is EUR into USD, which is not stored.
        var day = Today.AddDays(-5);
        var toR = await DerivedAsync(ledger, "EUR", "USD", "USD", day);
        Assert.Null(toR.Rate);
        Assert.Equal([new RateUnavailable("EUR", "USD", day)], toR.Unavailable);

        var fromR = await DerivedAsync(ledger, "USD", "EUR", "USD", day);
        Assert.Null(fromR.Rate);
        Assert.Equal([new RateUnavailable("EUR", "USD", day)], fromR.Unavailable);
    }

    [Fact]
    public async Task A_read_of_legs_never_declared_throws()
    {
        var ledger = await LedgerAsync("USD", "EUR", "GBP");
        await using var db = Db.NewRequestDbContextAsUser(ledger.UserId);
        var lookup = new ExchangeRateLookup(db, TimeProvider.System);
        var day = Today.AddDays(-5);
        var table = await lookup.LoadAsync(ledger.LedgerId, lookup.NewNeeds().Add("EUR", "USD", day));

        Assert.Throws<InvalidOperationException>(() => table.Derived("EUR", "GBP", "USD", day));
        Assert.Throws<ArgumentException>(() => table.Derived("EUR", "EUR", "USD", day));
    }
}
