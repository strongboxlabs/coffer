using System.Diagnostics;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Parse-time summary of a Moneydance export (per <c>obj_type</c> counts).
/// Produced without touching the database — backs the API import-preview step
/// (ADR-0071 D2).
/// </summary>
/// <param name="BaseCurrencyCode">The ISO code of the file's base currency (the
/// <c>curr</c> item with <c>isbase</c> set), which the wizard offers as the new ledger's
/// reporting currency (ADR-0114 D7); null when <paramref name="CurrencyProblems"/> says
/// why it cannot be read.</param>
/// <param name="CurrencyProblems">Each reason the file's currencies cannot be imported,
/// one sentence each, naming the account (<see cref="Mappers.MdCurrencies.Problems"/>).
/// A file with any is refused: no currency is ever defaulted.</param>
public sealed record MoneydanceImportPreview(
    string Exporter,
    long MoneydanceBuild,
    long ExportDate,
    int TotalItems,
    IReadOnlyList<ObjTypeCount> ObjTypeCounts,
    string? BaseCurrencyCode,
    IReadOnlyList<string> CurrencyProblems);

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
/// Reusable Moneydance import pipeline (ADR-0071 D2). The API import endpoint
/// drives <see cref="ImportAsync"/>, the only path that writes a ledger; the CLI's
/// read-only <c>reconcile</c> diagnostic drives <see cref="ReconcileAsync"/>.
/// </summary>
public interface IMoneydanceImportService
{
    /// <summary>Parse-time summary; no database access.</summary>
    /// <param name="reportingCurrency">The reporting currency the import was given, which
    /// its currencies are checked against (<see cref="Mappers.MdCurrencies.Problems"/>);
    /// null for the file's base currency, the wizard's default.</param>
    MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null);

    /// <summary>
    /// Run the full import inside a single transaction on the supplied open
    /// connection: create a new ledger named <paramref name="ledgerName"/>, owned
    /// by <paramref name="ownerUserId"/>, run every pipeline step into it,
    /// validate, and commit. It never writes into an existing ledger, so the
    /// ledger is seeded exactly once (ADR-0052 D2): the transaction that fills it
    /// is the one that creates it.
    /// </summary>
    /// <param name="beforeCommit">Runs last inside the import's transaction, with the
    /// ledger's id: what a caller derives from the imported rows commits or rolls back
    /// with them.</param>
    /// <param name="currencyCode">The new ledger's reporting currency (ADR-0114 D7), in
    /// stored form (three upper-case characters), written with the ledger row.</param>
    Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection connection,
        MdExport export,
        string ledgerName,
        Guid ownerUserId,
        IProgress<ImportProgressUpdate>? progress,
        CancellationToken cancellationToken,
        Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
        string currencyCode = LedgersRepository.DefaultCurrencyCode);

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

/// <summary>One security's position as a fresh import would compute it.
/// <paramref name="CurrencyCode"/> is its account's, the currency its cost basis is in.</summary>
public sealed record ReconcileHolding(string? Ticker, string Name, decimal Quantity, decimal CostBasis, string CurrencyCode);

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

    /// <summary>
    /// The step names an import reports, in the order it runs them: the
    /// <see cref="ImportStepResult.StepName"/> of each <see cref="ImportProgressUpdate"/>.
    /// Progress is reported after a step completes, so a reader that shows the step in
    /// progress reads the next name here. Its count is <see cref="PipelineStepCount"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> PipelineStepNames =
    [
        "securities",
        "accounts",
        "security_splits",
        "transactions",
        "investment_transactions",
        "security_prices",
        "trade-price-seed",
        "recurring_transactions",
        "balance-recompute",
        "posting-count-recompute",
    ];

    public MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null)
    {
        var counts = export.AllItems
            .GroupBy(item => item.ObjType, StringComparer.Ordinal)
            .Select(group => new ObjTypeCount(group.Key, group.Count()))
            .OrderByDescending(row => row.Count)
            .ToList();

        var currencies = Mappers.MdCurrencies.From(export);
        return new MoneydanceImportPreview(
            export.Metadata.Exporter,
            export.Metadata.MoneydanceBuild,
            export.Metadata.ExportDate,
            export.AllItems.Count,
            counts,
            currencies.BaseCurrencyCode,
            currencies.Problems(export, reportingCurrency));
    }

    public async Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection connection,
        MdExport export,
        string ledgerName,
        Guid ownerUserId,
        IProgress<ImportProgressUpdate>? progress,
        CancellationToken cancellationToken,
        Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
        string currencyCode = LedgersRepository.DefaultCurrencyCode)
    {
        var stopwatch = Stopwatch.StartNew();

        // ADR-0114: every account takes its own Moneydance currency and every price the
        // file's base currency, so a file where either cannot be read is refused before
        // anything is written. The endpoint has refused it already; this is the
        // pipeline's own guarantee for any other caller.
        var currencies = Mappers.MdCurrencies.From(export);
        var currencyProblems = currencies.Problems(export, currencyCode);
        if (currencyProblems.Count > 0)
            throw new InvalidDataException(
                "The export's currencies cannot be imported: " + string.Join(" ", currencyProblems));

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // ADR-0052 D2: seed a fresh ledger, once. MD re-keys txn.Id on
        // online-merge, so importing into a populated ledger would resurrect
        // hidden/merged rows as duplicates. The ledger is created here, in the
        // transaction that fills it, so no other rows can be in it; the name is
        // only a label and is never looked up.
        var ledger = await new LedgersRepository(connection)
            .CreateOwnedAsync(ledgerName, ownerUserId, cancellationToken, currencyCode)
            .ConfigureAwait(false);

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
        // Everything above wrote its rows inside this transaction, so the planner
        // still holds PRE-IMPORT statistics: on a fresh install it believes
        // txn_legs is nearly empty when it now holds the whole import. The two
        // recompute steps below are the first statements big enough to care, and a
        // plan chosen for a near-empty table does not survive contact with a full
        // one. ANALYZE
        // inside a transaction sees this transaction's own uncommitted rows, which
        // is exactly what makes it usable here (VACUUM would not be).
        await connection.ExecuteAsync(new CommandDefinition(
            "ANALYZE txn_headers, txn_legs, txn_header_originals, accounts;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The importer uses Dapper / raw SQL, so the API's recompute interceptors
        // never see these writes — explicit recompute per the ADR-0032/0034/0046
        // call-site contract for non-EF paths.
        Advance(await BalanceRecomputeStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));
        Advance(await PostingCountRecomputeStep.RunAsync(connection, importContext, cancellationToken).ConfigureAwait(false));

        // Parity expects the count of MD accounts RESOLVED into the ledger
        // (created OR adopted) = AccountByMdId — not the accounts step's Written
        // count (0 on a seed-only re-run). Less the blank leaf categories that
        // resolve to the system Uncategorized: they share a Coffer-native row
        // and have no junction of their own to count.
        var expectedMdAccounts = importContext.AccountByMdId.Count - importContext.MdIdsOnUncategorized.Count;
        var validation = await new ImportValidator(connection)
            .ValidateAsync(ledger.Id, expectedMdAccountCount: expectedMdAccounts, cancellationToken)
            .ConfigureAwait(false);
        // Silent drops are data loss — a lossy import must FAIL its validation,
        // which the caller reports (the API's job status carries each failed check
        // and the steps' Skips to the wizard, and its log names the checks).
        validation = AppendNoDroppedTxnsCheck(validation, steps);
        validation = AppendSecurityTypesCheck(validation, importContext.UnrecognisedSecurityTypes);

        if (beforeCommit is not null)
            await beforeCommit(transaction, ledger.Id, cancellationToken).ConfigureAwait(false);

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
            .CreateOwnedAsync("__reconcile_scratch__", ownerUserId, cancellationToken)
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
                   h.quantity AS Quantity, h.cost_basis AS CostBasis,
                   a.currency_code AS CurrencyCode
            FROM holdings h JOIN securities s ON s.id = h.security_id
                            JOIN accounts a ON a.id = h.account_id
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

    /// <summary>The steps that import transactions. Their <see cref="ImportStepResult.Skipped"/>
    /// counts transactions; another step's counts other things (the securities step's are
    /// the fiat currencies it passes over), so only these are held to their list.</summary>
    internal static readonly IReadOnlySet<string> TransactionStepNames =
        new HashSet<string>(StringComparer.Ordinal) { "transactions", "investment_transactions" };

    /// <summary>
    /// Append a "no-dropped-transactions" check to the validation report, built
    /// from the steps' <see cref="ImportStepResult.Skips"/>. Fails when any
    /// transaction was dropped — silent drops are data loss and must never pass
    /// quietly. A transaction step whose <see cref="ImportStepResult.Skipped"/>
    /// count exceeds its list also fails it: a drop without a reason is still a
    /// drop, and the list is what the wizard shows.
    /// </summary>
    internal static ImportValidator.ValidationReport AppendNoDroppedTxnsCheck(
        ImportValidator.ValidationReport report,
        IReadOnlyList<ImportStepResult> steps)
    {
        var dropped = steps.SelectMany(s => s.Skips ?? []).ToList();
        var unlisted = steps
            .Where(s => TransactionStepNames.Contains(s.StepName))
            .Select(s => (s.StepName, Count: s.Skipped - (s.Skips?.Count ?? 0)))
            .Where(u => u.Count > 0)
            .ToList();
        var total = dropped.Count + unlisted.Sum(u => u.Count);
        var check = total == 0
            ? ImportValidator.CheckResult.Pass("no-dropped-transactions")
            : ImportValidator.CheckResult.Fail("no-dropped-transactions",
                $"{total} transaction(s) dropped — " +
                string.Join("; ", dropped
                    .GroupBy(d => (d.Reason, Sec: d.Ticker ?? d.Security ?? "(none)"))
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Count()}× {g.Key.Reason} [{g.Key.Sec}]")
                    .Concat(unlisted.Select(u => $"{u.Count}× without a reason [{u.StepName}]"))),
                CheckSummaries.NotImported(total));
        return report with { Checks = [.. report.Checks, check] };
    }

    /// <summary>
    /// Append a "security-types-recognised" check: fails when any security's
    /// <c>sec_type</c> code was not in <see cref="Mappers.MdSecurityType"/>'s table,
    /// so it was imported with vehicle type Other (ADR-0016, amendment of
    /// 2026-11-18). Nothing is lost — the security, its holdings and its trades are
    /// all imported — so, like every check, it only warns; the person can set the
    /// vehicle with Edit security.
    /// </summary>
    internal static ImportValidator.ValidationReport AppendSecurityTypesCheck(
        ImportValidator.ValidationReport report,
        IReadOnlyList<UnrecognisedSecurityType> unrecognised)
    {
        var codes = unrecognised
            .Select(u => u.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var check = unrecognised.Count == 0
            ? ImportValidator.CheckResult.Pass("security-types-recognised")
            : ImportValidator.CheckResult.Fail("security-types-recognised",
                $"{unrecognised.Count} security(ies) with an unrecognised sec_type code "
                + $"({string.Join(", ", codes)}) imported with vehicle type Other",
                CheckSummaries.UnrecognisedSecurityTypes(unrecognised.Count, codes));
        return report with { Checks = [.. report.Checks, check] };
    }
}
