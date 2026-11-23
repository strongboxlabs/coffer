using System.Text.Json;

using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Microsoft.Extensions.Options;

namespace Coffer.Api.Import;

/// <summary>
/// Starts and runs background Moneydance imports (ADR-0071 D2). Singleton.
/// The import writes across every ledger-scoped table and creates the ledger +
/// owner grant, so it connects as <c>coffer_service</c> (BYPASSRLS) — the same
/// role the migration runner uses — with the long command
/// timeout a large export's single COMMIT needs. The new ledger is owned by the importing
/// user, so RLS surfaces it to them immediately after.
/// </summary>
public sealed class ImportJobRunner
{
    private readonly ImportJobRegistry _registry;
    private readonly IMoneydanceImportService _service;
    private readonly string _serviceConnectionString;
    private readonly ServiceDbContextFactory _serviceDbFactory;
    private readonly ILogger<ImportJobRunner> _logger;
    private readonly TimeProvider _clock;
    private readonly DemoSeeder _seeder;

    public ImportJobRunner(
        ImportJobRegistry registry,
        IMoneydanceImportService service,
        IOptions<ApiOptions> options,
        ServiceDbContextFactory serviceDbFactory,
        ILogger<ImportJobRunner> logger,
        TimeProvider clock,
        DemoSeeder seeder)
    {
        _registry = registry;
        _service = service;
        _serviceConnectionString = options.Value.ServiceConnectionString;
        _serviceDbFactory = serviceDbFactory;
        _logger = logger;
        _clock = clock;
        _seeder = seeder;
    }

    /// <summary>
    /// Register + kick off an import into a brand-new ledger named
    /// <paramref name="ledgerName"/>, owned by <paramref name="userId"/>. Returns
    /// the job id, or null when the user already has an import running.
    /// </summary>
    /// <param name="export">
    /// TAKES OWNERSHIP. The export holds the parsed JSON document — several times the
    /// file's size, which for a large file is a lot of memory — and this method disposes it on every path: at the end of the
    /// background run, or immediately if the job is rejected. Callers must not
    /// dispose it themselves and must not use it after this returns.
    /// </param>
    /// <param name="memorySlot">TAKES OWNERSHIP, like <paramref name="export"/>: the
    /// <see cref="ImportMemoryGate"/> slot the document was parsed under, released with it
    /// (FU-092).</param>
    /// <param name="currencyCode">The new ledger's reporting currency (ADR-0114 D7), in
    /// <see cref="Quotes.CurrencyCodes"/>' stored form.</param>
    /// <param name="demo">Set for the generated Demo (ADR-0115 D3), null for a user's own
    /// export. A Demo job counts its examples phase in its steps, records its import as
    /// triggered by hand rather than by an upload, and runs the examples phase once the
    /// import has committed.</param>
    public Guid? Start(Guid userId, string ledgerName, MdExport export, IDisposable memorySlot,
        string currencyCode = DisplayCurrency.Fallback, DemoJobInput? demo = null)
    {
        var extraSteps = demo is null ? 0 : DemoSeedSteps.Count;
        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LedgerName = ledgerName,
            Kind = demo is null ? ImportJobKind.MoneydanceFile : ImportJobKind.Demo,
            ExtraSteps = extraSteps,
            CurrencyCode = currencyCode,
            Total = MoneydanceImportService.PipelineStepCount + extraSteps,
        };

        if (!_registry.TryStart(job))
        {
            // Nothing will ever run, so nothing else would release the document.
            // Rejection is a routine, retryable outcome ("an import is already
            // running"), so leaking here would leak once per impatient retry.
            export.Dispose();
            memorySlot.Dispose();
            return null;
        }

        // Fire-and-forget: the import outlives the HTTP request. Failures are
        // captured on the job, never thrown into the void.
        _ = Task.Run(() => RunAsync(job, export, memorySlot, demo));
        return job.Id;
    }

    /// <summary>A test's seam: runs once an import has committed, before its activity row
    /// is written, so a test can read what a client sees in that moment. The app sets
    /// none.</summary>
    internal Func<Guid, Task>? AfterImportCommitAsync { get; set; }

    /// <summary>The error a failed Demo job reports, which the hub shows as it is. It is
    /// true by construction: a Demo job fails only up to the import's single commit, so a
    /// failed one left no ledger behind.</summary>
    internal static string DemoFailedError(string ledgerName) =>
        $"Building {ledgerName} failed and nothing was kept. Try again, or check the server logs.";

    private async Task RunAsync(ImportJob job, MdExport export, IDisposable memorySlot, DemoJobInput? demo)
    {
        // The job's terminal state is published LAST, once nothing of the run is left:
        // the audit row committed, the connection returned, the document and its memory
        // slot released. "Succeeded" or "failed" is what the wizard polls for, and a
        // client that acts on it at once (opens Activity, starts the next import) must
        // find the run's work done and the slot free, so nothing may follow it.
        Action<ImportJob> terminal;
        Guid? committedLedgerId = null;
        var lossy = false;
        try
        {
            (terminal, committedLedgerId, lossy) = await ImportAsync(job, export).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import job {JobId} failed.", job.Id);
            var error = job.Kind == ImportJobKind.Demo
                ? DemoFailedError(job.LedgerName)
                : "The import failed. Check the server logs for details.";
            terminal = j =>
            {
                j.State = ImportJobState.Failed;
                j.Error = error;
            };
        }
        finally
        {
            // This is the sole owner of the parsed document from here on — the request
            // that parsed it has already returned. Items are views into it, so it has
            // to survive the whole import and be released on every exit path. The slot
            // is released after it, once the memory is free.
            export.Dispose();
            memorySlot.Dispose();
        }

        // A Demo's examples phase runs once its import has committed, with the export
        // and its memory slot already released, so the next export can be read while
        // it runs. The ledger id is already on the job, from the commit: the hub shows the
        // build as preparing and hides the ledger's own row until the job ends. Nothing in
        // this phase fails the job, because the ledger it works on is already kept:
        // every outcome lands on the Demo's marker, which the ledger list reads.
        if (demo is not null && committedLedgerId is { } ledgerId)
        {
            _registry.Update(job.Id, j =>
            {
                j.Phase = ImportJobPhase.Examples;
                j.LedgerId = ledgerId;
            });

            // The examples are written against the rows the manifest expects, so an
            // import that failed a check or left a row out gets none (fail closed).
            try
            {
                if (lossy)
                    await _seeder.CloseNotStartedAsync(ledgerId, DemoSeeder.NotStartedLossyImport).ConfigureAwait(false);
                else
                    await _seeder.SeedAsync(
                        new DemoSeedContext(ledgerId, job.UserId, demo.AsOf, demo.Manifest, demo.CardStatementCsv),
                        completed => _registry.Update(job.Id, j => j.Completed = completed)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The seeder records every outcome itself and does not throw; this only
                // keeps the terminal state below from depending on that.
                _logger.LogError(ex, "Import job {JobId}: the Demo's examples phase threw.", job.Id);
            }
        }

        _registry.Update(job.Id, terminal);
    }

    /// <summary>
    /// Run the import and record it, returning the update that marks the job succeeded
    /// for <see cref="RunAsync"/> to publish, the committed ledger's id, and whether the
    /// import was lossy: a failed check, or a transaction left out. The import's
    /// connection is closed by the time this returns.
    /// </summary>
    private async Task<(Action<ImportJob> Terminal, Guid LedgerId, bool Lossy)> ImportAsync(ImportJob job, MdExport export)
    {
        MoneydanceImportResult result;
        var factory = new DbConnectionFactory(_serviceConnectionString);
        await using (var connection = await factory.OpenAsync().ConfigureAwait(false))
        {
            // The import counts its own steps; a Demo job's total also holds the
            // examples phase's, which the import does not know about.
            var progress = new SyncProgress<ImportProgressUpdate>(u =>
                _registry.Update(job.Id, j =>
                {
                    j.Completed = u.Completed;
                    j.Total = u.Total + j.ExtraSteps;
                    j.Step = u.Detail;
                }));

            result = await _service.ImportAsync(
                connection,
                export,
                ledgerName: job.LedgerName,
                ownerUserId: job.UserId,
                progress,
                CancellationToken.None,
                // These run last inside the import's own transaction, so the ledger never
                // exists without them. The importer writes past the API's change tracker,
                // so the stored notes of the new ledger are computed by the one rule here;
                // a reminder imported with auto-commit turns the auto-post job on, as
                // saving one in the app does; and a Demo opens the marker its examples
                // phase records itself on, so a Demo never exists without one.
                beforeCommit: async (tx, ledgerId, ct) =>
                {
                    await LegNotes.RecomputeLedgerOnAsync(tx, ledgerId, ct).ConfigureAwait(false);
                    await ReminderAutoPostOnImport.EnsureOnAsync(
                        tx, ledgerId, job.UserId, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                    if (job.Kind == ImportJobKind.Demo)
                        await DemoMarker.OpenOnAsync(tx, ledgerId, job.UserId, ct).ConfigureAwait(false);
                },
                // The reporting currency the wizard chose (ADR-0114 D7), written with the
                // ledger row the import creates.
                currencyCode: job.CurrencyCode).ConfigureAwait(false);
        }

        // A Demo's ledger is listed from its commit on, and its marker reads as missing
        // every example until the examples phase records one, unless a running job holds
        // the ledger. So the job holds it from the commit: the hub keeps showing the build
        // and the list names nothing missing while the job is still running.
        if (job.Kind == ImportJobKind.Demo)
            _registry.Update(job.Id, j => j.LedgerId = result.LedgerId);
        if (AfterImportCommitAsync is { } afterCommit) await afterCommit(job.Id).ConfigureAwait(false);

        // The ledger is committed whatever the validator found, so a failed check
        // does not fail the job: the job carries each failed check and the
        // transactions not imported to the wizard's last step, which is the only
        // place they are shown.
        var warnings = ImportJobOutcome.Warnings(result.Validation);
        var (skipped, notListed) = ImportJobOutcome.Skipped(result.Steps);
        _logger.LogInformation(
            "Import job {JobId} succeeded: ledger {LedgerId} '{LedgerName}'.",
            job.Id, result.LedgerId, result.LedgerName);
        // The job status lives only in memory and is gone once the user starts
        // another import or the server restarts, so the failed checks are also
        // logged, by name.
        if (warnings.Count > 0)
            _logger.LogWarning(
                "Import job {JobId}: ledger {LedgerId} was imported with {FailedCount} failed "
                + "validation check(s): {FailedChecks}.",
                job.Id, result.LedgerId, warnings.Count, string.Join(", ", warnings.Select(w => w.Check)));

        await RecordImportOperationAsync(job, result).ConfigureAwait(false);

        Action<ImportJob> succeeded = j =>
        {
            j.State = ImportJobState.Succeeded;
            j.LedgerId = result.LedgerId;
            j.Completed = j.Total;
            j.Step = null;
            j.Warnings = warnings;
            j.SkippedTransactions = skipped;
            j.SkippedTransactionsNotListed = notListed;
        };
        return (succeeded, result.LedgerId, warnings.Count > 0 || skipped.Count + notListed > 0);
    }

    /// <summary>
    /// Write the durable <c>ledger_operations</c> audit row for a SUCCESSFUL import
    /// (ADR-0055/0086): family <c>ingest</c>, provider <c>moneydance</c> — a sibling
    /// to the OFX/QIF file imports already in that log, surfaced in Settings→Activity.
    /// A user's upload is recorded as <c>file-upload</c>; the Demo, which nobody
    /// uploaded, as <c>manual</c>, the hub action that started it. Only successes are recorded here: a failed bootstrap import rolls its whole
    /// ledger-creating transaction back, so there is no <c>ledger_id</c> to anchor an
    /// audit row to — the failure lives in the application log (logged above). Runs
    /// on the BYPASSRLS service role (the import already writes as that role) and
    /// never rethrows: the ledger is already committed, so a failed audit write must
    /// not surface as an import failure — it is logged instead.
    /// </summary>
    private async Task RecordImportOperationAsync(ImportJob job, MoneydanceImportResult result)
    {
        try
        {
            var details = new Dictionary<string, object?>
            {
                ["duration_seconds"] = (int)result.Elapsed.TotalSeconds,
            };
            foreach (var step in result.Steps)
                details[step.StepName] = step.Written;

            await using var db = _serviceDbFactory.Create();
            var operations = new LedgerOperationsRepository(db);
            await operations.RecordTerminalAsync(
                ledgerId: result.LedgerId,
                family: LedgerOperationsRepository.MoneydanceImportFamily,
                providerKey: LedgerOperationsRepository.MoneydanceImportProviderKey,
                triggeredVia: job.Kind == ImportJobKind.Demo ? "manual" : "file-upload",
                triggeredByUserId: job.UserId,
                status: "completed",
                errorMessage: null,
                detailsJson: JsonSerializer.Serialize(details),
                completedAt: _clock.GetUtcNow().UtcDateTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Import job {JobId}: ledger {LedgerId} was created, but its ledger_operations "
                + "audit row could not be written.", job.Id, result.LedgerId);
        }
    }

    /// <summary>IProgress that runs its callback synchronously on the caller's
    /// thread — the import step thread — so updates stay ordered (unlike
    /// <see cref="Progress{T}"/>, which would post to the thread pool).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
