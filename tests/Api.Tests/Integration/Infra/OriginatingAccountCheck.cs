using Npgsql;

using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The test-time check of the originating-account invariants: the one every test
/// that writes or seeds headers ends with.
/// </summary>
/// <remarks>
/// It runs the import check's own query (<see cref="OriginatingAccountInvariants"/>),
/// whose source brokerage is the database's definition, the one migration 263 fills
/// and asserts with, so the tests, the import and the upgrade judge a ledger alike.
/// It reads through the service connection: the source-brokerage function belongs to
/// the schema owner, not the request role.
/// </remarks>
internal static class OriginatingAccountCheck
{
    /// <summary>Every (header, clause) of the ledger that breaks an invariant.</summary>
    public static async Task<IReadOnlyList<string>> ViolationsAsync(string connectionString, Guid ledgerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT v.header_id, v.clause FROM ({OriginatingAccountInvariants.ViolationsSql}) v ORDER BY 2, 1",
            connection);
        command.Parameters.AddWithValue("LedgerId", ledgerId);
        var violations = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            violations.Add($"{reader.GetString(1)} {reader.GetGuid(0)}");
        return violations;
    }

    /// <summary>Fails the test, naming each header and the clause it breaks.</summary>
    public static async Task AssertHoldsAsync(string connectionString, Guid ledgerId)
    {
        var violations = await ViolationsAsync(connectionString, ledgerId);
        Assert.True(violations.Count == 0,
            "headers whose originating account breaks an invariant: " + string.Join(", ", violations));
    }

    public static Task AssertHoldsAsync(TestDatabase database, Guid ledgerId) =>
        AssertHoldsAsync(database.ServiceConnectionString, ledgerId);

    /// <summary>
    /// The subqueries in the check's plan that run once per row of another node, one
    /// line each; empty when the plan has none.
    /// </summary>
    /// <remarks>
    /// <para>The check runs over a whole ledger, so a subquery evaluated per header
    /// costs headers times its own cost; over a materialised set, that is the square of
    /// the ledger. Whether a subquery stays one is decided when the query is parsed, not
    /// by cost, so the plan's shape shows it at any size, where a timing shows it only
    /// on a ledger large enough to hurt; the stress seed's invariant check asserts this
    /// at every scale.</para>
    /// <para>Every <c>SubPlan</c> counts, and every <c>InitPlan</c> except a CTE's: the
    /// planner evaluates a CTE once, while an InitPlan under a per-row node is
    /// re-evaluated whenever the outer value it reads changes.</para>
    /// <para>It does not see a join planned as a nested loop whose inner side rescans
    /// an unindexed set, which costs the same. That plan is a cost-based choice the
    /// planner makes only when its row estimates are large and wrong, so a small ledger
    /// cannot show it, and flagging nested loops there would refuse cheap plans; the
    /// stress lane's <c>OriginatingAccountCheckCostTests</c> times the check at scale
    /// instead.</para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> PerRowSubplansAsync(
        string connectionString, Guid ledgerId, string sql = OriginatingAccountInvariants.ViolationsSql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"EXPLAIN (FORMAT JSON) {sql}", connection);
        command.Parameters.AddWithValue("LedgerId", ledgerId);
        var json = (string)(await command.ExecuteScalarAsync())!;

        using var plan = System.Text.Json.JsonDocument.Parse(json);
        var found = new List<string>();
        Walk(plan.RootElement[0].GetProperty("Plan"));
        return found;

        void Walk(System.Text.Json.JsonElement node)
        {
            var relationship = node.TryGetProperty("Parent Relationship", out var r) ? r.GetString() : null;
            var name = node.TryGetProperty("Subplan Name", out var n) ? n.GetString() ?? "" : "";
            if (relationship == "SubPlan"
                || (relationship == "InitPlan" && !name.StartsWith("CTE ", StringComparison.Ordinal)))
                found.Add($"{name}: {node.GetProperty("Node Type").GetString()}");
            if (node.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child);
        }
    }
}
