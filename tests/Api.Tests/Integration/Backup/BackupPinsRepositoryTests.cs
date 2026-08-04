using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The <c>backup_pins</c> gateway (mig 144, ADR-0062 ④b+c): pin / unpin / list,
/// service-role. Pins are deployment-global; each test resets the table first.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BackupPinsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public BackupPinsRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private BackupPinsRepository NewRepo() => new(_fixture.NewServiceFactory());

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_pins;");
    }

    private async Task<Guid> NewUserAsync() => (await SyntheticLedger.CreateAsync(_fixture)).UserId;

    [Fact]
    public async Task Pin_then_get_then_unpin()
    {
        await ResetAsync();
        var repo = NewRepo();
        var actor = await NewUserAsync();

        Assert.Empty(await repo.GetPinnedIdsAsync());

        await repo.PinAsync("coffer-20260625T010101001Z-aabbccdd", actor);
        await repo.PinAsync("coffer-20260625T010101002Z-eeff0011", actor);
        var pinned = await repo.GetPinnedIdsAsync();
        Assert.Equal(2, pinned.Count);
        Assert.Contains("coffer-20260625T010101001Z-aabbccdd", pinned);

        // Idempotent re-pin.
        await repo.PinAsync("coffer-20260625T010101001Z-aabbccdd", actor);
        Assert.Equal(2, (await repo.GetPinnedIdsAsync()).Count);

        await repo.UnpinAsync("coffer-20260625T010101001Z-aabbccdd");
        var after = await repo.GetPinnedIdsAsync();
        Assert.Single(after);
        Assert.DoesNotContain("coffer-20260625T010101001Z-aabbccdd", after);

        // Unpin of an unknown id is a no-op (idempotent).
        await repo.UnpinAsync("never-pinned");
        Assert.Single(await repo.GetPinnedIdsAsync());
    }
}
