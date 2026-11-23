using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Quotes;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Securities catalog gateway (slice A3). Owns CRUD over the
/// per-ledger <c>securities</c> table, plus the aggregated reads the
/// Securities pages need (per-security totals, latest prices,
/// transaction history). LINQ + EF only — complex shape lives in
/// Postgres views, not raw SQL here (see feedback_no_raw_sql_in_api).
/// </summary>
/// <remarks>
/// Ledger-membership is the authoritative scope: the endpoint proves
/// <paramref name="ledgerId" /> is visible to the caller before
/// reaching the repo. Defence in depth is RLS once Phase D ships;
/// until then the WHERE clauses are the gate.
/// </remarks>
public sealed class SecuritiesRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public SecuritiesRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ADR-0067: economic classes only (vehicle moved to vehicle_type).
    private static readonly IReadOnlySet<string> ValidAssetClasses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "equity", "fixed_income", "multi_asset", "cash", "real_assets", "alternative",
        };

    /// <summary>
    /// Catalog list: every security in the ledger, with the per-security
    /// aggregates the catalog table needs in one round-trip. <paramref name="today"/>
    /// is the caller's local date, as the register takes it (null for its fallback).
    /// </summary>
    public async Task<IReadOnlyList<SecuritySummaryDto>> ListByLedgerAsync(
        Guid ledgerId,
        string? search,
        DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        var q = _db.Securities.AsNoTracking().Where(s => s.LedgerId == ledgerId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Case-insensitive substring match on ticker / cusip / name.
            // The catalog is O(N) per ledger (the design assumes a small
            // catalog) — no tsvector index until volume demands it.
            var needle = search.Trim();
            q = q.Where(s =>
                EF.Functions.ILike(s.Name, "%" + needle + "%")
                || (s.Ticker != null && EF.Functions.ILike(s.Ticker, "%" + needle + "%"))
                || (s.Cusip  != null && EF.Functions.ILike(s.Cusip,  "%" + needle + "%")));
        }

        var rows = await q
            .OrderBy(s => s.Ticker == null)            // nulls last
            .ThenBy(s => s.Ticker)
            .ThenBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Select(s => new SecurityListRow
            {
                Id          = s.Id,
                Ticker      = s.Ticker,
                Cusip       = s.Cusip,
                Name        = s.Name,
                AssetClass  = s.AssetClass,
                Exchange    = s.Exchange,
                IsActive    = s.IsActive,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Aggregates on the caller's today (migration 259), the read the portfolio
        // card values with: total quantity = the positions held that day, summed
        // across every holdings sibling in the ledger; latest price = the latest close
        // dated on or before it, else the last trade's price, on the day's split
        // basis. Not the holdings projection and the newest price row, which count a
        // trade, split or price the register calls Scheduled, so the catalog's value
        // for a security would move before the card's did. One read each for the
        // whole ledger, rather than a function call per row, at one boundary; the
        // currencies below come from the same two reads.
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(today, _clock);
        var held = await SecurityCurrencies.HeldNowAsync(_db, ledgerId, scheduledFrom, null, cancellationToken)
            .ConfigureAwait(false);
        var quantityBySecurity = held
            .GroupBy(h => h.SecurityId)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Quantity));
        var priceBySecurity = await _db.SecurityPricesAsOfNow(ledgerId, scheduledFrom, null)
            .AsNoTracking()
            .ToDictionaryAsync(p => p.SecurityId, cancellationToken)
            .ConfigureAwait(false);

        // The currency each row's price and amount are in, by the detail's rule: the
        // price's, else the holders', else the ledger's. Nothing converts.
        var priceCurrency = await SecurityCurrencies.PriceRowAsync(
                _db, ledgerId, priceBySecurity.Values, null, cancellationToken)
            .ConfigureAwait(false);
        var holders = SecurityCurrencies.Holders(held);
        var ledgerCurrency = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r =>
            {
                var price = priceBySecurity.GetValueOrDefault(r.Id);
                return new SecuritySummaryDto(
                    r.Id, r.Ticker, r.Cusip, r.Name, r.AssetClass, r.Exchange, r.IsActive,
                    quantityBySecurity.GetValueOrDefault(r.Id),
                    price?.Price, price?.PriceDate,
                    PriceCurrencyCode: priceCurrency.GetValueOrDefault(r.Id)
                        ?? (holders.TryGetValue(r.Id, out var holding) ? holding.Code : ledgerCurrency));
            })
            .ToList();
    }

    /// <summary>Detail view: hero data + 10 most-recent prices. <paramref name="today"/>
    /// is the caller's local date, as the register takes it (null for its fallback).</summary>
    public async Task<SecurityDetailDto?> GetByIdAsync(
        Guid ledgerId,
        Guid securityId,
        DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        var s = await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && s.Id == securityId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (s is null) return null;

        // The hero's figures are the caller's today (migration 259), as the portfolio
        // card's are: what is held that day, at the latest close dated on or before
        // it, else the last trade's price, on the day's split basis. A trade, split or
        // price the register calls Scheduled does not move them. The recent-prices
        // list below is a history, so it stays unbounded.
        var scheduledFrom = ScheduledBoundary.ScheduledFrom(today, _clock);
        var only = new[] { securityId };
        var held = await SecurityCurrencies.HeldNowAsync(_db, ledgerId, scheduledFrom, only, cancellationToken)
            .ConfigureAwait(false);
        var totalQuantity = held.Sum(h => h.Quantity);

        // The cost basis combines every account holding the security, so it is in the
        // ledger's reporting currency (ADR-0114 D7): a position kept in another currency
        // converts lot by lot at each lot's acquisition date, as a trade converts at its
        // trade date; one in the reporting currency keeps the basis it reports. A missing
        // rate makes it null and names the pair and day (ADR-0114 D9).
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var reporting = currencies.Reporting;
        bool Foreign(Guid holdingsAccountId) => currencies.OfPositionsOn(holdingsAccountId) != reporting;
        var lots = await LotCosts.OpenAsync(
                _db, ledgerId, ScheduledBoundary.LastCountedInstant(today, _clock),
                [.. held.Select(h => h.AccountId).Where(Foreign).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        var latest = await _db.SecurityPricesAsOfNow(ledgerId, scheduledFrom, only)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // The market value combines every account holding the security too, so it is in
        // the reporting currency (ADR-0114 D7). It is a current value: each position from
        // the price's currency into its brokerage's, as that brokerage shows it, then into
        // the reporting currency, both at the latest rate, summed and rounded once. The
        // price's currency is the one the valuation reads (holdings_as_of_now's), so the
        // value converts from the currency it was computed in.
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var h in held.Where(h => Foreign(h.AccountId)))
            LotCosts.Declare(needs, lots[(h.AccountId, h.SecurityId)], currencies.OfPositionsOn(h.AccountId), reporting);
        if (latest is not null)
        {
            foreach (var h in held)
            {
                var account = currencies.OfPositionsOn(h.AccountId);
                needs.AddLatest(latest.PriceCurrency, account);
                needs.AddLatest(account, reporting);
            }
        }
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        var costTotal = rates.NewTotal(reporting);
        foreach (var h in held)
        {
            if (Foreign(h.AccountId))
                LotCosts.AddTo(costTotal, lots[(h.AccountId, h.SecurityId)], currencies.OfPositionsOn(h.AccountId));
            else
                costTotal.Add(h.CostBasis, reporting, RateDay.Latest());   // the identity
        }
        var totalCostBasis = costTotal.Result;

        // With no current price there is no market value and no gain, and no rate to name.
        var marketValue = new Converted(null, RateReasons.None);
        var gainUnavailable = RateReasons.None;
        if (latest is not null)
        {
            var valueTotal = rates.NewTotal(reporting);
            foreach (var h in held)
            {
                var account = currencies.OfPositionsOn(h.AccountId);
                var own = rates.NewTotal(account)
                    .AddLatest(ReportingScale.MarketValue(h.Quantity, latest.Price), latest.PriceCurrency)
                    .Result;
                valueTotal.Add(own, account, RateDay.Latest());
            }
            marketValue = valueTotal.Result;
            gainUnavailable = Converted.Reasons(marketValue, totalCostBasis);
        }
        decimal? unrealizedGain = marketValue.Amount is { } mv && totalCostBasis.Amount is { } cb ? mv - cb : null;

        // The currencies the hero's other figures are in, so each is printed in its own.
        // The holding accounts' currency: the one the accounts that hold the security that
        // day agree on, else the first in ordinal order, else the ledger's. The price is in
        // the price's currency (SecurityCurrencies.PriceRowAsync), else the holders'. Both
        // come from the two reads above.
        var holdingsCurrency = SecurityCurrencies.Holders(held).TryGetValue(securityId, out var holders)
            ? holders.Code
            : reporting;
        var priceCurrency = (await SecurityCurrencies.PriceRowAsync(
                    _db, ledgerId, latest is null ? [] : [latest], only, cancellationToken)
                .ConfigureAwait(false))
            .GetValueOrDefault(securityId)
            ?? holdingsCurrency;

        var recentPrices = await _db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .OrderByDescending(p => p.PriceDate)
            .Take(10)
            .Select(p => new SecurityPricePointDto(
                // The `security_prices` table doesn't track a free-form
                // source label today — every row is importer-or-manual
                // and the importer doesn't stamp a column for it.
                // Surface NULL for now; a future feed slice can add a
                // column + populate it.
                p.PriceDate, p.Price, (string?)null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SecurityDetailDto(
            Id: s.Id,
            Ticker: s.Ticker,
            Cusip: s.Cusip,
            Name: s.Name,
            AssetClass: s.AssetClass,
            Exchange: s.Exchange,
            IsActive: s.IsActive,
            TotalQuantity: totalQuantity,
            TotalCostBasis: totalCostBasis.Amount,
            LatestPrice: latest?.Price,
            LatestPriceAsOf: latest?.PriceDate,
            RecentPrices: recentPrices,
            QuoteSymbol: s.QuoteSymbol,
            AutoPrice: s.AutoPrice,
            QuoteSymbolPublic: s.QuoteSymbolPublic,
            VehicleType: s.VehicleType,
            Region: s.Region,
            EquitySize: s.EquitySize,
            EquityStyle: s.EquityStyle,
            FiDuration: s.FiDuration,
            FiCredit: s.FiCredit,
            TaxCharacter: s.TaxCharacter,
            ClassificationSource: s.ClassificationSource,
            ClassificationConfidence: s.ClassificationConfidence,
            PriceCurrencyCode: priceCurrency,
            HoldingsCurrencyCode: holdingsCurrency,
            CostBasisCurrencyCode: reporting,
            CostBasisRateUnavailable: totalCostBasis.Unavailable,
            MarketValue: marketValue.Amount,
            MarketValueCurrencyCode: reporting,
            MarketValueRateUnavailable: marketValue.Unavailable,
            UnrealizedGain: unrealizedGain,
            UnrealizedGainCurrencyCode: reporting,
            UnrealizedGainRateUnavailable: gainUnavailable);
    }

    /// <summary>Cursor-paginated transactions touching this security.
    ///
    /// <para>One row per <c>txn_header</c>: a Reinvest event from MD is
    /// "one transaction" in the user's head, not 4 legs across 2 postings.
    /// Per header, ≥2 legs typically have OWN <c>security_id</c> stamped
    /// (income-cash leg from <c>MakeCategoryPair</c> + holdings leg from
    /// <c>MakeSecPair</c>); the canonical row is the qty-bearing one (the
    /// holdings leg) so the rendered row shows the shares, the price and
    /// the amount ("+Q sh @ P · +A") rather than the no-qty income leg.</para>
    ///
    /// <para>Query reads <c>txn_legs</c> directly, NOT the
    /// <c>resolved_transactions</c> view, because the view's
    /// <c>COALESCE(l.security_id, other.security_id)</c> and
    /// <c>COALESCE(l.quantity, other.quantity)</c> projections leak the
    /// counterparty's security/qty onto both sides of a posting, which
    /// surfaces every event as 4 candidate legs with mixed amount signs
    /// and makes any per-header dedupe non-deterministic. Querying the
    /// underlying table gives unambiguous OWN values.</para>
    ///
    /// <para>Cursor encodes <c>posted_at | account_id | leg_id</c> — the
    /// canonical leg's own keys — so &gt;page-size rows on the same date
    /// don't truncate. Header <c>posted_at</c>/<c>payee</c> come off
    /// <c>txn_headers</c> directly (not the per-leg view — see above), which
    /// since migration 230 IS the curated value, so the drill-in orders and
    /// labels the same way the register does.</para>
    /// </summary>
    public async Task<SecurityTransactionsPage> ListTransactionsAsync(
        Guid ledgerId,
        Guid securityId,
        TxnCursor? olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        _ = ledgerId;  // composite FK (txn_legs.security_id, ledger_id) → securities(id, ledger_id)
                       // ensures the leg can't cross a ledger boundary; endpoint pre-validates
                       // (ledgerId, securityId).

        // Candidate legs: every leg whose OWN security_id is this one.
        // Then dedupe per header — prefer qty-bearing leg (holdings),
        // tiebreak by lowest leg id for determinism. Picking the holdings
        // leg gives a row with positive qty + price + amount that matches
        // the "shares acquired/disposed" semantics the user expects.
        // Pure cash Dividend (no reinvest) has no qty-bearing leg; the lowest-
        // id own-security leg becomes canonical.
        var q = _db.TxnLegs.AsNoTracking()
            .Where(l => l.SecurityId == securityId)
            .Where(l => l.Id == _db.TxnLegs.AsNoTracking()
                .Where(x => x.HeaderId == l.HeaderId && x.SecurityId == securityId)
                .OrderBy(x => x.Quantity == null)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .First());

        // Join the header for posted_at/action/payee — fields the leg
        // doesn't carry directly. Since migration 230 these ARE the curated
        // values, so this list orders and labels the same way the register does.
        //
        // Apply the SAME visibility predicate the register + holdings use.
        // Without it the per-security list leaks hidden/merged legs — e.g. a
        // hidden import-overlap duplicate shows here as a phantom second buy and
        // inflates the total count — while the register and the holdings total
        // exclude it.
        var withHeader = from l in q
                         join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                         where !h.IsHidden && h.IsMergedInto == null
                         select new
                         {
                             LegId = l.Id,
                             l.HeaderId,
                             l.AccountId,
                             l.Amount,
                             l.Quantity,
                             l.UnitPrice,
                             PostedAt = h.PostedAt,
                             h.Action,
                             Payee = h.Payee,
                         };

        if (olderThan is { } cursor)
        {
            // Composite "strictly after cursor in (PostedAt DESC,
            // AccountId ASC, LegId DESC) ordering" predicate. Same
            // structure as before; only the source changed.
            withHeader = withHeader.Where(r =>
                r.PostedAt < cursor.PostedAt
                || (r.PostedAt == cursor.PostedAt && r.AccountId > cursor.AccountId)
                || (r.PostedAt == cursor.PostedAt
                    && r.AccountId == cursor.AccountId
                    && r.LegId < cursor.LegId));
        }

        // Name the BROKERAGE that owns the canonical leg, not the leg's own
        // account: the holdings leg sits on the Holdings sibling, and the
        // user thinks of the trade as belonging to the brokerage they
        // opened. That brokerage owns the leg's account (its
        // holdings_account_id). Another brokerage on the header (the other
        // side of an in-kind transfer, the funding brokerage of a
        // buy/sell/dividend transfer) is not this trade's. The row's
        // currency is the owner's, so the price and amount are named in the
        // currency they were traded in.
        //
        // holdings_account_id is not unique, so when more than one brokerage
        // names the leg's account the row names the one the trade-price rule
        // reads the currency of (migration 274's fn_trade_currency): the first
        // by currency code in ordinal order, then the lower id. A row and the
        // trade price its line sets then name the same currency. A leg that
        // sits on the brokerage itself (a cash dividend's) has no owner and
        // names its own account, as the rule reads that account's currency.
        var paged = await (
            from r in withHeader
                .OrderByDescending(r => r.PostedAt)
                .ThenBy(r => r.AccountId)
                .ThenByDescending(r => r.LegId)
                .Take(pageSize + 1)
            let brokerage = _db.Accounts.AsNoTracking()
                .Where(b => b.HoldingsAccountId == r.AccountId)
                .OrderBy(b => EF.Functions.Collate(b.CurrencyCode, "C"))
                .ThenBy(b => b.Id)
                .FirstOrDefault()
            join legAccount in _db.Accounts.AsNoTracking() on r.AccountId equals legAccount.Id
            select new
            {
                Dto = new SecurityTransactionDto(
                    HeaderId: r.HeaderId,
                    AccountId: brokerage != null ? brokerage.Id : legAccount.Id,
                    AccountName: brokerage != null ? brokerage.Name : legAccount.Name,
                    PostedAt: r.PostedAt,
                    Action: r.Action,
                    Amount: r.Amount,
                    Quantity: r.Quantity,
                    UnitPrice: r.UnitPrice,
                    Payee: r.Payee,
                    CurrencyCode: brokerage != null ? brokerage.CurrencyCode : legAccount.CurrencyCode),
                SortPostedAt = r.PostedAt,
                SortAccountId = r.AccountId,
                SortLegId = r.LegId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;
        var items = paged.Select(p => p.Dto).ToList();
        if (paged.Count > pageSize)
        {
            var keep = paged.Take(pageSize).ToList();
            var last = keep[^1];
            nextCursor = TxnCursor.Encode(
                last.SortPostedAt, last.SortAccountId, last.SortLegId);
            items = keep.Select(p => p.Dto).ToList();
        }

        // Total count = distinct VISIBLE headers with at least one own-security
        // leg. Same override-aware visibility predicate as the list above, so
        // the SPA's "loaded / total" badge matches the rows actually shown (a
        // hidden or merged duplicate must not inflate the count).
        var totalCount = await (
                from l in _db.TxnLegs.AsNoTracking()
                where l.SecurityId == securityId
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where !h.IsHidden && h.IsMergedInto == null
                select l.HeaderId)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SecurityTransactionsPage(items, nextCursor, totalCount);
    }

    /// <summary>Opaque composite cursor for transaction pagination.
    /// Encodes the full sort key — posted_at, account_id (Holdings
    /// sibling), leg_id — so page boundaries never split a same-key
    /// run incorrectly.</summary>
    public sealed record TxnCursor(DateTime PostedAt, Guid AccountId, Guid LegId)
    {
        public static string Encode(DateTime postedAt, Guid accountId, Guid legId) =>
            $"{postedAt:O}~{accountId:D}~{legId:D}";

        public static TxnCursor? TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Split('~');
            if (parts.Length != 3) return null;
            if (!DateTime.TryParse(parts[0],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var posted)) return null;
            if (!Guid.TryParse(parts[1], out var accountId)) return null;
            if (!Guid.TryParse(parts[2], out var legId)) return null;
            return new TxnCursor(posted, accountId, legId);
        }
    }

    // ----------------------------------------------------------------
    // Prices (slice A3 follow-on)
    // ----------------------------------------------------------------

    /// <summary>Paginated price list for one security. Newest-first
    /// to match the transactions list's ordering; cursor is the
    /// last row's <c>price_date</c>.</summary>
    public async Task<SecurityPricesPage> ListPricesAsync(
        Guid ledgerId,
        Guid securityId,
        DateOnly? olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        _ = ledgerId;  // security_prices.security_id composite FK (migration 049) ties
                      // every row to the security's ledger; the endpoint pre-checks
                      // (ledgerId, securityId) before calling here.

        var q = _db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId);
        if (olderThan is { } cursor)
        {
            q = q.Where(p => p.PriceDate < cursor);
        }

        var items = await q
            .OrderByDescending(p => p.PriceDate)
            .ThenByDescending(p => p.Id)
            .Take(pageSize + 1)
            .Select(p => new SecurityPriceRowDto(
                Id: p.Id,
                AsOf: p.PriceDate,
                Price: p.Price,
                CurrencyCode: p.CurrencyCode,
                High: p.High,
                Low: p.Low,
                Volume: p.Volume,
                Source: p.Source,
                Unbacked: false))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The unbacked dates in ONE pass, then stamped onto the page. The
        // per-row predicate rescans the security's legs for every price, so
        // its cost is prices × legs, and a
        // security with a long history made its own price list slow.
        if (items.Count > 0)
        {
            var unbacked = (await _db.UnbackedPriceDates(ledgerId, securityId)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(d => d.PriceDate)
                .ToHashSet();
            if (unbacked.Count > 0)
            {
                items = items
                    .Select(i => unbacked.Contains(i.AsOf) ? i with { Unbacked = true } : i)
                    .ToList();
            }
        }

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            var keep = items.Take(pageSize).ToList();
            nextCursor = keep[^1].AsOf.ToString("O");
            items = keep;
        }

        var totalCount = await _db.SecurityPrices.AsNoTracking()
            .CountAsync(p => p.SecurityId == securityId, cancellationToken)
            .ConfigureAwait(false);

        var defaultCurrency = await DefaultPriceCurrencyAsync(ledgerId, securityId, cancellationToken)
            .ConfigureAwait(false);

        return new SecurityPricesPage(items, nextCursor, totalCount, defaultCurrency);
    }

    public enum AddPriceResult
    {
        Ok,
        SecurityNotInLedger,
        PriceRequired,
        PriceDateRequired,
        HighLowInvalid,
        /// <summary>The currency given is not a currency code (<see cref="CurrencyCodes"/>).</summary>
        CurrencyInvalid,
        /// <summary>No currency was given and none can be inferred: the security has no
        /// price yet, and the accounts holding it are none or disagree.</summary>
        CurrencyRequired,
    }
    public sealed record AddPriceOutcome(AddPriceResult Kind, Guid? PriceId, string? CurrencyCode = null);

    /// <summary>
    /// The currency a hand-entered price takes when none is given (ADR-0114 D5): the
    /// security's latest price's currency, else the one currency every account holding it
    /// is in. Null when neither answers — no price yet, and no holding account or holding
    /// accounts in more than one currency — so the caller must ask.
    /// </summary>
    /// <remarks>
    /// "Holding" is a non-zero quantity in <c>holdings</c>, one row per (Holdings account,
    /// security), and a Holdings account takes its brokerage's currency. Also what the
    /// price editor shows prefilled (<see cref="SecurityPricesPage.DefaultCurrencyCode"/>),
    /// so the prefill and the server's default are the same answer.
    /// </remarks>
    public async Task<string?> DefaultPriceCurrencyAsync(
        Guid ledgerId,
        Guid securityId,
        CancellationToken cancellationToken = default)
    {
        var latest = await _db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.SecurityId == securityId)
            .OrderByDescending(p => p.PriceDate)
            .Select(p => p.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is not null) return latest;

        var holding = await (
                from h in _db.Holdings.AsNoTracking()
                where h.LedgerId == ledgerId && h.SecurityId == securityId && h.Quantity != 0m
                join a in _db.Accounts.AsNoTracking() on h.AccountId equals a.Id
                select a.CurrencyCode)
            .Distinct()
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return holding.Count == 1 ? holding[0] : null;
    }

    /// <summary>
    /// The currency a hand-entered price is stored in: <paramref name="given"/> in stored
    /// form when one is given, else <see cref="DefaultPriceCurrencyAsync"/>. The security
    /// must already be known to be in the ledger.
    /// </summary>
    public async Task<(AddPriceResult Result, string? CurrencyCode)> ResolvePriceCurrencyAsync(
        Guid ledgerId,
        Guid securityId,
        string? given,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return CurrencyCodes.TryNormalize(given, out var code)
                ? (AddPriceResult.Ok, code)
                : (AddPriceResult.CurrencyInvalid, null);
        }

        var inferred = await DefaultPriceCurrencyAsync(ledgerId, securityId, cancellationToken)
            .ConfigureAwait(false);
        return inferred is null
            ? (AddPriceResult.CurrencyRequired, null)
            : (AddPriceResult.Ok, inferred);
    }

    public async Task<AddPriceOutcome> AddPriceAsync(
        Guid ledgerId,
        Guid securityId,
        CreateSecurityPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Price <= 0m)
            return new AddPriceOutcome(AddPriceResult.PriceRequired, null);
        if (request.PriceDate == default)
            return new AddPriceOutcome(AddPriceResult.PriceDateRequired, null);
        if (HighLowInvalid(request.High, request.Low))
            return new AddPriceOutcome(AddPriceResult.HighLowInvalid, null);
        if (!string.IsNullOrWhiteSpace(request.CurrencyCode) && !CurrencyCodes.TryNormalize(request.CurrencyCode, out _))
            return new AddPriceOutcome(AddPriceResult.CurrencyInvalid, null);

        // Ledger membership: the price's ledger must match the security's
        // ledger (migration 049 composite FK refuses cross-ledger inserts).
        var sec = await _db.Securities.AsNoTracking()
            .Where(s => s.Id == securityId && s.LedgerId == ledgerId)
            .Select(s => new { s.Id, s.LedgerId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sec is null) return new AddPriceOutcome(AddPriceResult.SecurityNotInLedger, null);

        var priceDate = request.PriceDate;
        // ADR-0114 D5: the currency given, else the security's latest price's, else its
        // holding accounts' when they agree. Never an assumed USD.
        var (currencyResult, resolvedCurrency) = await ResolvePriceCurrencyAsync(
                sec.LedgerId, securityId, request.CurrencyCode, cancellationToken)
            .ConfigureAwait(false);
        if (currencyResult != AddPriceResult.Ok)
            return new AddPriceOutcome(currencyResult, null);
        var currency = resolvedCurrency!;

        // Manual tops the source ladder (ADR-0070): a hand-entered price OWNS its
        // day. If one already exists for (security, day) — from any source —
        // replace it rather than rejecting.
        var existing = await _db.SecurityPrices
            .FirstOrDefaultAsync(
                p => p.LedgerId == sec.LedgerId && p.SecurityId == securityId && p.PriceDate == priceDate,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Price = request.Price;
            existing.CurrencyCode = currency;
            existing.High = request.High;
            existing.Low = request.Low;
            existing.Volume = request.Volume;
            existing.Source = PriceSource.Manual;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AddPriceOutcome(AddPriceResult.Ok, existing.Id, currency);
        }

        var row = new SecurityPriceRow
        {
            Id = Guid.NewGuid(),
            SecurityId = securityId,
            LedgerId = sec.LedgerId,
            Price = request.Price,
            CurrencyCode = currency,
            PriceDate = priceDate,
            High = request.High,
            Low = request.Low,
            Volume = request.Volume,
            Source = PriceSource.Manual,
        };
        _db.SecurityPrices.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AddPriceOutcome(AddPriceResult.Ok, row.Id, currency);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out _))
        {
            // Lost a race (another writer inserted this (security, day) between
            // the check and the insert). Manual still wins — overwrite it.
            _db.Entry(row).State = EntityState.Detached;
            var raced = await _db.SecurityPrices.FirstAsync(
                p => p.LedgerId == sec.LedgerId && p.SecurityId == securityId && p.PriceDate == priceDate,
                cancellationToken).ConfigureAwait(false);
            raced.Price = request.Price;
            raced.CurrencyCode = currency;
            raced.High = request.High;
            raced.Low = request.Low;
            raced.Volume = request.Volume;
            raced.Source = PriceSource.Manual;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AddPriceOutcome(AddPriceResult.Ok, raced.Id, currency);
        }
    }

    public enum PatchPriceResult
    {
        Ok,
        PriceNotInSecurity,
        PriceRequired,
        DateConflict,
        HighLowInvalid,
        /// <summary>The currency given is not a currency code (<see cref="CurrencyCodes"/>).</summary>
        CurrencyInvalid,
    }

    public async Task<PatchPriceResult> PatchPriceAsync(
        Guid ledgerId,
        Guid securityId,
        Guid priceId,
        PatchSecurityPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Load the price + verify it belongs to (ledger, security).
        var row = await _db.SecurityPrices
            .Where(p => p.Id == priceId
                && p.SecurityId == securityId
                && p.LedgerId == ledgerId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return PatchPriceResult.PriceNotInSecurity;

        if (request.Price is { } price)
        {
            if (price <= 0m) return PatchPriceResult.PriceRequired;
            row.Price = price;
        }
        if (request.PriceDate is { } priceDate)
        {
            row.PriceDate = priceDate;
        }
        if (!string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            if (!CurrencyCodes.TryNormalize(request.CurrencyCode, out var currency))
                return PatchPriceResult.CurrencyInvalid;
            row.CurrencyCode = currency;
        }
        if (request.High is { } high) row.High = high;
        if (request.Low is { } low)   row.Low  = low;
        if (request.Volume is { } volume) row.Volume = volume;

        // A hand-edit makes the row manual-owned (ADR-0054 D2) — now
        // protected from automated fetch overwrites.
        row.Source = PriceSource.Manual;

        if (HighLowInvalid(row.High, row.Low))
            return PatchPriceResult.HighLowInvalid;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out _))
        {
            return PatchPriceResult.DateConflict;
        }
        return PatchPriceResult.Ok;
    }

    public enum DeletePriceResult { Ok, NotInSecurity }

    public async Task<DeletePriceResult> DeletePriceAsync(
        Guid ledgerId,
        Guid securityId,
        Guid priceId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _db.SecurityPrices
            .Where(p => p.Id == priceId
                && p.SecurityId == securityId
                && p.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0 ? DeletePriceResult.Ok : DeletePriceResult.NotInSecurity;
    }

    private sealed record SecuritySnapshot(Guid Id, Guid LedgerId);

    /// <summary>High &lt; Low is the only structural inconsistency we
    /// reject up-front (matches the security_prices CHECK constraint).
    /// Either being null is fine (a price source can carry only the
    /// close, not the OHLC band).</summary>
    private static bool HighLowInvalid(decimal? high, decimal? low) =>
        high is { } h && low is { } l && h < l;

    public enum CreateResult { Ok, NameRequired, AssetClassInvalid, DuplicateTicker, DuplicateCusip, NotPublicNeedsSymbol }
    public sealed record CreateOutcome(CreateResult Kind, Guid? SecurityId);

    public async Task<CreateOutcome> CreateAsync(
        Guid ledgerId,
        CreateSecurityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return new CreateOutcome(CreateResult.NameRequired, null);

        var ticker = string.IsNullOrWhiteSpace(request.Ticker) ? null : request.Ticker.Trim();
        var cusip  = string.IsNullOrWhiteSpace(request.Cusip)  ? null : request.Cusip.Trim();
        var assetClass = string.IsNullOrWhiteSpace(request.AssetClass) ? null : request.AssetClass.Trim();
        var exchange = string.IsNullOrWhiteSpace(request.Exchange) ? null : request.Exchange.Trim();
        var quoteSymbol = string.IsNullOrWhiteSpace(request.QuoteSymbol) ? null : request.QuoteSymbol.Trim();

        // ADR-0054 D2: a non-public quote symbol requires a quote symbol — a bare
        // ticker is always public (mirrors the mig-156 CHECK; clean 422 vs a 500).
        if (!request.QuoteSymbolPublic && quoteSymbol is null)
            return new CreateOutcome(CreateResult.NotPublicNeedsSymbol, null);

        if (assetClass is not null && !ValidAssetClasses.Contains(assetClass))
            return new CreateOutcome(CreateResult.AssetClassInvalid, null);

        // Soft check inside the same transaction the INSERT runs in.
        // The DB partial-unique indexes (migration 048) are the final
        // authority — we still translate a 23505 PostgresException into
        // the matching 422 below.
        if (ticker is not null
            && await _db.Securities.AsNoTracking().AnyAsync(
                s => s.LedgerId == ledgerId
                    && s.Ticker != null
                    && s.Ticker.ToLower() == ticker.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            return new CreateOutcome(CreateResult.DuplicateTicker, null);
        }
        if (cusip is not null
            && await _db.Securities.AsNoTracking().AnyAsync(
                s => s.LedgerId == ledgerId && s.Cusip == cusip,
                cancellationToken).ConfigureAwait(false))
        {
            return new CreateOutcome(CreateResult.DuplicateCusip, null);
        }

        var row = new SecurityRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Ticker = ticker,
            Cusip = cusip,
            Name = name,
            AssetClass = assetClass,
            Exchange = exchange,
            IsActive = true,
            QuoteSymbol = quoteSymbol,
            AutoPrice = request.AutoPrice,
            QuoteSymbolPublic = request.QuoteSymbolPublic,
            ShareDecimals = 4,
        };

        _db.Securities.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out var constraint)
            && IsKnownSecurityUniqueConstraint(constraint))
        {
            // DB-side last-line defence: race condition between the
            // pre-check above and INSERT loses to the unique index.
            // Only known constraints are translated to 422; an
            // unexpected unique violation (e.g. a new index we forgot
            // to map) falls through to the 500 path so it's visible.
            return constraint switch
            {
                "uq_securities_ticker_per_ledger" =>
                    new CreateOutcome(CreateResult.DuplicateTicker, null),
                _ => new CreateOutcome(CreateResult.DuplicateCusip, null),
            };
        }
        return new CreateOutcome(CreateResult.Ok, row.Id);
    }

    private static bool IsKnownSecurityUniqueConstraint(string constraint) =>
        constraint is "uq_securities_ticker_per_ledger"
                   or "uq_securities_cusip_per_ledger";

    public enum PatchResult { Ok, NotInLedger, NameRequired, AssetClassInvalid, DuplicateTicker, DuplicateCusip, NotPublicNeedsSymbol }

    public async Task<PatchResult> PatchAsync(
        Guid ledgerId,
        Guid securityId,
        PatchSecurityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await _db.Securities
            .FirstOrDefaultAsync(s => s.LedgerId == ledgerId && s.Id == securityId,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return PatchResult.NotInLedger;

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return PatchResult.NameRequired;
            row.Name = name;
        }
        if (request.Ticker is not null)
        {
            // Empty string => clear; non-empty => set + uniqueness check.
            var ticker = request.Ticker.Trim();
            row.Ticker = ticker.Length == 0 ? null : ticker;
            if (row.Ticker is not null
                && await _db.Securities.AsNoTracking().AnyAsync(
                    s => s.LedgerId == ledgerId
                        && s.Id != securityId
                        && s.Ticker != null
                        && s.Ticker.ToLower() == row.Ticker.ToLower(),
                    cancellationToken).ConfigureAwait(false))
            {
                return PatchResult.DuplicateTicker;
            }
        }
        if (request.Cusip is not null)
        {
            var cusip = request.Cusip.Trim();
            row.Cusip = cusip.Length == 0 ? null : cusip;
            if (row.Cusip is not null
                && await _db.Securities.AsNoTracking().AnyAsync(
                    s => s.LedgerId == ledgerId
                        && s.Id != securityId
                        && s.Cusip == row.Cusip,
                    cancellationToken).ConfigureAwait(false))
            {
                return PatchResult.DuplicateCusip;
            }
        }
        if (request.AssetClass is not null)
        {
            var assetClass = request.AssetClass.Trim();
            if (assetClass.Length == 0)
            {
                row.AssetClass = null;
            }
            else if (!ValidAssetClasses.Contains(assetClass))
            {
                return PatchResult.AssetClassInvalid;
            }
            else
            {
                row.AssetClass = assetClass;
            }
        }
        if (request.Exchange is not null)
        {
            var exchange = request.Exchange.Trim();
            row.Exchange = exchange.Length == 0 ? null : exchange;
        }
        if (request.IsActive is { } isActive)
        {
            row.IsActive = isActive;
        }
        if (request.QuoteSymbol is not null)
        {
            // Empty string => clear (→ the provider falls back to the ticker).
            var quoteSymbol = request.QuoteSymbol.Trim();
            row.QuoteSymbol = quoteSymbol.Length == 0 ? null : quoteSymbol;
        }
        if (request.AutoPrice is { } autoPrice)
        {
            row.AutoPrice = autoPrice;
        }
        if (request.QuoteSymbolPublic is { } quoteSymbolPublic)
        {
            row.QuoteSymbolPublic = quoteSymbolPublic;
        }
        // ADR-0054 D2: a non-public quote symbol requires a quote symbol — a bare
        // ticker is always public (mirrors the mig-156 CHECK; clean 422 vs a 500).
        if (!row.QuoteSymbolPublic && row.QuoteSymbol is null)
            return PatchResult.NotPublicNeedsSymbol;

        // Rich classification (ADR-0067). Empty string clears (→ null), a value
        // sets; the DB CHECK validates the enums. Any classification edit (incl.
        // asset_class above) marks the row manually curated so re-import won't
        // overwrite it (seed-once, ADR-0067 D5).
        static string? Norm(string? s) => s is null || s.Trim().Length == 0 ? null : s.Trim();
        var classified = request.AssetClass is not null;
        if (request.VehicleType is not null) { row.VehicleType = Norm(request.VehicleType); classified = true; }
        if (request.Region is not null) { row.Region = Norm(request.Region); classified = true; }
        if (request.EquitySize is not null) { row.EquitySize = Norm(request.EquitySize); classified = true; }
        if (request.EquityStyle is not null) { row.EquityStyle = Norm(request.EquityStyle); classified = true; }
        if (request.FiDuration is not null) { row.FiDuration = Norm(request.FiDuration); classified = true; }
        if (request.FiCredit is not null) { row.FiCredit = Norm(request.FiCredit); classified = true; }
        if (request.TaxCharacter is not null) { row.TaxCharacter = Norm(request.TaxCharacter); classified = true; }
        if (classified)
        {
            row.ClassificationSource = "manual";
            row.ClassificationConfidence = "known";
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out var constraint)
            && IsKnownSecurityUniqueConstraint(constraint))
        {
            return constraint switch
            {
                "uq_securities_ticker_per_ledger" => PatchResult.DuplicateTicker,
                _ => PatchResult.DuplicateCusip,
            };
        }
        return PatchResult.Ok;
    }

    // ---- Look-through components (ADR-0067) ----------------------------------

    public enum ComponentsResult { Ok, NotInLedger, Invalid }

    private static readonly string[] ValidComponentClasses =
        { "equity", "fixed_income", "cash", "real_assets", "alternative" };
    private static readonly string[] ValidComponentRegions =
        { "us", "developed_ex_us", "emerging", "global", "na" };

    public async Task<IReadOnlyList<SecurityComponentDto>?> GetComponentsAsync(
        Guid ledgerId, Guid securityId, CancellationToken cancellationToken = default)
    {
        var inLedger = await _db.Securities.AsNoTracking()
            .AnyAsync(s => s.Id == securityId && s.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (!inLedger) return null;

        return await _db.SecurityComponents.AsNoTracking()
            .Where(c => c.SecurityId == securityId)
            .OrderByDescending(c => c.Weight)
            .Select(c => new SecurityComponentDto(c.ComponentAssetClass, c.ComponentRegion, c.Weight))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Replace the whole look-through set for a security (delete + insert
    /// in one transaction). Validates each sleeve's class/region/weight.</summary>
    public async Task<ComponentsResult> ReplaceComponentsAsync(
        Guid ledgerId, Guid securityId, IReadOnlyList<SecurityComponentDto> components,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);

        var sec = await _db.Securities.AsNoTracking()
            .Where(s => s.Id == securityId && s.LedgerId == ledgerId)
            .Select(s => new { s.Id, s.LedgerId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sec is null) return ComponentsResult.NotInLedger;

        foreach (var c in components)
        {
            if (!ValidComponentClasses.Contains(c.AssetClass)) return ComponentsResult.Invalid;
            if (c.Region is not null && !ValidComponentRegions.Contains(c.Region)) return ComponentsResult.Invalid;
            if (c.Weight < 0m) return ComponentsResult.Invalid;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _db.SecurityComponents.Where(c => c.SecurityId == securityId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var c in components)
        {
            _db.SecurityComponents.Add(new SecurityComponentRow
            {
                Id = Guid.NewGuid(),
                SecurityId = securityId,
                LedgerId = sec.LedgerId,
                ComponentAssetClass = c.AssetClass,
                ComponentRegion = c.Region,
                Weight = c.Weight,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ComponentsResult.Ok;
    }

    // ---- ADR-0068: merge duplicate securities (MCP write surface) -----------------

    public enum MergeSecuritiesResult { Ok, SourceNotInLedger, TargetNotInLedger, SameSecurity }

    public sealed record MergeSecuritiesOutcome(
        MergeSecuritiesResult Result, int LegsMoved, int AccountsRecomputed);

    /// <summary>
    /// Merge duplicate/alias security <paramref name="sourceId"/> into
    /// <paramref name="targetId"/> (ADR-0068). Repoints every reference that points AT
    /// the security — investment <c>txn_legs</c>, <c>realized_gains</c>,
    /// <c>provider_security_mappings</c> — to the keeper, rebuilds holdings + lots for
    /// both securities on every account holding the source or carrying its shares on a
    /// transaction's line (source empties, target absorbs the moved legs), re-derives the
    /// keeper's trade price on every day a moved execution prices
    /// (ADR-0084 D4, 2026-11-03 amendment), then <b>deactivates</b> the source
    /// (reversible; not deleted — its own prices/components stay with it harmlessly).
    /// Provider-mapping rows that would collide
    /// with the keeper's existing (provider, ticker) are dropped, the rest repointed.
    /// <paramref name="dryRun"/> reports the counts that would move. Atomic, and the source
    /// is locked before anything is read, so a line added to it while the merge runs is
    /// either read and moved with the rest or waits for the merge. The transaction is
    /// opened by the ledger write lock (<see cref="LedgerWriteLock"/>, migration 275), its
    /// first statement, ahead of the source's row lock and of every read; a dry run takes
    /// neither.
    /// </summary>
    public async Task<MergeSecuritiesOutcome> MergeSecuritiesAsync(
        Guid ledgerId, Guid sourceId, Guid targetId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId) return new(MergeSecuritiesResult.SameSecurity, 0, 0);

        // The ledger write lock (migration 275), first: the merge recomputes holdings, which
        // the database refuses in a transaction that wrote or row-locked without it, and the
        // source's row lock below is one. Every read from here on decides what moves, so it
        // reads under it. A dry run reads what would move and locks nothing.
        await using var write = dryRun
            ? null
            : await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var sourceOk = await _db.Securities.AsNoTracking()
            .AnyAsync(s => s.LedgerId == ledgerId && s.Id == sourceId, cancellationToken).ConfigureAwait(false);
        if (!sourceOk) return new(MergeSecuritiesResult.SourceNotInLedger, 0, 0);
        var targetOk = await _db.Securities.AsNoTracking()
            .AnyAsync(s => s.LedgerId == ledgerId && s.Id == targetId, cancellationToken).ConfigureAwait(false);
        if (!targetOk) return new(MergeSecuritiesResult.TargetNotInLedger, 0, 0);

        // A dry run reads what would move, and locks nothing.
        if (write is null)
        {
            var (dryLegs, dryAccounts) = await SourceReferencesAsync(ledgerId, sourceId, cancellationToken)
                .ConfigureAwait(false);
            return new(MergeSecuritiesResult.Ok, dryLegs, dryAccounts.Count);
        }

        // The source is locked before its lines are read (fn_lock_securities, step 0 of
        // the trade-price lock order, migration 274). A line another request adds to it
        // after the read would otherwise move below with its holdings and its day with the
        // keeper never derived. One being added now is waited for and read; one that
        // starts later waits for this merge.
        _ = await _db.LockSecurities([sourceId]).Select(r => r.Locked)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var (legCount, accounts) = await SourceReferencesAsync(ledgerId, sourceId, cancellationToken)
            .ConfigureAwait(false);

        // The days the source's executions price, read before they move. The repoint
        // bypasses TradePriceFromLegInterceptor, so they are re-derived below: each such
        // execution leaves (source, day), where nothing of the source remains and its
        // price is kept (ADR-0084 D4), and arrives on (target, day), which then prices
        // by the rule over both securities' executions.
        var tradeDays = await (
                from l in _db.TxnLegs.AsNoTracking()
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where l.LedgerId == ledgerId && l.SecurityId == sourceId
                      && !h.IsRecurringTemplate
                      && l.Quantity != null && l.Quantity != 0m
                      && l.UnitPrice != null && l.UnitPrice > 0m
                select h.PostedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await _db.TxnLegs.Where(l => l.LedgerId == ledgerId && l.SecurityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.SecurityId, (Guid?)targetId), cancellationToken)
            .ConfigureAwait(false);
        await _db.RealizedGains.Where(g => g.LedgerId == ledgerId && g.SecurityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.SecurityId, targetId), cancellationToken)
            .ConfigureAwait(false);
        // Repoint provider mappings to the keeper. The unique key is
        // (ledger_id, provider_key, provider_security_id) (mig 075) — each provider-ticker
        // maps to exactly ONE security — so the source's tickers can't already be on the
        // target, and the repoint can't collide.
        await _db.ProviderSecurityMappings.Where(m => m.LedgerId == ledgerId && m.SecurityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SecurityId, targetId), cancellationToken)
            .ConfigureAwait(false);

        // Rebuild holdings + lots for both securities on every account holding the source or
        // carrying its shares on a line (SourceReferencesAsync): the source empties, the
        // target absorbs the moved legs. Reuses the canonical holdings recompute (the same
        // service every leg-mutating writer uses).
        var recompute = new HoldingsRecomputeService(_db);
        await recompute.RecomputeAsync(
            accounts.SelectMany(a => new[] { (a, sourceId), (a, targetId) }), cancellationToken)
            .ConfigureAwait(false);

        // The keeper's trade price on each day the moved executions price, by the
        // writer's own re-derivation (rank-gated). The source's days need none: no
        // execution of it is left, so the rule derives nothing there and its rows stay.
        await new TradePriceRecomputeService(_db).ReseedAsync(
                tradeDays.Select(d => (ledgerId, targetId, HeaderDates.UtcDay(d))), cancellationToken)
            .ConfigureAwait(false);

        // Deactivate the merged-away source (reversible; not deleted).
        await _db.Securities.Where(s => s.LedgerId == ledgerId && s.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken)
            .ConfigureAwait(false);

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(MergeSecuritiesResult.Ok, legCount, accounts.Count);
    }

    /// <summary>
    /// How many lines reference the source (what a merge moves), and the accounts whose
    /// position in it the merge recomputes: those with a line that carries shares of it on
    /// a transaction, and those holding it.
    /// </summary>
    /// <remarks>
    /// The positions are the ones the holdings walk derives, chosen by the predicate
    /// <see cref="HoldingsRecomputeInterceptor"/> applies to a save: a line with a security
    /// and a quantity, on a header that is not a reminder's template. A line that names the
    /// source with no quantity (the brokerage's cash line of a dividend, a fee or a
    /// miscellaneous event) and a template's line move with the rest but hold no shares,
    /// and recomputing a pair with no stored holding creates one.
    /// </remarks>
    private async Task<(int LegCount, List<Guid> Accounts)> SourceReferencesAsync(
        Guid ledgerId, Guid sourceId, CancellationToken cancellationToken)
    {
        var legAccounts = await (
                from l in _db.TxnLegs.AsNoTracking()
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where l.LedgerId == ledgerId && l.SecurityId == sourceId
                      && l.Quantity != null
                      && !h.IsRecurringTemplate
                select l.AccountId)
            .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var holdingAccounts = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.SecurityId == sourceId)
            .Select(h => h.AccountId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var accounts = legAccounts.Union(holdingAccounts).Distinct().ToList();
        var legCount = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && l.SecurityId == sourceId)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        return (legCount, accounts);
    }

    private static bool IsUniqueViolation(DbUpdateException ex, out string constraint)
    {
        if (ex.InnerException is PostgresException pg
            && pg.SqlState == "23505")
        {
            constraint = pg.ConstraintName ?? string.Empty;
            return true;
        }
        constraint = string.Empty;
        return false;
    }

    // Internal projection used inside ListByLedgerAsync. EF Core's LINQ
    // translator handles anonymous types; this named shape keeps the
    // Select(...) body skim-readable.
    private sealed class SecurityListRow
    {
        public Guid Id { get; init; }
        public string? Ticker { get; init; }
        public string? Cusip { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? AssetClass { get; init; }
        public string? Exchange { get; init; }
        public bool IsActive { get; init; }
    }
}
