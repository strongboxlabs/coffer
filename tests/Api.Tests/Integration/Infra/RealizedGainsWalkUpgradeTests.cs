using Npgsql;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Migration 217 rehearsed against an install that already exists.
/// </summary>
/// <remarks>
/// <para>
/// 217 changes the SHAPE of a function the application binds to: <c>realized_gains_walk</c>
/// went from six returned columns to nine. Every test that exercises the walk builds its
/// database from an empty one and runs every migration, so all of them see the nine-column
/// version and none of them can tell you what happens to a host that has the six-column one
/// already. That is the gap this file closes, and it is the gap
/// <see cref="UpgradeRehearsal"/> was built for.
/// </para>
/// <para>
/// <b>The failure mode is specific.</b> A <c>RETURNS TABLE</c> shape cannot be changed under
/// <c>CREATE OR REPLACE</c> — Postgres refuses — so 217 has to DROP and CREATE. A drop is the
/// one DDL that can take something else with it, and a re-create is the one that can leave two
/// overloads behind if an argument type drifts. Either outcome breaks the EF binding on
/// upgrade while leaving a fresh install perfectly healthy.
/// </para>
/// <para>
/// <b>What this does NOT cover, stated so it is not over-cited.</b> It proves the function
/// object survives the upgrade with the right shape and can be applied twice. It does not
/// prove the walk's arithmetic — the two consistency tests added alongside 217 do that, on
/// seeded disposals at head — and it does not prove the container upgrades, which is
/// the deployment drill in the maintainer tooling. Three different properties, three
/// checks; none of them is evidence for the other two.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RealizedGainsWalkUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public RealizedGainsWalkUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig217 = "217_realized_gains_walk_sees_long_term.sql";

    private static string NewWorkDir()
        => Directory.CreateTempSubdirectory("coffer-gains-walk-").FullName;

    /// <summary>
    /// The one <c>realized_gains_walk</c> in <c>public</c>, as Postgres renders its result
    /// type. Throws unless there is exactly one.
    /// </summary>
    /// <remarks>
    /// The count is half the assertion. DROP + CREATE leaves an ORPHANED OVERLOAD if the new
    /// argument list differs from the dropped one by so much as a type, and the symptom is not
    /// a migration failure — it is <c>function realized_gains_walk(uuid, uuid) is not unique</c>
    /// at runtime, on the first consistency check after the upgrade. Returning the row without
    /// counting would hide exactly that.
    /// </remarks>
    private static async Task<string> WalkResultShapeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_get_function_result(p.oid), pg_get_function_identity_arguments(p.oid) "
            + "FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'public' AND p.proname = 'realized_gains_walk' "
            + "ORDER BY 1", connection);

        var shapes = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                shapes.Add($"({reader.GetString(1)}) -> {reader.GetString(0)}");
        }

        Assert.True(
            shapes.Count == 1,
            $"expected exactly one realized_gains_walk in public, found {shapes.Count}: "
            + string.Join(" ||| ", shapes));

        return shapes[0];
    }

    /// <summary>
    /// The upgrade itself: a database carrying the six-column function gets the nine-column
    /// one, under an unchanged signature.
    /// </summary>
    /// <remarks>
    /// The pre-assertions are not ceremony. If staging ever stopped short in the wrong place —
    /// or ran everything — the post-assertions would still pass, and this test would certify an
    /// upgrade it never performed. Proving the long-term columns are ABSENT first is what makes
    /// their presence afterwards mean something.
    /// </remarks>
    [Fact]
    public async Task An_install_on_the_six_column_walk_gains_the_long_term_columns()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_gains_walk");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageBefore(Mig217, work));

        var before = await WalkResultShapeAsync(connectionString);
        Assert.Contains("realized_gain", before, StringComparison.Ordinal);
        Assert.DoesNotContain("proceeds_lt", before, StringComparison.Ordinal);
        Assert.DoesNotContain("cost_basis_sold_lt", before, StringComparison.Ordinal);
        Assert.DoesNotContain("realized_gain_lt", before, StringComparison.Ordinal);

        UpgradeRehearsal.Apply(connectionString, work, Mig217);

        var after = await WalkResultShapeAsync(connectionString);
        Assert.Contains("proceeds_lt", after, StringComparison.Ordinal);
        Assert.Contains("cost_basis_sold_lt", after, StringComparison.Ordinal);
        Assert.Contains("realized_gain_lt", after, StringComparison.Ordinal);

        // The signature must be untouched, or the EF binding that calls it by
        // (account, security) stops resolving even though the columns are all there.
        Assert.StartsWith("(p_account_id uuid, p_security_id uuid) ->", after, StringComparison.Ordinal);

        // Six short-term/shared columns plus three long-term ones. Counting guards the
        // case where a column is renamed rather than added: every Contains above would
        // still pass while the row type EF maps lost a member. Count over the RESULT
        // only — the argument list carries a comma of its own.
        var returned = after[(after.IndexOf("-> ", StringComparison.Ordinal) + 3)..];
        Assert.Equal(9, returned.Split(',').Length);
    }

    /// <summary>
    /// 217 applied twice against the database it just changed.
    /// </summary>
    /// <remarks>
    /// Not hypothetical for this one. <c>DROP FUNCTION IF EXISTS</c> then <c>CREATE FUNCTION</c>
    /// is re-runnable by construction, but only while the <c>IF EXISTS</c> and the plain
    /// <c>CREATE</c> stay in that pairing — swap the create for one without a preceding drop and
    /// the script becomes a one-shot that fails on the operator's next restore, long after the
    /// upgrade it was tested on.
    /// </remarks>
    [Fact]
    public async Task Applying_217_twice_leaves_one_function_and_no_error()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_gains_walk_rerun");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageBefore(Mig217, work));

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(connectionString, work, Mig217);

        var after = await WalkResultShapeAsync(connectionString);
        Assert.Contains("realized_gain_lt", after, StringComparison.Ordinal);
    }
}
