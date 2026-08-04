using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

[Collection(ApiCollection.Name)]
public sealed class ChallengeStoreTests
{
    private readonly PostgresFixture _fixture;

    public ChallengeStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Save_then_Consume_returns_the_persisted_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        var id = await store.SaveAsync(
            ChallengeStore.SetupFlow,
            userId: null,
            optionsJson: "{\"challenge\":\"abc\"}",
            metadataJson: "{\"username\":\"alice\"}",
            ttl: TimeSpan.FromMinutes(2));

        var consumed = await store.ConsumeAsync(id, ChallengeStore.SetupFlow);
        Assert.NotNull(consumed);
        Assert.Equal(ChallengeStore.SetupFlow, consumed!.Flow);
        Assert.Equal("{\"challenge\":\"abc\"}", consumed.OptionsJson);
        Assert.Equal("{\"username\":\"alice\"}", consumed.MetadataJson);
        Assert.NotNull(consumed.ConsumedAt);
    }

    [Fact]
    public async Task Consume_is_single_shot()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        var id = await store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.FromMinutes(2));

        Assert.NotNull(await store.ConsumeAsync(id, ChallengeStore.SetupFlow));
        Assert.Null(await store.ConsumeAsync(id, ChallengeStore.SetupFlow));
    }

    [Fact]
    public async Task Consume_rejects_a_different_flow_than_the_one_persisted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        var id = await store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.FromMinutes(2));

        // The login flow asking for a setup-flow challenge id must miss —
        // mixing them would let an attacker pivot a setup ceremony into a
        // login one.
        Assert.Null(await store.ConsumeAsync(id, ChallengeStore.LoginFlow));

        // The original flow still consumes.
        Assert.NotNull(await store.ConsumeAsync(id, ChallengeStore.SetupFlow));
    }

    [Fact]
    public async Task Consume_returns_null_for_unknown_id()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        Assert.Null(await store.ConsumeAsync(Guid.NewGuid(), ChallengeStore.SetupFlow));
    }

    [Fact]
    public async Task Save_rejects_zero_or_negative_ttl()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public async Task Sweep_deletes_expired_and_consumed_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var store = new ChallengeStore(_fixture.NewServiceFactory());

        // One unconsumed + still valid: should survive the sweep.
        var fresh = await store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.FromMinutes(2));
        // One consumed: should be swept.
        var consumed = await store.SaveAsync(ChallengeStore.SetupFlow, null, "{}", null, TimeSpan.FromMinutes(2));
        await store.ConsumeAsync(consumed, ChallengeStore.SetupFlow);

        var swept = await store.SweepAsync();
        Assert.True(swept >= 1, "consumed row must be swept");

        // The fresh row's still consumable.
        Assert.NotNull(await store.ConsumeAsync(fresh, ChallengeStore.SetupFlow));
    }
}
