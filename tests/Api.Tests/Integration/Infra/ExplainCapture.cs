using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Db;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// While <see cref="Enabled"/>, runs <c>EXPLAIN (ANALYZE, BUFFERS)</c> for each query
/// the API sends that <see cref="Selects"/> picks, and keeps the plans: a diagnostic for
/// what a timed call costs in the database.
/// </summary>
/// <remarks>
/// <para>The plan is taken on the API's own connection, inside its transaction and with
/// its session settings, just before the real command runs, with the same parameters,
/// so the plan is the one the call gets: the role, the row-level security context and
/// the planner's view of the parameters are all the same.</para>
/// <para>Off, it does nothing, so a timed sample never pays for it. Its plans are a
/// diagnostic only; nothing judges them.</para>
/// </remarks>
internal sealed class ExplainCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<(string Sql, string Plan)> _plans = new();

    /// <summary>Which statements to explain; every query when unset.</summary>
    public Func<string, bool> Selects { get; init; } = _ => true;

    /// <summary>Explain the queries the API sends from now on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The plans taken since the last call, in the order the queries ran.</summary>
    public IReadOnlyList<(string Sql, string Plan)> Drain()
    {
        var plans = new List<(string, string)>();
        while (_plans.TryDequeue(out var plan)) plans.Add(plan);
        return plans;
    }

    /// <summary>Adds this capture to the API's database context.</summary>
    public void Register(IServiceCollection services) =>
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(this));

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Enabled && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && Selects(command.CommandText))
            _plans.Enqueue((command.CommandText, await ExplainAsync(command, cancellationToken).ConfigureAwait(false)));
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The options of the plan that runs the statement; replaced only to make that plan fail.</summary>
    internal string AnalyzeOptions { get; init; } = "ANALYZE, BUFFERS";

    private const string Savepoint = "explain_capture";

    /// <summary>
    /// The plan with the statement run (<see cref="AnalyzeOptions"/>), else the plan
    /// alone, headed by why the first one failed.
    /// </summary>
    /// <remarks>
    /// A statement slow enough to time out under <c>ANALYZE</c> is the one whose plan
    /// is needed most, and the plain plan does not run it. Inside a transaction the
    /// failed attempt is rolled back to a savepoint, so the API's own statement still
    /// runs as it would have.
    /// </remarks>
    private async Task<string> ExplainAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var transaction = command.Transaction;
        if (transaction is not null) await transaction.SaveAsync(Savepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var analyzed = await PlanAsync(command, $"EXPLAIN ({AnalyzeOptions}) ", cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.ReleaseAsync(Savepoint, cancellationToken).ConfigureAwait(false);
            return analyzed;
        }
        catch (DbException e) when (!cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null) await transaction.RollbackAsync(Savepoint, cancellationToken).ConfigureAwait(false);
            var plan = await PlanAsync(command, "EXPLAIN ", cancellationToken).ConfigureAwait(false);
            return $"(the plan with the statement run failed: {e.Message.ReplaceLineEndings(" ")}; the plan alone follows)"
                   + Environment.NewLine + plan;
        }
    }

    private static async Task<string> PlanAsync(DbCommand command, string prefix, CancellationToken cancellationToken)
    {
        await using var explain = command.Connection!.CreateCommand();
        explain.Transaction = command.Transaction;
        // The API's own limit, not the driver's default: ANALYZE runs the statement.
        explain.CommandTimeout = command.CommandTimeout;
        explain.CommandText = prefix + command.CommandText;
        foreach (DbParameter parameter in command.Parameters)
            explain.Parameters.Add(((ICloneable)parameter).Clone());

        var plan = new StringBuilder();
        await using var reader = await explain.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            plan.AppendLine(reader.GetString(0));
        return plan.ToString();
    }
}
