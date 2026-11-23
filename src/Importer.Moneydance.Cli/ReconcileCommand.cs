using System.ComponentModel;
using System.Diagnostics;
using Coffer.Domain.Money;
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
/// import fidelity (e.g. a share undercount on a pair of securities) without a full
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

        [Description("Comma-separated tickers to restrict the holdings snapshot + diff to (e.g. AAA,BBB).")]
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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
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

        // Adopt the parsed document so it is released on every later exit
        // path; MdExport owns the JsonDocument every MdItem is a view into.
        using var owned = export;

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
        PrintStepSummary(steps);
        PrintSkips(steps);
        PrintHoldings(result.Holdings, settings.OnlyTickers);
        PrintDiffs(result.Diffs);

        var dropped = steps.Where(s => s.Skips is not null).Sum(s => s.Skips!.Count);
        AnsiConsole.MarkupLine(
            $"[grey]Reconcile (rolled back, nothing persisted) in {stopwatch.Elapsed.TotalSeconds:F2}s.[/]");
        return dropped > 0 ? 1 : 0;
    }

    private static void PrintStepSummary(IReadOnlyList<ImportStepResult> results)
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

    /// <summary>
    /// Itemise every transaction the import would drop — silent drops are data
    /// loss and must be visible. Prints a per-(step, reason, ticker) aggregate
    /// with total shares lost.
    /// </summary>
    private static void PrintSkips(IReadOnlyList<ImportStepResult> results)
    {
        var skips = results
            .Where(r => r.Skips is { Count: > 0 })
            .SelectMany(r => r.Skips!.Select(s => (Step: r.StepName, Skip: s)))
            .ToList();
        if (skips.Count == 0)
            return;

        AnsiConsole.MarkupLine(
            $"[red]⚠ {skips.Count:N0} transaction(s) would be DROPPED (not imported):[/]");

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
                // Money at its holding's currency's places (ADR-0114 D15).
                $"{h.CostBasis.ToString("N" + CurrencyMinorUnits.PlacesOf(h.CurrencyCode))} {h.CurrencyCode}");
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
