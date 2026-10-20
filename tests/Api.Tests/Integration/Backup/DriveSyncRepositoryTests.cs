using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The <c>drive_sync</c> singleton gateway (ADR-0062, mig 142). Connect / status
/// / oauth-ciphertext / disconnect / record-outcome / re-wrap round-trips
/// against the real service-role DB. The row is a deployment singleton, so each
/// test resets it first (ApiCollection runs sequentially).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DriveSyncRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public DriveSyncRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private DriveSyncRepository NewRepo() => new(_fixture.NewServiceFactory());

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewDbContext();   // service role
        await db.Database.ExecuteSqlRawAsync("DELETE FROM drive_sync;");
    }

    [Fact]
    public async Task Missing_row_reads_as_not_configured()
    {
        await ResetAsync();
        var status = await NewRepo().GetStatusAsync();

        Assert.False(status.Connected);
        Assert.False(status.Enabled);
        Assert.Null(status.ConnectedEmail);
    }

    [Fact]
    public async Task Connect_then_status_reports_connected_without_leaking_token()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewAdminUserAsync();
        var token = Encoding.UTF8.GetBytes("sealed-oauth-blob");

        var status = await repo.ConnectAsync(
            token, "folder-123", "Coffer Backups", "user@example.com", actor, DateTime.UtcNow);

        Assert.True(status.Connected);
        Assert.True(status.Enabled);
        Assert.Equal("user@example.com", status.ConnectedEmail);
        Assert.Equal("Coffer Backups", status.FolderName);

        // The ciphertext is reachable only via the dedicated accessors, never status.
        var ciphertext = await repo.GetOauthCiphertextAsync();
        Assert.Equal(token, ciphertext);

        var conn = await repo.GetConnectionAsync();
        Assert.NotNull(conn);
        Assert.Equal("folder-123", conn!.FolderId);
        Assert.Equal(token, conn.OauthCiphertext);
    }

    [Fact]
    public async Task Disconnect_clears_token_and_folder()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewAdminUserAsync();
        await repo.ConnectAsync(
            Encoding.UTF8.GetBytes("blob"), "f1", "Coffer Backups", "u@e.com", actor, DateTime.UtcNow);

        await repo.DisconnectAsync(DateTime.UtcNow);

        var status = await repo.GetStatusAsync();
        Assert.False(status.Connected);
        Assert.False(status.Enabled);
        Assert.Null(status.ConnectedEmail);
        Assert.Null(await repo.GetOauthCiphertextAsync());
        Assert.Null(await repo.GetConnectionAsync());
    }

    [Fact]
    public async Task Record_outcome_surfaces_in_status()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewAdminUserAsync();
        await repo.ConnectAsync(
            Encoding.UTF8.GetBytes("blob"), "f1", "Coffer Backups", "u@e.com", actor, DateTime.UtcNow);

        var at = DateTime.UtcNow;
        await repo.RecordSyncOutcomeAsync("error", "boom", at);

        var status = await repo.GetStatusAsync();
        Assert.Equal("error", status.LastSyncStatus);
        Assert.Equal("boom", status.LastSyncError);
        Assert.NotNull(status.LastSyncAt);
    }

    [Fact]
    public async Task Replace_ciphertext_swaps_only_when_connected()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewAdminUserAsync();

        // Not connected → no-op (no row to update).
        await repo.ReplaceOauthCiphertextAsync(Encoding.UTF8.GetBytes("new"));
        Assert.Null(await repo.GetOauthCiphertextAsync());

        await repo.ConnectAsync(
            Encoding.UTF8.GetBytes("old"), "f1", "Coffer Backups", "u@e.com", actor, DateTime.UtcNow);
        await repo.ReplaceOauthCiphertextAsync(Encoding.UTF8.GetBytes("rewrapped"));

        Assert.Equal("rewrapped", Encoding.UTF8.GetString((await repo.GetOauthCiphertextAsync())!));
    }

    [Fact]
    public async Task Install_id_is_stable_and_survives_disconnect()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewAdminUserAsync();

        var first = await repo.EnsureInstallIdAsync("aaa111");
        Assert.Equal("aaa111", first);
        // Idempotent: a different candidate doesn't change an already-set id.
        Assert.Equal("aaa111", await repo.EnsureInstallIdAsync("bbb222"));

        // Connect then disconnect — the install id must persist so a reconnect
        // resolves the same folder.
        await repo.ConnectAsync(
            Encoding.UTF8.GetBytes("blob"), "f1", "Coffer Backups [aaa111]", "u@e.com", actor, DateTime.UtcNow);
        Assert.Equal("aaa111", (await repo.GetStatusAsync()).InstallId);

        await repo.DisconnectAsync(DateTime.UtcNow);
        var afterDisconnect = await repo.GetStatusAsync();
        Assert.False(afterDisconnect.Connected);
        Assert.Equal("aaa111", afterDisconnect.InstallId);
        Assert.Equal("aaa111", await repo.EnsureInstallIdAsync("ccc333"));
    }

    /// <summary>The <c>configured_by_user_id</c> FK requires a real user row.</summary>
    private async Task<Guid> NewAdminUserAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        return ledger.UserId;
    }
}
