namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>provider_security_mappings</c> (ADR-0031
/// Phase 3a). Persists the link between a pull provider's
/// security identifier (SimpleFIN holdings symbol / ticker
/// extracted from description, future OFX CUSIP, future CSV
/// ticker) and a Coffer <see cref="SecurityRow"/>.
/// </summary>
/// <remarks>
/// Identity is the composite <c>(LedgerId, ProviderKey,
/// ProviderSecurityId)</c> — one mapping per (ledger, provider,
/// ticker). Looked up on every classified ingested row in the
/// orchestrator's brokerage branch; recorded on every investment
/// editor save that resolved a provider-hinted ticker for the
/// first time.
/// </remarks>
internal sealed class ProviderSecurityMappingRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
    public string ProviderSecurityId { get; init; } = string.Empty;
    public Guid SecurityId { get; set; }
    public DateTime CreatedAt { get; init; }
    public Guid? CreatedByUserId { get; init; }
}
