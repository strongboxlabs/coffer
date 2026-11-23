namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// One step of the Demo's examples phase: what the seeding adds to the imported Demo
/// once the import has committed (ADR-0115 D3).
/// </summary>
/// <param name="Number">The step's number in the Demo job's progress, after the import's
/// own steps.</param>
/// <param name="Key">The step's key in the Demo marker's details.</param>
/// <param name="Label">What the hub shows while the step is in progress.</param>
/// <param name="MissingName">How the hub names the step when it was not added, or null for
/// a step that adds nothing of its own.</param>
public sealed record DemoSeedStep(int Number, string Key, string Label, string? MissingName);

/// <summary>
/// The examples phase of a Demo job, in the order it runs (ADR-0115 D3, D9). The step
/// numbers follow the import's own steps, so a Demo job counts both phases as one run.
/// </summary>
public static class DemoSeedSteps
{
    /// <summary>The key of the step that finds the imported rows every later step
    /// needs. It adds nothing itself; when it fails, every later step is missing.</summary>
    public const string Resolve = "resolve";

    private const int First = Coffer.Importer.Moneydance.Pipeline.MoneydanceImportService.PipelineStepCount + 1;

    /// <summary>Every step, by number.</summary>
    public static readonly IReadOnlyList<DemoSeedStep> All =
    [
        new(First + 0, Resolve, "matching the imported accounts", null),
        new(First + 1, "folders", "adding folders", "folders"),
        new(First + 2, "investments", "adding investment settings", "investment settings"),
        new(First + 3, "budgets", "adding budget targets", "budget targets"),
        new(First + 4, "register", "adding register examples", "register examples"),
        new(First + 5, "card-statement", "importing the card statement", "the card statement import"),
        new(First + 6, "reminders", "adding reminders", "reminders"),
        new(First + 7, "reports", "adding saved reports", "saved reports"),
        new(First + 8, "home", "arranging Home", "the Home layout"),
        new(First + 9, "snapshot", "taking a snapshot", "the snapshot"),
        new(First + 10, "check", "checking the ledger", "the consistency check"),
    ];

    /// <summary>How many steps the examples phase adds to a Demo job.</summary>
    public static int Count => All.Count;
}
