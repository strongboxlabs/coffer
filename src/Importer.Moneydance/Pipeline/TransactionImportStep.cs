using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Third step of the import pipeline: walk every <c>txn</c> item that is
/// not an investment transaction and persist its normalised header + legs
/// under ADR-0022. One header per MD txn; two legs per MD split (paired
/// structurally by shared <c>posting_index</c>). Memos and tags sit where
/// <see cref="MoneydanceNotes"/> planned them: on legs for a one-split, on the
/// header and each line's entered-side leg for a multi-split.
/// </summary>
public sealed class TransactionImportStep
{
    private readonly TransactionsRepository _transactionsRepo;
    private readonly TagsRepository _tagsRepo;
    private readonly string _importSource;

    public TransactionImportStep(
        TransactionsRepository transactionsRepo,
        TagsRepository tagsRepo,
        string importSource)
    {
        _transactionsRepo = transactionsRepo;
        _tagsRepo = tagsRepo;
        _importSource = importSource;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var read = 0;
        // Every transaction this step declines is listed with its reason: a skip
        // is data loss, and the no-dropped-transactions check fails a step whose
        // count runs ahead of its list (see ImportStepResult.Skips).
        var skips = new List<SkippedTxn>();
        // MD id → its date, so a FITID duplicate dropped after mapping is listed
        // with the same date a mapper skip would carry.
        var dateByMdId = new Dictionary<string, int>(StringComparer.Ordinal);

        // Pass 1: pure mapping. Collect every header + its legs, plus the
        // tag names per header and leg so the tag rows are ensured in one batch.
        var allHeaders   = new List<TxnHeaderRow>();
        var allLegs      = new List<TxnLegRow>();
        var allLegRecons = new List<LegReconSeed>();
        var allOriginals = new List<HeaderOriginalsSeed>();
        // In encounter order: the first spelling of a new tag name is the one created.
        var tagSets      = new List<ImportTagSet>();

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "txn") continue;

            var txn = MdTxn.From(item);
            if (txn.IsInvestmentShape) continue;   // handled by InvestmentTransactionImportStep
            read++;

            var result = TransactionMapper.Map(
                txn, context.AccountByMdId, context.LedgerId, _importSource);
            if (result.Skip is not null || result.Header is null)
            {
                skips.Add(new SkippedTxn(
                    TxnId: txn.Id,
                    Reason: result.Skip?.ToString() ?? "NullHeader",
                    Date: txn.Date));
                continue;
            }

            dateByMdId.TryAdd(txn.Id, txn.Date);
            allHeaders.Add(result.Header);
            allLegs.AddRange(result.Legs);
            allLegRecons.AddRange(result.LegRecons);
            if (result.Originals is not null) allOriginals.Add(result.Originals);
            if (result.HeaderTags.Count > 0)
                tagSets.Add(new ImportTagSet(result.Header.Id, IsHeader: true, result.HeaderTags));
            foreach (var seed in result.LegTags)
                tagSets.Add(new ImportTagSet(seed.LegId, IsHeader: false, seed.Tags));
        }

        if (allHeaders.Count == 0)
            return new ImportStepResult(StepName: "transactions", Read: read, Written: 0, Skipped: skips.Count, Skips: skips);

        // Pass 1b: dedup by OFX (fi_id, fitid).
        var dedupResult = DedupByFitid(allHeaders, allLegs);
        allHeaders = dedupResult.Headers;
        allLegs    = dedupResult.Legs;
        foreach (var mdId in dedupResult.DroppedMdIds)
        {
            skips.Add(new SkippedTxn(
                TxnId: mdId,
                Reason: DuplicateFitidReason,
                Date: dateByMdId.TryGetValue(mdId, out var date) ? date : null));
        }

        // Drop recon seeds whose leg didn't survive dedup (its header was a
        // FITID duplicate). Seeds key on the proposed leg id; the surviving
        // legs are the source of truth.
        var keptLegIds = allLegs.Select(l => l.Id).ToHashSet();
        allLegRecons = allLegRecons.Where(r => keptLegIds.Contains(r.LegId)).ToList();
        // The originals seeds (FU-024) need no such filter: their insert joins
        // the header rows just written, so a dropped duplicate's seed finds none.

        // Pass 2: bulk-insert headers + legs.
        await _transactionsRepo
            .BulkUpsertAsync(allHeaders, allLegs, legRecons: allLegRecons,
                headerOriginals: allOriginals, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Pass 3: tags, for the headers and legs that SURVIVED the fitid dedup
        // above. A dropped duplicate's legs are gone, and a link to one would fail
        // the composite FK — loudly, but on a path where "this Moneydance file
        // imported" is the only thing the operator sees. Seed-once makes every
        // proposed id the persisted one (BulkUpsertAsync).
        var survivingHeaderIds = allHeaders.Select(h => h.Id).ToHashSet();
        var survivingLegIds = allLegs.Select(l => l.Id).ToHashSet();
        await _tagsRepo.LinkImportedAsync(
                context.LedgerId,
                [.. tagSets.Where(s => s.IsHeader ? survivingHeaderIds.Contains(s.TargetId) : survivingLegIds.Contains(s.TargetId))],
                cancellationToken)
            .ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "transactions",
            Read: read,
            Written: allHeaders.Count + allLegs.Count,
            Skipped: skips.Count,
            Skips: skips);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        string importSource,
        CancellationToken cancellationToken = default)
    {
        var step = new TransactionImportStep(
            new TransactionsRepository(connection),
            new TagsRepository(connection),
            importSource);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The reason a FITID duplicate is listed under, beside the
    /// mapper's <see cref="TransactionMapper.SkipReason"/> names.</summary>
    public const string DuplicateFitidReason = "DuplicateFitid";

    /// <summary>The headers kept, their legs, and the Moneydance ids of the
    /// headers dropped as FITID duplicates, in encounter order.</summary>
    public sealed record DedupResult(
        List<TxnHeaderRow> Headers,
        List<TxnLegRow> Legs,
        IReadOnlyList<string> DroppedMdIds)
    {
        public int SkippedDuplicates => DroppedMdIds.Count;
    }

    /// <summary>
    /// Deduplicate headers by their OFX <c>(online_match_fi_id,
    /// online_match_fitid)</c> tuple. A single MD export can carry the
    /// same FITID on two different txn rows of one account. The DB's
    /// <c>uq_txn_headers_online_match</c> partial unique index would
    /// reject the second on INSERT, aborting the whole import.
    /// </summary>
    /// <remarks>
    /// Headers without both FITID fields set are passed through
    /// untouched (the unique index is partial — only enforced when
    /// both fields are non-null). Order is preserved; the first
    /// occurrence of each FITID pair wins. Each dropped header is
    /// returned by its Moneydance id so the caller can list it.
    /// </remarks>
    public static DedupResult DedupByFitid(
        IReadOnlyList<TxnHeaderRow> headers,
        IReadOnlyList<TxnLegRow> legs)
    {
        var seen = new HashSet<(string FiId, string Fitid)>();
        var keptHeaders = new List<TxnHeaderRow>(headers.Count);
        var keptHeaderIds = new HashSet<Guid>();
        var dropped = new List<string>();
        foreach (var h in headers)
        {
            if (h.OnlineMatchFitid is { } fitid && h.OnlineMatchFiId is { } fiId
                && !seen.Add((fiId, fitid)))
            {
                // An imported header always carries its MD id; the header id
                // stands in only for a hand-built row without one.
                dropped.Add(h.ExternalId ?? h.Id.ToString());
                continue;
            }
            keptHeaders.Add(h);
            keptHeaderIds.Add(h.Id);
        }
        var keptLegs = legs.Where(l => keptHeaderIds.Contains(l.HeaderId)).ToList();
        return new DedupResult(keptHeaders, keptLegs, dropped);
    }
}
