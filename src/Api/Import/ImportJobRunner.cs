using System.Text.Json;

using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Microsoft.Extensions.Options;

namespace Coffer.Api.Import;

/// <summary>
/// Starts and runs background Moneydance imports (ADR-0071 D2). Singleton.
/// The import writes across every ledger-scoped table and creates the ledger +
/// owner grant, so it connects as <c>coffer_service</c> (BYPASSRLS) — the same
/// role the migration runner and CLI importer use — with the long command
/// timeout the ~108k-row COMMIT needs. The new ledger is owned by the importing
/// user, so RLS surfaces it to them immediately after.
/// </summary>
public sealed class ImportJobRunner
{
    private readonly ImportJobRegistry _registry;
    private readonly IMoneydanceImportService _service;
    private readonly string _serviceConnectionString;
    private readonly ServiceDbContextFactory _serviceDbFactory;
    private readonly ILogger<ImportJobRunner> _logger;

    public ImportJobRunner(
        ImportJobRegistry registry,
        IMoneydanceImportService service,
        IOptions<ApiOptions> options,
        ServiceDbContextFactory serviceDbFactory,
        ILogger<ImportJobRunner> logger)
    {
        _registry = registry;
        _service = service;
        _serviceConnectionString = options.Value.ServiceConnectionString;
        _serviceDbFactory = serviceDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Register + kick off an import into a brand-new ledger named
    /// <paramref name="ledgerName"/>, owned by <paramref name="userId"/>. Returns
    /// the job id, or null when the user already has an import running.
    /// </summary>
    public Guid? Start(Guid userId, string ledgerName, MdExport export)
    {
        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LedgerName = ledgerName,
            Total = MoneydanceImportService.PipelineStepCount,
        };

        if (!_registry.TryStart(job))
            return null;

        // Fire-and-forget: the import outlives the HTTP request. Failures are
        // captured on the job, never thrown into the void.
        _ = Task.Run(() => RunAsync(job, export));
        return job.Id;
    }

    private async Task RunAsync(ImportJob job, MdExport export)
    {
        try
        {
            var factory = new DbConnectionFactory(_serviceConnectionString);
            await using var connection = await factory.OpenAsync().ConfigureAwait(false);

            var progress = new SyncProgress<ImportProgressUpdate>(u =>
                _registry.Update(job.Id, j =>
                {
                    j.Completed = u.Completed;
                    j.Total = u.Total;
                    j.Step = u.Detail;
                }));

            var result = await _service.ImportAsync(
                connection,
                export,
                existingLedgerId: null,
                newLedgerName: job.LedgerName,
                ownerUserId: job.UserId,
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _registry.Update(job.Id, j =>
            {
                j.State = ImportJobState.Succeeded;
                j.LedgerId = result.LedgerId;
                j.Completed = j.Total;
                j.Step = null;
            });
            _logger.LogInformation(
                "Import job {JobId} succeeded: ledger {LedgerId} '{LedgerName}'.",
                job.Id, result.LedgerId, result.LedgerName);

            await RecordImportOperationAsync(job, result).ConfigureAwait(false);
        }
        catch (ImportRefusedException ex)
        {
            _registry.Update(job.Id, j => { j.State = ImportJobState.Failed; j.Error = ex.Message; });
            _logger.LogWarning("Import job {JobId} refused: {Message}", job.Id, ex.Message);
        }
        catch (Exception ex)
        {
            _registry.Update(job.Id, j =>
            {
                j.State = ImportJobState.Failed;
                j.Error = "The import failed. Check the server logs for details.";
            });
            _logger.LogError(ex, "Import job {JobId} failed.", job.Id);
        }
    }

    /// <summary>
    /// Write the durable <c>ledger_operations</c> audit row for a SUCCESSFUL import
    /// (ADR-0055/0086): family <c>ingest</c>, provider <c>moneydance</c> — a sibling
    /// to the OFX/QIF file imports already in that log, surfaced in Settings→Activity.
    /// Only successes are recorded here: a failed bootstrap import rolls its whole
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
                triggeredVia: "file-upload",
                triggeredByUserId: job.UserId,
                status: "completed",
                errorMessage: null,
                detailsJson: JsonSerializer.Serialize(details),
                completedAt: DateTime.UtcNow).ConfigureAwait(false);
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
