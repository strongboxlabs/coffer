using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The stored-equals-computed check over every ledger in a test database: each leg's
/// stored memo-and-tags presentation against what the rule computes from the leg's
/// current rows, a leg with none counting as a difference.
/// </summary>
/// <remarks>One source for both test assemblies (the importer's tests link this file),
/// so every test of both projects ends with the same check over its own database
/// (each project's <c>DatabaseTest</c> teardown). A test that seeded or changed an input
/// of the rule past every writer, or imported without the import's in-transaction
/// recompute, and did not end with the backfill fails here.</remarks>
internal static class StoredNotesDatabaseCheck
{
    /// <summary>Throws, naming the headers that differ (by payee, which the tests choose), unless every leg's stored presentation is what the rule computes.</summary>
    public static async Task AssertComputedAsync(AppDbContext db, string where)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Database.SetCommandTimeout(600);
        var headers = await db.TxnHeaders.AsNoTracking().OrderBy(h => h.Id).Select(h => h.Id).ToListAsync().ConfigureAwait(false);
        var drifted = await LegNotes.DiffAsync(db, headers, CancellationToken.None).ConfigureAwait(false);
        if (drifted.Count == 0) return;

        var ids = drifted.Keys.ToList();
        var rows = await db.TxnHeaders.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.LedgerId, h.Payee })
            .ToListAsync().ConfigureAwait(false);
        var report = rows.GroupBy(h => h.LedgerId)
            .Select(g => $"ledger {g.Key}: {g.Count()} header(s), payees [{string.Join(", ", g.Select(h => h.Payee ?? "(none)").Distinct().Order().Take(5))}]; "
                         + drifted[g.First().Id][0])
            .Order(StringComparer.Ordinal)
            .ToList();
        throw new InvalidOperationException(
            $"{where}: {drifted.Count} header(s) in {report.Count} ledger(s) hold a stored memo-and-tags presentation that is not "
            + "what the rule computes: a test seeded or changed their rows past every writer, or imported without the "
            + "import's in-transaction recompute, and did not end with the backfill "
            + "(SyntheticLedger.RecomputeAllNotesAsync / LegNotesBackfill.RunAsync / LegNotes.RecomputeLedgerOnAsync).\n"
            + string.Join("\n", report.Take(25)));
    }
}
