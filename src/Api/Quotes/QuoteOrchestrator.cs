using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Quotes;

/// <summary>
/// Single orchestration point for the quote-provider family
/// (ADR-0033). Mirrors <c>IngestOrchestrator</c>'s role for the
/// ingest family: providers translate, orchestrator persists.
/// </summary>
/// <remarks>
/// <para>Providers register via DI as <see cref="IQuotePullProvider"/>
/// and/or <see cref="IQuotePushProvider"/>. The orchestrator
/// dispatches by <see cref="IQuotePullProvider.ProviderKey"/>
/// (lookup → invoke → persist).</para>
///
/// <para>Persistence: UPSERT into <c>security_prices</c> keyed by
/// <c>(security_id, price_date)</c>. EF-side load-then-decide
/// pattern (not raw SQL) — N+1 queries are acceptable at quote-
/// refresh scale, which the design assumes is tens of securities
/// per ledger, not thousands.</para>
///
/// <para>Every run is recorded to <c>ledger_operations</c> (family
/// <c>quote</c>) by <c>WriteRunAsync</c> — ADR-0055; failures also
/// surface in the typed <see cref="QuoteRunOutcome"/> returned to
/// the caller. <see cref="RunAllPullsAsync"/> fans to every
/// registered provider (explicit / scheduled refresh);
/// <see cref="RunPullAsync"/> runs exactly one (the post-sync path,
/// which must not reach external providers).</para>
/// </remarks>
public sealed class QuoteOrchestrator
{
    /// <summary>
    /// Decimal places <c>security_prices.price</c> stores — NUMERIC(19,4), mig 155.
    /// Named rather than inlined so the reason a quote is rounded is the column, not
    /// a number someone picked.
    /// </summary>
    private const int PriceScale = 4;

    /// <summary>Error code for a quote a provider handed over with a currency code no
    /// stored price may carry; the quote is not written.</summary>
    public const string CurrencyInvalidCode = CurrencyCodes.InvalidErrorCode;

    /// <summary>A stored price's currency is already in <see cref="CurrencyCodes"/>' normal
    /// form: a code that normalizes to something else (<c>GBp</c>, <c> usd</c>) is refused,
    /// not silently rewritten.</summary>
    private static bool IsStoredCurrencyShape(string? code) =>
        CurrencyCodes.TryNormalize(code, out var normal) && string.Equals(normal, code, StringComparison.Ordinal);

    private readonly AppDbContext _db;
    private readonly IReadOnlyDictionary<string, IQuotePullProvider> _pullProviders;
    private readonly IReadOnlyDictionary<string, IQuotePushProvider> _pushProviders;
    private readonly Db.Repositories.UserPreferencesRepository _prefs;
    private readonly ILogger<QuoteOrchestrator> _logger;
    private readonly TimeProvider _clock;
    private readonly MarketSeries.MarketSeriesFetcher? _marketSeries;

    /// <param name="marketSeries">The benchmark and exchange-rate fetch that rides
    /// <see cref="RunAllPullsAsync"/> (ADR-0114 D11). Null runs prices only, which is what
    /// the post-sync pull (<see cref="RunPullAsync"/>) does whether or not it is set.</param>
    public QuoteOrchestrator(
        AppDbContext db,
        IEnumerable<IQuotePullProvider> pullProviders,
        IEnumerable<IQuotePushProvider> pushProviders,
        Db.Repositories.UserPreferencesRepository prefs,
        ILogger<QuoteOrchestrator> logger,
        TimeProvider clock,
        MarketSeries.MarketSeriesFetcher? marketSeries = null)
    {
        _db = db;
        _pullProviders = pullProviders.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
        _pushProviders = pushProviders.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
        _prefs = prefs;
        _logger = logger;
        _clock = clock;
        _marketSeries = marketSeries;
    }

    /// <summary>Error code for a market-series step that threw: folded into the run, as a
    /// provider exception is, so the refresh is still audited.</summary>
    public const string MarketSeriesExceptionCode = "market-series-exception";

    /// <summary>The source key stamped on the benchmark and exchange-rate fetch's errors
    /// (<see cref="QuoteError.ProviderKey"/>): that fetch is not a quote provider, so it
    /// has no provider key of its own.</summary>
    public const string MarketSeriesProviderKey = "market-series";

    /// <summary>
    /// Stamp <paramref name="key"/> on every error that does not name its source yet. The
    /// orchestrator calls this on everything a source returns, so a provider never has to
    /// name itself and an error can never reach the run without a source.
    /// </summary>
    internal static IReadOnlyList<QuoteError> FromSource(IEnumerable<QuoteError> errors, string key) =>
        errors.Select(e => e.ProviderKey is null ? e with { ProviderKey = key } : e).ToList();

    /// <summary>A provider's result with its key stamped on each quote and each error.</summary>
    private static QuoteResult FromSource(QuoteResult result, string key) =>
        new(
            result.Quotes.Select(q => q.ProviderKey is null ? q with { ProviderKey = key } : q).ToList(),
            FromSource(result.Errors, key));

    /// <summary>
    /// Invoke ONE pull-capable provider for one ledger, recording the run
    /// (ADR-0055). Throws <see cref="InvalidOperationException"/> if the
    /// provider key isn't registered. A provider exception is folded into the
    /// run as an error (status "partial") rather than propagated, mirroring
    /// <see cref="RunAllPullsAsync"/> — the run is always audited.
    /// </summary>
    /// <remarks>
    /// This is the post-sync entry point (ADR-0033 §5 / ADR-0054): a SimpleFIN
    /// sync calls it with <c>simplefin-holdings</c> to lift prices out of the
    /// payload it just fetched — it deliberately does NOT fan out to external
    /// market-data providers (Yahoo et al.), which would turn every bank sync
    /// into outbound egress. Those run via <see cref="RunAllPullsAsync"/> on an
    /// explicit refresh or the scheduled worker.
    /// </remarks>
    public async Task<QuoteRunOutcome> RunPullAsync(
        Guid ledgerId,
        string providerKey,
        string triggeredVia,
        Guid? triggeredByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!_pullProviders.TryGetValue(providerKey, out var provider))
        {
            throw new InvalidOperationException(
                $"No IQuotePullProvider registered for key '{providerKey}'.");
        }

        var startedAt = _clock.GetUtcNow().UtcDateTime;
        var context = await BuildContextAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        QuoteRunOutcome outcome;
        if (context.Securities.Count == 0)
        {
            outcome = EmptyOutcome(providerKey);
        }
        else
        {
            QuoteResult result;
            try
            {
                result = await provider.PullAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Quote pull failed: provider={ProviderKey} ledger={LedgerId}",
                    providerKey, ledgerId);
                result = new QuoteResult(
                    Array.Empty<QuoteEntry>(),
                    new[]
                    {
                        new QuoteError(
                            SecurityId: null,
                            Ticker: string.Empty,
                            Code: "provider-exception",
                            Message: $"{providerKey}: {ex.Message}"),
                    });
            }

            outcome = await PersistAsync(ledgerId, new[] { providerKey }, FromSource(result, providerKey), cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteRunAsync(ledgerId, triggeredVia, triggeredByUserId, startedAt, outcome, cancellationToken)
            .ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Push path: caller supplies a payload (file bytes, parsed
    /// CSV rows, webhook JSON) that originated from a
    /// push-capable provider. Orchestrator routes by
    /// <see cref="QuotePushPayload.ProviderKey"/> and invokes the
    /// matching provider's parse routine. No push providers in
    /// v1; the path is here so future ones plug in without
    /// orchestrator refactor.
    /// </summary>
    public async Task<QuoteRunOutcome> RunPushAsync(
        QuotePushPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_pushProviders.TryGetValue(payload.ProviderKey, out var provider))
        {
            throw new InvalidOperationException(
                $"No IQuotePushProvider registered for key '{payload.ProviderKey}'.");
        }
        var result = await provider.PushAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return await PersistAsync(payload.LedgerId, new[] { payload.ProviderKey }, FromSource(result, payload.ProviderKey), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fan out to every registered pull-capable provider for the
    /// ledger. Per ADR-0033 §5, this is what
    /// <c>IngestOrchestrator</c> calls at end of a successful
    /// SimpleFIN sync. Errors from one provider don't short-
    /// circuit the others — the outcome merges all
    /// <see cref="QuoteResult.Errors"/>.
    /// </summary>
    public async Task<QuoteRunOutcome> RunAllPullsAsync(
        Guid ledgerId,
        string triggeredVia,
        Guid? triggeredByUserId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _clock.GetUtcNow().UtcDateTime;
        var context = await BuildContextAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        QuoteRunOutcome outcome;
        if (context.Securities.Count == 0 || _pullProviders.Count == 0)
        {
            outcome = EmptyOutcome();
        }
        else
        {
            // Opt-in (external-egress) providers run only when the acting
            // ledger pref enables them (ADR-0057). The pref is the run's own
            // user — the acting user for a manual refresh, the system user for a
            // scheduled run (ADR-0055 attribution). A run with no user attaches
            // no opt-in providers (only the always-on, no-egress ones run).
            var enabled = triggeredByUserId is { } uid
                ? new HashSet<string>(
                    (await _prefs.GetQuotesAsync(uid, ledgerId, cancellationToken)
                        .ConfigureAwait(false)).EnabledProviders,
                    StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var allQuotes = new List<QuoteEntry>();
            var allErrors = new List<QuoteError>();
            var keys = new List<string>(_pullProviders.Count);

            foreach (var (key, provider) in _pullProviders)
            {
                if (provider.RequiresOptIn && !enabled.Contains(key))
                    continue;
                keys.Add(key);
                // Egress (external) providers only see securities whose symbol is a
                // public ticker (ADR-0054 D2). A private / feed-internal quote symbol
                // (e.g. a 529 portfolio number) is matched only by the no-egress feed
                // provider — never sent to Yahoo et al.
                var providerContext = provider.RequiresOptIn
                    ? context with { Securities = context.Securities.Where(s => s.QuoteSymbolPublic).ToList() }
                    : context;
                try
                {
                    var result = FromSource(
                        await provider.PullAsync(providerContext, cancellationToken).ConfigureAwait(false),
                        key);
                    allQuotes.AddRange(result.Quotes);
                    allErrors.AddRange(result.Errors);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Quote pull failed during run-all: provider={ProviderKey} ledger={LedgerId}",
                        key, ledgerId);
                    allErrors.Add(new QuoteError(
                        SecurityId: null,
                        Ticker: string.Empty,
                        // Shared with the scheduled handler, which classifies a run
                        // carrying this code as degraded rather than successful. A
                        // string literal in both places is one rename away from a
                        // dead-man's switch that silently goes back to lying.
                        Code: Scheduling.QuoteRefreshJobHandler.ProviderExceptionCode,
                        Message: $"{key}: {ex.Message}",
                        ProviderKey: key));
                }
            }

            var merged = new QuoteResult(allQuotes, allErrors);
            outcome = await PersistAsync(ledgerId, keys, merged, cancellationToken)
                .ConfigureAwait(false);
        }

        // ADR-0114 D5 (amendment): the one-time currency pass, whatever was held. Outside
        // the branch above, so a ledger with nothing held still has its unheld securities'
        // fetched prices confirmed.
        outcome = await ConfirmUnconfirmedCurrenciesAsync(ledgerId, triggeredByUserId, outcome, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0114 D11: benchmarks and exchange rates ride this refresh, each under the
        // run user's consent for its own destination (ADR-0114 D8). They run whether or not any
        // security is held: a ledger of bank accounts in two currencies needs rates too.
        if (_marketSeries is not null)
        {
            MarketSeries.MarketSeriesOutcome series;
            try
            {
                series = await _marketSeries.FetchAsync(ledgerId, triggeredByUserId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Market-series fetch failed during a quote refresh: ledger={LedgerId}", ledgerId);
                series = new MarketSeries.MarketSeriesOutcome(0, 0,
                    [new QuoteError(null, string.Empty, MarketSeriesExceptionCode,
                        "The benchmark and exchange-rate fetch failed. Detail is in the server log.")]);
            }
            outcome = outcome with
            {
                Errors = [.. outcome.Errors, .. FromSource(series.Errors, MarketSeriesProviderKey)],
                RatesInserted = series.RatesInserted,
                BenchmarkDaysInserted = series.BenchmarkDaysInserted,
            };
        }

        // ADR-0055: record the run — even when nothing was eligible — so a
        // refresh that changed nothing is still visible + attributed.
        await WriteRunAsync(ledgerId, triggeredVia, triggeredByUserId, startedAt, outcome, cancellationToken)
            .ConfigureAwait(false);
        return outcome;
    }

    // ----- internals -----

    /// <summary>
    /// The auto-priceable predicate (ADR-0054 D2): the security opted into
    /// auto-pricing and resolves to a provider symbol (quote_symbol, else
    /// ticker). Shared by the pull working set and the unresolved tally so
    /// the two never drift.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<SecurityRow, bool>>
        AutoPriceable = s => s.AutoPrice && (s.QuoteSymbol != null || s.Ticker != null);

    /// <summary>
    /// The quote-pull working set for one ledger: auto-priceable securities
    /// (<see cref="AutoPriceable"/>) that are currently HELD — they have a
    /// holdings row with a non-zero quantity. Refreshing a security with no
    /// live position is wasted egress, so unheld securities are excluded from
    /// both the pull and the unresolved tally. <c>auto_price</c> stays a pure
    /// user-intent flag; "held" is derived live here (no flag mutation), so a
    /// buy/sell takes effect immediately without backfilling a column.
    /// <para>
    /// Held today (<c>holdings_as_of_now</c>, migration 259), the holding every
    /// current value is computed from. Not the <c>holdings</c> projection, which
    /// counts trades the register calls Scheduled: a position sold by a future-dated
    /// sale has no projection quantity, so refreshing from the projection would stop
    /// pricing it while the portfolio card still values it today. A security held only through a
    /// future-dated buy is picked up by the first run on or after that date. A refresh
    /// has no caller's date, so it takes the register's fallback (the server clock's
    /// UTC date, <see cref="ScheduledBoundary"/>).
    /// </para>
    /// </summary>
    private async Task<IQueryable<SecurityRow>> HeldAutoPriceableAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var heldNow = await _db.HoldingsAsOfNow(
                ledgerId, ScheduledBoundary.ScheduledFrom(null, _clock), null)
            .AsNoTracking()
            .Select(h => h.SecurityId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .Where(AutoPriceable)
            .Where(s => heldNow.Contains(s.Id));
    }

    private async Task<QuotePullContext> BuildContextAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        // The working set for quote pulls (ADR-0054): held + auto-priceable
        // securities, resolving to quote_symbol when set, else the ticker.
        var securities = await (await HeldAutoPriceableAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false))
            .Select(s => new QuoteSecurityRef(
                s.Id,
                s.QuoteSymbol ?? s.Ticker!,
                s.QuoteSymbolPublic))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Per-ledger wrapped LEK. Most v1 providers don't use it;
        // surfacing it on the context anyway matches ADR-0031's
        // PullContext shape so future providers needing per-ledger
        // secret material (Yahoo API key, IEX token) plug in
        // without a context shape-change.
        var ledger = await _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => new { l.WrappedLek })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var wrappedLek = ledger?.WrappedLek ?? Array.Empty<byte>();

        return new QuotePullContext(ledgerId, wrappedLek, securities);
    }

    private async Task<QuoteRunOutcome> PersistAsync(
        Guid ledgerId,
        IReadOnlyList<string> providerKeys,
        QuoteResult result,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var updated = 0;
        // Attribute each WRITTEN price to its winning source (ADR-0070) so the
        // run can report which provider actually moved prices, not just a count.
        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);

        // Every stored price names a major currency code (ADR-0114 D5). Each
        // provider resolves its own, and this is the single write path they all
        // converge on, so a provider that hands over a blank, minor-unit or
        // otherwise malformed code has that quote dropped here with an error rather
        // than stored as-is — the same reason the scale is normalized below.
        var badCurrency = result.Quotes.Where(q => !IsStoredCurrencyShape(q.CurrencyCode)).ToList();
        if (badCurrency.Count > 0)
        {
            result = new QuoteResult(
                result.Quotes.Where(q => IsStoredCurrencyShape(q.CurrencyCode)).ToList(),
                result.Errors.Concat(badCurrency.Select(q => new QuoteError(
                    q.SecurityId, string.Empty, CurrencyInvalidCode,
                    $"A {q.Source} quote named currency '{q.CurrencyCode}', which is not a stored currency code.",
                    ProviderKey: q.ProviderKey)))
                    .ToList());
        }

        if (result.Quotes.Count > 0)
        {
            // ADR-0070: one price per (security, day). Collapse each quote's
            // as-of instant to its UTC calendar day; when more than one quote
            // hits the same (security, day) in a run, keep the highest-ranked
            // source (manual == Yahoo > simplefin > import) so a true Yahoo
            // close wins over an intraday SimpleFIN balance for that day.
            var perKey = result.Quotes
                .GroupBy(q => (q.SecurityId, Day: DateOnly.FromDateTime(q.PriceAsOfUtc)))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(q => PriceSource.Rank(q.Source)).Last());

            // Load existing rows for the touched (security, day) set in one
            // query so we know per-entry whether to INSERT or UPDATE. The
            // UNIQUE (security_id, price_date) index makes the lookup cheap.
            var securityIds = perKey.Keys.Select(k => k.SecurityId).Distinct().ToList();
            var days = perKey.Keys.Select(k => k.Day).Distinct().ToList();
            var existing = await _db.SecurityPrices
                .Where(p => p.LedgerId == ledgerId
                            && securityIds.Contains(p.SecurityId)
                            && days.Contains(p.PriceDate))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var existingByKey = existing.ToDictionary(
                p => (p.SecurityId, Day: p.PriceDate), p => p);

            foreach (var (key, entry) in perKey)
            {
                // Normalize to the STORED scale before anything compares or writes it.
                // security_prices.price is NUMERIC(19,4) (mig 155), so a provider that
                // hands over more decimals gets rounded by Postgres on assignment — and
                // then the value in memory no longer equals the value in the column.
                // That breaks change detection rather than the price: the comparison
                // below never matches, so every refetch rewrites the row and counts an
                // update that changed nothing. A Yahoo close is float-derived (mig 155
                // cites a close from that same `fetch` source carrying a float tail out
                // to the twelfth decimal), so it fired essentially every time.
                //
                // Rounded HERE, at the single write path, rather than trusting each
                // provider to remember: mig 155 narrowed the column on the premise that
                // "current producers already round to 4dp", and the premise was already
                // stale — the Yahoo provider predated it. Three providers each restating
                // one rule is three chances to get it wrong; this is the one place all
                // of them converge.
                var q = entry with { Price = decimal.Round(entry.Price, PriceScale) };

                if (existingByKey.TryGetValue(key, out var existingRow))
                {
                    // Source-priority upsert (ADR-0070 D2): overwrite the day's
                    // row only when the incoming source ranks at least as high.
                    // A lower-ranked source (e.g. an intraday SimpleFIN balance
                    // landing on a day a Yahoo close already owns) is dropped.
                    if (PriceSource.Rank(q.Source) < PriceSource.Rank(existingRow.Source))
                        continue;

                    // Update price + currency + source (the new winner);
                    // high/low/volume are provider-absent and not NULLed out.
                    if (existingRow.Price != q.Price
                        || !string.Equals(existingRow.CurrencyCode, q.CurrencyCode, StringComparison.Ordinal)
                        || !string.Equals(existingRow.Source, q.Source, StringComparison.Ordinal))
                    {
                        existingRow.Price = q.Price;
                        existingRow.CurrencyCode = q.CurrencyCode;
                        existingRow.Source = q.Source;
                        updated++;
                        bySource[q.Source] = bySource.GetValueOrDefault(q.Source) + 1;
                    }
                }
                else
                {
                    _db.SecurityPrices.Add(new SecurityPriceRow
                    {
                        Id = Guid.NewGuid(),
                        SecurityId = q.SecurityId,
                        LedgerId = ledgerId,
                        Price = q.Price,
                        CurrencyCode = q.CurrencyCode,
                        PriceDate = DateOnly.FromDateTime(q.PriceAsOfUtc),
                        Source = q.Source,
                    });
                    inserted++;
                    bySource[q.Source] = bySource.GetValueOrDefault(q.Source) + 1;
                }
            }
            if (inserted > 0 || updated > 0)
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Unresolved = securities the orchestrator asked for that
        // no provider returned. Surfaced so the SPA can flag
        // "couldn't refresh" pills per-position. Computed even
        // when no quotes came back (the empty-quotes case is when
        // unresolved matters MOST — every ticker is unresolved).
        var requested = await (await HeldAutoPriceableAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var quoted = result.Quotes.Select(q => q.SecurityId).ToHashSet();
        var unresolved = requested.Where(id => !quoted.Contains(id)).ToList();

        // ADR-0114 D5 (amendment): a fetch quote names its security's currency, which
        // confirms it and re-stamps that security's older fetch rows in another currency.
        // Every fetch quote counts, stored or not: one the source ladder dropped for a day
        // a manual price owns still says what Yahoo quotes the symbol in.
        var confirmed = await QuoteCurrencyConfirmation.ConfirmAsync(
                _db, ledgerId,
                result.Quotes
                    .Where(q => q.Source == PriceSource.Fetch)
                    .Select(q => new QuoteCurrency(q.SecurityId, q.CurrencyCode, q.QuotedMinorUnitFactor)),
                _clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);

        return new QuoteRunOutcome(
            ProviderKeys: providerKeys,
            PricesInserted: inserted,
            PricesUpdated: updated,
            PricesWrittenBySource: bySource,
            SecuritiesUnresolved: unresolved,
            Errors: result.Errors,
            CurrenciesConfirmed: confirmed.CurrenciesConfirmed,
            RowsRestamped: confirmed.RowsRestamped);
    }

    /// <summary>
    /// The one-time currency pass (ADR-0114 D5, amendment): every security with fetched
    /// prices whose currency no Yahoo read has confirmed, held or not, gets one
    /// currency-only lookup, which stores no price and re-stamps its fetch rows. The one
    /// exception to quoting held securities only (ADR-0054). "Once" because a confirmed
    /// security drops out; no flag of its own.
    /// </summary>
    /// <remarks>
    /// Egress, so only under the run's user's opt-in for a provider that can look a
    /// currency up; a run with no user, or with that provider off, skips it. A security
    /// that already has an error this run was just asked and is not asked again.
    /// </remarks>
    private async Task<QuoteRunOutcome> ConfirmUnconfirmedCurrenciesAsync(
        Guid ledgerId, Guid? triggeredByUserId, QuoteRunOutcome outcome, CancellationToken cancellationToken)
    {
        if (triggeredByUserId is not { } uid) return outcome;
        var enabled = (await _prefs.GetQuotesAsync(uid, ledgerId, cancellationToken).ConfigureAwait(false))
            .EnabledProviders;
        var lookup = _pullProviders.Values
            .Where(p => p.RequiresOptIn && enabled.Contains(p.ProviderKey, StringComparer.Ordinal))
            .OfType<IQuoteCurrencyLookup>()
            .FirstOrDefault();
        if (lookup is null) return outcome;

        var asked = outcome.Errors.Where(e => e.SecurityId is not null).Select(e => e.SecurityId!.Value).ToHashSet();
        var candidates = await QuoteCurrencyConfirmation.PassCandidatesAsync(_db, ledgerId, asked, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0) return outcome;

        var (confirmed, errors) = await QuoteCurrencyConfirmation.LookupAndConfirmAsync(
                _db, ledgerId, lookup, candidates, _clock, cancellationToken)
            .ConfigureAwait(false);
        return outcome with
        {
            ProviderKeys = outcome.ProviderKeys.Contains(lookup.ProviderKey, StringComparer.Ordinal)
                ? outcome.ProviderKeys
                : [.. outcome.ProviderKeys, lookup.ProviderKey],
            Errors = [.. outcome.Errors, .. FromSource(errors, lookup.ProviderKey)],
            CurrenciesConfirmed = outcome.CurrenciesConfirmed + confirmed.CurrenciesConfirmed,
            RowsRestamped = outcome.RowsRestamped + confirmed.RowsRestamped,
        };
    }

    /// <summary>
    /// Record one ledger_operations row per quote refresh (ADR-0055): family
    /// <c>quote</c>, aggregate across the fanned-out providers. <c>who</c> is
    /// the real user, or the system user for scheduled runs; counts live in
    /// the <c>details</c> jsonb.
    /// </summary>
    private Task WriteRunAsync(
        Guid ledgerId,
        string triggeredVia,
        Guid? triggeredByUserId,
        DateTime startedAt,
        QuoteRunOutcome outcome,
        CancellationToken cancellationToken) =>
        WriteRunAsync(
            _db, _clock, ledgerId, QuoteRefreshProviderKey, triggeredVia, triggeredByUserId, startedAt,
            new QuoteRunDetails
            {
                PricesInserted = outcome.PricesInserted,
                PricesUpdated = outcome.PricesUpdated,
                SecuritiesUnresolved = outcome.SecuritiesUnresolved.Count,
                PricesFromFetch = outcome.PricesWrittenBySource.GetValueOrDefault(PriceSource.Fetch),
                PricesFromSimplefin = outcome.PricesWrittenBySource.GetValueOrDefault(PriceSource.Simplefin),
                CurrenciesConfirmed = outcome.CurrenciesConfirmed,
                RowsRestamped = outcome.RowsRestamped,
                RatesInserted = outcome.RatesInserted,
                BenchmarkDaysInserted = outcome.BenchmarkDaysInserted,
            },
            outcome.Errors, cancellationToken);

    /// <summary>The <c>provider_key</c> of a quote refresh's <c>ledger_operations</c> row.</summary>
    public const string QuoteRefreshProviderKey = "quote-refresh";

    /// <summary>
    /// Write one family-<c>quote</c> <c>ledger_operations</c> row and an error row per
    /// <paramref name="errors"/> entry: a quote refresh's, and a standalone market-series
    /// backfill's (ADR-0114 D11), which share the shape.
    /// </summary>
    internal static async Task WriteRunAsync(
        AppDbContext db,
        TimeProvider clock,
        Guid ledgerId,
        string providerKey,
        string triggeredVia,
        Guid? triggeredByUserId,
        DateTime startedAt,
        QuoteRunDetails details,
        IReadOnlyList<QuoteError> errors,
        CancellationToken cancellationToken)
    {
        var run = new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Family = "quote",
            ProviderKey = providerKey,
            TriggeredVia = triggeredVia,
            TriggeredByUserId = triggeredByUserId,
            Status = errors.Count > 0 ? "partial" : "completed",
            DetailsJson = LedgerOperationDetails.Serialize(details),
            StartedAt = startedAt,
            CompletedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.LedgerOperations.Add(run);
        foreach (var e in errors)
        {
            db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = run.Id,
                LedgerId = ledgerId,
                Code = e.Code,
                Message = e.Message,
                ProviderKey = e.ProviderKey,
                // The feed's account id, in the column a sync's errors already use for
                // it, so the Activity log names the account the same way for both.
                SimpleFinAccountId = e.FeedAccountId,
            });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static QuoteRunOutcome EmptyOutcome(string? providerKey = null) =>
        new(
            ProviderKeys: providerKey is null
                ? Array.Empty<string>()
                : new[] { providerKey },
            PricesInserted: 0,
            PricesUpdated: 0,
            PricesWrittenBySource: new Dictionary<string, int>(),
            SecuritiesUnresolved: Array.Empty<Guid>(),
            Errors: Array.Empty<QuoteError>());
}
