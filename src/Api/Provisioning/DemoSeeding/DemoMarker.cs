using System.Data.Common;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The Demo marker: the one <c>ledger_operations</c> row (family
/// <see cref="LedgerOperationsRepository.DemoMarkerFamily"/>, provider
/// <see cref="LedgerOperationsRepository.DemoMarkerProviderKey"/>) that records which of a
/// Demo's examples were added (ADR-0115 D3, D9).
/// </summary>
/// <remarks>
/// <para>It is opened as <c>running</c>, with empty details, inside the Demo import's own
/// transaction, so a Demo ledger never exists without it and an import that rolls back
/// leaves none. The examples phase claims it once, in one guarded write that only an
/// unclaimed marker takes, then records each step in its details as it goes, keyed by
/// <see cref="DemoSeedStep.Key"/> (<c>1</c> added, <c>0</c> not), and closes it once, as
/// <c>completed</c>, <c>partial</c> or <c>failed</c>. A marker still <c>running</c> when the
/// server starts belongs to a run the restart ended; the startup sweep closes it.</para>
/// <para>It is the activity log's record of the examples, not an import: it created no
/// transaction, so it cannot be undone as one.</para>
/// </remarks>
public static class DemoMarker
{
    /// <summary>The status of a marker whose examples were all added.</summary>
    public const string Completed = DemoExamplesMissing.Completed;

    /// <summary>The status while the examples are being added.</summary>
    public const string Running = DemoExamplesMissing.Running;

    /// <summary>The status of a marker with some examples added and some not.</summary>
    public const string Partial = "partial";

    /// <summary>The status of a marker none of whose examples could be added.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// Open the marker of <paramref name="ledgerId"/> on the import's
    /// <paramref name="transaction"/>, last, before it commits.
    /// </summary>
    /// <remarks>Built as the import's other last writes are: a context on the import's own
    /// connection, enlisted in its transaction, so the marker commits or rolls back with
    /// the ledger. The start time written here is the transaction's; the examples phase
    /// replaces it with its own when it claims the marker, so Activity lists the examples
    /// after the import they follow.</remarks>
    internal static async Task OpenOnAsync(
        DbTransaction transaction, Guid ledgerId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The import's transaction has no connection.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        db.LedgerOperations.Add(new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Family = LedgerOperationsRepository.DemoMarkerFamily,
            ProviderKey = LedgerOperationsRepository.DemoMarkerProviderKey,
            TriggeredVia = "manual",
            TriggeredByUserId = ownerUserId,
            Status = Running,
            DetailsJson = LedgerOperationsRepository.UnclaimedDemoMarkerDetails,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The details of a marker: one entry per step, in step order, <c>1</c> for a
    /// step in <paramref name="added"/> and <c>0</c> for every other.</summary>
    public static string Details(IReadOnlySet<string> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in DemoSeedSteps.All) entries[step.Key] = added.Contains(step.Key) ? 1 : 0;
        return JsonSerializer.Serialize(entries);
    }

    /// <summary>What the startup sweep writes on a marker a restart left running: the
    /// steps its details do not record as added, by the names the hub uses.</summary>
    public static string InterruptedMessage(string? detailsJson)
    {
        var missing = DemoExamplesMissing.From(Partial, detailsJson, jobIsLive: false) ?? [];
        return "Interrupted: the server restarted before these examples were added: "
            + string.Join(", ", missing) + ".";
    }
}
