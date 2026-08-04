using System.ComponentModel;
using System.Diagnostics;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance;

/// <summary>
/// CLI entry point for `coffer-import-moneydance import`.
///
/// Today (PR 2.3): always parses the export and prints the per-obj_type
/// summary. When invoked without --dry-run AND a connection string is
/// available, runs the security import step (the first DB-touching mapper).
/// Subsequent mappers (accounts, transactions, holdings, ...) land in
/// PRs 2.4 onward and plug into the same pipeline.
/// </summary>
internal sealed class ImportCommand : AsyncCommand<ImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the Moneydance JSON export file.")]
        [CommandArgument(0, "<export-file>")]
        public string ExportFile { get; init; } = string.Empty;

        [Description("Connection string for the target Postgres database. " +
                     "Falls back to COFFER_DB_CONNECTION if not supplied.")]
        [CommandOption("--db <CONNECTION_STRING>")]
        public string? ConnectionString { get; init; }

        [Description("Parse and validate the export without writing to the database.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [Description("Import into an existing ledger by id (UUID). Mutually exclusive with --ledger-name.")]
        [CommandOption("--ledger-id <UUID>")]
        public Guid? LedgerId { get; init; }

        [Description("Import into a ledger by name. The ledger is created if it doesn't exist; the system user becomes its owner.")]
        [CommandOption("--ledger-name <NAME>")]
        public string? LedgerName { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ExportFile))
                return ValidationResult.Error("export-file is required");
            if (!File.Exists(ExportFile))
                return ValidationResult.Error($"export-file not found: {ExportFile}");
            if (LedgerId is not null && !string.IsNullOrWhiteSpace(LedgerName))
                return ValidationResult.Error("--ledger-id and --ledger-name are mutually exclusive");
            // One of them is now REQUIRED (ADR-0088). There used to be an implicit
            // fallback to the seeded …0001 "Default" ledger; that row is gone, and
            // silently picking a destination for a bulk financial import is the
            // wrong default anyway. Fail here rather than deep in the repository so
            // the operator gets a usage error, not a stack trace.
            if (LedgerId is null && string.IsNullOrWhiteSpace(LedgerName))
                return ValidationResult.Error(
                    "a target ledger is required: pass --ledger-name <NAME> to create " +
                    "(or reuse) one, or --ledger-id <UUID> for an existing ledger");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"Reading [cyan]{settings.ExportFile}[/] ...");

        MdExport export;
        try
        {
            export = MdItemReader.ReadFile(settings.ExportFile);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read export:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // ADR-0071 D2: the pipeline itself lives in MoneydanceImportService, shared
        // with the API. This command is the CLI adapter — parse, render, exit code.
        var service = new MoneydanceImportService();
        var preview = service.Preview(export);

        AnsiConsole.MarkupLine(
            $"Parsed [green]{preview.TotalItems:N0}[/] items in [yellow]{stopwatch.Elapsed.TotalSeconds:F2}s[/].");
        AnsiConsole.MarkupLine(
            $"Exporter: [grey]{Markup.Escape(preview.Exporter)}[/] " +
            $"build [grey]{preview.MoneydanceBuild}[/] " +
            $"date [grey]{preview.ExportDate}[/]");

        PrintObjTypeSummary(preview);

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[grey]--dry-run set; not contacting the database.[/]");
            return 0;
        }

        DbConnectionFactory factory;
        try
        {
            factory = DbConnectionFactory.FromCliOrEnvironment(settings.ConnectionString);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        MoneydanceImportResult result;
        try
        {
            await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            // The CLI grants ownership of a new ledger to the system user
            // (ADR-0071 D2); UI imports pass the importing human's id instead.
            result = await service.ImportAsync(
                connection, export, settings.LedgerId, settings.LedgerName,
                LedgerRow.SystemUserId, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportRefusedException ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Refusing import:[/] ledger [cyan]{Markup.Escape(ex.LedgerName)}[/] " +
                $"already has [yellow]{ex.ExistingTransactions:N0}[/] transaction(s). The Moneydance " +
                $"import seeds a fresh ledger only - create a new ledger " +
                $"([grey]--ledger-name[/]), or wipe this one first " +
                $"([grey]prune-batch[/] / Demo refresh).");
            return 4;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Database error:[/] {Markup.Escape(ex.Message)}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                AnsiConsole.MarkupLine($"[red]  caused by:[/] {Markup.Escape(inner.GetType().Name)}: {Markup.Escape(inner.Message)}");
            return 3;
        }

        AnsiConsole.MarkupLine(
            $"Ledger: [cyan]{Markup.Escape(result.LedgerName)}[/] [grey]({result.LedgerId})[/]");
        PrintStepSummary(result.Steps);
        PrintSkips(result.Steps);

        var validationReport = result.Validation;
        if (validationReport.AllPassed)
        {
            AnsiConsole.MarkupLine(
                $"[green]Validator: all {validationReport.Checks.Count} check(s) passed.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Validator: {validationReport.Failed} of {validationReport.Checks.Count} check(s) failed[/]");
            foreach (var c in validationReport.Checks.Where(c => !c.Passed))
                AnsiConsole.MarkupLine($"  [yellow]⚠ {c.Name}[/]: {Markup.Escape(c.Message ?? "(no detail)")}");
        }
        AnsiConsole.MarkupLine(
            $"[green]Import complete[/] in [yellow]{stopwatch.Elapsed.TotalSeconds:F2}s[/].");
        return validationReport is { AllPassed: false } ? 1 : 0;
    }

    private static void PrintObjTypeSummary(MoneydanceImportPreview preview)
    {
        var table = new Table()
            .AddColumn("obj_type")
            .AddColumn(new TableColumn("count").RightAligned());
        foreach (var row in preview.ObjTypeCounts)
            table.AddRow(Markup.Escape(row.ObjType), row.Count.ToString("N0"));
        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Itemise every dropped transaction — silent drops are data loss and
    /// must be visible. Prints a per-(step, reason, ticker) aggregate with
    /// total shares lost, then the individual rows.
    /// </summary>
    internal static void PrintSkips(IReadOnlyList<ImportStepResult> results)
    {
        var skips = results
            .Where(r => r.Skips is { Count: > 0 })
            .SelectMany(r => r.Skips!.Select(s => (Step: r.StepName, Skip: s)))
            .ToList();
        if (skips.Count == 0)
            return;

        AnsiConsole.MarkupLine(
            $"[red]⚠ {skips.Count:N0} transaction(s) DROPPED (not imported):[/]");

        var agg = new Table()
            .AddColumn("step")
            .AddColumn("reason")
            .AddColumn("ticker / security")
            .AddColumn(new TableColumn("count").RightAligned())
            .AddColumn(new TableColumn("shares lost").RightAligned());
        foreach (var g in skips
            .GroupBy(x => (x.Step, x.Skip.Reason, Sec: x.Skip.Ticker ?? x.Skip.Security ?? "(none)"))
            .OrderByDescending(g => g.Count()))
        {
            agg.AddRow(
                Markup.Escape(g.Key.Step),
                Markup.Escape(g.Key.Reason),
                Markup.Escape(g.Key.Sec),
                g.Count().ToString("N0"),
                g.Sum(x => x.Skip.Shares ?? 0m).ToString("N4"));
        }
        AnsiConsole.Write(agg);
    }

    internal static void PrintStepSummary(IReadOnlyList<ImportStepResult> results)
    {
        var table = new Table()
            .AddColumn("step")
            .AddColumn(new TableColumn("read").RightAligned())
            .AddColumn(new TableColumn("written").RightAligned())
            .AddColumn(new TableColumn("skipped").RightAligned());
        foreach (var result in results)
            table.AddRow(
                Markup.Escape(result.StepName),
                result.Read.ToString("N0"),
                result.Written.ToString("N0"),
                result.Skipped.ToString("N0"));
        AnsiConsole.Write(table);
    }
}
