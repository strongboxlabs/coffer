using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Third step of the import pipeline: walk every <c>txn</c> item that is
/// not an investment transaction and persist its normalised header + legs
/// under ADR-0022. One header per MD txn; two legs per MD split (paired
/// structurally by shared <c>posting_index</c>). Tags attach to the
/// header so register filters by tag pick up every leg of one event.
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
        var skipped = 0;

        // Pass 1: pure mapping. Collect every header + its legs, plus the
        // tag names per header so we can ensure the tag rows in one batch.
        var allHeaders   = new List<TxnHeaderRow>();
        var allLegs      = new List<TxnLegRow>();
        var allLegRecons = new List<LegReconSeed>();
        var tagsByHeader = new List<(Guid HeaderId, IReadOnlyList<string> Tags)>();
        var allTagNames  = new HashSet<string>(StringComparer.Ordinal);

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
                skipped++;
                continue;
            }

            allHeaders.Add(result.Header);
            allLegs.AddRange(result.Legs);
            allLegRecons.AddRange(result.LegRecons);
            if (result.Tags.Count > 0)
            {
                tagsByHeader.Add((result.Header.Id, result.Tags));
                foreach (var name in result.Tags) allTagNames.Add(name);
            }
        }

        if (allHeaders.Count == 0)
            return new ImportStepResult(StepName: "transactions", Read: read, Written: 0, Skipped: skipped);

        // Pass 1b: dedup by OFX (fi_id, fitid).
        var dedupResult = DedupByFitid(allHeaders, allLegs);
        allHeaders = dedupResult.Headers;
        allLegs    = dedupResult.Legs;
        skipped   += dedupResult.SkippedDuplicates;

        // Drop recon seeds whose leg didn't survive dedup (its header was a
        // FITID duplicate). Seeds key on the proposed leg id; the surviving
        // legs are the source of truth.
        var keptLegIds = allLegs.Select(l => l.Id).ToHashSet();
        allLegRecons = allLegRecons.Where(r => keptLegIds.Contains(r.LegId)).ToList();

        // Pass 2: bulk-upsert headers + legs. Headers come back keyed by
        // (ledger_id, external_id) so we can re-target tag links to the
        // PERSISTED header id (existing on conflict, supplied on insert).
        var upsertResult = await _transactionsRepo
            .BulkUpsertAsync(allHeaders, allLegs, legRecons: allLegRecons, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Guid PersistedHeaderId(TxnHeaderRow proposed) =>
            proposed.ExternalId is null
                ? proposed.Id
                : upsertResult.Headers[(proposed.LedgerId, proposed.ExternalId)];

        // Pass 3: resolve tags. Each header's set of tag names maps to
        // a set of (header_id, tag_id) links once the tag rows are
        // ensured in bulk.
        if (allTagNames.Count > 0)
        {
            var tagIdByName = await _tagsRepo.EnsureTagsAsync(context.LedgerId, allTagNames, cancellationToken)
                                             .ConfigureAwait(false);

            var headersByProposedId = allHeaders.ToDictionary(h => h.Id);

            var links = new List<(Guid HeaderId, Guid TagId)>();
            var taggedHeaderIds = new HashSet<Guid>();
            foreach (var (proposedHeaderId, tagNames) in tagsByHeader)
            {
                if (!headersByProposedId.TryGetValue(proposedHeaderId, out var header)) continue;
                var persistedId = PersistedHeaderId(header);
                taggedHeaderIds.Add(persistedId);
                foreach (var name in tagNames)
                    if (tagIdByName.TryGetValue(name, out var tagId))
                        links.Add((persistedId, tagId));
            }

            await _tagsRepo.BulkSetTagsAsync(context.LedgerId, taggedHeaderIds.ToArray(), links, cancellationToken)
                           .ConfigureAwait(false);
        }

        return new ImportStepResult(
            StepName: "transactions",
            Read: read,
            Written: allHeaders.Count + allLegs.Count,
            Skipped: skipped);
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

    public sealed record DedupResult(
        List<TxnHeaderRow> Headers,
        List<TxnLegRow> Legs,
        int SkippedDuplicates);

    /// <summary>
    /// Deduplicate headers by their OFX <c>(online_match_fi_id,
    /// online_match_fitid)</c> tuple. A single MD export can carry the
    /// same FITID on two different txn rows (observed in real data
    /// on a credit-card account where two purchase rows shared a
    /// fitxnid). The DB's
    /// <c>uq_txn_headers_online_match</c> partial unique index would
    /// reject the second on INSERT, aborting the whole import.
    /// </summary>
    /// <remarks>
    /// Headers without both FITID fields set are passed through
    /// untouched (the unique index is partial — only enforced when
    /// both fields are non-null). Order is preserved; the first
    /// occurrence of each FITID pair wins.
    /// </remarks>
    public static DedupResult DedupByFitid(
        IReadOnlyList<TxnHeaderRow> headers,
        IReadOnlyList<TxnLegRow> legs)
    {
        var seen = new HashSet<(string FiId, string Fitid)>();
        var keptHeaders = new List<TxnHeaderRow>(headers.Count);
        var keptHeaderIds = new HashSet<Guid>();
        var skipped = 0;
        foreach (var h in headers)
        {
            if (h.OnlineMatchFitid is { } fitid && h.OnlineMatchFiId is { } fiId
                && !seen.Add((fiId, fitid)))
            {
                skipped++;
                continue;
            }
            keptHeaders.Add(h);
            keptHeaderIds.Add(h.Id);
        }
        var keptLegs = legs.Where(l => keptHeaderIds.Contains(l.HeaderId)).ToList();
        return new DedupResult(keptHeaders, keptLegs, skipped);
    }
}
