using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// Migration 266: the views lose their own memo and tag columns and the register's
/// SQL functions go, and the upgrade refuses a function that would read a dropped
/// column only when called.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class Migration266Tests : DatabaseTest
{
    private const string Mig266 = "266_drop_view_notes_columns_and_register_functions.sql";

    private static readonly string[] Views = ["live_txn_headers", "resolved_transactions", "template_txn_headers"];

    public Migration266Tests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task At_head_the_views_carry_no_memo_or_tag_column_and_the_register_functions_are_gone()
    {
        var cs = Db.ServiceConnectionString;

        var dropped = await ListAsync(cs, """
            SELECT table_name || '.' || column_name FROM information_schema.columns
             WHERE table_schema = 'public'
               AND table_name IN ('resolved_transactions', 'live_txn_headers', 'template_txn_headers')
               AND column_name IN ('memo', 'tags', 'leg_memo', 'header_memo')
            """);
        Assert.Empty(dropped);
        // What stays: the override flag on the view (the originals stay), the stored
        // presentation, and every other header column on the header views.
        Assert.Contains("has_overrides", await ColumnsAsync(cs, "resolved_transactions"));
        Assert.Contains("notes_memo", await ColumnsAsync(cs, "resolved_transactions"));
        Assert.Contains("counterparty_notes_memo", await ColumnsAsync(cs, "resolved_transactions"));
        Assert.Contains("payee", await ColumnsAsync(cs, "live_txn_headers"));
        Assert.Contains("payee", await ColumnsAsync(cs, "template_txn_headers"));

        Assert.Empty(await ListAsync(cs, """
            SELECT p.oid::regprocedure::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'public' AND p.proname IN ('register_entry_keys', 'register_filtered_entries')
            """));

        // Recreated views are the RLS boundary only with security_invoker, and the
        // request role reads them and writes nothing through them.
        foreach (var view in Views)
        {
            Assert.Equal(["security_invoker=true"], await ListAsync(cs,
                $"SELECT unnest(reloptions) FROM pg_class WHERE relname = '{view}' AND relnamespace = 'public'::regnamespace"));
            Assert.Equal(["SELECT"], await ListAsync(cs,
                $"SELECT privilege_type FROM information_schema.role_table_grants WHERE table_name = '{view}' AND grantee = 'coffer_app' ORDER BY 1"));
        }
    }

    [Fact]
    public async Task The_upgrade_refuses_a_function_that_reads_a_dropped_column_and_names_it()
    {
        // A string-bodied SQL or PL/pgSQL function is not tracked as a view's dependent,
        // so it would fail only when called. The upgrade stops before dropping anything.
        var work = Directory.CreateTempSubdirectory("coffer-mig266-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig266_refuses");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig266, work));
        await ExecAsync(cs, """
            CREATE FUNCTION public.probe_sql_reads_view_memo() RETURNS text LANGUAGE sql
                AS 'SELECT memo FROM resolved_transactions LIMIT 1';
            CREATE FUNCTION public.probe_plpgsql_reads_template_memo() RETURNS text LANGUAGE plpgsql AS $$
            BEGIN
                RETURN (SELECT t.memo FROM template_txn_headers t LIMIT 1);
            END $$;
            CREATE FUNCTION public.probe_reads_view_without_memo() RETURNS bigint LANGUAGE sql
                AS 'SELECT count(*) FROM live_txn_headers';
            """);

        var refused = Assert.ThrowsAny<Exception>(() => UpgradeRehearsal.Apply(cs, work, Mig266));
        var message = refused.ToString();
        Assert.Contains("probe_sql_reads_view_memo()", message, StringComparison.Ordinal);
        Assert.Contains("probe_plpgsql_reads_template_memo()", message, StringComparison.Ordinal);
        Assert.DoesNotContain("probe_reads_view_without_memo", message, StringComparison.Ordinal);
        // Nothing dropped, nothing journaled.
        Assert.Contains("memo", await ColumnsAsync(cs, "resolved_transactions"));
        Assert.Single(await ListAsync(cs, "SELECT proname FROM pg_proc WHERE proname = 'register_entry_keys'"));
        Assert.Empty(await ListAsync(cs, $"SELECT scriptname FROM __schema_migrations WHERE scriptname LIKE '%{Mig266}'"));

        // With the readers re-issued without the column, the upgrade goes through, and
        // the function that never read one still runs against the recreated view.
        await ExecAsync(cs, """
            DROP FUNCTION public.probe_sql_reads_view_memo();
            DROP FUNCTION public.probe_plpgsql_reads_template_memo();
            """);
        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig266);
        Assert.DoesNotContain("memo", await ColumnsAsync(cs, "resolved_transactions"));
        Assert.Equal(["0"], await ListAsync(cs, "SELECT public.probe_reads_view_without_memo()::text"));
    }

    private static Task<List<string>> ColumnsAsync(string cs, string view) => ListAsync(cs,
        $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{view}'");

    private static async Task<List<string>> ListAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetValue(0).ToString()!);
        return result;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
