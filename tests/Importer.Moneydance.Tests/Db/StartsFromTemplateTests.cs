using Dapper;
using Xunit.Sdk;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Two tests that each require the template's state (the test ledger, nothing else)
/// and then write, run in name order. Whichever runs second would see the first one's
/// rows if the two shared a database.
/// </summary>
[Collection(DbCollection.Name)]
[TestCaseOrderer(StartsFromTemplate.ForwardOrderer, StartsFromTemplate.Assembly)]
public sealed class StartsFromTemplateForwardTests : DatabaseTest
{
    public StartsFromTemplateForwardTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public Task A_runs_first() => StartsFromTemplate.ThenWritesAsync(Db);

    [Fact]
    public Task B_runs_second() => StartsFromTemplate.ThenWritesAsync(Db);
}

/// <summary>The same pair, in the other order.</summary>
[Collection(DbCollection.Name)]
[TestCaseOrderer(StartsFromTemplate.ReverseOrderer, StartsFromTemplate.Assembly)]
public sealed class StartsFromTemplateReverseTests : DatabaseTest
{
    public StartsFromTemplateReverseTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public Task A_runs_second() => StartsFromTemplate.ThenWritesAsync(Db);

    [Fact]
    public Task B_runs_first() => StartsFromTemplate.ThenWritesAsync(Db);
}

internal static class StartsFromTemplate
{
    public const string Assembly = "Importer.Moneydance.Tests";
    public const string ForwardOrderer = "Coffer.Importer.Moneydance.Tests.Db.ByMethodNameOrderer";
    public const string ReverseOrderer = "Coffer.Importer.Moneydance.Tests.Db.ByMethodNameDescendingOrderer";

    public static async Task ThenWritesAsync(TestDatabase db)
    {
        await using var conn = db.OpenConnection();
        var ledgers = (await conn.QueryAsync<Guid>("SELECT id FROM ledgers")).ToList();
        Assert.Equal([TestLedger.Id], ledgers);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM accounts"));

        await conn.ExecuteAsync("INSERT INTO ledgers (id, name) VALUES (gen_random_uuid(), 'order-independence')");
        await conn.ExecuteAsync(
            """
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code)
            VALUES (gen_random_uuid(), @L, 'order-independence', 'bank', 'USD')
            """, new { L = TestLedger.Id });
    }
}

/// <summary>Runs a class's tests in method-name order.</summary>
public sealed class ByMethodNameOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}

/// <summary>Runs a class's tests in reverse method-name order.</summary>
public sealed class ByMethodNameDescendingOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderByDescending(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}
