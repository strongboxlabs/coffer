using System.Text.Json;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// Which of a Demo's examples were not added, as the ledger list reports them
/// (<c>demoExamplesMissing</c>, ADR-0115 D9), read from the ledger's Demo marker.
/// </summary>
/// <remarks>
/// <para>The marker is the Demo's <c>ledger_operations</c> row, provider
/// <see cref="Db.Repositories.LedgerOperationsRepository.DemoMarkerProviderKey"/>. Its
/// details hold one entry per examples step, keyed by <see cref="DemoSeedStep.Key"/>:
/// <c>1</c> when the step was added, <c>0</c> or absent when it was not.</para>
/// <para>Null means nothing to warn about: a ledger with no marker (any ledger that is
/// not a Demo), a completed marker, and a running marker whose job is still live (the
/// hub shows that build as preparing). Every other marker names the steps not recorded
/// as added: a marker closed as partial or failed, and a running one whose job is gone,
/// which the startup sweep would otherwise close. When the step that finds the imported
/// rows was not added, no later step could have been, so all are named.</para>
/// </remarks>
public static class DemoExamplesMissing
{
    /// <summary>The marker status of a Demo whose examples were all added.</summary>
    public const string Completed = "completed";

    /// <summary>The marker status while the examples are being added.</summary>
    public const string Running = "running";

    /// <summary>The names of the examples not added, or null when there is nothing to say.</summary>
    /// <param name="status">The marker's status, or null when the ledger has no marker.</param>
    /// <param name="detailsJson">The marker's details.</param>
    /// <param name="jobIsLive">Whether a Demo job is still running for this ledger.</param>
    public static IReadOnlyList<string>? From(string? status, string? detailsJson, bool jobIsLive)
    {
        if (status is null || status == Completed) return null;
        if (status == Running && jobIsLive) return null;

        var added = AddedSteps(detailsJson);
        var missing = added.Contains(DemoSeedSteps.Resolve)
            ? DemoSeedSteps.All.Where(s => s.MissingName is not null && !added.Contains(s.Key))
            : DemoSeedSteps.All.Where(s => s.MissingName is not null);
        var names = missing.Select(s => s.MissingName!).ToList();
        return names.Count == 0 ? null : names;
    }

    /// <summary>The keys recorded as added. Details that are not an object of numbers
    /// record nothing, so every step is named rather than none.</summary>
    private static HashSet<string> AddedSteps(string? detailsJson)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(detailsJson)) return added;
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return added;
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Number
                    && entry.Value.TryGetInt32(out var done) && done == 1)
                    added.Add(entry.Name);
        }
        catch (JsonException)
        {
            // Unreadable details record nothing as added.
        }
        return added;
    }
}
