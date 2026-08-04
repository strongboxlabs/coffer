namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>feed_connection_accounts</c> (slice 2c.4,
/// migration 041). One row per SimpleFIN account the bank
/// surfaces on a feed connection. Sync upserts on every run;
/// <see cref="LastSeenAt"/> decays so future cleanup can detect
/// stale entries (an external_id the bank stopped returning).
/// </summary>
internal sealed class FeedConnectionAccountRow
{
    public Guid Id { get; init; }
    public Guid FeedConnectionId { get; init; }
    /// <summary>
    /// Denormalized from <c>feed_connections.ledger_id</c> (migration
    /// 072). Composite FK locks the parent's ledger; RLS gates on this
    /// column.
    /// </summary>
    public Guid LedgerId { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OrgName { get; set; }
    public string? Currency { get; set; }
    public decimal? Balance { get; set; }
    public DateTime? BalanceAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    /// <summary>
    /// ADR-0031 follow-up (migration 080): verbatim per-account JSON
    /// from the provider, including the holdings[] block. Overwritten
    /// on every sync's directory upsert. Diagnostic + classifier-
    /// iteration use only — not a source of truth for derived data.
    /// Mapped as JSONB via HasColumnType("jsonb").
    /// </summary>
    public string? LastProviderRawPayload { get; set; }
    public DateTime CreatedAt { get; init; }
}
