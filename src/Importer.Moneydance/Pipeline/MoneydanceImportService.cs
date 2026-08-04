using System.Diagnostics;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Parse-time summary of a Moneydance export (per <c>obj_type</c> counts).
/// Produced without touching the database — backs both the CLI <c>--dry-run</c>
/// and the API import-preview step (ADR-0071 D2).
/// </summary>
public sealed record MoneydanceImportPreview(
    string Exporter,
    long MoneydanceBuild,
    long ExportDate,
    int TotalItems,
    IReadOnlyList<ObjTypeCount> ObjTypeCounts);

/// <summary>One <c>obj_type</c> bucket and how many items it holds.</summary>
public sealed record ObjTypeCount(string ObjType, int Count);

/// <summary>
/// Progress tick emitted after each pipeline step so a caller (the API
/// background job) can drive a progress bar. <see cref="Completed"/> of
/// <see cref="Total"/> steps done; <see cref="Detail"/> names the step just
/// finished.
/// </summary>
public sealed record ImportProgressUpdate(int Completed, int Total, string Detail);

/// <summary>Structured outcome of a completed import.</summary>
public sealed record MoneydanceImportResult(
    Guid LedgerId,
    string LedgerName,
    IReadOnlyList<ImportStepResult> Steps,
    ImportValidator.ValidationReport Validation,
    TimeSpan Elapsed);

/// <summary>
/// Thrown when an import targets a ledger that already holds transactions.
/// The Moneydance import SEEDS a fresh ledger once (ADR-0052 D2); it is never a
/// re-import. Callers map this to a friendly "create a new ledger" error.
/// </summary>
public sealed class ImportRefusedException : Exception
{
    public ImportRefusedException(Guid ledgerId, string ledgerName, int existingTransactions)
        : base($"Ledger '{ledgerName}' already has {existingTransactions:N0} transaction(s); " +
               "the Moneydance import seeds a fresh ledger only.")
    {
        LedgerId = ledgerId;
        LedgerName = ledgerName;
        ExistingTransactions = existingTransactions;
    }

    public Guid LedgerId { get; }
    public string LedgerName { get; }
    public int ExistingTransactions { get; }
}

/// <summary>
/// Reusable Moneydance import pipeline, decoupled from the CLI (ADR-0071 D2).
/// The CLI command and the API import endpoint both drive it; the only
/// CLI-specific concern left in <c>ImportCommand</c> is console rendering.
/// </summary>
public interface IMoneydanceImportService
{
    /// <summary>Parse-time summary; no database access.</summary>
    MoneydanceImportPreview Preview(MdExport export);

    /// <summary>
    /// Run the full import inside a single transaction on the supplied open
    /// connection. Resolves/creates the target ledger (granting
    /// <paramref name="ownerUserId"/> ownership of a new one), enforces the
    /// seed-once guard, runs every pipeline step, validates, and commits.
    /// Throws <see cref="ImportRefusedException"/> if the target ledger already
    /// holds transactions.
    /// </summary>
    Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection connection,
        MdExport export,
        Guid? existingLedgerId,
        string? newLedgerName,
        Guid ownerUserId,
        IProgress<ImportProgressUpdate>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-only reconcile: run the mapping-bearing steps against an
    /// EPHEMERAL ledger inside a transaction that is always ROLLED BACK,
    /// then return the step results (including <see cref="ImportStepResult.Skips"/>)
    /// so a caller can see exactly which transactions the current importer
    /// would drop — without persisting anything or touching real ledgers.
    /// Skips the reminder step (its external_id uniqueness is global, so it
    /// would collide with any already-imported ledger) since reconcile only
    /// cares about transaction/holdings fidelity.
    /// </summary>
    Task<ReconcileResult> ReconcileAsync(
        NpgsqlConnection connection,
        MdExport export,
        Guid ownerUserId,
        Guid? compareLedgerId,
        IReadOnlyList<string> diffTickers,
        CancellationToken cancellationToken);
}

/// <summary>One security's position as a fresh import would compute it.</summary>
public sealed record ReconcileHolding(string? Ticker, string Name, decimal Quantity, decimal CostBasis);

/// <summary>
/// One (ticker, date, quantity) bucket where the fresh import and the compared
/// real ledger disagree on how many legs exist. <see cref="Fresh"/> is the
/// authoritative count; <see cref="RealVisible"/> counts only visible
/// (non-hidden, non-merged) real legs (what holdings sees); <see cref="RealHidden"/>
/// counts hidden/merged real legs, to distinguish "never imported" from
/// "imported then hidden".
/// </summary>
public sealed record ReconcileDiffRow(
    string Ticker,
    DateOnly Date,
    decimal Quantity,
    long Fresh,
    long RealVisible,
    long RealHidden);

/// <summary>
/// Outcome of <see cref="IMoneydanceImportService.ReconcileAsync"/>: the step
/// results (with per-step <see cref="ImportStepResult.Skips"/>), the holdings a
/// clean import of this export would produce, and (when a compare ledger was
/// supplied) the per-transaction diffs against it.
/// </summary>
public sealed record ReconcileResult(
    IReadOnlyList<ImportStepResult> Steps,
    IReadOnlyList<ReconcileHolding> Holdings,
    IReadOnlyList<ReconcileDiffRow> Diffs);

/// <inheritdoc />
public sealed class MoneydanceImportService : IMoneydanceImportService
{
    // Mig 107 bootstrap marker: every row from the MD JSON dump is stamped
    // with this import_source. The per-file suffix is unused — the bootstrap
    // runs once per ledger.
    private const string ImportSource = "moneydance_export";

    /// <summary>Number of pipeline steps — the progress denominator.</summary>
    public const int PipelineStepCount = 10;

    public MoneydanceImportPreview Preview(MdExport export)
    {
        var counts = export.AllItems
            .GroupBy(item => item.ObjType, StringComparer.Ordinal)
            .Select(group => new ObjTypeCount(group.Key, group.Count()))
            .OrderByDescending(row => row.Count)
            .ToList();

        return new MoneydanceImportPreview(
            export.Metadata.Exporter,
            export.Metadata.MoneydanceBuild,
            export.Metadata.ExportDate,
            export.AllItems.Count,
            counts);
    }

    public async Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection connection,
        MdExport export,
        Guid? existingLedgerId,
        string? newLedgerName,
        Guid ownerUserId,
        IProgress<ImportProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the ledger up front (creating it if newLedgerName names a new
        // one) so every step stamps rows consistently.
        var ledgersRepo = new LedgersRepository(connection);
        var ledger = await ledgersRepo
            .ResolveOrCreateAsync(existingLedgerId, newLedgerName, ownerUserId, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0052 D2: seed a fresh ledger, once. MD re-keys txn.Id on
        // online-merge, so importing into a populated ledger would resurrect
        // hidden/merged rows as duplicates. Refuse any ledger with transactions.
        var existingTxns = await new TransactionsRepository(connection)
            .CountTransactionHeadersAsync(ledger.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existingTxns > 0)
            throw new ImportRefusedException(ledger.Id, ledger.Name, existingTxns);

        var importContext = new ImportContext(export, ledger.Id);
        var steps = new List<ImportStepResult>(PipelineStepCount);

        void Advance(ImportStepResult result)
        {
            steps.Add(result);
            progress?.Report(new ImportProgressUpdate(steps.Count, PipelineStepCount, result.StepName));
        }

        Advance(await SecurityImportStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        Advance(await AccountImportStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        // Splits land before InvestmentTransactionImportStep so its Pass 5
        // recompute walks the unified (legs ∪ splits) stream with splits visible.
        Advance(await SecuritySplitImportStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        Advance(await TransactionImportStep.RunAsync(connection, importContext, ImportSource, cancellationToken).ConfigureAwait(false));
        Advance(await InvestmentTransactionImportStep.RunAsync(connection, importContext, ImportSource, cancellationToken).ConfigureAwait(false));
        Advance(await PriceSnapshotImportStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        // ADR-0084: seed trade-derived prices AFTER the csnap `import` snapshots
        // so a native/future import gets a real price observation per trade day —
        // the Dapper importer bypasses the API's TradePriceFromLegInterceptor, so
        // seed explicitly (the per-ledger analogue of the mig-177 backfill).
        Advance(await TradePriceSeedStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        Advance(await ReminderImportStep.RunAsync(connection, importContext, ImportSource, cancellationToken).ConfigureAwait(false));
        // The importer uses Dapper / raw SQL, so the API's recompute interceptors
        // never see these writes — explicit recompute per the ADR-0032/0034/0046
        // call-site contract for non-EF paths.
        Advance(await BalanceRecomputeStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        Advance(await PostingCountRecomputeStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));

        // Parity expects the count of MD accounts RESOLVED into the ledger
        // (created OR adopted) = AccountByMdId — not the accounts step's Written
        // count (0 on a seed-only re-run).
        var validation = await new ImportValidator(connection)
            .ValidateAsync(ledger.Id, expectedMdAccountCount: importContext.AccountByMdId.Count, cancellationToken)
            .ConfigureAwait(false);
        // Silent drops are data loss — a lossy import must FAIL its validation
        // so both the CLI and the UI import wizard surface it (they render the
        // report). Detail lives in each step's Skips; the CLI also prints the
        // per-transaction table.
        validation = AppendNoDroppedTxnsCheck(validation, steps);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MoneydanceImportResult(ledger.Id, ledger.Name, steps, validation, stopwatch.Elapsed);
    }

    public async Task<ReconcileResult> ReconcileAsync(
        NpgsqlConnection connection,
        MdExport export,
        Guid ownerUserId,
        Guid? compareLedgerId,
        IReadOnlyList<string> diffTickers,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Ephemeral ledger — the transaction never commits, so it (and every
        // row written below) vanishes on rollback.
        var ledger = await new LedgersRepository(connection)
            .ResolveOrCreateAsync(null, "__reconcile_scratch__", ownerUserId, cancellationToken)
            .ConfigureAwait(false);

        var ctx = new ImportContext(export, ledger.Id);
        var steps = new List<ImportStepResult>(4);

        // Security + Account build the id maps the investment mapper needs;
        // SecuritySplit keeps the Pass-5 recompute happy. Investment is the
        // step whose skip report we're after. No reminder/price/balance steps.
        steps.Add(await SecurityImportStep.RunAsync(connection, ctx, cancellationToken).ConfigureAwait(false));
        steps.Add(await AccountImportStep.RunAsync(connection, ctx, cancellationToken).ConfigureAwait(false));
        steps.Add(await SecuritySplitImportStep.RunAsync(connection, ctx, cancellationToken).ConfigureAwait(false));
        steps.Add(await InvestmentTransactionImportStep.RunAsync(connection, ctx, ImportSource, cancellationToken).ConfigureAwait(false));

        // Snapshot the holdings the fresh import computed, BEFORE rollback, so
        // the caller can diff them against a real ledger's positions.
        const string holdingsSql = """
            SELECT s.ticker AS Ticker, s.name AS Name,
                   h.quantity AS Quantity, h.cost_basis AS CostBasis
            FROM holdings h JOIN securities s ON s.id = h.security_id
            WHERE h.ledger_id = @ledgerId
            ORDER BY h.quantity DESC
            """;
        var holdings = (await connection.QueryAsync<ReconcileHolding>(
            holdingsSql, new { ledgerId = ledger.Id }, transaction).ConfigureAwait(false)).AsList();

        // Transaction-level diff against a real ledger: for each (ticker, date,
        // quantity) bucket, compare the fresh import's leg count against the
        // real ledger's visible and hidden counts. Rows where fresh > real
        // visible are the shares missing from holdings; the hidden count says
        // whether the txn was imported-then-hidden vs never imported. Matches
        // securities across ledgers by ticker (the stable cross-source key).
        var diffs = new List<ReconcileDiffRow>();
        if (compareLedgerId is { } compareId)
        {
            const string diffSql = """
                WITH e AS (
                    SELECT s.ticker AS tk, (h.posted_at)::date AS d, l.quantity AS q, count(*) AS c
                    FROM txn_legs l
                    JOIN txn_headers h ON h.id = l.header_id
                    JOIN securities s ON s.id = l.security_id
                    WHERE l.ledger_id = @eph AND l.quantity IS NOT NULL
                      AND s.ticker IS NOT NULL
                      AND (cardinality(@tickers::text[]) = 0 OR s.ticker = ANY(@tickers))
                    GROUP BY 1, 2, 3
                ),
                rv AS (
                    SELECT s.ticker AS tk, (h.posted_at)::date AS d, l.quantity AS q, count(*) AS c
                    FROM txn_legs l
                    JOIN txn_headers h ON h.id = l.header_id
                    JOIN securities s ON s.id = l.security_id
                    WHERE l.ledger_id = @real AND l.quantity IS NOT NULL
                      AND s.ticker IS NOT NULL
                      AND NOT h.is_hidden AND h.is_merged_into IS NULL
                      AND (cardinality(@tickers::text[]) = 0 OR s.ticker = ANY(@tickers))
                    GROUP BY 1, 2, 3
                ),
                rh AS (
                    SELECT s.ticker AS tk, (h.posted_at)::date AS d, l.quantity AS q, count(*) AS c
                    FROM txn_legs l
                    JOIN txn_headers h ON h.id = l.header_id
                    JOIN securities s ON s.id = l.security_id
                    WHERE l.ledger_id = @real AND l.quantity IS NOT NULL
                      AND s.ticker IS NOT NULL
                      AND (h.is_hidden OR h.is_merged_into IS NOT NULL)
                      AND (cardinality(@tickers::text[]) = 0 OR s.ticker = ANY(@tickers))
                    GROUP BY 1, 2, 3
                )
                SELECT COALESCE(e.tk, rv.tk, rh.tk) AS Ticker,
                       COALESCE(e.d, rv.d, rh.d) AS Date,
                       COALESCE(e.q, rv.q, rh.q) AS Quantity,
                       COALESCE(e.c, 0) AS Fresh,
                       COALESCE(rv.c, 0) AS RealVisible,
                       COALESCE(rh.c, 0) AS RealHidden
                FROM e
                FULL OUTER JOIN rv ON e.tk = rv.tk AND e.d = rv.d AND e.q = rv.q
                FULL OUTER JOIN rh ON COALESCE(e.tk, rv.tk) = rh.tk
                                  AND COALESCE(e.d, rv.d) = rh.d
                                  AND COALESCE(e.q, rv.q) = rh.q
                WHERE COALESCE(e.c, 0) <> COALESCE(rv.c, 0)
                ORDER BY 1, 2, 3
                """;
            diffs = (await connection.QueryAsync<ReconcileDiffRow>(
                diffSql,
                new { eph = ledger.Id, real = compareId, tickers = diffTickers.ToArray() },
                transaction).ConfigureAwait(false)).AsList();
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new ReconcileResult(steps, holdings, diffs);
    }

    /// <summary>
    /// Append a "no-dropped-transactions" check to the validation report, built
    /// from the steps' <see cref="ImportStepResult.Skips"/>. Fails (surfacing in
    /// the CLI + UI) when any transaction was dropped — silent drops are data
    /// loss and must never pass quietly.
    /// </summary>
    internal static ImportValidator.ValidationReport AppendNoDroppedTxnsCheck(
        ImportValidator.ValidationReport report,
        IReadOnlyList<ImportStepResult> steps)
    {
        var dropped = steps.SelectMany(s => s.Skips ?? []).ToList();
        var check = dropped.Count == 0
            ? new ImportValidator.CheckResult("no-dropped-transactions", true, null)
            : new ImportValidator.CheckResult("no-dropped-transactions", false,
                $"{dropped.Count} transaction(s) dropped — " +
                string.Join("; ", dropped
                    .GroupBy(d => (d.Reason, Sec: d.Ticker ?? d.Security ?? "(none)"))
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Count()}× {g.Key.Reason} [{g.Key.Sec}]")));
        return report with { Checks = [.. report.Checks, check] };
    }
}
