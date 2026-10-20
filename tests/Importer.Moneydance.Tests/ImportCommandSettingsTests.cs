using Coffer.Importer.Moneydance;
using Spectre.Console;

namespace Coffer.Importer.Moneydance.Tests;

/// <summary>
/// Smoke-level tests for the CLI surface that PR 2.1 ships. These prove the
/// scaffolding compiles, the package references resolve, and the test runner
/// works end-to-end. Real import-pipeline tests land alongside the pipeline
/// (PR 2.2 onward).
/// </summary>
public sealed class ImportCommandSettingsTests
{
    [Fact]
    public void Validate_rejects_blank_export_file()
    {
        var settings = new ImportCommand.Settings { ExportFile = string.Empty };

        ValidationResult result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("export-file is required", result.Message);
    }

    [Fact]
    public void Validate_rejects_missing_file()
    {
        var settings = new ImportCommand.Settings { ExportFile = "C:/nonexistent/path/to/no-such-file.json" };

        ValidationResult result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Validate_accepts_existing_file_with_a_target_ledger()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"coffer-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "{}");
        try
        {
            var settings = new ImportCommand.Settings
            {
                ExportFile = tempPath,
                LedgerName = "Personal",
            };
            ValidationResult result = settings.Validate();
            Assert.True(result.Successful);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// ADR-0088: a target ledger is required. The seeded …0001 "Default" ledger
    /// this used to fall back to no longer exists, and silently choosing a
    /// destination for a bulk financial import would be wrong regardless — a
    /// mistyped flag would write into the wrong book.
    /// </summary>
    [Fact]
    public void Validate_rejects_a_file_with_no_target_ledger()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"coffer-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "{}");
        try
        {
            var settings = new ImportCommand.Settings { ExportFile = tempPath };
            ValidationResult result = settings.Validate();

            Assert.False(result.Successful);
            Assert.Contains("--ledger-name", result.Message);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
