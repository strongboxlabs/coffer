namespace Coffer.Api.Db.Entities;

/// <summary>
/// Origin tag for a <see cref="SecurityPriceRow"/> plus its priority
/// <see cref="Rank"/> (ADR-0070 / ADR-0084). Drives the per-day source-priority
/// upsert in <c>QuoteOrchestrator</c>, the manual price endpoint, and the
/// trade-derived price path: a write to a (security, day) overwrites the
/// existing row only when its source ranks at least as high —
/// <c>manual == fetch (Yahoo) &gt; trade &gt; simplefin &gt; import</c>.
/// </summary>
/// <remarks>
/// These constants mirror the <c>ck_security_prices_source</c> CHECK (migrations
/// 130 + 154 + 177). The importer is a separate assembly that can't reference
/// this type; it writes the literal <c>'import'</c> and relies on import being
/// the rank floor (its upsert only refreshes an existing import row), so it
/// needs no <see cref="Rank"/>. Keep the five string constants in sync with the
/// CHECK.
/// </remarks>
internal static class PriceSource
{
    /// <summary>Importer-seeded (Moneydance <c>csnap</c>) — the rank floor (ADR-0070).</summary>
    public const string Import = "import";

    /// <summary>Market-data provider fetch (Yahoo, ADR-0054) — a true EOD close.</summary>
    public const string Fetch = "fetch";

    /// <summary>Hand-entered via the price CRUD endpoint. Tops the ladder (ADR-0070).</summary>
    public const string Manual = "manual";

    /// <summary>SimpleFIN holdings-derived spot price (ADR-0070). Ranks below Yahoo so a
    /// true EOD close wins the day; a fallback for securities a market-data provider
    /// doesn't cover.</summary>
    public const string Simplefin = "simplefin";

    /// <summary>Trade-derived execution price (ADR-0084): |cash| / |shares| on an
    /// investment trade leg, seeded into <c>security_prices</c> by the
    /// <c>TradePriceFromLegInterceptor</c>. A real market observation, so it beats
    /// the one-time import seed and the SimpleFIN intraday balance — but a Yahoo EOD
    /// close or a manual gap-fill outranks it, so the scheduled feed reclaims the
    /// day.</summary>
    public const string Trade = "trade";

    /// <summary>
    /// Source-priority rank (ADR-0070 D2 / ADR-0084 D1): <c>manual == fetch (Yahoo)
    /// [3] &gt; trade [2] &gt; simplefin [1] &gt; import [0]</c>. A write to
    /// (security, day) overwrites the existing row iff <c>Rank(incoming) &gt;=
    /// Rank(existing)</c>. manual == Yahoo is intentional last-write-wins (manual
    /// prices are gap-fills); only the ordering matters, so the QuoteOrchestrator /
    /// AddPrice comparisons are unaffected by the absolute values shifting.
    /// </summary>
    public static int Rank(string source) => source switch
    {
        Manual => 3,
        Fetch => 3,
        Trade => 2,
        Simplefin => 1,
        _ => 0,   // import (and any unknown) is the floor
    };
}
