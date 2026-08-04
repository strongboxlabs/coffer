namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>feed_connections</c>. The API doesn't
/// surface this table directly yet (PR 3.7 only reads accounts and the
/// register), but EF Core needs it mapped because <c>accounts.feed_connection_id</c>
/// references it — an unmapped FK target is the same hazard as an
/// unmapped FK on an entity (engineering-standards §4.2.2).
/// </summary>
public sealed class FeedConnectionRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string? ProviderItemId { get; init; }
    public string Status { get; set; } = "active";
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? TokenExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>
    /// SimpleFIN access URL sealed under the owning ledger's LEK
    /// (ADR-0026). Layout: AES-GCM nonce(12) || ciphertext || tag(16).
    /// Decrypt via <c>LedgerKeyService.Open(ledger.WrappedLek, this)</c>;
    /// the plaintext URL embeds Basic-auth credentials and must never
    /// hit a log line or response body. Migration 036.
    /// </summary>
    public byte[]? AccessUrlCiphertext { get; init; }
    /// <summary>FI display name surfaced from SimpleFIN /info or the
    /// first synced account's <c>org.name</c>. NULL until first sync
    /// populates; SPA falls back to "SimpleFIN".</summary>
    public string? InstitutionName { get; set; }
    /// <summary>User who initiated this connection — audit only.
    /// NULL after that user is removed (FK ON DELETE SET NULL).
    /// </summary>
    public Guid? CreatedByUserId { get; init; }
}
