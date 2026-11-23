using System.Globalization;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.MarketSeries;
using Coffer.Api.Quotes.Yahoo;

namespace Coffer.Api.Endpoints;

/// <summary>
/// A ledger's benchmark list (ADR-0114 D1, D2), its exchange rates view and hand entry
/// (ADR-0114 D10), and the derived rate between two account currencies (ADR-0114 D13,
/// ADR-0114 D14). Reads are open to any member; every write needs owner or editor (ADR-0083), the
/// group's method-based gate.
/// </summary>
public static partial class MarketSeriesEndpoints
{
    public static IEndpointRouteBuilder MapMarketSeriesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/benchmarks", ListBenchmarksAsync);
        group.MapPost("/benchmarks", AddBenchmarkAsync);
        group.MapDelete("/benchmarks/{benchmarkId:guid}", RemoveBenchmarkAsync);

        group.MapGet("/fx-rates", RatesViewAsync);
        group.MapGet("/fx-rates/{fromCurrency}/{toCurrency}", RatesPageAsync);
        group.MapGet("/fx-rates/{fromCurrency}/{toCurrency}/derived", DerivedRateAsync);
        group.MapPost("/fx-rates", AddRateAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/benchmarks</c> — the list, in order, each with whether
    /// the Returns report can draw it for the REQUESTING user
    /// (<see cref="BenchmarkAvailabilityDto"/>, ADR-0114 D1): its history, and the series
    /// currencies it has no rate into. The returns view reads its own series' currency out
    /// of that list, so the read takes no parameter and Settings → Quotes shares it.
    /// </summary>
    private static async Task<IResult> ListBenchmarksAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        UserPreferencesRepository prefs,
        MarketSeriesRepository series,
        CancellationToken cancellationToken)
    {
        var enabled = (await prefs.GetQuotesAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false)).EnabledProviders;
        var yahooOn = enabled.Contains(YahooFinanceQuoteProvider.Key, StringComparer.Ordinal);
        return Results.Ok(await series.ListBenchmarksAsync(ledgerId, yahooOn, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/benchmarks</c> — add a fund or ETF (ADR-0114 D1, D2).
    /// 201 with the <see cref="BenchmarkDto"/>.
    /// <para>The add-time check is one Yahoo chart request, so it needs the REQUESTING
    /// user's Yahoo opt-in on this ledger (ADR-0114 D8): without it, 422
    /// <c>benchmark-yahoo-off</c> and nothing is sent. The cheap refusals come first, before
    /// any request: <c>benchmark-symbol-invalid</c>, <c>benchmark-already-added</c>,
    /// <c>benchmark-limit-reached</c> (three per ledger). A symbol the check refuses answers
    /// with the check's code and its message (see <see cref="BusinessError.Codes"/>'
    /// Benchmarks note). The name is Yahoo's long name, else its short name, else the
    /// symbol.</para>
    /// <para>On success a backfill of the ledger's series starts (ADR-0114 D11) and the response
    /// does not wait for it.</para>
    /// </summary>
    private static async Task<IResult> AddBenchmarkAsync(
        Guid ledgerId,
        AddBenchmarkRequest request,
        ICurrentUserAccessor currentUser,
        UserPreferencesRepository prefs,
        MarketSeriesRepository series,
        YahooBenchmarkClient yahoo,
        MarketSeriesBackfill backfill,
        CancellationToken cancellationToken)
    {
        var symbol = request.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!BenchmarkSymbol.IsValid(symbol))
            return BusinessError.Problem(BusinessError.Codes.BenchmarkSymbolInvalid,
                "Enter a Yahoo symbol for a fund or ETF, such as VT.");

        var enabled = (await prefs.GetQuotesAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false)).EnabledProviders;
        if (!enabled.Contains(YahooFinanceQuoteProvider.Key, StringComparer.Ordinal))
            return BusinessError.Problem(BusinessError.Codes.BenchmarkYahooOff,
                "Checking a benchmark asks Yahoo Finance. Turn Yahoo on for this ledger first.");

        var pre = await series.CanAddBenchmarkAsync(ledgerId, symbol, cancellationToken).ConfigureAwait(false);
        if (pre != MarketSeriesRepository.AddBenchmarkResult.Ok) return AddRefusal(pre, symbol);

        var check = await yahoo.CheckAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (!check.Accepted)
            return BusinessError.Problem(check.RefusalCode!, check.Message ?? $"{symbol} cannot be a benchmark.");

        var name = string.IsNullOrWhiteSpace(check.Name) ? symbol : check.Name!;
        var (result, added) = await series.AddBenchmarkAsync(ledgerId, symbol, name, check.CurrencyCode!, cancellationToken)
            .ConfigureAwait(false);
        if (result != MarketSeriesRepository.AddBenchmarkResult.Ok) return AddRefusal(result, symbol);

        backfill.Start(ledgerId, currentUser.UserId);
        return Results.Created($"/api/ledgers/{ledgerId}/benchmarks/{added!.Id}", added);
    }

    private static IResult AddRefusal(MarketSeriesRepository.AddBenchmarkResult result, string symbol) =>
        result == MarketSeriesRepository.AddBenchmarkResult.AlreadyAdded
            ? BusinessError.Problem(BusinessError.Codes.BenchmarkAlreadyAdded,
                $"{symbol} is already one of this ledger's benchmarks.")
            : BusinessError.Problem(BusinessError.Codes.BenchmarkLimitReached,
                $"A ledger keeps up to {MarketSeriesRepository.MaxBenchmarks} benchmarks. Remove one to add another.");

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/benchmarks/{benchmarkId}</c> — 204. The rest close
    /// up (positions stay 1..n); the symbol's stored growth is kept (ADR-0114 D1). 422
    /// <c>benchmark-not-found</c>.
    /// </summary>
    private static async Task<IResult> RemoveBenchmarkAsync(
        Guid ledgerId, Guid benchmarkId, MarketSeriesRepository series, CancellationToken cancellationToken) =>
        await series.RemoveBenchmarkAsync(ledgerId, benchmarkId, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.BenchmarkNotFound,
                "That benchmark is not one of this ledger's.");

    /// <summary><c>GET /api/ledgers/{ledgerId}/fx-rates</c> — the rates view
    /// (<see cref="FxRatesViewDto"/>): the needed pairs and each one's coverage.</summary>
    private static async Task<IResult> RatesViewAsync(
        Guid ledgerId, MarketSeriesRepository series, CancellationToken cancellationToken) =>
        Results.Ok(await series.RatesViewAsync(ledgerId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/fx-rates/{from}/{to}?before=yyyy-MM-dd&amp;limit=100</c>
    /// — one pair's stored rates, newest first, any pair (a pair no longer needed keeps its
    /// days). <c>limit</c> 1..500, default 100. 422 <c>fx-rate-currency-invalid</c>,
    /// <c>fx-rate-date-invalid</c> for an unreadable <c>before</c>.
    /// </summary>
    private static async Task<IResult> RatesPageAsync(
        Guid ledgerId,
        string fromCurrency,
        string toCurrency,
        string? before,
        int? limit,
        MarketSeriesRepository series,
        CancellationToken cancellationToken)
    {
        if (!TryPair(fromCurrency, toCurrency, out var from, out var to))
            return CurrencyInvalid();
        DateOnly? beforeDay = null;
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (!TryDay(before, out var d)) return DateInvalid();
            beforeDay = d;
        }
        return Results.Ok(await series.RatesPageAsync(ledgerId, from, to, beforeDay, limit ?? 100, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/fx-rates/{from}/{to}/derived?date=yyyy-MM-dd&amp;amount=</c>
    /// — the rate between two account currencies for <c>date</c>, derived through the
    /// ledger's reporting currency, and <c>amount</c> (in <c>from</c>) converted at it
    /// (<see cref="DerivedRateDto"/>, ADR-0114 D13, ADR-0114 D14): what the editors prefill a
    /// transfer's second amount from and what their rate line says.
    /// <para>Each stored leg into the reporting currency is read by the one rate rule for the
    /// day, through the one derived reader (<see cref="ExchangeRateTable.Derived(string, string, string, DateOnly)"/>),
    /// so the prefill reads what an estimate or a lot move reads. A missing rate is not a
    /// refusal: the answer names each missing stored leg. It writes nothing; a transfer never
    /// writes <c>fx_rates</c> (ADR-0114 D14).</para>
    /// <para>422 <c>fx-rate-currency-invalid</c> (not a code, or from and to the same),
    /// <c>fx-rate-date-invalid</c> (missing or unreadable). An amount too large to convert
    /// is a 400, as an unreadable one is.</para>
    /// </summary>
    private static async Task<IResult> DerivedRateAsync(
        Guid ledgerId,
        string fromCurrency,
        string toCurrency,
        string? date,
        decimal? amount,
        AppDbContext db,
        ExchangeRateLookup rates,
        CancellationToken cancellationToken)
    {
        if (!TryPair(fromCurrency, toCurrency, out var from, out var to))
            return CurrencyInvalid();
        if (!TryDay(date, out var day)) return DateInvalid();

        var reporting = await DisplayCurrency.ForLedgerAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var needs = rates.NewNeeds().AddDerived(from, to, reporting, day);
        var table = await rates.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        var derived = table.Derived(from, to, reporting, day);

        decimal? converted;
        try
        {
            converted = amount is { } a ? derived.Convert(a) : null;
        }
        catch (OverflowException)
        {
            return Results.BadRequest();
        }

        return Results.Ok(new DerivedRateDto(
            from, to, day, reporting, derived.Rate, derived.RateDate,
            [.. derived.Legs.Select(l => new DerivedRateLegDto(l.FromCurrency, l.ToCurrency, l.Day, l.Rate))],
            converted,
            derived.Unavailable));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/fx-rates</c> — a hand-entered rate (ADR-0114 D10).
    /// 201 with the <see cref="FxRateDto"/>. Only for a pair the ledger needs and a day with
    /// no stored rate; never overrides a fetched one. 422 <c>fx-rate-currency-invalid</c>,
    /// <c>fx-rate-date-invalid</c> (unreadable), <c>fx-rate-date-in-future</c> (after the
    /// server's UTC today), <c>fx-rate-invalid</c>,
    /// <c>fx-pair-not-needed</c>, <c>fx-rate-day-taken</c>.
    /// </summary>
    private static async Task<IResult> AddRateAsync(
        Guid ledgerId,
        AddFxRateRequest request,
        ICurrentUserAccessor currentUser,
        MarketSeriesRepository series,
        CancellationToken cancellationToken)
    {
        if (!TryPair(request.FromCurrency, request.ToCurrency, out var from, out var to))
            return CurrencyInvalid();
        if (!TryDay(request.Date, out var day)) return DateInvalid();

        var (result, rate) = await series.AddManualRateAsync(
                ledgerId, currentUser.UserId, from, to, day, request.Rate, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            MarketSeriesRepository.AddRateResult.Ok =>
                Results.Created($"/api/ledgers/{ledgerId}/fx-rates/{from}/{to}", rate),
            MarketSeriesRepository.AddRateResult.RateInvalid =>
                BusinessError.Problem(BusinessError.Codes.FxRateInvalid,
                    "A rate is a positive number: how much one unit of the first currency is worth in the second."),
            MarketSeriesRepository.AddRateResult.DateInFuture =>
                BusinessError.Problem(BusinessError.Codes.FxRateDateInFuture,
                    $"{day:yyyy-MM-dd} is after today (UTC). A rate can be typed only for a day that has begun."),
            MarketSeriesRepository.AddRateResult.PairNotNeeded =>
                BusinessError.Problem(BusinessError.Codes.FxPairNotNeeded,
                    $"This ledger does not convert {from} into {to}. The rates view lists the pairs it needs."),
            MarketSeriesRepository.AddRateResult.DayTaken =>
                BusinessError.Problem(BusinessError.Codes.FxRateDayTaken,
                    $"A {from}/{to} rate is already stored for {day:yyyy-MM-dd}. A stored rate is never replaced."),
            _ => Results.Problem("Unknown add-rate result.", statusCode: 500),
        };
    }

    private static bool TryPair(string? rawFrom, string? rawTo, out string from, out string to)
    {
        to = string.Empty;
        return CurrencyCodes.TryNormalize(rawFrom, out from)
               && CurrencyCodes.TryNormalize(rawTo, out to)
               && !string.Equals(from, to, StringComparison.Ordinal);
    }

    private static bool TryDay(string? raw, out DateOnly day) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static IResult CurrencyInvalid() =>
        BusinessError.Problem(BusinessError.Codes.FxRateCurrencyInvalid,
            "A rate converts one 3-letter ISO currency into another.");

    private static IResult DateInvalid() =>
        BusinessError.Problem(BusinessError.Codes.FxRateDateInvalid,
            "The day must be a date, written yyyy-MM-dd.");
}
