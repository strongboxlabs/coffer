namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>security_prices</c>. Per-day closing price snapshots
/// per security. The Portfolio View reads the latest row per
/// <see cref="SecurityId"/> to compute current value; a future
/// recurring-price worker will append new rows on schedule (see
/// follow-ups.md "Recurring price update service").
/// </summary>
internal sealed class SecurityPriceRow
{
    public Guid Id { get; init; }
    public Guid SecurityId { get; init; }
    /// <summary>Denormalized from the security's ledger
    /// (migration 049).</summary>
    public Guid LedgerId { get; init; }

    // Patchable from this layer for the prices CRUD path (slice A3
    // follow-on). The Detail page exposes an edit-price dialog that
    // mutates these fields via the EF change tracker; identity
    // fields (Id / SecurityId / LedgerId) stay init-only.
    public decimal Price { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    // Calendar DATE — one closing price per (security, day) (ADR-0070). A
    // DateOnly (not DateTime) so the API type matches the `date` column: no
    // time-of-day, no Kind, symmetric round-trip through Npgsql.
    public DateOnly PriceDate { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public long? Volume { get; set; }

    /// <summary>
    /// Origin tag (ADR-0054 D2): <c>import</c> (importer-seeded),
    /// <c>fetch</c> (market-data / SimpleFIN provider), or <c>manual</c>
    /// (hand-entered via the price CRUD endpoint). Drives the source-aware
    /// upsert — an automated fetch never overwrites a manual/import price for
    /// the same (security, date). <c>required</c> so every writer declares its
    /// intent at construction; see <see cref="PriceSource"/>.
    /// </summary>
    public required string Source { get; set; }
}
