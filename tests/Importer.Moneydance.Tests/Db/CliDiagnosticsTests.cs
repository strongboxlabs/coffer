using System.IO;
using Dapper;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The CLI's two read-only diagnostics run end to end on the generated Demo's short
/// span, written to a temporary file as a user's export would be: <c>audit</c> reads
/// the export only, <c>reconcile</c> runs the mapping steps on a scratch ledger and
/// rolls it back.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class CliDiagnosticsTests : DatabaseTest, IDisposable
{
    private readonly string _exportFile;

    public CliDiagnosticsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _exportFile = Path.Combine(Path.GetTempPath(), $"coffer-demo-short-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(_exportFile, DemoTestExports.ShortBytes);
    }

    void IDisposable.Dispose()
    {
        try { File.Delete(_exportFile); }
        catch (IOException) { }
    }

    [Fact]
    public void Audit_reads_the_sample_and_reports()
    {
        var (exitCode, output) = Run("audit", _exportFile);

        Assert.Equal(0, exitCode);
        Assert.Contains("txns", output);
    }

    [Fact]
    public async Task Reconcile_reports_and_persists_nothing()
    {
        // 0 = nothing would be dropped. The generated Demo is lossless, so anything
        // else is a dropped row (1) or a run that never reached its report.
        var (exitCode, output) = Run("reconcile", _exportFile, "--db", Db.ConnectionString);

        Assert.True(exitCode == 0, $"reconcile exited {exitCode}:\n{output}");
        Assert.Contains("rolled back, nothing persisted", output);

        await using var conn = Db.OpenConnection();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ledgers WHERE name = '__reconcile_scratch__';"));
    }

    /// <summary>
    /// Run one command through the CLI's own command set. The commands write to the
    /// static console, so it is swapped for a capture and restored after.
    /// </summary>
    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var writer = new StringWriter();
        var previous = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        try
        {
            var app = new CommandApp();
            app.Configure(Program.Configure);
            var exitCode = app.Run(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }
}
