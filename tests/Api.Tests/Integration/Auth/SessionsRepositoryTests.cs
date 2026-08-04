using System.Security.Cryptography;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

[Collection(ApiCollection.Name)]
public sealed class SessionsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public SessionsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static byte[] FreshHash()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    [Fact]
    public async Task Insert_round_trips_every_field()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());
        var hash = FreshHash();
        var expiresAt = DateTime.UtcNow.AddDays(30);

        var inserted = await repo.InsertAsync(
            ledger.UserId, hash, "Mozilla/5.0", expiresAt);

        Assert.NotEqual(Guid.Empty, inserted.Id);
        Assert.Equal(ledger.UserId, inserted.UserId);
        Assert.Equal(hash,           inserted.SessionHash);
        Assert.Equal("Mozilla/5.0",  inserted.UserAgent);
        Assert.Null(inserted.RevokedAt);
        // Postgres rounds timestamps to microseconds; compare with tolerance.
        Assert.True((inserted.ExpiresAt - expiresAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Insert_rejects_a_hash_thats_not_32_bytes()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => repo.InsertAsync(
            ledger.UserId, new byte[16], null, DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task GetActiveByHash_returns_the_row_when_active_and_unexpired()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());
        var hash = FreshHash();

        await repo.InsertAsync(ledger.UserId, hash, null, DateTime.UtcNow.AddDays(30));

        var found = await repo.GetActiveByHashAsync(hash);
        Assert.NotNull(found);
        Assert.Equal(ledger.UserId, found!.UserId);
    }

    [Fact]
    public async Task GetActiveByHash_returns_null_for_expired_session()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());
        var hash = FreshHash();

        await repo.InsertAsync(
            ledger.UserId, hash, null,
            DateTime.UtcNow.AddSeconds(-1));      // already expired

        Assert.Null(await repo.GetActiveByHashAsync(hash));
    }

    [Fact]
    public async Task GetActiveByHash_returns_null_for_revoked_session()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());
        var hash = FreshHash();

        var inserted = await repo.InsertAsync(
            ledger.UserId, hash, null, DateTime.UtcNow.AddDays(30));
        await repo.RevokeAsync(inserted.Id);

        Assert.Null(await repo.GetActiveByHashAsync(hash));
    }

    [Fact]
    public async Task GetActiveByHash_returns_null_for_unknown_hash()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        Assert.Null(await repo.GetActiveByHashAsync(FreshHash()));
    }

    [Fact]
    public async Task GetActiveByHash_returns_null_for_wrong_size_hash()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        Assert.Null(await repo.GetActiveByHashAsync(new byte[16]));
    }

    [Fact]
    public async Task BumpLastSeen_updates_timestamp_visibly()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());
        var hash = FreshHash();

        var inserted = await repo.InsertAsync(
            ledger.UserId, hash, null, DateTime.UtcNow.AddDays(30));
        var initial = inserted.LastSeenAt;

        await Task.Delay(20);   // ensure clock advances past microsecond rounding
        await repo.BumpLastSeenAsync(inserted.Id);

        var refreshed = await repo.GetActiveByHashAsync(hash);
        Assert.NotNull(refreshed);
        Assert.True(refreshed!.LastSeenAt > initial,
            $"last_seen_at should advance: was {initial:o}, now {refreshed.LastSeenAt:o}");
    }

    [Fact]
    public async Task Revoke_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        var inserted = await repo.InsertAsync(
            ledger.UserId, FreshHash(), null, DateTime.UtcNow.AddDays(30));
        await repo.RevokeAsync(inserted.Id);
        await repo.RevokeAsync(inserted.Id);   // second call: no-op
    }

    [Fact]
    public async Task RevokeAllForUser_revokes_only_active_sessions()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(ledger.UserId, FreshHash(), null,
                DateTime.UtcNow.AddDays(30));

        var revoked = await repo.RevokeAllForUserAsync(ledger.UserId);
        Assert.Equal(3, revoked);

        var second = await repo.RevokeAllForUserAsync(ledger.UserId);
        Assert.Equal(0, second);   // already revoked, no rows touched
    }
}
