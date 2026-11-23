using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// <c>transfer_shares_source_brokerages</c> (migration 263), the one definition of an
/// in-kind transfer's source brokerage, which the upgrade, the import check and the
/// test-time check all read. What it answers is pinned by the backfill fixtures and the
/// import-check tests; this pins how it may be called.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransferSharesSourceBrokeragesTests : DatabaseTest
{
    public TransferSharesSourceBrokeragesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task It_is_an_inlinable_invoker_function_that_only_its_owner_calls()
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT p.provolatile::text, p.proisstrict, p.prosecdef, p.proconfig IS NULL,
                   ARRAY(SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE a.grantee::regrole::text END
                           FROM aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                          WHERE a.privilege_type = 'EXECUTE' AND a.grantee <> p.proowner
                          ORDER BY 1)
              FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace
               AND p.proname = 'transfer_shares_source_brokerages'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "transfer_shares_source_brokerages does not exist");

        Assert.Equal("s", reader.GetString(0));
        // A STRICT or SET-carrying set-returning SQL function is never inlined, and a
        // definer would read every member's rows past RLS.
        Assert.False(reader.GetBoolean(1), "must not be STRICT");
        Assert.False(reader.GetBoolean(2), "must not be SECURITY DEFINER");
        Assert.True(reader.GetBoolean(3), "must carry no SET clause");
        // Nothing on a request path calls it.
        Assert.Empty(reader.GetFieldValue<string[]>(4));
        Assert.False(await reader.ReadAsync(), "transfer_shares_source_brokerages is overloaded");
    }

    [Fact]
    public async Task It_is_inlined_into_the_caller()
    {
        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "EXPLAIN (COSTS OFF) SELECT * FROM transfer_shares_source_brokerages('00000000-0000-0000-0000-000000000000')",
            connection);
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
        var text = string.Join("\n", plan);
        Assert.DoesNotContain("Function Scan", text, StringComparison.Ordinal);
        Assert.Contains("txn_legs", text, StringComparison.Ordinal);
    }
}
