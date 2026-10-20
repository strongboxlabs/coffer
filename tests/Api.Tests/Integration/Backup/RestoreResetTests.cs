using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

using Coffer.Api.Backup;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// What a restore resets in the database it builds (FU-076). It used to bring every
/// session, MCP token, OAuth grant and pending invite back as it was at backup time, so
/// restoring a backup from before an incident handed back the access revoked since.
/// </summary>
/// <remarks>
/// Everything here runs in one transaction that is rolled back: the reset revokes every
/// session in the database, and the database is shared with the rest of the collection.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RestoreResetTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = Now.AddDays(-3);

    private readonly PostgresFixture _fixture;

    public RestoreResetTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_restore_revokes_every_way_in_and_keeps_who_everyone_is()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();

        var liveSession = Session(ledger.UserId, revokedAt: null);
        var revokedSession = Session(ledger.UserId, revokedAt: Earlier);
        var mcpToken = new McpAccessTokenRow
        {
            Id = Guid.NewGuid(), UserId = ledger.UserId, Name = "restore-reset",
            TokenHash = RandomNumberGenerator.GetBytes(32), CreatedAt = Earlier,
        };
        var app = new OpenIddictEntityFrameworkCoreApplication
        {
            Id = Guid.NewGuid().ToString(), ClientId = $"restore-reset-{Guid.NewGuid():N}",
            ConcurrencyToken = Guid.NewGuid().ToString(),
        };
        var authorization = new OpenIddictEntityFrameworkCoreAuthorization
        {
            Id = Guid.NewGuid().ToString(), Application = app, Subject = ledger.UserId.ToString(),
            Status = OpenIddictConstants.Statuses.Valid, Type = OpenIddictConstants.AuthorizationTypes.Permanent,
            ConcurrencyToken = Guid.NewGuid().ToString(), CreationDate = Earlier,
        };
        var oauthToken = new OpenIddictEntityFrameworkCoreToken
        {
            Id = Guid.NewGuid().ToString(), Application = app, Authorization = authorization,
            Subject = ledger.UserId.ToString(), Status = OpenIddictConstants.Statuses.Valid,
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            ConcurrencyToken = Guid.NewGuid().ToString(), CreationDate = Earlier,
        };
        var pendingInvite = Invite(ledger.UserId, consumedAt: null);
        var acceptedInvite = Invite(ledger.UserId, consumedAt: Earlier);
        db.AddRange(liveSession, revokedSession, mcpToken, app, authorization, oauthToken, pendingInvite, acceptedInvite);
        await db.SaveChangesAsync();
        var usersBefore = await db.Users.AsNoTracking().CountAsync();

        var result = await RestoreReset.ApplyAsync(db, Now, default);

        Assert.Equal(Now, (await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Id == liveSession.Id)).RevokedAt);
        // Revoked before the backup: when, is kept.
        Assert.Equal(Earlier, (await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Id == revokedSession.Id)).RevokedAt);
        Assert.Equal(Now, (await db.McpAccessTokens.AsNoTracking().SingleAsync(t => t.Id == mcpToken.Id)).RevokedAt);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked,
            (await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().SingleAsync(a => a.Id == authorization.Id)).Status);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked,
            (await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().SingleAsync(t => t.Id == oauthToken.Id)).Status);
        Assert.False(await db.Invites.AsNoTracking().AnyAsync(i => i.Id == pendingInvite.Id));

        // Who everyone is comes back as backed up, and a connector keeps its registration.
        Assert.True(await db.Invites.AsNoTracking().AnyAsync(i => i.Id == acceptedInvite.Id));
        Assert.True(await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().AnyAsync(a => a.Id == app.Id));
        Assert.Equal(usersBefore, await db.Users.AsNoTracking().CountAsync());

        Assert.True(result.Sessions >= 1 && result.McpTokens >= 1 && result.OauthAuthorizations >= 1
                    && result.OauthTokens >= 1 && result.Invites >= 1, $"counts: {result}");
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task A_restore_disconnects_drive_and_forgets_what_it_mirrored()
    {
        var actor = (await SyntheticLedger.CreateAsync(_fixture)).UserId;
        await using var db = _fixture.NewDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.DriveSync.ExecuteDeleteAsync();
        db.DriveSync.Add(new DriveSyncRow
        {
            Enabled = true, OauthCiphertext = [1, 2, 3], FolderId = "folder", FolderName = "Coffer Backups [abc]",
            InstallId = "abc", ConnectedEmail = "someone@example.com", LastSyncAt = Earlier,
            LastSyncStatus = "ok", ConfiguredByUserId = actor, CreatedAt = Earlier, UpdatedAt = Earlier,
        });
        db.DriveMirroredBackups.Add(new DriveMirroredBackupRow { BackupId = "coffer-20261014T000000000Z-00000000" });
        await db.SaveChangesAsync();

        await RestoreReset.ApplyAsync(db, Now, default);

        var drive = await db.DriveSync.AsNoTracking().SingleAsync();
        Assert.False(drive.Enabled);
        Assert.Null(drive.OauthCiphertext);
        Assert.Null(drive.FolderId);
        Assert.Null(drive.FolderName);
        Assert.Null(drive.InstallId);
        Assert.Null(drive.ConnectedEmail);
        Assert.Null(drive.LastSyncAt);
        Assert.Null(drive.LastSyncStatus);
        Assert.False(await db.DriveMirroredBackups.AnyAsync());
        await tx.RollbackAsync();
    }

    private static SessionRow Session(Guid userId, DateTime? revokedAt) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, SessionHash = RandomNumberGenerator.GetBytes(32),
        CreatedAt = Earlier, LastSeenAt = Earlier, ExpiresAt = Now.AddDays(30), RevokedAt = revokedAt,
    };

    private static InviteRow Invite(Guid issuedBy, DateTime? consumedAt) => new()
    {
        TokenHash = RandomNumberGenerator.GetBytes(32), Id = Guid.NewGuid(), IssuedByUserId = issuedBy,
        GrantsAdmin = true, ExpiresAt = Now.AddDays(7), ConsumedAt = consumedAt, CreatedAt = Earlier,
    };
}
