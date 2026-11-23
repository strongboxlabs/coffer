using Microsoft.EntityFrameworkCore;

using Coffer.Api.Crypto;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Api.Upgrades;

namespace Coffer.Api.Tests.Integration.Upgrades;

/// <summary>
/// The private-fork release token (mig 269, ADR-0110): sealed under the master key, one row or none.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UpgradeRepoTokenStoreTests : DatabaseTest
{
    private readonly TestClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));

    public UpgradeRepoTokenStoreTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private UpgradeRepoTokenStore NewStore(LedgerKeyService? keys = null) =>
        new(Db.NewServiceFactory(), keys ?? PostgresFixture.FixtureKeys(), _clock);

    [Fact]
    public async Task A_token_round_trips_and_is_stored_sealed()
    {
        await NewStore().ClearAsync();
        var store = NewStore();
        Assert.False(await store.HasAsync());
        Assert.Null(await store.GetAsync());

        await store.SetAsync("  synthetic-token-value  ", actorUserId: null);

        Assert.True(await store.HasAsync());
        Assert.Equal("synthetic-token-value", await store.GetAsync());
        await using var db = Db.NewDbContext();
        var stored = await db.UpgradeRepoToken.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("synthetic-token-value", System.Text.Encoding.UTF8.GetString(stored.TokenCiphertext));
    }

    [Fact]
    public async Task Setting_again_replaces_the_one_row_and_clearing_removes_it()
    {
        var store = NewStore();
        await store.SetAsync("first-synthetic", null);
        await store.SetAsync("second-synthetic", null);

        Assert.Equal("second-synthetic", await store.GetAsync());
        await using (var db = Db.NewDbContext())
            Assert.Equal(1, await db.UpgradeRepoToken.CountAsync());

        await store.ClearAsync();
        await store.ClearAsync();   // idempotent

        Assert.False(await store.HasAsync());
        Assert.Null(await store.GetAsync());
    }

    [Fact]
    public async Task A_token_this_installs_key_cannot_open_reads_as_none()
    {
        await NewStore().SetAsync("synthetic-token-value", null);
        await using (var db = Db.NewDbContext())
        {
            var row = await db.UpgradeRepoToken.SingleAsync();
            row.TokenCiphertext = [.. row.TokenCiphertext.Select(b => (byte)(b ^ 0xFF))];   // another install's blob
            await db.SaveChangesAsync();
        }

        Assert.Null(await NewStore().GetAsync());
        Assert.True(await NewStore().HasAsync());   // still stored: the screen can say it must be entered again
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_token_is_refused(string token)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => NewStore().SetAsync(token, null));
    }
}
