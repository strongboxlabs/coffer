using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

[Collection(DbCollection.Name)]
public sealed class SecuritiesRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public SecuritiesRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Upsert_inserts_a_new_row_and_returns_the_supplied_id()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new SecuritiesRepository(connection);
        var id = Guid.NewGuid();
        var externalId = $"md-{Guid.NewGuid():N}";

        var returnedId = await repo.UpsertByExternalIdAsync(new SecurityRow(
            Id: id,
            LedgerId: TestLedger.Id,
            Ticker: "IDXB",
            Cusip: "922908363",
            Name: "Index Fund B Admiral",
            AssetClass: "equity",
            VehicleType: "mutual_fund",
            ClassificationSource: "import",
            ClassificationConfidence: "assumed",
            Exchange: "NASDAQ",
            IsActive: true,
            ExternalId: externalId,
            ShareDecimals: 5));

        Assert.Equal(id, returnedId);
        Assert.Equal(1, await repo.CountAsync());

        var roundTrip = await repo.GetByExternalIdAsync(TestLedger.Id, externalId);
        Assert.NotNull(roundTrip);
        Assert.Equal(id,                   roundTrip!.Id);
        Assert.Equal("IDXB",               roundTrip.Ticker);
        Assert.Equal("922908363",            roundTrip.Cusip);
        Assert.Equal("equity",               roundTrip.AssetClass);
        Assert.Equal("mutual_fund",          roundTrip.VehicleType);
        Assert.True(roundTrip.IsActive);
        Assert.Equal(5, roundTrip.ShareDecimals);
    }

    [Fact]
    public async Task Upsert_updates_data_fields_but_preserves_id_on_conflict()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new SecuritiesRepository(connection);
        var externalId = $"md-{Guid.NewGuid():N}";

        var firstId = await repo.UpsertByExternalIdAsync(new SecurityRow(
            Id: Guid.NewGuid(), LedgerId: TestLedger.Id,
            Ticker: "OLD", Cusip: null,
            Name: "Old Name", AssetClass: "equity",
            VehicleType: "mutual_fund", ClassificationSource: "import",
            ClassificationConfidence: "assumed", Exchange: null,
            IsActive: true, ExternalId: externalId, ShareDecimals: 4));

        // Same external_id, different surface fields + a different proposed id, and
        // a different classification — which must NOT overwrite the seeded one
        // (classification is seed-once; Coffer owns it after import, ADR-0067).
        var proposedNewId = Guid.NewGuid();
        var secondId = await repo.UpsertByExternalIdAsync(new SecurityRow(
            Id: proposedNewId, LedgerId: TestLedger.Id,
            Ticker: "NEW", Cusip: "111222333",
            Name: "Renamed", AssetClass: "fixed_income",
            VehicleType: "etf", ClassificationSource: "import",
            ClassificationConfidence: "assumed", Exchange: "NYSE",
            IsActive: false, ExternalId: externalId, ShareDecimals: 5));

        Assert.Equal(firstId, secondId);
        Assert.NotEqual(proposedNewId, secondId);
        Assert.Equal(1, await repo.CountAsync());

        var roundTrip = await repo.GetByExternalIdAsync(TestLedger.Id, externalId);
        Assert.NotNull(roundTrip);
        Assert.Equal("NEW",       roundTrip!.Ticker);     // surface fields refresh
        Assert.Equal("111222333", roundTrip.Cusip);
        Assert.Equal("Renamed",   roundTrip.Name);
        Assert.Equal("NYSE",      roundTrip.Exchange);
        Assert.False(roundTrip.IsActive);
        Assert.Equal(5, roundTrip.ShareDecimals);         // refreshed on conflict
        Assert.Equal("equity",      roundTrip.AssetClass);  // classification preserved (seed-once)
        Assert.Equal("mutual_fund", roundTrip.VehicleType);
    }

    [Fact]
    public async Task Upsert_rejects_row_with_empty_external_id()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new SecuritiesRepository(connection);

        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertByExternalIdAsync(new SecurityRow(
            Id: Guid.NewGuid(), LedgerId: TestLedger.Id,
            Ticker: null, Cusip: null,
            Name: "x", AssetClass: null,
            VehicleType: null, ClassificationSource: null,
            ClassificationConfidence: null, Exchange: null,
            IsActive: true, ExternalId: null, ShareDecimals: 4)));
    }

    private static async Task TruncateAsync(Npgsql.NpgsqlConnection connection)
    {
        await connection.ExecuteAsync("TRUNCATE securities CASCADE;");
    }
}
