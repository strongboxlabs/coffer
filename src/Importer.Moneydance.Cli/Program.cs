using Coffer.Importer.Moneydance.Db;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance;

internal static class Program
{
    internal static int Main(string[] args)
    {
        // Register Dapper type handlers up front (DateOnly ↔ DATE) so every
        // repository call routes through them.
        DapperDateOnlyHandler.Register();

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("coffer-import-moneydance");
            config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");

            config.AddCommand<ImportCommand>("import")
                  .WithDescription("Import a Moneydance JSON export into the Coffer database.");

            config.AddCommand<AuditCommand>("audit")
                  .WithDescription("Count occurrences of known-lossy attributes in an MD export (per-leg tags, per-leg status, ol.orig-* trails, attachments, custom fields, FX). Companion to docs/moneydance-import-fidelity.md.");

            config.AddCommand<ReconcileCommand>("reconcile")
                  .WithDescription("Read-only: report which transactions the importer would DROP (skip) for an export, with the security + shares lost. Runs against an ephemeral rolled-back ledger; persists nothing.");

            config.AddCommand<PruneImportBatchCommand>("prune-batch")
                  .WithDescription("Surgically remove a transaction batch (ledger + import_source + created_at window) and recompute affected balances/holdings. Dry-run by default; --apply to execute. Undoes duplicates inserted by a re-import that re-keyed transactions.");
        });

        return app.Run(args);
    }
}
