using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The stored memo-and-tags presentation for rows a test wrote: the import's own
/// in-transaction recompute for rows the importer wrote, and the upgrade's backfill for
/// rows written past every writer (raw SQL, a migration rerun), so the ledger ends in a
/// state the app can make.
/// </summary>
/// <remarks>Each method that writes an input of the rule raw ends with the backfill
/// (<c>StoredNotesTestSeedTests</c> holds every test under <c>tests/</c> to it), and every
/// DB-backed test ends with the stored-equals-computed check over its own database
/// (<see cref="DatabaseTest.DisposeAsync"/>). The importer's tests read only the columns
/// they assert, never the stored presentation, so settling it changes nothing they
/// compare.</remarks>
internal static class StoredNotesSeed
{
    /// <summary>The upgrade's backfill over the ledger, for rows written past every writer.</summary>
    public static Task BackfillNotesAsync(NpgsqlConnection connection, Guid ledgerId) =>
        Coffer.Api.Migrations.LegNotesBackfill.RunAsync(connection, ledgerId, CancellationToken.None);

    /// <summary>
    /// The import's own recompute (the hook the API's import runs before it commits), for a
    /// test that drives the import's steps or repositories itself rather than through
    /// <see cref="Pipeline.MoneydanceImportService.ImportAsync"/>.
    /// </summary>
    public static async Task ComputeImportedNotesAsync(NpgsqlConnection connection, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        await Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync(transaction, ledgerId, CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }
}
