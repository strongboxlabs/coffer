using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Idempotent means the second revoke changes NOTHING — specifically that it
    /// leaves the original <c>revoked_at</c> in place.
    /// </summary>
    /// <remarks>
    /// This test asserted nothing at all: it called Revoke twice and passed as long
    /// as neither call threw. The property that actually matters lives in the
    /// repository's `RevokedAt == null` predicate — drop it and the second call
    /// re-stamps `revoked_at` to a later time, silently rewriting when the session
    /// was revoked. Nothing threw, so the old test could not tell the difference.
    /// </remarks>
    [Fact]
    public async Task Revoke_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = new SessionsRepository(_fixture.NewServiceFactory());

        var inserted = await repo.InsertAsync(
            ledger.UserId, FreshHash(), null, DateTime.UtcNow.AddDays(30));
        // A second session that must be left alone — a revoke that widened its
        // predicate would take this one with it.
        var bystander = await repo.InsertAsync(
            ledger.UserId, FreshHash(), null, DateTime.UtcNow.AddDays(30));

        await repo.RevokeAsync(inserted.Id);

        await using var db = _fixture.NewDbContext();
        var firstRevokedAt = (await db.AuthSessions.AsNoTracking()
            .SingleAsync(s => s.Id == inserted.Id)).RevokedAt;
        Assert.NotNull(firstRevokedAt);

        // Clock has to move, or "unchanged" would hold trivially.
        await Task.Delay(20);
        await repo.RevokeAsync(inserted.Id);   // second call: must be a no-op

        await using var db2 = _fixture.NewDbContext();
        var after = await db2.AuthSessions.AsNoTracking()
            .SingleAsync(s => s.Id == inserted.Id);
        Assert.Equal(firstRevokedAt, after.RevokedAt);

        var untouched = await db2.AuthSessions.AsNoTracking()
            .SingleAsync(s => s.Id == bystander.Id);
        Assert.Null(untouched.RevokedAt);
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
