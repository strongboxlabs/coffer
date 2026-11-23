using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// Every step of a Demo job has a readable label: the import's, then the examples', in
/// one count. The hub shows "Step N of M — label" from them.
/// </summary>
public sealed class DemoProgressLabelsTests
{
    [Fact]
    public void A_Demo_job_counts_the_import_and_the_examples_as_one_run()
    {
        Assert.Equal(21, DemoProgressLabels.Total);
        Assert.Equal(MoneydanceImportService.PipelineStepCount + DemoSeedSteps.Count, DemoProgressLabels.Total);
        Assert.Equal(11, DemoSeedSteps.Count);
    }

    [Fact]
    public void Every_step_has_a_label_of_its_own()
    {
        var labels = Enumerable.Range(1, DemoProgressLabels.Total).Select(DemoProgressLabels.ForStep).ToList();

        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_labels_are_the_ones_the_hub_shows()
    {
        Assert.Equal("importing transactions", DemoProgressLabels.ForStep(4));
        Assert.Equal("adding budget targets", DemoProgressLabels.ForStep(14));
        Assert.Equal("importing securities", DemoProgressLabels.ForStep(1));
        Assert.Equal("finishing the import", DemoProgressLabels.ForStep(10));
        Assert.Equal("matching the imported accounts", DemoProgressLabels.ForStep(11));
        Assert.Equal("checking the ledger", DemoProgressLabels.ForStep(21));
    }

    [Fact]
    public void The_examples_steps_follow_the_import_in_number_order()
    {
        Assert.Equal(
            Enumerable.Range(MoneydanceImportService.PipelineStepCount + 1, DemoSeedSteps.Count).ToList(),
            DemoSeedSteps.All.Select(s => s.Number).ToList());
        Assert.Equal(DemoSeedSteps.Resolve, DemoSeedSteps.All[0].Key);
        Assert.Equal(DemoSeedSteps.Count, DemoSeedSteps.All.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Only_the_step_that_adds_nothing_has_no_missing_name()
    {
        Assert.Equal(
            [DemoSeedSteps.Resolve],
            DemoSeedSteps.All.Where(s => s.MissingName is null).Select(s => s.Key).ToList());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    public void A_number_outside_the_run_is_refused(int stepNumber) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoProgressLabels.ForStep(stepNumber));
}
