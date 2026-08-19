using Dapper;

using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// Dapper cannot bind <see cref="DateOnly"/> without
/// <see cref="DapperDateOnlyHandler"/> registered — it throws
/// <c>NotSupportedException: The member … of type System.DateOnly cannot be used
/// as a parameter value</c>. The importer registered it from its CLI entry point
/// and its own test fixture, but NOT from the API, which runs the same importer
/// for demo provisioning and for the import endpoint.
///
/// That was a latent failure for a long time: the only DateOnly parameter was
/// <c>recurring_transactions.start_date</c>, and the bundled demo export carries
/// no reminders, so nothing in CI ever bound one from inside the API. An
/// API-side import of a real file WITH reminders would have failed. Seeding
/// <c>accounts.opened_on</c> made every account carry a DateOnly and turned it
/// into a certain failure, which is how it surfaced.
///
/// These run in the API test assembly on purpose: it is the process that does
/// not register the handler itself, so it is the only place the regression can
/// actually be observed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ImporterDateOnlyBindingTests
{
    private readonly PostgresFixture _fixture;

    public ImporterDateOnlyBindingTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Opening_an_importer_connection_makes_DateOnly_parameters_bindable()
    {
        // Going through the factory is the point — that is where registration
        // hangs, and every importer DB path in the API opens through it.
        var factory = new DbConnectionFactory(_fixture.ServiceConnectionString);
        await using var connection = await factory.OpenAsync();

        var roundTrip = await connection.ExecuteScalarAsync<DateOnly>(
            "SELECT @value::date;", new { value = new DateOnly(2018, 3, 14) });

        Assert.Equal(new DateOnly(2018, 3, 14), roundTrip);
    }

    [Fact]
    public async Task A_null_DateOnly_parameter_also_binds()
    {
        var factory = new DbConnectionFactory(_fixture.ServiceConnectionString);
        await using var connection = await factory.OpenAsync();

        var roundTrip = await connection.ExecuteScalarAsync<DateOnly?>(
            "SELECT @value::date;", new { value = (DateOnly?)null });

        Assert.Null(roundTrip);
    }
}
