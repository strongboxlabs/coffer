using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Pipeline;

/// <summary>
/// The importer must never drop a transaction silently (data loss). A lossy
/// import is surfaced as a FAILED "no-dropped-transactions" validation check,
/// built from each step's <see cref="ImportStepResult.Skips"/>, so both the CLI
/// and the UI import wizard (which render the report) show it.
/// </summary>
public sealed class NoDroppedTxnsCheckTests
{
    private static ImportValidator.ValidationReport EmptyReport() =>
        new([new ImportValidator.CheckResult("pre-existing", true, null)]);

    [Fact]
    public void Passes_when_no_step_dropped_anything()
    {
        var steps = new List<ImportStepResult>
        {
            new("securities", 10, 10, 0),
            new("investment_transactions", 100, 400, 0, Skips: []),
        };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        var check = report.Checks.Single(c => c.Name == "no-dropped-transactions");
        Assert.True(check.Passed);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Fails_and_names_the_drops_when_a_step_skipped_transactions()
    {
        var steps = new List<ImportStepResult>
        {
            new("investment_transactions", 100, 396, 2, Skips:
            [
                new SkippedTxn("txn-1", "UnknownShape", Security: "Foo Fund", Ticker: "FOO", Shares: 0m),
                new SkippedTxn("txn-2", "UnknownShape", Security: "Foo Fund", Ticker: "FOO", Shares: 0m),
                new SkippedTxn("txn-3", "UnknownSecurity", Ticker: "BAR", Shares: 12.5m),
            ]),
        };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        var check = report.Checks.Single(c => c.Name == "no-dropped-transactions");
        Assert.False(check.Passed);
        Assert.False(report.AllPassed);
        Assert.Contains("3 transaction(s) dropped", check.Message);
        // Aggregated by (reason, ticker): 2× the FOO/UnknownShape, 1× BAR/UnknownSecurity.
        Assert.Contains("2× UnknownShape [FOO]", check.Message);
        Assert.Contains("1× UnknownSecurity [BAR]", check.Message);
    }

    [Fact]
    public void Treats_null_skips_list_as_no_drops()
    {
        var steps = new List<ImportStepResult> { new("accounts", 5, 5, 0, Skips: null) };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        Assert.True(report.Checks.Single(c => c.Name == "no-dropped-transactions").Passed);
    }
}
