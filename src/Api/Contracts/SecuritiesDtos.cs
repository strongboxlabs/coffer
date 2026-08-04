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
    /// <summary>Sum of <c>holdings.quantity</c> across every Holdings
    /// sibling that touches this security in the ledger. 0 when the
    /// security has never been held.</summary>
    decimal TotalQuantity,
    /// <summary>Latest <c>security_prices.price</c> for this security;
    /// null when no price has ever been recorded.</summary>
    decimal? LatestPrice,
    /// <summary>Date-only as-of marker for <see cref="LatestPrice"/>.</summary>
    DateOnly? LatestPriceAsOf);

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
    decimal TotalQuantity,
    /// <summary>Sum of <c>holdings.cost_basis</c> across the ledger.</summary>
    decimal TotalCostBasis,
    decimal? LatestPrice,
    DateOnly? LatestPriceAsOf,
    /// <summary>Most recent ten prices, newest first.</summary>
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
    string? ClassificationConfidence);

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
    string Source);

/// <summary>Cursor-paginated prices page (matches the transactions shape).</summary>
public sealed record SecurityPricesPage(
    IReadOnlyList<SecurityPriceRowDto> Items,
    string? CursorForOlder,
    /// <summary>Total price-count across ALL pages for the badge.</summary>
    int TotalCount);

/// <summary>Body of <c>POST .../securities/{sid}/prices</c>.</summary>
public sealed class CreateSecurityPriceRequest
{
    public decimal Price { get; init; }
    public DateOnly PriceDate { get; init; }
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
    /// <summary>Header-level <c>txn_headers.action</c> (migration 047)
    /// — Buy / Sell / Div / etc.</summary>
    string? Action,
    /// <summary>Signed cash impact on the brokerage account. NULL on
    /// legs that don't book cash (e.g. holdings-side principal leg
    /// of a self-referential buyx where cash never moved).</summary>
    decimal Amount,
    decimal? Quantity,
    decimal? UnitPrice,
    string? Payee);

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
