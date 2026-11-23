using System.Net;
using System.Text;
using System.Web;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Quotes;

/// <summary>
/// The Frankfurter exchange-rate client (ADR-0114 D4) against responses authored here with
/// synthetic rates, in each shape the parser accepts. No test reaches the live service.
/// </summary>
public sealed class FrankfurterRatesClientTests
{
    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static FrankfurterRatesClient ClientFor(
        Func<HttpRequestMessage, HttpResponseMessage> handler, string? baseUrl = null, DateTimeOffset? now = null)
    {
        var http = new HttpClient(new DelegateHandler(handler));
        var options = Options.Create(new FrankfurterOptions { BaseUrl = baseUrl });
        return new FrankfurterRatesClient(
            http, options, NullLogger<FrankfurterRatesClient>.Instance, new TestClock(now ?? Now));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly DateOnly Fri = new(2026, 5, 15);
    private static readonly DateOnly Mon = new(2026, 5, 18);

    /// <summary>A week of EUR rates as root-level rows: Friday and Monday, nothing between.</summary>
    private const string RowsAcrossAWeekend = """
        [
          { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 1.0811, "provider": "ECB" },
          { "date": "2026-05-15", "base": "EUR", "quote": "GBP", "rate": 0.8604, "provider": "ECB" },
          { "date": "2026-05-18", "base": "EUR", "quote": "USD", "rate": 1.0827, "provider": "ECB" },
          { "date": "2026-05-18", "base": "EUR", "quote": "GBP", "rate": 0.8611, "provider": "BOE" }
        ]
        """;

    private static Task<FxRatesFetch> FetchAsync(FrankfurterRatesClient client, DateOnly from, DateOnly to, params string[] quotes) =>
        client.FetchRatesAsync("EUR", quotes.Length == 0 ? ["USD", "GBP"] : quotes, from, to);

    // ---- shapes --------------------------------------------------------------------

    [Fact]
    public async Task Rows_at_the_root_read_one_rate_per_day_and_pair_with_no_weekend_filled_in()
    {
        var client = ClientFor(_ => Ok(RowsAcrossAWeekend));

        var fetched = await FetchAsync(client, Fri, Mon);

        Assert.Null(fetched.ErrorCode);
        Assert.Equal(
            [
                new FxRate(Fri, "EUR", "GBP", 0.8604m, "ECB"),
                new FxRate(Fri, "EUR", "USD", 1.0811m, "ECB"),
                new FxRate(Mon, "EUR", "GBP", 0.8611m, "BOE"),
                new FxRate(Mon, "EUR", "USD", 1.0827m, "ECB"),
            ],
            fetched.Rates);
        // Saturday and Sunday have no row and get none: the reader converts them at Friday's.
        Assert.DoesNotContain(fetched.Rates, r => r.Day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(Mon, fetched.FetchedThrough);
    }

    [Fact]
    public async Task Rows_under_rates_take_the_roots_base_and_source()
    {
        var client = ClientFor(_ => Ok("""
            {
              "base": "EUR",
              "provider": "blended",
              "rates": [
                { "date": "2026-05-15", "quote": "USD", "rate": 1.0811 },
                { "date": "2026-05-18", "quote": "USD", "rate": 1.0827, "source": "ECB" }
              ]
            }
            """));

        var fetched = await FetchAsync(client, Fri, Mon, "USD");

        Assert.Equal(
            [new FxRate(Fri, "EUR", "USD", 1.0811m, "blended"), new FxRate(Mon, "EUR", "USD", 1.0827m, "ECB")],
            fetched.Rates);
    }

    [Fact]
    public async Task The_v1_date_map_reads_the_same_rates()
    {
        var client = ClientFor(_ => Ok("""
            {
              "amount": 1.0,
              "base": "EUR",
              "start_date": "2026-05-15",
              "end_date": "2026-05-18",
              "rates": {
                "2026-05-15": { "GBP": 0.8604, "USD": 1.0811 },
                "2026-05-18": { "GBP": 0.8611, "USD": 1.0827 }
              }
            }
            """));

        var fetched = await FetchAsync(client, Fri, Mon);

        Assert.Equal(4, fetched.Rates.Count);
        Assert.Contains(new FxRate(Mon, "EUR", "GBP", 0.8611m, null), fetched.Rates);
        Assert.All(fetched.Rates, r => Assert.Null(r.UpstreamSource));
    }

    [Fact]
    public async Task A_list_of_providers_is_named_joined()
    {
        var client = ClientFor(_ => Ok("""
            [ { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 1.0811, "providers": ["ECB", "FED"] } ]
            """));

        var fetched = await FetchAsync(client, Fri, Fri, "USD");

        Assert.Equal("ECB,FED", Assert.Single(fetched.Rates).UpstreamSource);
    }

    [Fact]
    public async Task A_timestamp_date_reads_as_its_day()
    {
        var client = ClientFor(_ => Ok("""
            [ { "date": "2026-05-15T00:00:00Z", "base": "EUR", "quote": "USD", "rate": 1.0811 } ]
            """));

        Assert.Equal(Fri, Assert.Single((await FetchAsync(client, Fri, Fri, "USD")).Rates).Day);
    }

    [Fact]
    public async Task A_rate_is_rounded_once_to_twelve_places()
    {
        var client = ClientFor(_ => Ok("""
            [ { "date": "2026-05-15", "base": "EUR", "quote": "JPY", "rate": 163.123456789012345678 } ]
            """));

        var rate = Assert.Single((await FetchAsync(client, Fri, Fri, "JPY")).Rates);

        Assert.Equal(163.123456789012m, rate.Rate);
        Assert.Equal(12, FrankfurterRatesClient.RateScale);
    }

    [Fact]
    public async Task Rows_for_a_currency_or_day_not_asked_for_are_ignored_and_a_repeat_keeps_the_first()
    {
        var client = ClientFor(_ => Ok("""
            [
              { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 1.0811 },
              { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 9.9999 },
              { "date": "2026-05-15", "base": "EUR", "quote": "CHF", "rate": 0.9412 },
              { "date": "2026-05-14", "base": "EUR", "quote": "USD", "rate": 1.0790 }
            ]
            """));

        var fetched = await FetchAsync(client, Fri, Fri, "USD");

        Assert.Equal([new FxRate(Fri, "EUR", "USD", 1.0811m, null)], fetched.Rates);
    }

    // ---- fail closed ---------------------------------------------------------------

    [Theory]
    [InlineData("""[ { "date": "2026-05-15", "base": "EUR", "quote": "USD" } ]""")]
    [InlineData("""[ { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 0 } ]""")]
    [InlineData("""[ { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": -1.2 } ]""")]
    [InlineData("""[ { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": "1.08" } ]""")]
    [InlineData("""[ { "date": "15/05/2026", "base": "EUR", "quote": "USD", "rate": 1.08 } ]""")]
    [InlineData("""[ { "base": "EUR", "quote": "USD", "rate": 1.08 } ]""")]
    [InlineData("""[ { "date": "2026-05-15", "base": "EUR", "rate": 1.08 } ]""")]
    [InlineData("""[ "2026-05-15" ]""")]
    [InlineData("""{ "base": "EUR", "rates": { "2026-05-15": { "USD": null } } }""")]
    [InlineData("""{ "base": "EUR", "rates": { "someday": { "USD": 1.08 } } }""")]
    [InlineData("""{ "base": "EUR", "rates": 1.08 }""")]
    [InlineData("""{ "base": "EUR", "data": [] }""")]
    [InlineData("""1.08""")]
    public async Task A_response_with_an_unreadable_row_or_shape_fails_the_year_closed(string json)
    {
        var client = ClientFor(_ => Ok(json));

        var fetched = await FetchAsync(client, Fri, Fri, "USD");

        Assert.Equal("fx-parse-failed", fetched.ErrorCode);
        Assert.Empty(fetched.Rates);
        Assert.Null(fetched.FetchedThrough);
    }

    [Fact]
    public async Task One_unreadable_row_drops_the_whole_year_not_just_itself()
    {
        var client = ClientFor(_ => Ok("""
            [
              { "date": "2026-05-15", "base": "EUR", "quote": "USD", "rate": 1.0811 },
              { "date": "2026-05-18", "base": "EUR", "quote": "USD", "rate": null }
            ]
            """));

        var fetched = await FetchAsync(client, Fri, Mon, "USD");

        Assert.Equal("fx-parse-failed", fetched.ErrorCode);
        Assert.Empty(fetched.Rates);
    }

    [Theory]
    [InlineData("""[ { "date": "2026-05-15", "base": "USD", "quote": "EUR", "rate": 0.925 } ]""")]
    [InlineData("""{ "base": "USD", "rates": { "2026-05-15": { "EUR": 0.925 } } }""")]
    public async Task A_response_for_another_base_fails_closed(string json)
    {
        var client = ClientFor(_ => Ok(json));

        var fetched = await client.FetchRatesAsync("EUR", ["USD"], Fri, Fri);

        Assert.Equal("fx-parse-failed", fetched.ErrorCode);
        Assert.Empty(fetched.Rates);
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_fails_closed()
    {
        var client = ClientFor(_ => Ok("<html>maintenance</html>"));

        var fetched = await FetchAsync(client, Fri, Fri);

        Assert.Equal("fx-parse-failed", fetched.ErrorCode);
    }

    [Fact]
    public async Task A_failed_request_fails_closed()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var fetched = await FetchAsync(client, Fri, Fri);

        Assert.Equal("fx-fetch-failed", fetched.ErrorCode);
        Assert.Null(fetched.FetchedThrough);
    }

    // ---- requests ------------------------------------------------------------------

    [Fact]
    public async Task The_request_asks_the_blended_feed_with_the_account_currency_as_base()
    {
        Uri? asked = null;
        var client = ClientFor(req => { asked = req.RequestUri; return Ok("[]"); });

        await client.FetchRatesAsync("EUR", ["USD", "GBP", "EUR"], Fri, Mon);

        Assert.Equal("https://api.frankfurter.dev/v2/rates", asked!.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(asked.Query);
        Assert.Equal("2026-05-15", query["from"]);
        Assert.Equal("2026-05-18", query["to"]);
        Assert.Equal("EUR", query["base"]);
        Assert.Equal("GBP,USD", query["quotes"]);   // the base itself is not asked for
        Assert.Null(query["providers"]);            // the blended feed, not providers=ecb
    }

    [Theory]
    [InlineData("http://frankfurter:8080", "http://frankfurter:8080/v2/rates")]
    [InlineData("https://fx.example.test/frankfurter/", "https://fx.example.test/frankfurter/v2/rates")]
    [InlineData("https://fx.example.test/frankfurter", "https://fx.example.test/frankfurter/v2/rates")]
    [InlineData("  ", "https://api.frankfurter.dev/v2/rates")]
    public async Task The_configured_base_url_is_where_the_request_goes(string baseUrl, string expected)
    {
        Uri? asked = null;
        var client = ClientFor(req => { asked = req.RequestUri; return Ok("[]"); }, baseUrl: baseUrl);

        await FetchAsync(client, Fri, Fri);

        Assert.Equal(expected, asked!.GetLeftPart(UriPartial.Path));
    }

    [Theory]
    [InlineData("frankfurter:8080")]
    [InlineData("ftp://fx.example.test")]
    [InlineData("https://fx.example.test/?key=1")]
    public async Task An_unusable_base_url_fails_closed_without_a_request(string baseUrl)
    {
        var called = false;
        var client = ClientFor(_ => { called = true; return Ok("[]"); }, baseUrl: baseUrl);

        var fetched = await FetchAsync(client, Fri, Fri);

        Assert.False(called);
        Assert.Equal("fx-base-url-invalid", fetched.ErrorCode);
    }

    [Fact]
    public async Task A_span_across_years_is_asked_for_one_year_at_a_time()
    {
        var asked = new List<(string From, string To)>();
        var client = ClientFor(req =>
        {
            var q = HttpUtility.ParseQueryString(req.RequestUri!.Query);
            asked.Add((q["from"]!, q["to"]!));
            return Ok($$"""[ { "date": "{{q["to"]}}", "base": "EUR", "quote": "USD", "rate": 1.1 } ]""");
        });

        var fetched = await FetchAsync(client, new DateOnly(2024, 12, 30), new DateOnly(2026, 1, 2), "USD");

        Assert.Equal(
            [("2024-12-30", "2024-12-31"), ("2025-01-01", "2025-12-31"), ("2026-01-01", "2026-01-02")],
            asked);
        Assert.Equal(3, fetched.Rates.Count);
        Assert.Equal(new DateOnly(2026, 1, 2), fetched.FetchedThrough);
    }

    [Fact]
    public async Task A_year_that_fails_keeps_the_years_before_it_and_their_coverage_only()
    {
        var calls = 0;
        var client = ClientFor(req =>
        {
            calls++;
            if (calls == 2) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var q = HttpUtility.ParseQueryString(req.RequestUri!.Query);
            return Ok($$"""[ { "date": "{{q["to"]}}", "base": "EUR", "quote": "USD", "rate": 1.1 } ]""");
        });

        var fetched = await FetchAsync(client, new DateOnly(2024, 12, 30), new DateOnly(2026, 1, 2), "USD");

        Assert.Equal(2, calls);   // stops at the failure, does not skip past it
        Assert.Equal("fx-fetch-failed", fetched.ErrorCode);
        Assert.Equal([new DateOnly(2024, 12, 31)], fetched.Rates.Select(r => r.Day));
        Assert.Equal(new DateOnly(2024, 12, 31), fetched.FetchedThrough);
    }

    [Fact]
    public async Task A_range_past_today_is_asked_for_only_through_today()
    {
        Uri? asked = null;
        var client = ClientFor(req => { asked = req.RequestUri; return Ok("[]"); });

        var fetched = await FetchAsync(client, new DateOnly(2026, 5, 25), new DateOnly(2026, 12, 31));

        Assert.Equal("2026-06-01", HttpUtility.ParseQueryString(asked!.Query)["to"]);
        Assert.Equal(new DateOnly(2026, 6, 1), fetched.FetchedThrough);
    }

    [Fact]
    public async Task Nothing_to_ask_for_makes_no_request()
    {
        var called = false;
        var client = ClientFor(_ => { called = true; return Ok("[]"); });

        var sameCurrency = await client.FetchRatesAsync("EUR", ["EUR"], Fri, Mon);
        var future = await client.FetchRatesAsync("EUR", ["USD"], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 9));

        Assert.False(called);
        Assert.Empty(sameCurrency.Rates);
        Assert.Null(sameCurrency.FetchedThrough);
        Assert.Null(future.FetchedThrough);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("")]
    public async Task A_currency_not_in_stored_form_is_a_caller_error(string currency)
    {
        var client = ClientFor(_ => Ok("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchRatesAsync(currency, ["USD"], Fri, Fri));
        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchRatesAsync("USD", [currency], Fri, Fri));
    }

    [Fact]
    public void Year_chunks_cover_the_span_exactly_once()
    {
        var chunks = FrankfurterRatesClient.YearChunks(new DateOnly(2023, 3, 1), new DateOnly(2025, 2, 28)).ToList();

        Assert.Equal(
            [
                (new DateOnly(2023, 3, 1), new DateOnly(2023, 12, 31)),
                (new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)),
                (new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 28)),
            ],
            chunks);
    }
}
