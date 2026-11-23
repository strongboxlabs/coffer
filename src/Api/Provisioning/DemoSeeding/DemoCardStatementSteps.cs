using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Quotes;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The examples step that imports a card statement as a person does (ADR-0115 D3): a saved
/// CSV layout, the statement read through it into the review queue, and the rows the card's
/// owner had already typed in merged with their statement copies.
/// </summary>
/// <remarks>
/// <para>Each write goes through the repository the app's own endpoint writes through: the
/// layout through the layouts' repository, which validates it; the import through the file
/// importer, given the account and the layout's value as the import endpoint gives them; and
/// each merge through the register's merge. Any refusal, and any outcome but the one the
/// action gives, is the step's failure.</para>
/// <para><b>Checked before the first write.</b> The statement is read through the layout
/// first, as the import dialog's preview reads it, and each row the owner typed in must have
/// exactly one statement row of its day and amount, and must itself be a settled, visible
/// row on the card of that day and amount: a step reports added or failed as a whole, so a
/// refusal after the import would list the statement missing while its rows are in the
/// review queue.</para>
/// <para><b>Nothing reaches out.</b> The importer is built with the app's file readers and
/// its connection lock, and with no feed and no quote source, so the step can read only
/// the file it is given.</para>
/// </remarks>
internal static class DemoCardStatementSteps
{
    /// <summary>The generator works in cents of the Demo's one currency.</summary>
    private const decimal CentsPerUnit = 100m;

    /// <summary>
    /// Step 16. (a) The statement's layout saved under its name. (b) The statement imported
    /// into the card through that saved layout: every row lands in review, under the
    /// import's own Activity entry. (c) Each statement row the owner had typed in already,
    /// merged into that typed row, which survives, settled.
    /// </summary>
    internal static async Task CardStatementAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var statement = run.Context.Manifest.CardStatement
            ?? throw new DemoSeedStepException("the manifest names no card statement");
        if (statement.ExportedRows.Count == 0)
            throw new DemoSeedStepException("the manifest names no card row typed in before the statement");
        // The import endpoint's account check, made before anything is written.
        var card = run.Ids.Accounts[DemoAccountRoles.RewardsCard];
        var accounts = new AccountsRepository(db, new LegDerivedRecomputeService(db), run.Clock);
        if (!await accounts.BelongsToLedgerAsync(run.LedgerId, card, cancellationToken).ConfigureAwait(false))
            throw new DemoSeedStepException("the card is not in the Demo's ledger");

        var draft = CsvMappingsRepository.Validate(DemoSeedContent.CardStatementLayoutYaml, out var layoutErrors)
            ?? throw new DemoSeedStepException("the card statement's layout does not validate: "
                + string.Join("; ", layoutErrors.Select(e => e.Path + ": " + e.Message)));
        var importer = Importer(run, db);
        var context = new FileIngestContext(
            LedgerId: run.LedgerId,
            AccountId: card,
            TriggeredByUserId: run.Context.OwnerUserId,
            CsvMapping: draft,
            ProviderAccountId: CsvGenericFileProvider.SingleAccountKey,
            // Nothing was uploaded: the hub action started it, as it starts the Demo's
            // Moneydance import, which records manual too (ADR-0115 D3).
            TriggeredVia: "manual");
        var preview = await PreviewAsync(importer, run.Context.CardStatementCsv, context, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in statement.ExportedRows)
        {
            var copies = preview.Transactions.Count(t => IsRow(t.PostedAt, t.Amount, row));
            if (copies != 1)
                throw new DemoSeedStepException(
                    $"the statement has {copies} rows of {row.Date:yyyy-MM-dd} for {Amount(row)}, not one, "
                    + $"for the typed-in row with the Moneydance id {row.TxnId}");
        }
        var winners = statement.ExportedRows.ToDictionary(r => r.TxnId, r => run.Ids.Transactions[r.TxnId], StringComparer.Ordinal);
        await EachIsASettledCardRowAsync(db, run.LedgerId, card, statement.ExportedRows, winners, cancellationToken)
            .ConfigureAwait(false);

        // (a) The layout, as the layouts' endpoint saves it: the repository validates it, and
        // a name another layout has is a refusal, not a second layout.
        var layouts = new CsvMappingsRepository(db, run.Clock);
        var saved = await layouts.SaveAsync(
                run.LedgerId, null, DemoSeedContent.CardStatementLayoutName, DemoSeedContent.CardStatementLayoutYaml,
                cancellationToken)
            .ConfigureAwait(false);
        if (saved.NameTaken)
            throw new DemoSeedStepException(
                $"the layout name '{DemoSeedContent.CardStatementLayoutName}' is taken by another layout");
        if (saved.Saved is null || saved.Errors.Count > 0)
            throw new DemoSeedStepException("the card statement's layout was refused: "
                + string.Join("; ", saved.Errors.Select(e => e.Path + ": " + e.Message)));

        // (b) The import, as the import endpoint runs it on the account checked above: the
        // file read through the SAVED layout's value, re-validated as the endpoint
        // re-validates a saved layout on read, and the account then remembered as imported by
        // this reader. The endpoint, and the merge's, also start the market-series backfill,
        // a fetch, when the write adds a needed pair or moves a start earlier: the Demo is in
        // one currency, the statement lies inside the history on a card, and each merge's
        // survivor keeps its day, so neither moves a start, and nothing is fetched.
        var mapping = CsvMappingsRepository.Validate(saved.Saved.DefinitionYaml, out _)
            ?? throw new DemoSeedStepException("the saved layout no longer validates");
        IngestRunOutcome outcome;
        await using (var file = File(run.Context.CardStatementCsv))
        {
            try
            {
                outcome = await importer.RunFileAsync(
                        CsvGenericFileProvider.Key, file, context with { CsvMapping = mapping }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new DemoSeedStepException("the card statement import was refused: " + ex.Message, ex);
            }
        }
        if (outcome.Errors.Count > 0)
            throw new DemoSeedStepException("the card statement import reported problems: "
                + string.Join("; ", outcome.Errors.Select(e => e.Code + ": " + e.Message)));
        if (outcome.TransactionsForReview != preview.Transactions.Count || outcome.AlreadyKnown != 0)
            throw new DemoSeedStepException(
                $"the card statement import added {outcome.TransactionsForReview} of its {preview.Transactions.Count} rows "
                + $"and found {outcome.AlreadyKnown} already known");
        await accounts.RememberImportProviderAsync(run.LedgerId, card, CsvGenericFileProvider.Key, cancellationToken)
            .ConfigureAwait(false);

        // (c) The merges, as the register's merge runs each, on what the import saved: the
        // statement's copy is folded into the row typed in, which keeps its identity.
        db.ChangeTracker.Clear();
        var imported = await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.LedgerId == run.LedgerId && header.LedgerOperationId == outcome.SyncRunId
                join leg in db.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == card
                select new { header.Id, header.PostedAt, leg.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var transactions = new TransactionsRepository(db, run.Clock);
        foreach (var row in statement.ExportedRows)
        {
            var copies = imported.Where(i => IsRow(i.PostedAt, i.Amount, row)).ToList();
            if (copies.Count != 1)
                throw new DemoSeedStepException(
                    $"the import saved {copies.Count} rows of {row.Date:yyyy-MM-dd} for {Amount(row)}, not one");
            var what = $"the merge of the statement's copy of the row with the Moneydance id {row.TxnId}";
            var merged = await transactions.PatchAsync(
                    run.LedgerId, copies[0].Id, new PatchTransactionRequest { MergeFromHeaderId = winners[row.TxnId] },
                    cancellationToken)
                .ConfigureAwait(false);
            if (merged.Refusal is { } refusal)
                throw new DemoSeedStepException($"{what} was refused: {BusinessError.Describe(AnnotationProblem.Of(refusal))}");
            if (merged.Result != TransactionsRepository.PatchResult.Ok)
                throw new DemoSeedStepException($"{what} was refused: {merged.Result}");
        }
    }

    /// <summary>
    /// The file importer the step imports through: the app's file readers and its
    /// connection lock, as the feed job builds it over a service context, but with no feed
    /// and no quote source, so it can reach nothing outside the Demo's own file.
    /// </summary>
    internal static IngestOrchestrator Importer(DemoSeedRun run, AppDbContext db) =>
        new(
            db,
            run.SyncLock,
            Array.Empty<IPullProvider>(),
            run.FileProviders,
            new QuoteOrchestrator(
                db,
                Array.Empty<IQuotePullProvider>(),
                Array.Empty<IQuotePushProvider>(),
                new UserPreferencesRepository(db, run.Clock),
                run.Loggers.CreateLogger<QuoteOrchestrator>(),
                run.Clock),
            run.Loggers.CreateLogger<IngestOrchestrator>(),
            run.Clock);

    /// <summary>The statement read through the layout, as the import dialog previews it:
    /// nothing is written, and a row it cannot read is the step's failure.</summary>
    private static async Task<FileResult> PreviewAsync(
        IngestOrchestrator importer, string csv, FileIngestContext context, CancellationToken cancellationToken)
    {
        FileResult preview;
        await using (var file = File(csv))
        {
            try
            {
                preview = await importer.PreviewFileAsync(CsvGenericFileProvider.Key, file, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new DemoSeedStepException("the card statement could not be read: " + ex.Message, ex);
            }
        }
        if (preview.Errors.Count > 0)
            throw new DemoSeedStepException("the card statement could not be read: "
                + string.Join("; ", preview.Errors.Select(e => e.Message)));
        if (preview.Transactions.Count == 0)
            throw new DemoSeedStepException("the card statement has no rows");
        return preview;
    }

    /// <summary>
    /// Each row typed in before the statement is a settled, visible, unmerged bank row of
    /// the ledger with a line on the card of the statement row's day and amount: the merge
    /// folds the statement's copy only into such a row, and moves the survivor to the
    /// copy's day.
    /// </summary>
    /// <exception cref="DemoSeedStepException">A typed-in row is not.</exception>
    private static async Task EachIsASettledCardRowAsync(
        AppDbContext db, Guid ledgerId, Guid card, IReadOnlyList<DemoCardStatementRow> rows,
        IReadOnlyDictionary<string, Guid> winners, CancellationToken cancellationToken)
    {
        var ids = winners.Values.ToList();
        var found = await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId && ids.Contains(header.Id)
                    && !header.NeedsReview && header.IsMergedInto == null && !header.IsHidden
                    && !header.IsRecurringTemplate && header.Action == null
                join leg in db.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == card
                select new { header.Id, header.PostedAt, leg.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (!found.Any(f => f.Id == winners[row.TxnId] && IsRow(f.PostedAt, f.Amount, row)))
                throw new DemoSeedStepException(
                    $"the row with the Moneydance id {row.TxnId} is not a settled, visible card row "
                    + $"of {row.Date:yyyy-MM-dd} for {Amount(row)}");
        }
    }

    /// <summary>Whether a saved or parsed row is the manifest's <paramref name="row"/>: posted
    /// on its day, for its amount. The manifest's day is widened to compare; no date is taken
    /// from the stored timestamp.</summary>
    private static bool IsRow(DateTime postedAt, decimal amount, DemoCardStatementRow row) =>
        postedAt.Date == row.Date.ToDateTime(TimeOnly.MinValue) && amount == row.Amount / CentsPerUnit;

    private static string Amount(DemoCardStatementRow row) => DemoMoney.Major(row.Amount);

    private static MemoryStream File(string csv) => new(Encoding.UTF8.GetBytes(csv), writable: false);
}
