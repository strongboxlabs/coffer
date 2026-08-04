using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Repository for <c>provider_security_mappings</c> (ADR-0031 Phase 3a/c).
/// Read path: orchestrator looks up the security_id for a classifier-
/// detected (provider_key, provider_security_id) on every brokerage row
/// during sync. Write path: the investment editor's save endpoint
/// records a new mapping the first time a user confirms a ticker (Phase 3d).
/// </summary>
public sealed class ProviderSecurityMappingsRepository
{
    private readonly AppDbContext _db;

    public ProviderSecurityMappingsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Resolve a provider's security identifier to a Coffer
    /// <see cref="SecurityRow"/> id within one ledger. Returns
    /// <c>null</c> when the user hasn't mapped this ticker yet.
    /// </summary>
    public async Task<Guid?> TryResolveSecurityIdAsync(
        Guid ledgerId,
        string providerKey,
        string providerSecurityId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey)
            || string.IsNullOrWhiteSpace(providerSecurityId))
            return null;

        return await _db.ProviderSecurityMappings
            .AsNoTracking()
            .Where(m => m.LedgerId == ledgerId
                        && m.ProviderKey == providerKey
                        && m.ProviderSecurityId == providerSecurityId)
            .Select(m => (Guid?)m.SecurityId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Upsert a (ledger, provider_key, provider_security_id) →
    /// security_id mapping. Idempotent — re-linking the same ticker
    /// to a different security overwrites; same security is a no-op.
    /// Used by the investment editor's save endpoint when the user
    /// resolves a classifier-detected ticker for the first time
    /// (Phase 3d).
    /// </summary>
    /// <remarks>
    /// Per ADR-0038 every header in the ledger that carries this
    /// ticker as its <c>ingest_security_ticker_hint</c> resolves to
    /// the new <paramref name="securityId"/> on its next read —
    /// <c>resolved_transactions</c> performs the join. No backfill
    /// is performed here.
    /// </remarks>
    public async Task UpsertAsync(
        Guid ledgerId,
        string providerKey,
        string providerSecurityId,
        Guid securityId,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSecurityId);

        var existing = await _db.ProviderSecurityMappings
            .Where(m => m.LedgerId == ledgerId
                        && m.ProviderKey == providerKey
                        && m.ProviderSecurityId == providerSecurityId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // No-op when mapping already matches; otherwise rewrite
            // the security_id (user re-linked the ticker).
            if (existing.SecurityId != securityId)
            {
                existing.SecurityId = securityId;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        _db.ProviderSecurityMappings.Add(new ProviderSecurityMappingRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            ProviderKey = providerKey,
            ProviderSecurityId = providerSecurityId,
            SecurityId = securityId,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = createdByUserId,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
