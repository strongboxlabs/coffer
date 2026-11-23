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
        new([ImportValidator.CheckResult.Pass("pre-existing")]);

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
        Assert.Null(check.Summary);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Fails_and_names_the_drops_when_a_step_skipped_transactions()
    {
        var steps = new List<ImportStepResult>
        {
            new("investment_transactions", 100, 396, 3, Skips:
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
        // The person importing gets the count; the wizard lists the transactions.
        Assert.Equal("3 transactions were not imported.", check.Summary);
    }

    [Fact]
    public void Count_without_reasons_fails()
    {
        // A transaction step that counts a drop it does not list still dropped it: the
        // list is what the wizard shows, so a count it cannot explain must fail the check.
        var steps = new List<ImportStepResult>
        {
            new("transactions", 10, 16, 2, Skips: null),
            new("investment_transactions", 5, 20, 2, Skips:
            [
                new SkippedTxn("txn-1", "UnknownSecurity", Ticker: "BAR", Shares: 1m),
            ]),
        };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        var check = report.Checks.Single(c => c.Name == "no-dropped-transactions");
        Assert.False(check.Passed);
        Assert.False(report.AllPassed);
        // Both steps' unlisted drops are counted with the listed one, and each step
        // that could not say why is named.
        Assert.Contains("4 transaction(s) dropped", check.Message);
        Assert.Contains("1× UnknownSecurity [BAR]", check.Message);
        Assert.Contains("2× without a reason [transactions]", check.Message);
        Assert.Contains("1× without a reason [investment_transactions]", check.Message);
        Assert.Equal("4 transactions were not imported.", check.Summary);
    }

    [Fact]
    public void Another_steps_count_is_not_a_dropped_transaction()
    {
        // The securities step counts the fiat currencies it passes over; they are not
        // transactions, and an import that skips them has lost nothing.
        var steps = new List<ImportStepResult>
        {
            new("securities", 12, 10, 2),
            new("transactions", 10, 20, 0, Skips: []),
        };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        Assert.True(report.Checks.Single(c => c.Name == "no-dropped-transactions").Passed);
    }

    [Fact]
    public void Treats_null_skips_list_as_no_drops()
    {
        var steps = new List<ImportStepResult> { new("accounts", 5, 5, 0, Skips: null) };

        var report = MoneydanceImportService.AppendNoDroppedTxnsCheck(EmptyReport(), steps);

        Assert.True(report.Checks.Single(c => c.Name == "no-dropped-transactions").Passed);
    }
}
