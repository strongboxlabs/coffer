using Coffer.Importer.Moneydance;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance.Tests;

/// <summary>
/// The CLI offers the read-only diagnostics and nothing that writes to a ledger:
/// importing is the in-app wizard's job, so a ledger is only ever created by the
/// path the API owns.
/// </summary>
public sealed class CliCommandSetTests
{
    [Theory]
    [InlineData("import")]
    [InlineData("prune-batch")]
    public void A_command_that_writes_to_a_ledger_is_not_offered(string command)
    {
        Assert.NotEqual(0, RunHelp(command));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("reconcile")]
    public void The_read_only_diagnostics_are_offered(string command)
    {
        Assert.Equal(0, RunHelp(command));
    }

    /// <summary>
    /// Fail-closed: a new command class has to be added here on purpose, after
    /// checking it writes nothing.
    /// </summary>
    [Fact]
    public void The_assembly_holds_only_the_read_only_commands()
    {
        var commands = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { nameof(AuditCommand), nameof(ReconcileCommand) }, commands);
    }

    private static int RunHelp(string command)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            Program.Configure(config);
            // A console of its own, so help text never lands in another test's capture.
            config.ConfigureConsole(AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(new StringWriter()),
            }));
        });
        return app.Run(new[] { command, "--help" });
    }
}
