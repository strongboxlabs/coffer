using System.Text.RegularExpressions;

using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Rls;

/// <summary>
/// The row-level security catalog, checked whole (FU-086). The API's own authorization
/// hid every gap this suite would have caught: tables shipped without RLS, write policies
/// that admitted viewers, a member able to delete another member's report. Each rule
/// reads the live catalog of the fully migrated database, so a new table, grant, policy,
/// view or function is held to it without anyone remembering to add it here.
/// </summary>
/// <remarks>
/// Exceptions are named below, one per entry, with the reason. An entry is the decision;
/// adding one to make a failure go away is the thing this suite exists to make visible.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class RlsCatalogTests
{
    private readonly PostgresFixture _fixture;

    public RlsCatalogTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Tables coffer_app reaches without row-level security.</summary>
    private static readonly Dictionary<string, string> WithoutRls = new()
    {
        ["OpenIddictApplications"] = OpenIddict,
        ["OpenIddictAuthorizations"] = OpenIddict,
        ["OpenIddictScopes"] = OpenIddict,
        ["OpenIddictTokens"] = OpenIddict,
        ["__schema_migrations"] =
            "DbUp's journal, read for the schema version; its rows are migration names, and "
            + "coffer_app may read them and nothing else (see The_migration_journal_is_read_only_to_the_request_role)",
    };

    private const string OpenIddict =
        "OpenIddict's stores run on the request context at the token endpoint and the /mcp "
        + "bearer check, where no user is signed in, so no per-user policy can admit them";

    /// <summary>Write policies scoped to the writer's own rows rather than to a ledger role.</summary>
    private static readonly Dictionary<string, string> SelfScopedWrites = new()
    {
        ["users.users_self"] = "a user's own row (its UPDATE is column-limited by grant)",
        ["mcp_access_tokens.mcp_access_tokens_per_user"] = "a user's own MCP tokens",
        ["mcp_tool_invocations.mcp_tool_invocations_per_user"] =
            "the MCP write audit; it records a refused attempt in the attempting user's context",
        ["report_pins.report_pins_per_user"] = "a user's own pins; a viewer pins reports too",
        ["user_preferences.user_preferences_per_user"] = "a user's own preferences",
        ["user_account_groups.user_account_groups_self"] = "a user's own sidebar groups",
        ["user_account_group_members.user_account_group_members_own_groups"] = "the members of a user's own groups",
        ["saved_reports.saved_reports_own"] = "a user's own saved reports; a viewer saves reports too",
    };

    /// <summary>SECURITY DEFINER functions, which run as their owner and so past RLS.</summary>
    private static readonly Dictionary<string, string> SecurityDefiners = new()
    {
        ["current_app_user_is_admin"] =
            "reads users.is_admin for policies; a definer so the check cannot recurse into users' own policy",
    };

    [GeneratedRegex(@"role = ANY \(ARRAY\['owner'::text, 'editor'::text\]\)|role = 'owner'::text")]
    private static partial Regex RoleGate();

    [GeneratedRegex(@"\b(user_id|owner_user_id|id) = current_app_user_id\(\)")]
    private static partial Regex OwnRowGate();

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(read(reader));
        return rows;
    }

    private static void AssertNone(IEnumerable<string> violations, string rule)
    {
        var list = violations.ToList();
        Assert.True(list.Count == 0, rule + ":\n  " + string.Join("\n  ", list));
    }

    [Fact]
    public async Task Every_table_coffer_app_can_reach_has_row_level_security()
    {
        var reachable = await QueryAsync("""
            SELECT c.relname, c.relrowsecurity
              FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')
               AND (has_any_column_privilege('coffer_app', c.oid, 'SELECT')
                 OR has_any_column_privilege('coffer_app', c.oid, 'INSERT')
                 OR has_any_column_privilege('coffer_app', c.oid, 'UPDATE')
                 OR has_table_privilege('coffer_app', c.oid, 'DELETE'))
            """, r => (Table: r.GetString(0), Rls: r.GetBoolean(1)));

        Assert.Contains(reachable, t => t.Table == "accounts");   // the query sees the grants at all
        AssertNone(reachable.Where(t => !t.Rls && !WithoutRls.ContainsKey(t.Table)).Select(t => t.Table),
            "coffer_app can reach these tables, which have no row-level security");
        AssertNone(WithoutRls.Keys.Where(k => reachable.Any(t => t.Table == k && t.Rls)),
            "these are listed as without RLS but have it; remove the entry");
    }

    [Fact]
    public async Task Every_privilege_coffer_app_holds_is_one_its_policies_serve()
    {
        // Granted on purpose, per table, since migration 254 removed the default grant.
        // A privilege no policy can use did nothing while RLS was on, and was the whole
        // door the day a table shipped without it.
        var excess = await QueryAsync("""
            WITH privs(priv) AS (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'))
            SELECT c.relname, p.priv
              FROM pg_class c CROSS JOIN privs p
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')
               AND CASE p.priv WHEN 'DELETE' THEN has_table_privilege('coffer_app', c.oid, 'DELETE')
                               ELSE has_any_column_privilege('coffer_app', c.oid, p.priv) END
               AND NOT EXISTS (
                     SELECT 1 FROM pg_policies pol
                      WHERE pol.schemaname = 'public' AND pol.tablename = c.relname
                        AND 'coffer_app' = ANY (pol.roles)
                        AND pol.cmd IN ('ALL', p.priv))
            ORDER BY 1, 2
            """, r => $"{r.GetString(0)} {r.GetString(1)}");

        AssertNone(excess.Where(e => !WithoutRls.ContainsKey(e.Split(' ')[0])),
            "coffer_app holds these privileges, and no policy of its can use them");

        // TRUNCATE ignores row-level security altogether; REFERENCES and TRIGGER are
        // never the request role's business.
        var other = await QueryAsync("""
            SELECT c.relname || ' ' || p.priv
              FROM pg_class c CROSS JOIN (VALUES ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) p(priv)
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v')
               AND has_table_privilege('coffer_app', c.oid, p.priv)
            """, r => r.GetString(0));
        AssertNone(other, "coffer_app holds privileges RLS does not govern");
    }

    [Fact]
    public async Task Every_write_policy_admits_a_ledger_role_an_admin_or_the_writers_own_rows()
    {
        var policies = await QueryAsync("""
            SELECT tablename, policyname, roles::text[], cmd, coalesce(qual, ''), coalesce(with_check, '')
              FROM pg_policies WHERE schemaname = 'public'
            """, r => (Table: r.GetString(0), Name: r.GetString(1), Roles: r.GetFieldValue<string[]>(2),
                       Cmd: r.GetString(3), Using: r.GetString(4), Check: r.GetString(5)));
        Assert.NotEmpty(policies);

        // Named for coffer_app, never PUBLIC: a PUBLIC policy reaches every role there is.
        AssertNone(policies.Where(p => !p.Roles.SequenceEqual(["coffer_app"])).Select(p => $"{p.Table}.{p.Name} ({string.Join(",", p.Roles)})"),
            "these policies are not for coffer_app alone");

        var violations = new List<string>();
        foreach (var p in policies.Where(p => p.Cmd != "SELECT"))
        {
            var key = $"{p.Table}.{p.Name}";
            // Both halves of a write: USING picks the rows UPDATE and DELETE may touch,
            // WITH CHECK the rows INSERT and UPDATE may leave. A gate on one half alone
            // let a member delete what they could only see.
            var halves = new[] { p.Using, p.Check }.Where(h => h.Length > 0).ToList();
            bool Gated(Func<string, bool> gate) => halves.Count > 0 && halves.All(gate);

            if (Gated(h => RoleGate().IsMatch(h))) continue;
            if (Gated(h => h.Contains("current_app_user_is_admin()", StringComparison.Ordinal))) continue;
            if (SelfScopedWrites.ContainsKey(key))
            {
                if (!Gated(h => OwnRowGate().IsMatch(h) || h.Contains("g.user_id = current_app_user_id()", StringComparison.Ordinal)))
                    violations.Add($"{key} is listed as the writer's own rows, but does not pin them to current_app_user_id()");
                continue;
            }
            violations.Add($"{key} ({p.Cmd}) admits any member: gate it on role IN ('owner','editor'), or declare it self-scoped with a reason");
        }
        AssertNone(violations, "write policies that let a viewer, or any member, write");

        AssertNone(SelfScopedWrites.Keys.Where(k => !policies.Any(p => $"{p.Table}.{p.Name}" == k)),
            "these self-scoped entries name no policy; remove them");
    }

    [Fact]
    public async Task Every_view_reads_as_the_caller_and_is_read_only_to_coffer_app()
    {
        var views = await QueryAsync("""
            SELECT c.relname,
                   coalesce('security_invoker=true' = ANY (c.reloptions), false),
                   has_table_privilege('coffer_app', c.oid, 'INSERT')
                OR has_table_privilege('coffer_app', c.oid, 'UPDATE')
                OR has_table_privilege('coffer_app', c.oid, 'DELETE')
              FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('v', 'm')
            """, r => (Name: r.GetString(0), Invoker: r.GetBoolean(1), Writable: r.GetBoolean(2)));

        Assert.NotEmpty(views);
        // Without security_invoker a view runs as its owner, the BYPASSRLS service role.
        AssertNone(views.Where(v => !v.Invoker).Select(v => v.Name), "views that read past RLS as their owner");
        AssertNone(views.Where(v => v.Writable).Select(v => v.Name), "views coffer_app can write through");
    }

    [Fact]
    public async Task Only_the_declared_functions_run_as_their_owner_and_each_pins_its_search_path()
    {
        var definers = await QueryAsync("""
            SELECT p.proname, coalesce(array_to_string(p.proconfig, ','), '')
              FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND p.prosecdef
            """, r => (Name: r.GetString(0), Config: r.GetString(1)));

        AssertNone(definers.Where(d => !SecurityDefiners.ContainsKey(d.Name)).Select(d => d.Name),
            "SECURITY DEFINER functions nobody declared; each runs past RLS");
        // A definer that resolves names through the caller's search_path runs whatever the
        // caller put first on it, as its owner.
        AssertNone(definers.Where(d => !d.Config.Contains("search_path=", StringComparison.Ordinal)).Select(d => d.Name),
            "SECURITY DEFINER functions without a fixed search_path");
    }

    [Fact]
    public async Task Nothing_created_later_is_granted_to_coffer_app_by_default()
    {
        // The default grant of migration 017, removed by 254: a new table is closed to
        // coffer_app until its migration grants what its policies serve.
        var defaults = await QueryAsync("""
            SELECT pg_get_userbyid(d.defaclrole) || ' ' || d.defaclobjtype::text
              FROM pg_default_acl d, aclexplode(d.defaclacl) a
             WHERE a.grantee = 'coffer_app'::regrole
            """, r => r.GetString(0));
        AssertNone(defaults, "default privileges still grant coffer_app");
    }

    [Fact]
    public async Task The_migration_journal_is_read_only_to_the_request_role()
    {
        // Deleting a journal row makes the runner apply that migration again. Reading it is
        // how the version endpoint and the snapshot paths learn the schema version.
        var privileges = await QueryAsync("""
            SELECT p.priv, has_table_privilege('coffer_app', 'public."__schema_migrations"', p.priv)
              FROM (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE')) p(priv)
            """, r => (Priv: r.GetString(0), Held: r.GetBoolean(1)));
        Assert.Equal(["SELECT"], privileges.Where(p => p.Held).Select(p => p.Priv));
    }
}
