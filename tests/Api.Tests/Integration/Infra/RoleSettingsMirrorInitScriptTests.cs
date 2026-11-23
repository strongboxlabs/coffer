using System.Text.RegularExpressions;

using Npgsql;
using Xunit.Abstractions;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The fixture's roles carry exactly the per-role settings production's
/// <c>db/init/00-init-roles.sh</c> gives them, and every test's database runs with them.
/// </summary>
/// <remarks>
/// A setting the script applies and the fixture leaves out, or one the fixture adds,
/// makes every test plan and time queries under a configuration no install runs. This
/// reads the script's <c>ALTER ROLE … SET</c> lines and requires the fixture's two roles
/// to carry those and nothing else, cluster-wide as the script sets them: a setting
/// scoped to one database would not reach the per-test clones.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class RoleSettingsMirrorInitScriptTests : DatabaseTest
{
    public RoleSettingsMirrorInitScriptTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [GeneratedRegex(@"^\s*ALTER\s+ROLE\s+(?<role>\w+)\s+SET\s+(?<name>\w+)\s*(?:=|TO)\s*(?<value>[^;\s]+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RoleSetting();

    [Fact]
    public async Task Every_role_setting_the_init_script_applies_is_in_effect_on_the_fixture()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "db", "init", "00-init-roles.sh"));
        var expected = RoleSetting().Matches(script)
            .Select(m => (Role: m.Groups["role"].Value, Name: m.Groups["name"].Value.ToLowerInvariant(),
                Value: m.Groups["value"].Value.Trim('\'').ToLowerInvariant()))
            .ToList();
        // Fail closed: a script whose lines this no longer recognises would check nothing.
        Assert.Contains(expected, e => e is { Role: "coffer_app", Name: "jit", Value: "off" });

        // Every setting of the two roles, cluster-wide or scoped to a database, so a
        // setting the fixture adds is caught as surely as one it leaves out.
        var applied = new List<string>();
        await using (var connection = new NpgsqlConnection(Db.SuperuserConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT r.rolname || ' in ' || coalesce(d.datname, '*') || ': ' || lower(c)
                  FROM pg_db_role_setting s
                  JOIN pg_roles r ON r.oid = s.setrole
                  LEFT JOIN pg_database d ON d.oid = s.setdatabase
                 CROSS JOIN LATERAL unnest(s.setconfig) c
                 WHERE r.rolname IN ('coffer_service', 'coffer_app')
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) applied.Add(reader.GetString(0));
        }
        var wanted = expected.Select(e => $"{e.Role} in *: {e.Name}={e.Value}").ToList();

        var missing = wanted.Except(applied, StringComparer.Ordinal).ToList();
        var extra = applied.Except(wanted, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            "the fixture's role settings differ from db/init's. Missing: [" + string.Join(", ", missing)
            + "]; not in db/init: [" + string.Join(", ", extra) + "]");

        // And they reach a session on this test's clone. Only settings of the request
        // role can be read this way (a session reports its own); the catalog check above
        // covers every role.
        foreach (var (_, name, value) in expected.Where(e => e.Role == "coffer_app"))
        {
            await using var app = new NpgsqlConnection(Db.AppConnectionString);
            await app.OpenAsync();
            await using var show = new NpgsqlCommand("SELECT current_setting(@n)", app);
            show.Parameters.AddWithValue("n", name);
            Assert.Equal(value, ((string)(await show.ExecuteScalarAsync())!).ToLowerInvariant());
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
