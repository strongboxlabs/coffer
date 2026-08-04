namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>securities</c>. The catalog of investment instruments
/// referenced by <c>holdings</c>, <c>lots</c>, <c>security_prices</c>, and
/// the holdings-side legs of investment transactions (<c>txn_legs.security_id</c>).
/// </summary>
internal sealed class SecurityRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }

    // Mutable from this layer for slice A3's PATCH /securities path.
    // Name/Ticker/Cusip/AssetClass/Exchange/IsActive are user-editable;
    // the EF change tracker writes the diff back inside the same
    // transaction that validated uniqueness.
    public string? Ticker { get; set; }
    public string? Cusip { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AssetClass { get; set; }
    // Rich classification (ADR-0067) — orthogonal, single-vocabulary dimensions;
    // style is split per asset class (only the relevant pair is populated).
    public string? VehicleType { get; set; }
    public string? Region { get; set; }
    public string? EquitySize { get; set; }
    public string? EquityStyle { get; set; }
    public string? FiDuration { get; set; }
    public string? FiCredit { get; set; }
    public string? TaxCharacter { get; set; }
    public string? ClassificationSource { get; set; }
    public string? ClassificationConfidence { get; set; }
    public string? Exchange { get; set; }
    public bool IsActive { get; set; }

    // ADR-0054 D2 (slice A2): market-data override knobs, user-editable.
    // QuoteSymbol overrides Ticker as the symbol sent to the price provider
    // (NULL → use Ticker). AutoPrice excludes the security from automated
    // fetches when false (manual-only / hand-pinned).
    public string? QuoteSymbol { get; set; }
    // Defaults true to match the DB column default (migration 131) so a
    // construction site that doesn't set it can't silently insert false
    // (the C# bool default). EF overwrites it from the column on read.
    public bool AutoPrice { get; set; } = true;

    // ADR-0054 D2: whether QuoteSymbol is a public market ticker. Default true;
    // false = a private / feed-internal symbol (e.g. a 529 portfolio number),
    // matched by the no-egress feed provider but never sent to external ones.
    // DB CHECK (mig 156): may only be false when QuoteSymbol is non-null.
    public bool QuoteSymbolPublic { get; set; } = true;

    // Importer-managed; not editable from the catalog UI (the share-
    // precision is a Moneydance-derived display hint per migration 012,
    // not a user-facing knob).
    public int ShareDecimals { get; init; }
    public string? ExternalId { get; init; }
    public DateTime CreatedAt { get; init; }
}
