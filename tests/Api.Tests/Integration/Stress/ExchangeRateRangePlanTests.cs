using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Reporting;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// At scale, the planner reads each rate range through the pair-day key, not a scan of
/// the rate table. The every-build test
/// (<c>ExchangeRateLookupTests.Each_rate_range_can_be_read_through_the_pair_day_key</c>)
/// holds that the statement can; whether the planner chooses to follows the table's size
/// and statistics, so it is checked here, on years of daily rates planned on fresh
/// statistics.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ExchangeRateRangePlanTests : DatabaseTest
{
    public ExchangeRateRangePlanTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task At_scale_each_rate_range_is_read_through_the_pair_day_key_not_a_scan_of_the_table()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // Years of daily rates for several pairs, so the table is large enough that a scan
        // of it would cost what the key avoids.
        await ExchangeRateLookupTests.SeedDailyRatesAsync(Db, ledger, new DateOnly(2021, 1, 1));
        await using (var conn = Db.OpenServiceConnection())
        await using (var analyze = new Npgsql.NpgsqlCommand("ANALYZE fx_rates", conn))
            await analyze.ExecuteNonQueryAsync();

        var plan = await ExchangeRateLookupTests.RateRangePlanAsync(Db, ledger);
        ExchangeRateLookupTests.AssertEachDayBoundIsAKeyCondition(plan, Output);
    }
}
