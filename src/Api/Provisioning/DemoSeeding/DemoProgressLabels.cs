using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The readable label of each step of a Demo job, as the hub shows it: "Step N of M —
/// label", where N is the step in progress (ADR-0115 D9).
/// </summary>
/// <remarks>
/// The import's steps are labelled by their reported name
/// (<see cref="MoneydanceImportService.PipelineStepNames"/>), the examples' by
/// <see cref="DemoSeedSteps"/>. A step with no label is a missing entry here, not a
/// blank on the hub: <see cref="ForStep"/> throws, and a unit test walks every step.
/// </remarks>
public static class DemoProgressLabels
{
    private static readonly IReadOnlyDictionary<string, string> ImportLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["securities"] = "importing securities",
            ["accounts"] = "importing accounts",
            ["security_splits"] = "importing stock splits",
            ["transactions"] = "importing transactions",
            ["investment_transactions"] = "importing investment transactions",
            ["security_prices"] = "importing prices",
            ["trade-price-seed"] = "pricing trades",
            ["recurring_transactions"] = "importing reminders",
            ["balance-recompute"] = "recomputing balances",
            ["posting-count-recompute"] = "finishing the import",
        };

    /// <summary>Every step of a Demo job: the import's, then the examples'.</summary>
    public static int Total => MoneydanceImportService.PipelineStepCount + DemoSeedSteps.Count;

    /// <summary>The label of step <paramref name="stepNumber"/>, counted from 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is not a step of a Demo job.</exception>
    /// <exception cref="KeyNotFoundException">An import step has no label here.</exception>
    public static string ForStep(int stepNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stepNumber, Total);

        var importSteps = MoneydanceImportService.PipelineStepNames;
        if (stepNumber <= importSteps.Count)
        {
            var name = importSteps[stepNumber - 1];
            return ImportLabels.TryGetValue(name, out var label)
                ? label
                : throw new KeyNotFoundException($"the import step '{name}' has no Demo progress label");
        }

        return DemoSeedSteps.All[stepNumber - importSteps.Count - 1].Label;
    }
}
