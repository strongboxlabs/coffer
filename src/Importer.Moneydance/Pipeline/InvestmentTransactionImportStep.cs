using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Fourth step of the import pipeline: Moneydance investment
/// transactions translated into the ADR-0022 header + legs shape.
/// </summary>
/// <remarks>
/// <para>Per-shape decomposition stays in
/// <see cref="InvestmentTransactionMapper.Map"/> (the ADR-0019 paired-
/// TransactionRow logic for buy/sell/buyx/sellx/div/divr/divx/bank/inc).
/// This step calls the wrapper
/// <see cref="InvestmentTransactionMapper.MapToHeaderAndLegs"/> which
/// pairs the emitted rows by <c>counterparty_id</c> and translates to
/// one <c>txn_headers</c> + N postings × 2 <c>txn_legs</c>.</para>
///
/// <para>Holdings deltas (per-(account, security) aggregate position)
/// upsert through <see cref="InvestmentRepository.BulkUpsertHoldingsAsync"/>.
/// Tax-lot rows (one per buy / dividend-reinvest) write through
/// <see cref="InvestmentRepository.BulkReplaceLotsAsync"/>, which
/// deletes the existing lots for the affected legs and bulk-inserts
/// the new set — keeps re-imports idempotent. ADR-0022 Phase 2
/// retargeted the lots FK from <c>transactions(id)</c> to
/// <c>txn_legs(id)</c>; the lot's <see cref="LotRow.LegId"/> points
/// at the holdings-side leg of the buy/divr posting.</para>
/// </remarks>
public sealed class InvestmentTransactionImportStep
{
    private readonly TransactionsRepository _transactionsRepo;
    private readonly InvestmentRepository _investmentRepo;
    private readonly string _importSource;

    public InvestmentTransactionImportStep(
        TransactionsRepository transactionsRepo,
        InvestmentRepository investmentRepo,
        string importSource)
    {
        _transactionsRepo = transactionsRepo;
        _investmentRepo = investmentRepo;
        _importSource = importSource;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        EnsureSecurityByMdSecAcctIdMap(context);

        var read = 0;
        var skipped = 0;

        // Pass 1: pure mapping. Collect every header + its legs, plus
        // holdings deltas and lot rows.
        var allHeaders   = new List<TxnHeaderRow>();
        var allLegs      = new List<TxnLegRow>();
        var allLegRecons = new List<LegReconSeed>();
        var deltas       = new List<InvestmentTransactionMapper.HoldingDelta>();
        var lots         = new List<LotRow>();
        // Raw skip records (txn + reason + the sec split), resolved to
        // security/ticker/shares after the loop so the common clean-import
        // path pays nothing. Silent drops are forbidden — a skip here is
        // data loss and must reach the caller (see ImportStepResult.Skips).
        var rawSkips = new List<(MdTxn Txn, string Reason, MdSplit? Sec)>();

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "txn") continue;

            var txn = MdTxn.From(item);
            if (!txn.IsInvestmentShape) continue;
            read++;

            var result = InvestmentTransactionMapper.MapToHeaderAndLegs(
                txn, context.AccountByMdId, context.SecurityByMdSecAcctId,
                context.LedgerId, _importSource);

            if (result.Skip is not null || result.Header is null)
            {
                skipped++;
                rawSkips.Add((
                    txn,
                    result.Skip?.ToString() ?? "NullHeader",
                    txn.Splits.FirstOrDefault(s => s.InvestSplitType == "sec")));
                continue;
            }
            allHeaders.Add(result.Header);
            allLegs.AddRange(result.Legs);
            allLegRecons.AddRange(result.LegRecons);
            if (result.HoldingDelta is not null) deltas.Add(result.HoldingDelta);
            if (result.NewLot is not null)       lots.Add(result.NewLot);
        }

        var skips = ResolveSkips(rawSkips, context);

        if (allHeaders.Count == 0)
            return new ImportStepResult("investment_transactions", read, 0, skipped, skips);

        // Pass 2: bulk-upsert headers + legs through the same path as
        // the non-investment importer. ON CONFLICT keys on
        // (ledger_id, external_id) for the header and
        // (header_id, posting_index, account_id) for the leg.
        var upsertResult = await _transactionsRepo
            .BulkUpsertAsync(allHeaders, allLegs, legRecons: allLegRecons, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Pass 3: aggregate holdings deltas per (account, security) and
        // upsert. The holdings table doesn't reference txn ids.
        var aggregated = deltas
            .GroupBy(d => (d.AccountId, d.SecurityId))
            .Select(g => new HoldingRow(
                Id: Guid.NewGuid(),
                LedgerId: context.LedgerId,
                AccountId: g.Key.AccountId,
                SecurityId: g.Key.SecurityId,
                Quantity: g.Sum(d => d.QuantityDelta),
                CostBasis: g.Sum(d => d.CostBasisDelta),
                AsOf: g.Max(d => d.AsOf)))
            .ToList();

        var holdingIdByKey = await _investmentRepo
            .BulkUpsertHoldingsAsync(aggregated, cancellationToken)
            .ConfigureAwait(false);

        // Pass 4: lots.
        //
        // Two indirections to resolve before lots can hit the table:
        //
        //   (a) HoldingId. The mapper sets it to Guid.Empty since it
        //       doesn't know the holding's persisted id at map time.
        //       Resolve here via (account, security) → holding.
        //
        //   (b) LegId. The mapper sets it to the proposed leg id, but
        //       the legs upsert may have hit ON CONFLICT and kept the
        //       EXISTING leg id. BulkUpsertAsync returns a proposed →
        //       persisted leg id map for exactly this case.
        //
        // On a fresh import (no prior data) (b) is a no-op — proposed
        // ids ARE persisted ids. On re-import (b) is mandatory.
        if (lots.Count > 0)
        {
            var legsById = allLegs.ToDictionary(l => l.Id);
            var resolved = new List<LotRow>(lots.Count);
            var lotLegIds = new HashSet<Guid>();
            foreach (var lot in lots)
            {
                if (!legsById.TryGetValue(lot.LegId, out var holdingsLeg)) continue;
                if (holdingsLeg.SecurityId is not { } secId) continue;
                var key = (holdingsLeg.AccountId, secId);
                if (!holdingIdByKey.TryGetValue(key, out var holdingId)) continue;
                if (!upsertResult.Legs.TryGetValue(lot.LegId, out var persistedLegId))
                    continue;

                lotLegIds.Add(persistedLegId);
                resolved.Add(lot with
                {
                    HoldingId = holdingId,
                    LegId     = persistedLegId,
                });
            }

            await _investmentRepo
                .BulkReplaceLotsAsync(lotLegIds, resolved, cancellationToken)
                .ConfigureAwait(false);
        }

        // Pass 5: avg-cost recompute of holdings.cost_basis.
        //
        // The HoldingDelta aggregation in Pass 3 only adds basis on
        // Buy / DivReinvest and leaves Sells at zero (ADR-0018 rule 4),
        // so the upserted basis = lifetime gross acquisition cost, not
        // the basis of currently-held shares. Migration 052's
        // `recompute_holdings_cost_basis(ledger_id)` walks every
        // holdings-side leg in posted_at order and applies avg-cost
        // basis reduction on dispositions, converging on the right
        // number regardless of import order or Sell interleaving.
        await _investmentRepo
            .RecomputeCostBasisAsync(context.LedgerId, cancellationToken)
            .ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "investment_transactions",
            Read: read,
            Written: allHeaders.Count + allLegs.Count,
            Skipped: skipped,
            Skips: skips);
    }

    /// <summary>
    /// Turn raw (txn, reason, sec-split) skip records into reportable
    /// <see cref="SkippedTxn"/>s: resolve the sec split's sub-account to a
    /// security name + ticker, and scale the raw MD share integer by the
    /// security's decimals. Builds the sub-account and currency lookups
    /// lazily (only when there are skips) so a clean import pays nothing.
    /// </summary>
    private static IReadOnlyList<SkippedTxn> ResolveSkips(
        IReadOnlyList<(MdTxn Txn, string Reason, MdSplit? Sec)> rawSkips,
        ImportContext context)
    {
        if (rawSkips.Count == 0) return [];

        // sec sub-account id → MdAcct (name + currid), and curr id → MdCurr
        // (ticker + decimals). acct.CurrId matches curr.Id (SecurityImportStep
        // keys SecurityByMdId on curr.Id).
        var subAcctById = new Dictionary<string, MdAcct>(StringComparer.Ordinal);
        var currById = new Dictionary<string, MdCurr>(StringComparer.Ordinal);
        foreach (var item in context.Export.AllItems)
        {
            switch (item.ObjType)
            {
                case "acct":
                    var acct = MdAcct.From(item);
                    if (acct.IsSecuritySubAccount) subAcctById[acct.Id] = acct;
                    break;
                case "curr":
                    var curr = MdCurr.From(item);
                    if (curr.IsSecurity) currById[curr.Id] = curr;
                    break;
            }
        }

        var resolved = new List<SkippedTxn>(rawSkips.Count);
        foreach (var (txn, reason, sec) in rawSkips)
        {
            string? securityName = null;
            string? ticker = null;
            decimal? shares = null;

            if (sec is not null)
            {
                MdCurr? curr = null;
                if (subAcctById.TryGetValue(sec.AcctId, out var subAcct))
                {
                    securityName = subAcct.Name;
                    if (subAcct.CurrId is { } cid) currById.TryGetValue(cid, out curr);
                }
                ticker = curr?.Ticker;
                securityName = curr?.Name ?? securityName;
                // Scale MD's raw share integer by 10^decimals (dec bounded
                // to [0,6] by schema; default 4 when the currency is
                // unresolvable — the very case that caused the skip).
                var decimals = Math.Clamp(curr?.Decimals ?? 4, 0, 6);
                long divisor = 1;
                for (var i = 0; i < decimals; i++) divisor *= 10;
                shares = sec.SplitAmount / (decimal)divisor;
            }

            resolved.Add(new SkippedTxn(
                TxnId: txn.Id,
                Reason: reason,
                Security: securityName,
                Ticker: ticker,
                Shares: shares,
                Date: txn.Date));
        }
        return resolved;
    }

    /// <summary>
    /// Build (or refresh) <see cref="ImportContext.SecurityByMdSecAcctId"/>
    /// by walking every <c>type='s'</c> acct in the export and looking up
    /// its <c>currid</c> through the security map populated by
    /// <see cref="SecurityImportStep"/>. Pure-CPU; idempotent.
    /// </summary>
    private static void EnsureSecurityByMdSecAcctIdMap(ImportContext context)
    {
        if (context.SecurityByMdSecAcctId.Count > 0) return;

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            var acct = MdAcct.From(item);
            if (!acct.IsSecuritySubAccount) continue;
            if (acct.CurrId is null) continue;
            if (!context.SecurityByMdId.TryGetValue(acct.CurrId, out var securityRef)) continue;
            context.SecurityByMdSecAcctId[acct.Id] = securityRef;
        }
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        string importSource,
        CancellationToken cancellationToken = default)
    {
        var step = new InvestmentTransactionImportStep(
            new TransactionsRepository(connection),
            new InvestmentRepository(connection),
            importSource);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
