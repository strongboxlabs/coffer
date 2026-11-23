using Dapper;

using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// Dapper cannot bind <see cref="DateOnly"/> without
/// <see cref="DapperDateOnlyHandler"/> registered — it throws
/// <c>NotSupportedException: The member … of type System.DateOnly cannot be used
/// as a parameter value</c>. Registration happens in the static constructor of
/// the importer's <see cref="DbConnectionFactory"/>, the gateway every importer
/// DB path opens its connection through, so every host that runs the importer
/// (the CLI, and the API's import job, which runs uploads and the hub's Demo) gets it.
///
/// Importer writes bind DateOnly parameters for <c>accounts.opened_on</c>,
/// <c>loan_terms.first_payment_date</c> and the
/// <c>recurring_transactions</c> dates (<c>start_date</c>, <c>end_date</c>,
/// <c>next_due_date</c>, <c>last_acknowledged_date</c>). Every account write
/// binds <c>opened_on</c>, so a missing registration fails every API-side
/// import, the hub's Demo included.
///
/// These run in the API test assembly on purpose: the API has no registration
/// of its own outside that factory, so it is the place where losing the
/// factory's registration is observable.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ImporterDateOnlyBindingTests : DatabaseTest
{
    public ImporterDateOnlyBindingTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Opening_an_importer_connection_makes_DateOnly_parameters_bindable()
    {
        // Going through the factory is the point — that is where registration
        // hangs, and every importer DB path in the API opens through it.
        var factory = new DbConnectionFactory(Db.ServiceConnectionString);
        await using var connection = await factory.OpenAsync();

        var roundTrip = await connection.ExecuteScalarAsync<DateOnly>(
            "SELECT @value::date;", new { value = new DateOnly(2018, 3, 14) });

        Assert.Equal(new DateOnly(2018, 3, 14), roundTrip);
    }

    [Fact]
    public async Task A_null_DateOnly_parameter_also_binds()
    {
        var factory = new DbConnectionFactory(Db.ServiceConnectionString);
        await using var connection = await factory.OpenAsync();

        var roundTrip = await connection.ExecuteScalarAsync<DateOnly?>(
            "SELECT @value::date;", new { value = (DateOnly?)null });

        Assert.Null(roundTrip);
    }
}
