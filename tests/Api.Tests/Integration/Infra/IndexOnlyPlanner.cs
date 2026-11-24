using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Turns the planner's table scans off (<c>enable_seqscan</c>, <c>enable_bitmapscan</c>)
/// on every connection the context opens, so a plan taken on a few rows shows whether
/// the statement CAN reach its rows through an index: which index it uses, and which of
/// its bounds are index conditions rather than filters. That is a property of how the
/// statement is written, the same on every machine and at any table size. Whether the
/// planner then CHOOSES that index at scale is a property of the data's size and
/// statistics, and is checked in the stress lane.
/// </summary>
/// <remarks>
/// A statement no index can serve still plans a scan with them off (they price a scan
/// up, they do not forbid it), so a bound the index cannot use still shows. The settings
/// are the session's; the pool resets a connection before its next use, as the app's
/// own per-session user setting relies on.
/// </remarks>
internal sealed class IndexOnlyPlanner : DbConnectionInterceptor
{
    private const string Settings = "SET enable_seqscan = off; SET enable_bitmapscan = off";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Settings;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Settings;
        cmd.ExecuteNonQuery();
    }
}
