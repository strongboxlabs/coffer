using System.Diagnostics;
using System.Globalization;

using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The originating-account check (<see cref="OriginatingAccountInvariants.ViolationsSql"/>)
/// over a whole stress ledger: the cost every Moneydance import pays once at its end,
/// and the stress seed pays on every check.
/// </summary>
/// <remarks>
/// <para>The import runs the check inside its own transaction, on its import connection,
/// and runs it again when it fails, so every second it takes is a second the import
/// holds that transaction open; a test seed runs it under Npgsql's default command
/// timeout. The test runs the shared text over the service connection, then prints its
/// plan with <c>EXPLAIN (ANALYZE, BUFFERS)</c>.</para>
/// <para>The bound is a fixed <see cref="Bound"/>, a tenth of the test seed's timeout,
/// and deliberately not read off any connection: a check whose cost grows with the
/// ledger, not with its square, still fits that timeout on a ledger ten times this one,
/// and a plan that looks each header up in a materialised set fails it by orders of
/// magnitude. This is the only guard against a join the planner runs as a nested loop
/// over an unindexed set, which a small ledger never shows.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OriginatingAccountCheckCostTests : DatabaseTest
{
    /// <summary>The most the check may take over a default stress ledger.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public OriginatingAccountCheckCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    [Fact]
    public async Task The_check_answers_a_default_stress_ledger_within_its_bound()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Default;
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seed:  {seeded.Elapsed.TotalSeconds,7:F1}s  ({scale.TotalTxns:N0} txns)"));

        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();

        // The shared text, as the import and the test seed run it. The command may run
        // past the bound, so a slow plan reports its time instead of a timeout.
        await using var check = new NpgsqlCommand(
            $"SELECT count(*)::int FROM ({OriginatingAccountInvariants.ViolationsSql}) v", connection)
        {
            CommandTimeout = 600,
        };
        check.Parameters.AddWithValue("LedgerId", ledger.LedgerId);
        var watch = Stopwatch.StartNew();
        var violations = (int)(await check.ExecuteScalarAsync())!;
        watch.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"check: {watch.Elapsed.TotalSeconds,7:F2}s  (bound {Bound.TotalSeconds:F0}s)"));

        // The plan is printed whatever the time, so a slow one shows why.
        await using var explain = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS) {OriginatingAccountInvariants.ViolationsSql}", connection)
        {
            CommandTimeout = 600,
        };
        explain.Parameters.AddWithValue("LedgerId", ledger.LedgerId);
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                _output.WriteLine(reader.GetString(0));
        }

        Assert.Equal(0, violations);
        Assert.True(watch.Elapsed < Bound,
            string.Create(CultureInfo.InvariantCulture,
                $"the check took {watch.Elapsed.TotalSeconds:F2}s on {scale.TotalTxns:N0} headers; " +
                $"the bound is {Bound.TotalSeconds:F0}s"));
    }
}
