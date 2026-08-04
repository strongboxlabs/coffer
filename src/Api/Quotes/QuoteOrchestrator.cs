using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

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
/// refresh scale (typically &lt;50 securities per ledger).</para>
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
    private readonly AppDbContext _db;
    private readonly IReadOnlyDictionary<string, IQuotePullProvider> _pullProviders;
    private readonly IReadOnlyDictionary<string, IQuotePushProvider> _pushProviders;
    private readonly Db.Repositories.UserPreferencesRepository _prefs;
    private readonly ILogger<QuoteOrchestrator> _logger;

    public QuoteOrchestrator(
        AppDbContext db,
        IEnumerable<IQuotePullProvider> pullProviders,
        IEnumerable<IQuotePushProvider> pushProviders,
        Db.Repositories.UserPreferencesRepository prefs,
        ILogger<QuoteOrchestrator> logger)
    {
        _db = db;
        _pullProviders = pullProviders.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
        _pushProviders = pushProviders.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
        _prefs = prefs;
        _logger = logger;
    }

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

        var startedAt = DateTime.UtcNow;
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

            outcome = await PersistAsync(ledgerId, new[] { providerKey }, result, cancellationToken)
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
        return await PersistAsync(payload.LedgerId, new[] { payload.ProviderKey }, result, cancellationToken)
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
        var startedAt = DateTime.UtcNow;
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
                    var result = await provider.PullAsync(providerContext, cancellationToken)
                        .ConfigureAwait(false);
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
                        Code: "provider-exception",
                        Message: $"{key}: {ex.Message}"));
                }
            }

            var merged = new QuoteResult(allQuotes, allErrors);
            outcome = await PersistAsync(ledgerId, keys, merged, cancellationToken)
                .ConfigureAwait(false);
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
    /// </summary>
    private IQueryable<SecurityRow> HeldAutoPriceable(Guid ledgerId) =>
        _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .Where(AutoPriceable)
            .Where(s => _db.Holdings.Any(
                h => h.SecurityId == s.Id
                     && h.LedgerId == ledgerId
                     && h.Quantity != 0m));

    private async Task<QuotePullContext> BuildContextAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        // The working set for quote pulls (ADR-0054): held + auto-priceable
        // securities, resolving to quote_symbol when set, else the ticker.
        var securities = await HeldAutoPriceable(ledgerId)
            .Select(s => new QuoteSecurityRef(
                s.Id,
                s.QuoteSymbol ?? s.Ticker!,
                "USD",
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

            foreach (var (key, q) in perKey)
            {
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
        var requested = await HeldAutoPriceable(ledgerId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var quoted = result.Quotes.Select(q => q.SecurityId).ToHashSet();
        var unresolved = requested.Where(id => !quoted.Contains(id)).ToList();

        return new QuoteRunOutcome(
            ProviderKeys: providerKeys,
            PricesInserted: inserted,
            PricesUpdated: updated,
            PricesWrittenBySource: bySource,
            SecuritiesUnresolved: unresolved,
            Errors: result.Errors);
    }

    /// <summary>
    /// Record one ledger_operations row per quote refresh (ADR-0055): family
    /// <c>quote</c>, aggregate across the fanned-out providers. <c>who</c> is
    /// the real user, or the system user for scheduled runs; counts live in
    /// the <c>details</c> jsonb.
    /// </summary>
    private async Task WriteRunAsync(
        Guid ledgerId,
        string triggeredVia,
        Guid? triggeredByUserId,
        DateTime startedAt,
        QuoteRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var run = new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Family = "quote",
            ProviderKey = "quote-refresh",
            TriggeredVia = triggeredVia,
            TriggeredByUserId = triggeredByUserId,
            Status = outcome.Errors.Count > 0 ? "partial" : "completed",
            DetailsJson = LedgerOperationDetails.Serialize(new QuoteRunDetails
            {
                PricesInserted = outcome.PricesInserted,
                PricesUpdated = outcome.PricesUpdated,
                SecuritiesUnresolved = outcome.SecuritiesUnresolved.Count,
                PricesFromFetch = outcome.PricesWrittenBySource.GetValueOrDefault(PriceSource.Fetch),
                PricesFromSimplefin = outcome.PricesWrittenBySource.GetValueOrDefault(PriceSource.Simplefin),
            }),
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
        };
        _db.LedgerOperations.Add(run);
        foreach (var e in outcome.Errors)
        {
            _db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = run.Id,
                LedgerId = ledgerId,
                Code = e.Code,
                Message = e.Message,
            });
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
