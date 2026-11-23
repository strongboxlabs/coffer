using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Upgrades;

/// <summary>
/// The token the release check reads a private fork with (ADR-0110): sealed under the master KEK
/// in <c>upgrade_repo_token</c> (mig 269), one row or none. Service-role only. A token the key no
/// longer opens reads as none, so the check falls back to an anonymous read and the screen asks for it again.
/// </summary>
public sealed class UpgradeRepoTokenStore
{
    private readonly ServiceDbContextFactory _factory;
    private readonly LedgerKeyService _keys;
    private readonly TimeProvider _clock;

    public UpgradeRepoTokenStore(ServiceDbContextFactory factory, LedgerKeyService keys, TimeProvider clock)
    {
        _factory = factory;
        _keys = keys;
        _clock = clock;
    }

    /// <summary>Whether a token is stored. The token itself never leaves this class except to the release check.</summary>
    public async Task<bool> HasAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        return await db.UpgradeRepoToken.AsNoTracking()
            .AnyAsync(t => t.TokenCiphertext.Length > 0, ct).ConfigureAwait(false);
    }

    /// <summary>The token in the clear, or null when none is stored or this install's key does not open it.</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        var row = await db.UpgradeRepoToken.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == (short)1, ct).ConfigureAwait(false);
        if (row is not { TokenCiphertext.Length: > 0 }) return null;
        try
        {
            var plain = _keys.OpenWithMasterKey(row.TokenCiphertext);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Seal and store <paramref name="token"/>, replacing any stored one.</summary>
    public async Task SetAsync(string token, Guid? actorUserId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var plain = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            using var sealing = _keys.EnterSealing();   // until the sealed token is saved (FU-078)
            var sealedBlob = _keys.SealWithMasterKey(plain);
            await using var db = _factory.Create();
            var row = await db.UpgradeRepoToken
                .FirstOrDefaultAsync(t => t.Id == (short)1, ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (row is null)
            {
                db.UpgradeRepoToken.Add(new UpgradeRepoTokenRow
                {
                    TokenCiphertext = sealedBlob, UpdatedAt = now, UpdatedBy = actorUserId,
                });
            }
            else
            {
                row.TokenCiphertext = sealedBlob;
                row.UpdatedAt = now;
                row.UpdatedBy = actorUserId;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Remove the stored token (idempotent).</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = _factory.Create();
        await db.UpgradeRepoToken.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
