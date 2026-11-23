using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// One sealed value of every kind an adopt run clears, all under the key it treats as lost,
/// written the way the app writes them: the feed token under the ledger's own key, the rest
/// under the master key. A clone already holds the OAuth pair, sealed under the fixture key.
/// </summary>
internal static class LostKeySeed
{
    /// <summary>The key the clone's OAuth pair is sealed under, and so the one "lost".</summary>
    public static LedgerKeyService LostKeys => PostgresFixture.FixtureKeys();

    /// <summary>Seed every kind. Without a ledger, there is no ledger key, feed token or
    /// ledger-scope target, which is the install whose cleared state holds nothing sealed.</summary>
    public static async Task SeedAsync(TestDatabase database, bool withLedger = true)
    {
        var lost = LostKeys;
        var sealedBytes = lost.SealWithMasterKey("{}"u8.ToArray());
        await using var db = database.NewDbContext();
        if (withLedger)
        {
            var ledgerId = Guid.NewGuid();
            var wrappedLek = lost.CreateWrappedLek();
            db.Ledgers.Add(new LedgerRow
            {
                Id = ledgerId,
                Name = $"adopt-{ledgerId:N}",
                CreatedAt = DateTime.UtcNow,
                WrappedLek = wrappedLek,
                LekKekId = "test",
                LekCreatedAt = DateTime.UtcNow,
            });
            // First: the rows below reference it, and the model has no navigation to order by.
            await db.SaveChangesAsync();
            db.FeedConnections.Add(new FeedConnectionRow
            {
                Id = Guid.NewGuid(), LedgerId = ledgerId, Provider = "simplefin", Status = "active",
                CreatedAt = DateTime.UtcNow,
                AccessUrlCiphertext = lost.Seal(wrappedLek, Encoding.UTF8.GetBytes("https://example.invalid/access")),
            });
            db.LedgerNotificationSubscribers.Add(new LedgerNotificationSubscriberRow
            {
                Id = Guid.NewGuid(), LedgerId = ledgerId, SubscriberKey = "webhook", DisplayName = "ledger target",
                ConfigCiphertext = sealedBytes, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        db.GlobalScheduledJobs.Add(new GlobalScheduledJobRow
        {
            JobType = GlobalJobTypes.Backup, HourLocal = 3, MinuteLocal = 0, Enabled = true,
            PassphraseCiphertext = lost.SealWithMasterKey(Encoding.UTF8.GetBytes("synthetic-passphrase")),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.DriveSync.Add(new DriveSyncRow
        {
            Enabled = true, OauthCiphertext = sealedBytes, FolderId = "folder", FolderName = "Coffer Backups",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.NotificationSubscribers.Add(new NotificationSubscriberRow
        {
            SubscriberKey = "webhook", DisplayName = "deployment target", ConfigCiphertext = sealedBytes,
        });
        db.UpgradeRepoToken.Add(new UpgradeRepoTokenRow
        {
            TokenCiphertext = lost.SealWithMasterKey(Encoding.UTF8.GetBytes("synthetic-repo-token")),
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Every sealed value in the database, and what says whether it is usable, as one string:
    /// equal before and after means nothing an adopt run touches changed.
    /// </summary>
    public static async Task<string> SealedStateAsync(TestDatabase database)
    {
        await using var db = database.NewDbContext();
        static string Hex(byte[]? b) => b is null ? "null" : Convert.ToHexString(b);
        var parts = new List<string>();
        parts.AddRange((await db.Ledgers.AsNoTracking().OrderBy(l => l.Id).ToListAsync())
            .Select(l => $"ledger {l.Id} {l.LekKekId} {Hex(l.WrappedLek)}"));
        parts.AddRange((await db.FeedConnections.AsNoTracking().OrderBy(f => f.Id).ToListAsync())
            .Select(f => $"feed {f.Id} {f.Status} {Hex(f.AccessUrlCiphertext)}"));
        parts.AddRange((await db.GlobalScheduledJobs.AsNoTracking().OrderBy(j => j.JobType).ToListAsync())
            .Select(j => $"job {j.JobType} {j.Enabled} {Hex(j.PassphraseCiphertext)}"));
        parts.AddRange((await db.DriveSync.AsNoTracking().ToListAsync())
            .Select(d => $"drive {d.Enabled} {Hex(d.OauthCiphertext)}"));
        parts.AddRange((await db.NotificationSubscribers.AsNoTracking().OrderBy(t => t.Id).ToListAsync())
            .Select(t => $"target {t.Id} {t.IsEnabled} {Hex(t.ConfigCiphertext)}"));
        parts.AddRange((await db.LedgerNotificationSubscribers.AsNoTracking().OrderBy(t => t.Id).ToListAsync())
            .Select(t => $"ledger-target {t.Id} {t.IsEnabled} {Hex(t.ConfigCiphertext)}"));
        parts.AddRange((await db.OidcServerKeys.AsNoTracking().ToListAsync())
            .Select(k => $"oauth {Hex(k.SigningKeyCiphertext)} {Hex(k.EncryptionKeyCiphertext)}"));
        parts.AddRange((await db.UpgradeRepoToken.AsNoTracking().ToListAsync())
            .Select(t => $"upgrade-token {Hex(t.TokenCiphertext)}"));
        parts.Add($"audit {await db.AdminAuditEvents.CountAsync()}");
        return string.Join("\n", parts);
    }

    /// <summary>True when <paramref name="keys"/> opens every sealed value left in the
    /// database: what a rotation needs, since it refuses anything that does not open.</summary>
    public static async Task<bool> EverythingOpensUnderAsync(TestDatabase database, LedgerKeyService keys)
    {
        await using var db = database.NewDbContext();
        bool Opens(byte[] blob)
        {
            try { keys.OpenWithMasterKey(blob); return true; }
            catch (System.Security.Cryptography.CryptographicException) { return false; }
        }
        var masterSealed = new List<byte[]>();
        masterSealed.AddRange(await db.Ledgers.Where(l => l.WrappedLek != null).Select(l => l.WrappedLek!).ToListAsync());
        masterSealed.AddRange(await db.GlobalScheduledJobs.Where(j => j.PassphraseCiphertext != null && j.PassphraseCiphertext.Length > 0).Select(j => j.PassphraseCiphertext!).ToListAsync());
        masterSealed.AddRange(await db.DriveSync.Where(d => d.OauthCiphertext != null && d.OauthCiphertext.Length > 0).Select(d => d.OauthCiphertext!).ToListAsync());
        masterSealed.AddRange(await db.NotificationSubscribers.Where(t => t.ConfigCiphertext.Length > 0).Select(t => t.ConfigCiphertext).ToListAsync());
        masterSealed.AddRange(await db.LedgerNotificationSubscribers.Where(t => t.ConfigCiphertext.Length > 0).Select(t => t.ConfigCiphertext).ToListAsync());
        foreach (var k in await db.OidcServerKeys.ToListAsync())
        {
            masterSealed.Add(k.SigningKeyCiphertext);
            masterSealed.Add(k.EncryptionKeyCiphertext);
        }
        masterSealed.AddRange(await db.UpgradeRepoToken.Where(t => t.TokenCiphertext.Length > 0).Select(t => t.TokenCiphertext).ToListAsync());
        return masterSealed.All(Opens);
    }
}
