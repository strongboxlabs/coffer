using System.ComponentModel;
using System.Diagnostics;

using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance;

/// <summary>
/// CLI entry point for <c>coffer-import-moneydance prune-batch</c> — surgically
/// removes a transaction batch identified by (ledger, import_source,
/// created_at window) and re-derives the balances + holdings the removal
/// affects.
/// </summary>
/// <remarks>
/// <para>Why this exists: a Moneydance re-import keys each transaction by MD's
/// <c>txn.Id</c> (see <see cref="Mappers.TransactionMapper"/> →
/// <c>ExternalId: txn.Id</c>). That id is NOT stable across exports — MD
/// reassigns it when it merges a register row with an online download. When the
/// id changes, the importer's <c>ON CONFLICT (ledger_id, external_id)</c> upsert
/// misses and INSERTs a fresh row with default flags
/// (<c>is_hidden=false</c>, <c>is_merged_into=null</c>), resurrecting
/// transactions the user had hidden or merged. This command undoes such a
/// batch. See ADR-0052.</para>
///
/// <para><b>Dry-run by default.</b> It prints exactly which headers it would
/// delete, classifies each by whether a pre-batch row still covers the same
/// (account, date, amount) — a "twin" the delete would fall back to — and
/// flags any row WITHOUT a twin (deleting it would lose a transaction).
/// <c>--apply</c> performs the delete + recompute in a single transaction.</para>
///
/// <para>The data logic lives in <see cref="PruneImportBatch"/> so it is
/// testable against a migrated schema; this command only parses options and
/// renders.</para>
/// </remarks>
internal sealed class PruneImportBatchCommand : AsyncCommand<PruneImportBatchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Connection string for the target Postgres database. " +
                     "Falls back to COFFER_DB_CONNECTION if not supplied.")]
        [CommandOption("--db <CONNECTION_STRING>")]
        public string? ConnectionString { get; init; }

        [Description("Ledger id (UUID) to prune within.")]
        [CommandOption("--ledger-id <UUID>")]
        public Guid LedgerId { get; init; }

        [Description("import_source marker of the batch to prune.")]
        [CommandOption("--import-source <SOURCE>")]
        public string ImportSource { get; init; } = "moneydance_export";

        [Description("Delete headers created at or after this instant (inclusive). ISO-8601.")]
        [CommandOption("--created-from <TIMESTAMP>")]
        public DateTimeOffset CreatedFrom { get; init; }

        [Description("Delete headers created strictly before this instant. ISO-8601.")]
        [CommandOption("--created-to <TIMESTAMP>")]
        public DateTimeOffset CreatedTo { get; init; }

        [Description("Also delete recurring-template headers in the window (default: keep them).")]
        [CommandOption("--include-templates")]
        public bool IncludeTemplates { get; init; }

        [Description("Perform the deletion + recompute. Without this flag the command is a dry-run.")]
        [CommandOption("--apply")]
        public bool Apply { get; init; }

        public override ValidationResult Validate()
        {
            if (LedgerId == Guid.Empty)
                return ValidationResult.Error("--ledger-id is required");
            if (CreatedFrom == default || CreatedTo == default)
                return ValidationResult.Error("--created-from and --created-to are required");
            if (CreatedTo <= CreatedFrom)
                return ValidationResult.Error("--created-to must be strictly after --created-from");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

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

        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var plan = await PruneImportBatch.PlanAsync(
            connection, settings.LedgerId, settings.ImportSource,
            settings.CreatedFrom, settings.CreatedTo, settings.IncludeTemplates,
            transaction: null, cancellationToken).ConfigureAwait(false);

        if (plan.Targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No headers match the given batch selector. Nothing to do.[/]");
            return 0;
        }

        PrintReport(plan, settings);

        if (!settings.Apply)
        {
            AnsiConsole.MarkupLine(
                "\n[grey]Dry-run only — no rows written. Re-run with [bold]--apply[/] to execute.[/]");
            return 0;
        }

        await using (var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var deleted = await PruneImportBatch
                .ApplyAsync(connection, transaction, settings.LedgerId, plan, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                $"\n[green]Applied[/]: deleted [bold]{deleted}[/] header(s); recomputed " +
                $"[bold]{plan.HoldingPairs.Count}[/] holding pair(s) and [bold]{plan.AffectedAccounts.Count}[/] " +
                $"account balance(s) in [yellow]{stopwatch.Elapsed.TotalSeconds:F2}s[/].");
        }

        return 0;
    }

    private static void PrintReport(PrunePlan plan, Settings settings)
    {
        AnsiConsole.MarkupLine(
            $"Batch: ledger [cyan]{settings.LedgerId}[/], import_source " +
            $"[cyan]{Markup.Escape(settings.ImportSource)}[/], created " +
            $"[grey]{settings.CreatedFrom:o}[/] .. [grey]{settings.CreatedTo:o}[/]");

        var table = new Table().Border(TableBorder.Minimal)
            .AddColumn("posted").AddColumn("payee").AddColumn("origin")
            .AddColumn("tmpl?").AddColumn("twin?");
        foreach (var t in plan.Targets)
        {
            var payee = t.Payee is null ? "[grey](none)[/]" : Markup.Escape(Trunc(t.Payee, 40));
            table.AddRow(
                t.PostedAt.ToString("yyyy-MM-dd"),
                payee,
                Markup.Escape(t.Origin),
                t.IsRecurringTemplate ? "[grey]yes[/]" : "no",
                t.IsRecurringTemplate ? "[grey]n/a[/]" : (t.HasTwin ? "[green]yes[/]" : "[red]NO[/]"));
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"\n[bold]{plan.Targets.Count}[/] header(s) selected " +
            $"({plan.RegisterRowCount} register row(s), {plan.TemplateCount} template(s)).");
        AnsiConsole.MarkupLine($"  register rows with a pre-batch twin: [green]{plan.TwinCount}[/]");
        AnsiConsole.MarkupLine(
            $"  register rows WITHOUT a twin:        " +
            (plan.NoTwin.Count == 0 ? "[green]0[/]" : $"[red]{plan.NoTwin.Count}[/]  <- a delete would LOSE these"));
        if (plan.NoTwin.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]  Review before applying — no pre-batch row covers these:[/]");
            foreach (var t in plan.NoTwin)
                AnsiConsole.MarkupLine(
                    $"    [red]{t.PostedAt:yyyy-MM-dd}[/] {Markup.Escape(Trunc(t.Payee ?? "(none)", 50))} ({Markup.Escape(t.Origin)})");
        }
        AnsiConsole.MarkupLine(
            $"\nRecompute on apply: [bold]{plan.AffectedAccounts.Count}[/] account balance(s), " +
            $"[bold]{plan.HoldingPairs.Count}[/] holding (account,security) pair(s).");
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
