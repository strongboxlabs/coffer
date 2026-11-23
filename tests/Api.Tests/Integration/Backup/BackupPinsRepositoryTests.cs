using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The <c>backup_pins</c> gateway (mig 144, ADR-0062 ④b+c): pin / unpin / list,
/// service-role. Pins are deployment-global; each test resets the table first.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BackupPinsRepositoryTests : DatabaseTest
{
    public BackupPinsRepositoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private BackupPinsRepository NewRepo() => new(Db.NewServiceFactory());

    private async Task<Guid> NewUserAsync() => (await SyntheticLedger.CreateAsync(Db)).UserId;

    [Fact]
    public async Task Pin_then_get_then_unpin()
    {
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

    // ---- upgrade pins (mig 268, ADR-0110)

    private static async Task SpacedAsync() => await Task.Delay(15);

    [Fact]
    public async Task An_upgrade_pin_holds_the_backup_but_is_not_an_admin_pin()
    {
        var repo = NewRepo();
        await repo.PinForUpgradeAsync("b-up-1", "before upgrade 0.128.1 to 0.129.0", null);

        Assert.Contains("b-up-1", await repo.GetPinnedIdsAsync());          // retention keeps it
        Assert.DoesNotContain("b-up-1", await repo.GetAdminPinnedIdsAsync()); // the list's pinned flag does not
        var labels = await repo.GetUpgradeLabelsAsync();
        Assert.Equal("before upgrade 0.128.1 to 0.129.0", labels["b-up-1"]);
    }

    [Fact]
    public async Task An_admin_pin_on_an_upgrade_backup_converts_it_and_survives_the_trim()
    {
        var repo = NewRepo();
        var actor = await NewUserAsync();
        await repo.PinForUpgradeAsync("b-up-1", "before upgrade 1 to 2", null); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-2", "before upgrade 2 to 3", null); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-3", "before upgrade 3 to 4", null);

        await repo.PinAsync("b-up-1", actor);   // the admin keeps the oldest

        Assert.Contains("b-up-1", await repo.GetAdminPinnedIdsAsync());
        Assert.DoesNotContain("b-up-1", (await repo.GetUpgradeLabelsAsync()).Keys);
        Assert.Empty(await repo.TrimUpgradePinsAsync());                     // two upgrade pins are left: nothing to trim
        Assert.Contains("b-up-1", await repo.GetPinnedIdsAsync());
    }

    [Fact]
    public async Task An_upgrade_never_takes_over_an_admin_pin()
    {
        var repo = NewRepo();
        var actor = await NewUserAsync();
        await repo.PinAsync("b-admin", actor);

        await repo.PinForUpgradeAsync("b-admin", "before upgrade 1 to 2", actor);
        await repo.ReleaseUpgradePinAsync("b-admin");

        Assert.Contains("b-admin", await repo.GetAdminPinnedIdsAsync());
        Assert.Empty(await repo.GetUpgradeLabelsAsync());
    }

    [Fact]
    public async Task The_trim_keeps_the_newest_two_upgrade_pins_and_touches_nothing_else()
    {
        var repo = NewRepo();
        var actor = await NewUserAsync();
        await repo.PinAsync("b-admin", actor); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-1", "one", null); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-2", "two", null); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-3", "three", null); await SpacedAsync();
        await repo.PinForUpgradeAsync("b-up-4", "four", null);

        var released = await repo.TrimUpgradePinsAsync();

        Assert.Equal(["b-up-2", "b-up-1"], released);
        var pinned = await repo.GetPinnedIdsAsync();
        Assert.Equal(["b-admin", "b-up-3", "b-up-4"], pinned.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(await repo.TrimUpgradePinsAsync());   // idempotent
    }

    [Fact]
    public async Task Releasing_an_upgrade_pin_returns_the_backup_to_normal_retention()
    {
        var repo = NewRepo();
        await repo.PinForUpgradeAsync("b-up-1", "before upgrade 1 to 2", null);

        await repo.ReleaseUpgradePinAsync("b-up-1");

        Assert.Empty(await repo.GetPinnedIdsAsync());
        await repo.ReleaseUpgradePinAsync("b-up-1");        // idempotent
    }
}
