using Npgsql;
using NpgsqlTypes;

using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// One rule for an investment event's memos (the event rule, ADR-0108 D11), on both sides of the upgrade: the
/// importer's <see cref="AnnotationText.JoinDistinctMemos"/> and migration 264's
/// <c>mig264_join_distinct</c>, which Steps B and D join an event's memos with, give the
/// same text for the same parts.
/// </summary>
/// <remarks>
/// <para>The SQL is read from the migration file as it ships, not restated: 264 drops its
/// helpers at its end, so the three it takes (<c>mig264_trim</c>, <c>mig264_norm</c>,
/// <c>mig264_join_distinct</c>) are created inside a transaction that is rolled back, and
/// no function is left in the schema the catalog tests read.</para>
/// <para>Precedent: <c>TagNameKeyTests</c> holds the SQL key to .NET's trim over every
/// UTF-16 code unit; the white-space case here does the same for the memo trim.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MemoJoinDistinctParityTests : DatabaseTest
{
    public MemoJoinDistinctParityTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Migration = "264_annotation_data.sql";
    private static readonly string[] Functions = ["mig264_trim", "mig264_norm", "mig264_join_distinct"];

    public static TheoryData<string, string?[], string?> Cases => new()
    {
        { "a repeated part is kept once, where it first appears", ["a", "b", "a"], "a · b" },
        { "order is kept", ["b", "a", "b", "c", "a"], "b · a · c" },
        { "case is not folded", ["Trip", "trip", "TRIP", "trip"], "Trip · trip · TRIP" },
        { "parts equal after trimming are one", [" a ", "\ta\n", "\u00A0a\u3000", "a\u0085", "\u2028a", "a"], "a" },
        { "inner white space is part of the text", ["two  words", "two words", "two  words"], "two  words · two words" },
        { "a zero-width space is not white space", ["\u200Ba", "a"], "\u200Ba · a" },
        { "blank and null parts are dropped", [null, "", "   ", "a", null, "\u3000", "b", "\t"], "a · b" },
        { "a single part", ["a"], "a" },
        { "a single part is trimmed", ["  a\t"], "a" },
        { "every part blank", [null, "", " \t", "\u00A0", "\u3000"], null },
        { "no parts", [], null },
        { "a joined part is compared whole, never split", ["a · b", "a", "b", "a · b"], "a · b · a · b" },
        { "a part equal to a joined one is dropped", ["a", "b", "a · b", " a · b "], "a · b · a · b" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_importer_and_the_upgrade_join_an_events_memos_alike(string name, string?[] parts, string? expected)
    {
        var sql = await SqlJoinAsync([parts]);

        Assert.Equal(expected, AnnotationText.JoinDistinctMemos(parts));
        Assert.True(sql[0] == expected, $"{name}: mig264_join_distinct gave {Show(sql[0])}, expected {Show(expected)}");
    }

    [Fact]
    public async Task Both_trim_exactly_dotnet_white_space()
    {
        // Every UTF-16 code unit a text value can hold (no NUL, no lone surrogate), each
        // around a part that follows an equal untrimmed one: a white-space unit makes the
        // two one part, any other unit keeps both.
        var units = Enumerable.Range(1, 0xFFFF)
            .Where(c => c < 0xD800 || c > 0xDFFF)
            .Select(c => (char)c)
            .ToArray();
        var inputs = units.Select(c => new string?[] { "x", $"{c}x{c}" }).ToArray();

        var sql = await SqlJoinAsync(inputs);

        Assert.Equal(units.Length, sql.Count);
        var disagreements = units
            .Select((c, i) => (c, sql: sql[i], dotnet: AnnotationText.JoinDistinctMemos(inputs[i])))
            .Where(x => x.sql != x.dotnet)
            .Select(x => $"U+{(int)x.c:X4}: SQL {Show(x.sql)}, .NET {Show(x.dotnet)}")
            .ToList();
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements.Take(20)));
        // Not vacuous: the white-space units collapse to one part, and only they do.
        Assert.Equal(units.Count(char.IsWhiteSpace), sql.Count(s => s == "x"));
        Assert.True(sql.Count(s => s == "x") > 20);
    }

    /// <summary><c>mig264_join_distinct</c> of each input, in order, as 264 defines it.</summary>
    private async Task<List<string?>> SqlJoinAsync(IReadOnlyList<string?[]> inputs)
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var definition in Definitions())
        {
            await using var create = new NpgsqlCommand(definition, connection, tx);
            await create.ExecuteNonQueryAsync();
        }

        // One row per input: its parts as a JSON array, so an input of no parts and a
        // part that is NULL both survive the trip.
        await using var command = new NpgsqlCommand("""
            SELECT mig264_join_distinct(ARRAY(SELECT e.v FROM jsonb_array_elements_text(i.parts) WITH ORDINALITY AS e(v, o)
                                               ORDER BY e.o))
              FROM unnest(@inputs) WITH ORDINALITY AS i(parts, ord)
             ORDER BY i.ord
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter("inputs", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            Value = inputs.Select(p => System.Text.Json.JsonSerializer.Serialize(p)).ToArray(),
        });
        var joined = new List<string?>(inputs.Count);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                joined.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        await tx.RollbackAsync();
        return joined;
    }

    /// <summary>Each helper's <c>CREATE OR REPLACE FUNCTION</c> statement, read from the shipped file.</summary>
    private static IEnumerable<string> Definitions()
    {
        var script = File.ReadAllText(Path.Combine(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), Migration));
        foreach (var function in Functions)
        {
            var head = $"CREATE OR REPLACE FUNCTION {function}(";
            var start = script.IndexOf(head, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{Migration} no longer defines {function}");
            Assert.True(script.IndexOf(head, start + head.Length, StringComparison.Ordinal) < 0,
                $"{Migration} defines {function} more than once");
            const string end = "$fn$;";
            var stop = script.IndexOf(end, start, StringComparison.Ordinal);
            Assert.True(stop > start, $"{function} in {Migration} no longer has the shape this test reads");
            yield return script[start..(stop + end.Length)];
        }
    }

    private static string Show(string? value) => value is null ? "null" : $"'{value}'";
}
