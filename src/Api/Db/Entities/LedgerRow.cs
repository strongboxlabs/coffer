namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>ledgers</c> (API-side). Distinct
/// from <c>Coffer.Importer.Moneydance.Db.LedgerRow</c> on purpose — the
/// API uses different operations than the importer's resolve-or-create
/// flow, so the namespaces stay separate.
/// </summary>
/// <remarks>
/// No well-known "default" ledger id here (ADR-0088). Migration 014 seeded one at
/// …0001 for the setup ceremony to hand to the first user; migration 186 drops it,
/// setup no longer assigns a ledger, and tests seed their own.
/// </remarks>
public sealed class LedgerRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Per-ledger encryption key (ADR-0026), wrapped by the master
    /// KEK identified by <see cref="LekKekId"/>. Layout: AES-GCM
    /// nonce(12) || ciphertext(32) || tag(16) = 60 bytes. NULL on
    /// rows that pre-date migration 035; freshly created ledgers
    /// always populate it.
    /// </summary>
    public byte[]? WrappedLek { get; set; }

    /// <summary>Identifier of the master KEK that wrapped this
    /// LEK. Starts at <c>"v1"</c>; master-KEK rotation introduces
    /// <c>"v2"</c> etc.</summary>
    public string? LekKekId { get; set; }

    /// <summary>Timestamp the LEK was generated. Distinct from
    /// <see cref="CreatedAt"/>: the LEK can rotate without the
    /// ledger row itself being touched.</summary>
    public DateTime? LekCreatedAt { get; set; }
}
