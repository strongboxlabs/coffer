namespace Coffer.Api.Contracts;

/// <summary>
/// One row of the Securities catalog (slice A3). Returned by
/// <c>GET /api/ledgers/{lid}/securities</c>. Carries the catalog row
/// plus the per-security aggregates the catalog table renders inline
/// (total quantity, latest price + as-of). Mirrored on the SPA side.
/// </summary>
public sealed record SecuritySummaryDto(
    Guid Id,
    string? Ticker,
    string? Cusip,
    string Name,
    string? AssetClass,
    string? Exchange,
    bool IsActive,
    /// <summary>Quantity held on the caller's today (migration 259) across every
    /// Holdings sibling in the ledger: a trade or split the register calls Scheduled
    /// is not counted yet. 0 when the security is not held today.</summary>
    decimal TotalQuantity,
    /// <summary>Latest <c>security_prices.price</c> dated on or before the caller's
    /// today, else the unit price of the security's last trade in the ledger
    /// (ADR-0084, 2026-10-21 amendment), back-adjusted onto the day's split basis for
    /// any split after it, so <see cref="TotalQuantity"/> times it is today's value.
    /// Null when the security has neither.</summary>
    decimal? LatestPrice,
    /// <summary>The date <see cref="LatestPrice"/> was observed: the close's, or the
    /// trade's.</summary>
    DateOnly? LatestPriceAsOf,
    /// <summary>The currency <see cref="LatestPrice"/> is in, and so the amount the
    /// catalog prints beside it (the price times <see cref="TotalQuantity"/>), by the
    /// detail's rule (<see cref="SecurityDetailDto.PriceCurrencyCode"/>): the price's,
    /// else the holding accounts', else the ledger's.</summary>
    string PriceCurrencyCode);

/// <summary>
/// Body of <c>POST /api/ledgers/{lid}/securities</c>. Mirrors the Add
/// dialog. Name is required; ticker / cusip / asset class / exchange
/// optional and validated by the server.
/// </summary>
public sealed class CreateSecurityRequest
{
    public string? Ticker { get; init; }
    public string? Cusip { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? AssetClass { get; init; }
    public string? Exchange { get; init; }
    /// <summary>ADR-0054 D2: symbol sent to the price provider when it differs
    /// from the ticker. Null/empty → use the ticker.</summary>
    public string? QuoteSymbol { get; init; }
    /// <summary>ADR-0054 D2: participate in automated price fetches. Defaults
    /// true; false pins the price to manual entry.</summary>
    public bool AutoPrice { get; init; } = true;
    /// <summary>ADR-0054 D2: is QuoteSymbol a public ticker (default true)?
    /// False = a feed-only symbol; requires a non-null QuoteSymbol.</summary>
    public bool QuoteSymbolPublic { get; init; } = true;
}

/// <summary>
/// Body of <c>PATCH /api/ledgers/{lid}/securities/{sid}</c>. Every
/// field nullable = "leave it alone." Null is distinct from
/// empty-string — to clear ticker / cusip, send the empty string;
/// to keep the existing value, omit the field. Matches the
/// override-style PATCH semantics elsewhere in the API.
/// </summary>
public sealed class PatchSecurityRequest
{
    public string? Ticker { get; init; }
    public string? Cusip { get; init; }
    public string? Name { get; init; }
    public string? AssetClass { get; init; }
    public string? Exchange { get; init; }
    public bool? IsActive { get; init; }
    /// <summary>ADR-0054 D2. Null = leave alone; empty = clear (→ use ticker).</summary>
    public string? QuoteSymbol { get; init; }
    /// <summary>ADR-0054 D2. Null = leave alone.</summary>
    public bool? AutoPrice { get; init; }
    /// <summary>ADR-0054 D2. Null = leave alone. False requires a quote symbol
    /// (a bare ticker is always public).</summary>
    public bool? QuoteSymbolPublic { get; init; }
    // Rich classification (ADR-0067). Null = leave alone; "" clears (→ null).
    // Any classification edit marks the row source='manual', confidence='known'.
    public string? VehicleType { get; init; }
    public string? Region { get; init; }
    public string? EquitySize { get; init; }
    public string? EquityStyle { get; init; }
    public string? FiDuration { get; init; }
    public string? FiCredit { get; init; }
    public string? TaxCharacter { get; init; }
}

/// <summary>
/// Full per-security view returned by <c>GET .../securities/{sid}</c>.
/// The hero data plus a small slice of recent prices for the Detail
/// page's price-history panel. Recent transactions are paginated
/// separately via the <c>.../transactions</c> sub-endpoint so the
/// Detail page can lazy-load and a "Load more" affordance is feasible.
/// </summary>
public sealed record SecurityDetailDto(
    Guid Id,
    string? Ticker,
    string? Cusip,
    string Name,
    string? AssetClass,
    string? Exchange,
    bool IsActive,
    /// <summary>Quantity held on the caller's today across the ledger (migration
    /// 259), as <see cref="SecuritySummaryDto.TotalQuantity"/>.</summary>
    decimal TotalQuantity,
    /// <summary>FIFO cost basis of what is held that day, across the ledger, in
    /// <see cref="CostBasisCurrencyCode"/> (ADR-0114 D7): a position kept in another
    /// currency converts lot by lot at each lot's acquisition date. Null when a rate is
    /// missing, named in <see cref="CostBasisRateUnavailable"/> (ADR-0114 D9).</summary>
    decimal? TotalCostBasis,
    /// <summary>As <see cref="SecuritySummaryDto.LatestPrice"/>: the latest close
    /// dated on or before the day, else the last trade's price, on the day's split
    /// basis.</summary>
    decimal? LatestPrice,
    DateOnly? LatestPriceAsOf,
    /// <summary>Most recent ten prices, newest first, whatever their date: a
    /// history, not today's figures.</summary>
    IReadOnlyList<SecurityPricePointDto> RecentPrices,
    /// <summary>ADR-0054 D2: provider-symbol override (null → use ticker).</summary>
    string? QuoteSymbol,
    /// <summary>ADR-0054 D2: participates in automated price fetches.</summary>
    bool AutoPrice,
    /// <summary>ADR-0054 D2: is QuoteSymbol a public ticker? False = feed-only
    /// (matched by the feed provider, never sent to external providers).</summary>
    bool QuoteSymbolPublic,
    // Rich classification (ADR-0067) for the Detail page's classification editor.
    string? VehicleType,
    string? Region,
    string? EquitySize,
    string? EquityStyle,
    string? FiDuration,
    string? FiCredit,
    string? TaxCharacter,
    string? ClassificationSource,
    string? ClassificationConfidence,
    /// <summary>The currency <see cref="LatestPrice"/> is in: the currency of the close it
    /// was read from; for a last trade's price, the security's newest price row's; with no
    /// price row at all, <see cref="HoldingsCurrencyCode"/>. Only the price is in it: the
    /// market value and the gain combine the holding accounts, so they are in the
    /// reporting currency (<see cref="MarketValueCurrencyCode"/>).</summary>
    string PriceCurrencyCode,
    /// <summary>The currency of the accounts that hold the security that day: the one
    /// code they share, or the first in ordinal order when they span several. With no
    /// account holding it, the ledger's reporting currency. The fallback for
    /// <see cref="PriceCurrencyCode"/>; no figure here sums in it.</summary>
    string HoldingsCurrencyCode,
    /// <summary>The currency <see cref="TotalCostBasis"/> is in: the ledger's reporting
    /// currency, because the basis combines every account holding the security.</summary>
    string CostBasisCurrencyCode,
    /// <summary>Why <see cref="TotalCostBasis"/> is null: each missing pair and day
    /// (ADR-0114 D9). Empty when it has a value.</summary>
    IReadOnlyList<RateUnavailable> CostBasisRateUnavailable,
    /// <summary>What is held that day at <see cref="LatestPrice"/>, across the ledger, in
    /// <see cref="MarketValueCurrencyCode"/> (ADR-0114 D7): each position from the price's
    /// currency into its brokerage's, then into the reporting currency, both at the latest
    /// rate, summed and rounded once. Null with no current price (and no reason), or when
    /// a rate is missing, named in <see cref="MarketValueRateUnavailable"/> (ADR-0114 D9).</summary>
    decimal? MarketValue,
    /// <summary>The currency <see cref="MarketValue"/> is in: the ledger's reporting
    /// currency, because the value combines every account holding the security.</summary>
    string MarketValueCurrencyCode,
    /// <summary>Why <see cref="MarketValue"/> is null when a rate is missing: each pair and
    /// day (ADR-0114 D9). Empty otherwise.</summary>
    IReadOnlyList<RateUnavailable> MarketValueRateUnavailable,
    /// <summary><see cref="MarketValue"/> less <see cref="TotalCostBasis"/>, in
    /// <see cref="UnrealizedGainCurrencyCode"/>. Null when either is.</summary>
    decimal? UnrealizedGain,
    /// <summary>The currency <see cref="UnrealizedGain"/> is in: the reporting currency,
    /// as both its terms are.</summary>
    string UnrealizedGainCurrencyCode,
    /// <summary>Why <see cref="UnrealizedGain"/> is null when a rate is missing: the
    /// market value's and the cost basis's missing rates, distinct (ADR-0114 D9). Empty
    /// otherwise.</summary>
    IReadOnlyList<RateUnavailable> UnrealizedGainRateUnavailable);

/// <summary>One multi-asset look-through sleeve (ADR-0067): the percent (0-100)
/// of the wrapper in an asset class + optional region.</summary>
public sealed record SecurityComponentDto(string AssetClass, string? Region, decimal Weight);

/// <summary>Body of <c>PUT .../securities/{sid}/components</c> — replaces the
/// whole look-through set for the security (simpler than per-row CRUD for the
/// editor).</summary>
public sealed class ReplaceSecurityComponentsRequest
{
    public IReadOnlyList<SecurityComponentDto> Components { get; init; } = [];
}

public sealed record SecurityPricePointDto(
    DateOnly AsOf,
    decimal Price,
    /// <summary>Free-form source label — e.g. "moneydance_import",
    /// "manual", or a future feed identifier.</summary>
    string? Source);

/// <summary>
/// One row of the prices table (richer than <see cref="SecurityPricePointDto"/>:
/// carries the price id so the SPA can target edit / delete, plus OHLC
/// + volume for completeness). Returned by the paginated prices list.
/// </summary>
public sealed record SecurityPriceRowDto(
    Guid Id,
    DateOnly AsOf,
    decimal Price,
    string CurrencyCode,
    decimal? High,
    decimal? Low,
    long? Volume,
    /// <summary>Price origin (ADR-0054 D2 / ADR-0070): import | fetch | manual | simplefin.</summary>
    string Source,
    /// <summary>
    /// The row says a trade produced it, and no trade currently does — usually
    /// because that trade was deleted or edited since (migration 240).
    /// </summary>
    /// <remarks>
    /// ADVISORY. <c>security_prices</c> records THAT a trade wrote a row and
    /// never WHICH one, so this is inferred by re-deriving and finding nothing;
    /// ADR-0084 D4 keeps the price a deleted trade left behind on purpose, so a
    /// flagged row may be perfectly correct. Editing it makes it <c>manual</c>
    /// and clears the flag; deleting it removes the row.
    /// </remarks>
    bool Unbacked);

/// <summary>Cursor-paginated prices page (matches the transactions shape).</summary>
public sealed record SecurityPricesPage(
    IReadOnlyList<SecurityPriceRowDto> Items,
    string? CursorForOlder,
    /// <summary>Total price-count across ALL pages for the badge.</summary>
    int TotalCount,
    /// <summary>The currency a new hand-entered price takes when none is given, for the
    /// editor to prefill (ADR-0114 D5): the latest price's currency, else the one currency
    /// every account holding the security is in. Null when neither answers; an add without
    /// a currency is then refused with <c>security-price-currency-required</c>.</summary>
    string? DefaultCurrencyCode = null);

/// <summary>Body of <c>POST .../securities/{sid}/prices</c>.</summary>
public sealed class CreateSecurityPriceRequest
{
    public decimal Price { get; init; }
    public DateOnly PriceDate { get; init; }
    /// <summary>The price's ISO 4217 currency (ADR-0114 D5), trimmed and upper-cased; not
    /// three characters is <c>security-price-currency-invalid</c>. Omitted or blank, the
    /// security's latest price's currency, else its holding accounts' when they agree,
    /// else <c>security-price-currency-required</c>.</summary>
    public string? CurrencyCode { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public long? Volume { get; init; }
}

/// <summary>Body of <c>PATCH .../securities/{sid}/prices/{priceId}</c>.
/// Omitted fields are left alone. <c>PriceDate</c> is editable (e.g. the
/// user corrects a typo'd date), but a date collision with an existing
/// row of the same security surfaces as 422.</summary>
public sealed class PatchSecurityPriceRequest
{
    public decimal? Price { get; init; }
    public DateOnly? PriceDate { get; init; }
    public string? CurrencyCode { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public long? Volume { get; init; }
}

/// <summary>
/// One row of <c>GET .../securities/{sid}/transactions</c>. A
/// projection-flattened view of every investment-leg this security
/// participates in, sorted newest-first. Cursor pagination so the
/// Detail page's "Load more" is cheap on accounts with hundreds of
/// txns. <c>HeaderId</c> + <c>AccountId</c> let the SPA navigate
/// to the owning account's register with the row focused.
/// </summary>
public sealed record SecurityTransactionDto(
    Guid HeaderId,
    Guid AccountId,
    string AccountName,
    DateTime PostedAt,
    /// <summary>Header-level <c>txn_headers.action</c> (migration 047):
    /// the stored ADR-0027 catalog value (<c>buy</c> / <c>sell</c> /
    /// <c>dividend_cash</c> / <c>dividend_reinvest</c> / … and ADR-0065's
    /// <c>transfer_shares</c>), never the word the SPA shows for it. NULL when
    /// the header has no action.</summary>
    string? Action,
    /// <summary>Signed cash impact on the brokerage account. NULL on
    /// legs that don't book cash (e.g. holdings-side principal leg
    /// of a self-referential buyx where cash never moved).</summary>
    decimal Amount,
    decimal? Quantity,
    decimal? UnitPrice,
    string? Payee,
    /// <summary>The currency of the account the row names (<see cref="AccountId"/>), the
    /// brokerage that owns the trade's own leg, never another brokerage on the same
    /// transaction (the other side of an in-kind transfer, a funding brokerage): its
    /// <see cref="Amount"/> and <see cref="UnitPrice"/> are in it, the way a reminder, a
    /// security's price and a line carry theirs (ADR-0109 D9). A closed brokerage keeps
    /// its own.</summary>
    string CurrencyCode);

/// <summary>Cursor-paginated transactions page for the Detail panel.</summary>
public sealed record SecurityTransactionsPage(
    IReadOnlyList<SecurityTransactionDto> Items,
    /// <summary>Opaque cursor to fetch the next (older) page; null
    /// when at the end.</summary>
    string? CursorForOlder,
    /// <summary>Total leg-count touching this security across ALL
    /// pages — the SPA renders "loaded / total" on the section
    /// badge so the user knows how far they've scrolled.</summary>
    int TotalCount);
