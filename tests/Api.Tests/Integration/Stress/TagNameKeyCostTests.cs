using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Db;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// <c>tag_name_key</c> and <c>tag_name_keys</c> (migration 263) over a whole ledger's tags:
/// keying every tag at once, and matching a list of names as long as the vocabulary.
/// </summary>
/// <remarks>
/// <para>The key has no index until 264, so every match computes it once per tag of the
/// ledger. That is linear while the planner hashes the tags; it becomes names times
/// tags if it loops over them per name instead. The second case shows which plan the
/// writers' LINQ shape gets, under a custom plan and under the generic plan a
/// prepared statement falls back to, where the list's length is unknown.</para>
/// <para>The figures are the output; nothing is asserted about time.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class TagNameKeyCostTests : DatabaseTest
{
    private const int Tags = 20_000;

    private readonly ITestOutputHelper _output;

    public TagNameKeyCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    [GeneratedRegex(@"^-- @(\w+)=", RegexOptions.Multiline)]
    private static partial Regex ParameterLine();

    [Fact]
    public async Task Keying_a_whole_vocabulary_prints_its_cost()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using (var seed = new NpgsqlConnection(Db.ServiceConnectionString))
        {
            await seed.OpenAsync();
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO tags (id, ledger_id, name, created_at)
                SELECT gen_random_uuid(), @l,
                       CASE WHEN n % 3 = 0 THEN '  Tag ' || n || E' ' ELSE 'Tag ' || n END,
                       TIMESTAMPTZ '2026-01-01 00:00:00+00'
                  FROM generate_series(1, {Tags}) n;
                ANALYZE tags;
                """, seed);
            insert.Parameters.AddWithValue("l", ledger.LedgerId);
            await insert.ExecuteNonQueryAsync();
        }

        // Half the names match a tag under the key, half are new.
        var names = Enumerable.Range(1, Tags)
            .Select(n => n % 2 == 0 ? $"TAG {n}" : $"new tag {n}")
            .ToArray();

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var query =
            from k in db.TagNameKeys(names)
            from t in db.Tags
                .Where(t => t.LedgerId == ledger.LedgerId && AppDbContext.TagNameKey(t.Name) == k.Key)
                .DefaultIfEmpty()
            select new { k.Name, k.Key, TagId = (Guid?)t!.Id };
        var sql = query.ToQueryString();
        var body = string.Join('\n', sql.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));
        var parameters = ParameterLine().Matches(sql).Select(m => m.Groups[1].Value).ToList();
        _output.WriteLine(body);

        var linq = Stopwatch.StartNew();
        var rows = await query.ToListAsync();
        linq.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"tag-key: match {names.Length:N0} names against {Tags:N0} tags (LINQ, custom plan): {linq.Elapsed.TotalMilliseconds:F0} ms, {rows.Count(r => r.TagId is not null):N0} matched"));

        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();

        await PrintAsync(connection, "key every tag", """
            EXPLAIN (ANALYZE, BUFFERS)
            SELECT tag_name_key(t.name), count(*) FROM tags t WHERE t.ledger_id = @l GROUP BY 1
            """, c => c.Parameters.AddWithValue("l", ledger.LedgerId));

        void Bind(NpgsqlCommand c)
        {
            foreach (var p in parameters)
            {
                if (p.Contains("names", StringComparison.OrdinalIgnoreCase)) c.Parameters.AddWithValue(p, names);
                else c.Parameters.AddWithValue(p, ledger.LedgerId);
            }
        }

        await PrintAsync(connection, "match, custom plan", "EXPLAIN (ANALYZE, BUFFERS) " + body, Bind);

        // A prepared statement's generic plan does not know the list's length. Its plan is
        // read through auto_explain, which only the superuser may load.
        _output.WriteLine("----- match, generic plan");
        await using var su = new NpgsqlConnection(Db.SuperuserConnectionString);
        su.Notice += (_, e) => _output.WriteLine(e.Notice.MessageText);
        await su.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_analyze = on;
            SET auto_explain.log_level = notice;
            SET auto_explain.log_parameter_max_length = 0;
            SET plan_cache_mode = force_generic_plan;
            """, su))
            await setup.ExecuteNonQueryAsync();
        await using var generic = new NpgsqlCommand(body, su) { CommandTimeout = 600 };
        Bind(generic);
        await generic.PrepareAsync();
        var watch = Stopwatch.StartNew();
        var n = 0;
        await using (var reader = await generic.ExecuteReaderAsync())
            while (await reader.ReadAsync()) n++;
        watch.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"tag-key: match, generic plan: {watch.Elapsed.TotalMilliseconds:F0} ms, {n:N0} rows"));
    }

    private async Task PrintAsync(NpgsqlConnection connection, string title, string sql, Action<NpgsqlCommand> bind)
    {
        _output.WriteLine($"----- {title}");
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        bind(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            _output.WriteLine(reader.GetString(0));
    }
}
