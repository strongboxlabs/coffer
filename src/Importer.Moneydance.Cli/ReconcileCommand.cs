using System.ComponentModel;
using System.Diagnostics;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance;

/// <summary>
/// CLI entry point for `coffer-import-moneydance reconcile`.
///
/// Read-only: runs the mapping-bearing import steps against an ephemeral
/// ledger inside a rolled-back transaction and reports which transactions the
/// current importer would DROP (skip), with the security + shares lost.
/// Nothing is persisted; no existing ledger is touched. Used to diagnose
/// import fidelity (e.g. the TDLM/TDLP share undercount) without a full
/// re-import.
/// </summary>
internal sealed class ReconcileCommand : AsyncCommand<ReconcileCommand.Settings>
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

        [Description("Comma-separated tickers to restrict the holdings snapshot + diff to (e.g. TDLM,TDLP).")]
        [CommandOption("--tickers <LIST>")]
        public string? Tickers { get; init; }

        [Description("Real ledger id (UUID) to diff against, per (ticker, date, quantity). " +
                     "Lists transactions the fresh import has that the real ledger is missing.")]
        [CommandOption("--compare-ledger <UUID>")]
        public Guid? CompareLedger { get; init; }

        public string[] OnlyTickers =>
            string.IsNullOrWhiteSpace(Tickers)
                ? []
                : Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ExportFile))
                return ValidationResult.Error("export-file is required");
            if (!File.Exists(ExportFile))
                return ValidationResult.Error($"export-file not found: {ExportFile}");
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

        ReconcileResult result;
        try
        {
            await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var service = new MoneydanceImportService();
            result = await service
                .ReconcileAsync(connection, export, LedgerRow.SystemUserId,
                    settings.CompareLedger, settings.OnlyTickers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Database error:[/] {Markup.Escape(ex.Message)}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                AnsiConsole.MarkupLine($"[red]  caused by:[/] {Markup.Escape(inner.GetType().Name)}: {Markup.Escape(inner.Message)}");
            return 3;
        }

        var steps = result.Steps;
        ImportCommand.PrintStepSummary(steps);
        ImportCommand.PrintSkips(steps);
        PrintHoldings(result.Holdings, settings.OnlyTickers);
        PrintDiffs(result.Diffs);

        var dropped = steps.Where(s => s.Skips is not null).Sum(s => s.Skips!.Count);
        AnsiConsole.MarkupLine(
            $"[grey]Reconcile (rolled back, nothing persisted) in {stopwatch.Elapsed.TotalSeconds:F2}s.[/]");
        return dropped > 0 ? 1 : 0;
    }

    /// <summary>
    /// Print the positions a fresh import would compute — the ground truth to
    /// diff against a real ledger. <paramref name="onlyTickers"/> restricts the
    /// view when supplied.
    /// </summary>
    private static void PrintHoldings(IReadOnlyList<ReconcileHolding> holdings, string[] onlyTickers)
    {
        var rows = onlyTickers.Length == 0
            ? holdings
            : holdings.Where(h => h.Ticker is not null
                && onlyTickers.Contains(h.Ticker, StringComparer.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0)
            return;

        AnsiConsole.MarkupLine("[cyan]Holdings a fresh import of this export would compute:[/]");
        var table = new Table()
            .AddColumn("ticker")
            .AddColumn("security")
            .AddColumn(new TableColumn("quantity").RightAligned())
            .AddColumn(new TableColumn("cost basis").RightAligned());
        foreach (var h in rows)
            table.AddRow(
                Markup.Escape(h.Ticker ?? "(none)"),
                Markup.Escape(h.Name),
                h.Quantity.ToString("N4"),
                h.CostBasis.ToString("N2"));
        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Print the per-(ticker, date, quantity) diff against the compared real
    /// ledger. fresh &gt; visible ⇒ shares missing from holdings; the hidden
    /// column distinguishes "imported then hidden" from "never imported".
    /// </summary>
    private static void PrintDiffs(IReadOnlyList<ReconcileDiffRow> diffs)
    {
        if (diffs.Count == 0)
            return;

        AnsiConsole.MarkupLine(
            $"[yellow]Transaction diff vs real ledger — {diffs.Count} mismatched (ticker,date,qty) bucket(s):[/]");
        var table = new Table()
            .AddColumn("ticker")
            .AddColumn("date")
            .AddColumn(new TableColumn("quantity").RightAligned())
            .AddColumn(new TableColumn("fresh").RightAligned())
            .AddColumn(new TableColumn("real vis").RightAligned())
            .AddColumn(new TableColumn("real hidden").RightAligned())
            .AddColumn("verdict");
        foreach (var d in diffs)
        {
            var missing = d.Fresh - d.RealVisible;
            string verdict =
                missing <= 0 ? "extra in real"
                : d.RealHidden >= missing ? "IMPORTED-THEN-HIDDEN"
                : d.RealHidden > 0 ? "PARTLY HIDDEN + MISSING"
                : "NEVER IMPORTED";
            table.AddRow(
                Markup.Escape(d.Ticker),
                d.Date.ToString("yyyy-MM-dd"),
                d.Quantity.ToString("N4"),
                d.Fresh.ToString(),
                d.RealVisible.ToString(),
                d.RealHidden.ToString(),
                Markup.Escape(verdict));
        }
        AnsiConsole.Write(table);
    }
}
