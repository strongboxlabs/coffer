using Coffer.Domain.Investment;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Per-shape translation of Moneydance investment transactions into Coffer
/// <c>transactions</c> + <c>holdings</c>-delta + <c>lots</c> rows under the
/// symmetric posting model (ADR-0019). The dispatch table mirrors
/// <see href="../decisions/0018-investment-and-cross-account-translation.md">ADR-0018</see>;
/// <see href="../decisions/0019-symmetric-postings.md">ADR-0019</see> rewires
/// the row shapes (no <c>splits</c>, no <c>inv_txn_securities</c>; security
/// metadata lives on the holdings-side row of each pair).
/// </summary>
/// <remarks>
/// <para><b>Decomposition by shape</b>:</para>
/// <list type="bullet">
///   <item><description><b>buy / sell</b> — sec leg pairs brokerage-cash with the
///   Holdings sibling; an optional fee leg pairs brokerage-cash with the
///   fee category. Holdings-side row carries <c>security_id</c>, qty, price.
///   The fee category leg books the cash impact; the lot's
///   <c>unit_cost</c> is computed as <c>price + apportioned_commission</c>
///   per IRS convention. The legacy <c>txn_legs.commission</c> redundancy
///   was dropped in migration 046 — fees live exclusively on their own
///   paired row under one <c>txn_group_id</c>.</description></item>
///   <item><description><b>buyx / sellx</b> — Holdings sibling pairs directly with the
///   external (xfr) account; no brokerage-cash leg, since no cash flows
///   through the brokerage.</description></item>
///   <item><description><b>div</b> (cash dividend) — single pair: brokerage-cash ↔
///   income category. <c>security_id</c> on the cash side (qty/price = 0)
///   to keep the dividend in the per-security register query.</description></item>
///   <item><description><b>divr</b> (reinvest) — two pairs, four rows: inc pair
///   (cash ↔ income) + sec pair (cash ↔ holdings sibling). The two
///   brokerage-cash rows share a <c>txn_group_id</c>.</description></item>
///   <item><description><b>divx</b> (dividend transferred) — two pairs: inc pair
///   (cash ↔ income) + xfr pair (cash ↔ other account).</description></item>
///   <item><description><b>bank</b> — single pair: brokerage-cash ↔ other account.
///   No security side.</description></item>
///   <item><description><b>inc</b> (misc income) — one pair per inc/fee leg, all
///   sharing a <c>txn_group_id</c> on the brokerage cash side.</description></item>
/// </list>
///
/// <para>Money sums work on Moneydance's minor units (long) and convert to
/// <c>decimal</c> only at the boundary
/// (<see cref="AccountMapper.MinorUnitsToDecimal"/>). Share quantities use
/// <see cref="MdSplit.SplitAmount"/> with 4 implicit decimals.</para>
/// </remarks>
public static class InvestmentTransactionMapper
{
    public enum SkipReason
    {
        NotInvestmentTxn,
        UnknownShape,
        UnknownBrokerageAccount,
        BrokerageMissingHoldingsSibling,
        UnknownXferAccount,
        UnknownSecurity,
        UnknownIncomeOrFeeCategory,
    }

    /// <summary>
    /// Outcome of mapping one Moneydance investment transaction. <see cref="Rows"/>
    /// is the full set of paired rows (every row's <c>counterparty_id</c>
    /// references another row in the same set). <see cref="HoldingDelta"/>
    /// (when set) names the Holdings sibling account + security; the pipeline
    /// aggregates deltas across all txns and upserts <c>holdings</c> after
    /// the row pass. <see cref="NewLot"/> (when set) carries
    /// <see cref="LotRow.LegId"/> = the proposed id of the
    /// holdings-side row in <see cref="Rows"/>; the pipeline rebinds it to
    /// the persisted id post-upsert.
    /// </summary>
    public sealed record MapResult(
        IReadOnlyList<TransactionRow> Rows,
        HoldingDelta? HoldingDelta,
        LotRow? NewLot,
        SkipReason? Skip);

    /// <summary>
    /// Per-(account, security) holding change emitted by one mapped txn. The
    /// <see cref="AccountId"/> is the Holdings sibling's account id (where
    /// the security position lives, not the brokerage).
    /// </summary>
    public sealed record HoldingDelta(
        Guid AccountId,
        Guid SecurityId,
        decimal QuantityDelta,
        decimal CostBasisDelta,
        DateTimeOffset AsOf);

    /// <summary>
    /// Outcome of <see cref="MapToHeaderAndLegs"/>: the ADR-0022 shape.
    /// One <see cref="TxnHeaderRow"/> per MD txn, two <see cref="TxnLegRow"/>
    /// per posting (paired structurally via shared <c>PostingIndex</c>).
    /// <see cref="HoldingDelta"/> drives the per-(account, security)
    /// holdings upsert. <see cref="NewLot"/> (when set) carries
    /// <see cref="LotRow.LegId"/> = the proposed id of the holdings-side
    /// leg of a buy/divr; the pipeline rebinds it to the persisted id
    /// post-upsert if needed.
    /// </summary>
    public sealed record HeaderLegsResult(
        TxnHeaderRow? Header,
        IReadOnlyList<TxnLegRow> Legs,
        IReadOnlyList<LegReconSeed> LegRecons,
        HoldingDelta? HoldingDelta,
        LotRow? NewLot,
        SkipReason? Skip);

    /// <summary>
    /// Map one MD investment txn into one ADR-0022 header + N postings.
    /// Every MD investment txntype (buy/sell/buyx/sellx/div/divr/divx/
    /// bank/inc and the corresponding short/cover/exp shapes documented
    /// in docs/moneydance-investment-actions.md) translates to exactly
    /// one Ledger header; counterparty-paired rows become postings under
    /// that header in input order.
    /// </summary>
    /// <remarks>
    /// MiscInc-with-fee (`[sec, fee, inc]`) used to be fanned out into
    /// N single-posting headers under a "single-posting MiscInc
    /// invariant" (migrations 058/059). That invariant was based on a
    /// misread of MD's data — the multi-posting shape is the standard
    /// user-creatable MiscInc-with-fee, not an automated-import artifact.
    /// Migration 061 reversed the data + dropped the trigger; this
    /// mapper now treats MiscInc identically to every other action.
    /// </remarks>
    public static HeaderLegsResult MapToHeaderAndLegs(
        MdTxn txn,
        IReadOnlyDictionary<string, AccountRef> accountByMdId,
        IReadOnlyDictionary<string, SecurityRef> securityByMdSecAcctId,
        Guid ledgerId,
        string importSource)
    {
        var paired = Map(txn, accountByMdId, securityByMdSecAcctId, importSource, ledgerId);
        if (paired.Skip is not null)
            return new HeaderLegsResult(
                Header: null, Legs: [], LegRecons: [], HoldingDelta: null, NewLot: null, Skip: paired.Skip);

        if (paired.Rows.Count == 0)
            return new HeaderLegsResult(
                Header: null, Legs: [], LegRecons: [], HoldingDelta: null, NewLot: null, Skip: SkipReason.UnknownShape);

        // Pair rows by counterparty_id, walking in input order so the
        // posting_index sequence is stable.
        var pairs = PairByCounterparty(paired.Rows);
        return BuildSingleHeader(paired, pairs, ledgerId);
    }

    /// <summary>
    /// Walk the row list once, pairing rows by <c>CounterpartyId</c> in
    /// encounter order. Stable: the pair containing the first row in
    /// <paramref name="rows"/> comes first; later pairs follow in the
    /// order their first row appears.
    /// </summary>
    private static List<(TransactionRow A, TransactionRow B)> PairByCounterparty(
        IReadOnlyList<TransactionRow> rows)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var seen = new HashSet<Guid>();
        var pairs = new List<(TransactionRow, TransactionRow)>(rows.Count / 2);
        foreach (var row in rows)
        {
            if (seen.Contains(row.Id)) continue;
            if (!byId.TryGetValue(row.CounterpartyId, out var partner)) continue;
            seen.Add(row.Id);
            seen.Add(partner.Id);
            pairs.Add((row, partner));
        }
        return pairs;
    }

    /// <summary>
    /// Build one <see cref="HeaderLegsResult"/> with N postings — the
    /// shape every action except MiscInc-multi uses. Header envelope
    /// is lifted from the first row; legs are translated 1:1, with
    /// posting_index assigned in pair order.
    /// </summary>
    private static HeaderLegsResult BuildSingleHeader(
        MapResult paired,
        List<(TransactionRow A, TransactionRow B)> pairs,
        Guid ledgerId)
    {
        var anyRow = paired.Rows[0];
        var headerId = Guid.NewGuid();
        var header = BuildHeader(anyRow, headerId, ledgerId,
            externalId: StripLegSuffix(anyRow.ExternalId),
            payee: anyRow.FeedPayee,
            action: paired.Rows
                        .Select(r => r.InvestmentAction)
                        .FirstOrDefault(a => a is not null && a != "transfer")
                     ?? paired.Rows
                        .Select(r => r.InvestmentAction)
                        .FirstOrDefault(a => a is not null));

        // Walk pairs in input order, translating each into a posting
        // (two legs sharing posting_index). Track row-id → leg-id so
        // the lot's LegId pointer (originally the holdings-side
        // TransactionRow.Id) can be rebound to the corresponding leg.
        var legs = new List<TxnLegRow>(paired.Rows.Count);
        var legIdByOldRowId = new Dictionary<Guid, Guid>(paired.Rows.Count);
        // ADR-0082 per-leg reconciliation seeds. Each row carries its own
        // recon source (ReconStat): brokerage cash <- parent stat, external
        // counterparty <- its split stat, Holdings/security <- NULL (skip).
        // Category legs may seed but are dropped at persist. Absent/space
        // stat => uncleared => no seed.
        var legRecons = new List<LegReconSeed>();
        void AddSeed(Guid legId, TransactionRow row)
        {
            var (recStatus, recCleared) = TransactionMapper.NormalizeMdStatus(row.ReconStat);
            if (recStatus != "uncleared")
                legRecons.Add(new LegReconSeed(
                    legId, recStatus, recCleared ? row.FeedPostedAt : (DateTimeOffset?)null));
        }
        var postingIndex = 0;
        foreach (var (a, b) in pairs)
        {
            var legA = ToLeg(a, headerId, ledgerId, postingIndex);
            var legB = ToLeg(b, headerId, ledgerId, postingIndex);
            legs.Add(legA);
            legs.Add(legB);
            legIdByOldRowId[a.Id] = legA.Id;
            legIdByOldRowId[b.Id] = legB.Id;
            AddSeed(legA.Id, a);
            AddSeed(legB.Id, b);
            postingIndex++;
        }

        var newLot = paired.NewLot is null
            ? null
            : paired.NewLot with
              {
                  LegId = legIdByOldRowId.TryGetValue(paired.NewLot.LegId, out var leg)
                      ? leg
                      : paired.NewLot.LegId,
              };

        return new HeaderLegsResult(header, legs, legRecons, paired.HoldingDelta, newLot, Skip: null);
    }

    /// <summary>
    /// Header envelope builder used by <see cref="BuildSingleHeader"/>.
    /// Pulls payee/memo/posted-at/status/etc. from
    /// <paramref name="source"/>; the caller supplies
    /// <paramref name="headerId"/>, <paramref name="externalId"/>,
    /// <paramref name="payee"/>, and <paramref name="action"/>.
    /// </summary>
    private static TxnHeaderRow BuildHeader(
        TransactionRow source,
        Guid headerId,
        Guid ledgerId,
        string? externalId,
        string? payee,
        string? action)
    {
        var (status, isCleared) = TransactionMapper.NormalizeMdStatus(source.FeedStatus);
        return new TxnHeaderRow(
            Id:                  headerId,
            LedgerId:            ledgerId,
            Origin:              source.Origin,
            ExternalId:          externalId,
            Payee:               payee,
            Memo:                source.FeedMemo,
            PostedAt:            source.FeedPostedAt,
            TransactedAt:        source.FeedTransactedAt,
            Status:              status,
            CheckNumber:         source.CheckNumber,
            IsPending:           source.IsPending,
            IsHidden:            false,
            IsMergedInto:        null,
            ImportSource:        source.ImportSource,
            ClearedAt:           isCleared ? source.FeedPostedAt : null,
            ClearedByUserId:     null,
            OnlineMatchFitid:    null,
            OnlineMatchFiId:     null,
            Action:              action,
            // Mig 107: provider_key was computed by the MapCtx
            // decompose at row-emit time and stamped on every
            // TransactionRow in the pair; pass it through here.
            // is_merge_winner=false on import; flipped by the API
            // merge path post-bootstrap.
            ProviderKey:         source.ProviderKey,
            IsMergeWinner:       false,
            // Mig 109 / ADR-0035 §3: forward the verbatim MD JSON
            // for this row from the upstream TransactionRow.
            ProviderRawPayload:  source.ProviderRawPayload);
    }

    private static TxnLegRow ToLeg(TransactionRow row, Guid headerId, Guid ledgerId, int postingIndex) =>
        new(
            Id:                Guid.NewGuid(),
            HeaderId:          headerId,
            LedgerId:          ledgerId,
            AccountId:         row.AccountId,
            PostingIndex:      postingIndex,
            LegMemo:           null,                      // investment txns don't carry per-leg memos
            Amount:            row.FeedAmount,
            SecurityId:        row.SecurityId,
            Quantity:          row.Quantity,
            UnitPrice:         row.UnitPrice,
            PostingRole:       row.PostingRole);

    /// <summary>
    /// Strip the <c>:&lt;legIndex&gt;</c> suffix from a leg-keyed external
    /// id to recover the MD txn id. ADR-0022 puts external_id at the
    /// header level (no suffix); per-leg idempotency moves to the
    /// (header_id, posting_index, account_id) unique index.
    /// </summary>
    private static string? StripLegSuffix(string? legExternalId)
    {
        if (legExternalId is null) return null;
        var colon = legExternalId.LastIndexOf(':');
        return colon >= 0 ? legExternalId[..colon] : legExternalId;
    }

    public static MapResult Map(
        MdTxn txn,
        IReadOnlyDictionary<string, AccountRef> accountByMdId,
        IReadOnlyDictionary<string, SecurityRef> securityByMdSecAcctId,
        string importSource,
        Guid ledgerId = default)
    {
        ArgumentNullException.ThrowIfNull(txn);

        // Investment-flavoured txns are recognised by either an explicit
        // `invest.txntype` tag *or* by an investment `xfer_type`. Real
        // exports include a population of buy/sell/dividend-shaped txns
        // (likely manually-entered or migrated from another tool) where
        // the txntype tag is missing but the xfer_type still names the
        // event class — these used to fall through to the non-investment
        // mapper and get silently dropped (their sec split's target is a
        // type='s' sub-account that's not a Coffer account). We infer the
        // shape from xfer_type + the sec split's share-direction.
        if (!txn.IsInvestmentShape)
            return Skip(SkipReason.NotInvestmentTxn);

        if (!accountByMdId.TryGetValue(txn.AcctId, out var brokerageRef))
            return Skip(SkipReason.UnknownBrokerageAccount);

        if (brokerageRef.HoldingsAccountId is not { } holdingsAccountId)
            return Skip(SkipReason.BrokerageMissingHoldingsSibling);

        var posted = ResolvePostedAt(txn);
        var ctx = new MapCtx(txn, brokerageRef, holdingsAccountId, ledgerId, posted, importSource,
                             accountByMdId, securityByMdSecAcctId);

        // Three ordered sources for the txntype, per ADR-0027:
        //   1. invest.txntype (primary).
        //   2. qif_invst_action (secondary — QIF-imported txns).
        //   3. Structural classification (tertiary — bare rows).
        // Every signal comes from a field MD wrote; no inference.
        var classifiedType =
            txn.InvestTxnType is { Length: > 0 } primary    ? primary
          : MapQifInvstActionToTxnType(txn.QifInvstAction)
                                       is { Length: > 0 } qif ? qif
          : ClassifyInvestTxnType(ctx);

        // Cross-validation: an `xfrtp_dividend` txn that carries an `xfr`
        // split is semantically a transferred dividend (QIF "IntIncX"
        // style — MD mis-tagged it as `div`/`divr`). Promote to `divx`
        // regardless of which classification source produced the type.
        if ((classifiedType == "div" || classifiedType == "divr")
            && txn.XferType == "xfrtp_dividend"
            && ctx.RequireXfrSplit() is not null)
        {
            classifiedType = "divx";
        }

        // Translate MD txntype to the Ledger `action` (ADR-0027). This
        // is the single per-action switch — every other downstream
        // decision dispatches off `posting_role` at the leg level, not
        // off action at the header level.
        var action = MapTxnTypeToLedgerAction(classifiedType);
        if (action is null) return Skip(SkipReason.UnknownShape);

        // Resolve the security up front (every investment txntype except
        // `bank` has a `sec` split, even if `pamt=samt=0` for div/divx/
        // inc/exp — that's MD's way of pinning a security_id link).
        var sec = ctx.RequireSecSplit();
        SecurityRef? secRef = null;
        if (sec is not null)
        {
            if (!ctx.SecurityByMdSecAcctId.TryGetValue(sec.AcctId, out var sr))
                return Skip(SkipReason.UnknownSecurity);
            secRef = sr;
        }
        var securityId = secRef?.Id;

        // Self-referential buyx/sellx: the xfr target IS the primary
        // brokerage itself. MD's accounting nets the brokerage cash to
        // zero (sec.pamt + xfr.pamt cancel). Detect once so the sec-pair
        // builder can zero cash and the xfr-pair builder can skip.
        var xfr = ctx.RequireXfrSplit();
        var isSelfRefXfr = xfr is not null && xfr.AcctId == ctx.Txn.AcctId;

        // All cash-side legs share one txn_group so the SPA aggregator
        // collapses the multi-posting header into a single register row.
        var groupId = (Guid?)Guid.NewGuid();
        var rows = new List<TransactionRow>();
        TransactionRow? holdingsRow = null;
        decimal totalCommission = 0m;

        // Walk EVERY split in MD's encounter order; build the right
        // pair per splittype. The mapping splittype → posting_role is
        // fixed and uniform across actions (ADR-0027). This is the
        // entire dispatch — no per-action mappers.
        foreach (var split in ctx.Txn.Splits)
        {
            switch (split.InvestSplitType)
            {
                case "sec":
                    if (secRef is null) continue;
                    // Skip the sec PAIR when nothing actually moved
                    // (div / divx / inc / exp emit `sec` with pamt=0
                    // AND samt=0 just to link a security_id; the cash
                    // flow lives in the inc/xfr pairs and securityId
                    // is stamped on those via the `securityId` param).
                    if (split.SplitAmount == 0 && split.ParentAmount == 0) continue;

                    var quantity      = ToShareQuantity(split.SplitAmount, secRef.ShareDecimals);
                    var unitPrice     = ComputeUnitPrice(split, secRef.ShareDecimals);
                    var secCash       = AccountMapper.MinorUnitsToDecimal(split.ParentAmount);
                    var holdingsValue = -secCash;

                    // Self-referential buysellxfr (xfr target == this
                    // brokerage): book the sec pair NORMALLY -- brokerage
                    // cash moves by the proceeds, Holdings by the same and
                    // opposite -- and let the `xfr` case below skip the
                    // self-loop leg. The proceeds genuinely sit in the
                    // brokerage: a share-class exchange sells one class for
                    // cash that funds the new class; a fee-funding sell
                    // leaves cash to pay the fee.
                    //
                    // An earlier version zeroed secCash here on a "MD nets
                    // the brokerage cash to zero" premise. But that netting
                    // happens across the PAIRED buy / fee leg, not within
                    // this single header -- so zeroing dropped the sale
                    // proceeds and left every self-ref buyx/sellx unbalanced
                    // by the trade amount (ADR-0052 D4). The default
                    // secCash/holdingsValue above is correct for both
                    // directions: a sell carries pamt>0 (cash in, holdings
                    // out); a buy carries pamt<0 (cash out, holdings in).
                    // A pure share transfer (pamt=0) stays 0/0, still balanced.

                    var (cashSec, hold) = MakeSecPair(
                        ctx, split, action, secRef.Id, quantity, unitPrice,
                        secCash, holdingsValue, groupId, legIndex: split.Index);
                    rows.Add(cashSec);
                    rows.Add(hold);
                    holdingsRow = hold;
                    break;

                case "fee":
                {
                    if (!ctx.AccountByMdId.TryGetValue(split.AcctId, out var feeRef))
                        return Skip(SkipReason.UnknownIncomeOrFeeCategory);
                    var (cashFee, feeCat) = MakeCategoryPair(
                        ctx, split, feeRef.Id, action, groupId,
                        securityId, postingRole: "fee");
                    rows.Add(cashFee);
                    rows.Add(feeCat);
                    totalCommission += Math.Abs(AccountMapper.MinorUnitsToDecimal(split.ParentAmount));
                    break;
                }

                case "inc":
                case "exp":
                {
                    // Per ADR-0027: BOTH `inc` and `exp` splittypes stamp
                    // posting_role='income'. Direction (income vs expense)
                    // lives in the sign on the brokerage-cash-side amount,
                    // not in the role.
                    if (!ctx.AccountByMdId.TryGetValue(split.AcctId, out var catRef))
                        return Skip(SkipReason.UnknownIncomeOrFeeCategory);
                    var (cashCat, catRow) = MakeCategoryPair(
                        ctx, split, catRef.Id, action, groupId,
                        securityId, postingRole: "income");
                    rows.Add(cashCat);
                    rows.Add(catRow);
                    break;
                }

                case "xfr":
                {
                    // Self-ref: the xfr would point back to the brokerage;
                    // skip it (the sec pair already absorbed the cash impact).
                    if (isSelfRefXfr) continue;
                    if (!ctx.AccountByMdId.TryGetValue(split.AcctId, out var xferRef))
                        return Skip(SkipReason.UnknownXferAccount);
                    var (cashXfr, xferRow) = MakeXferPair(ctx, split, xferRef.Id, groupId);
                    rows.Add(cashXfr);
                    rows.Add(xferRow);
                    break;
                }

                default:
                    // Unknown splittype — defensive skip the whole txn.
                    return Skip(SkipReason.UnknownShape);
            }
        }

        if (rows.Count == 0) return Skip(SkipReason.UnknownShape);

        // `txn_group_id` is the "multi-posting event" marker — strip it
        // back to null when only one pair landed (a solo Buy / Sell /
        // Div / etc. without a fee). Keeps the discriminator meaningful
        // for queries that look for grouped events.
        if (rows.Count == 2)
        {
            rows[0] = rows[0] with { TxnGroupId = null };
            rows[1] = rows[1] with { TxnGroupId = null };
        }

        var (delta, lot) = BuildHoldingsImpact(action, sec, secRef, totalCommission, holdingsRow, ctx);

        return new MapResult(rows, delta, lot, Skip: null);
    }

    /// <summary>
    /// MD <c>invest.txntype</c> → Ledger <c>txn_headers.action</c>
    /// (ADR-0027). The only per-action header decision. Returns
    /// <c>null</c> for txntypes outside the catalog (e.g. MD's
    /// <c>short</c> / <c>cover</c> — declined per ADR-0027).
    /// </summary>
    private static string? MapTxnTypeToLedgerAction(string mdTxnType) => mdTxnType switch
    {
        "buy"   => "buy",
        "buyx"  => "buyx",
        "sell"  => "sell",
        "sellx" => "sellx",
        "div"   => "dividend_cash",
        "divr"  => "dividend_reinvest",
        "divx"  => "divx",
        "bank"  => "transfer",
        "inc"   => "misc",
        "exp"   => "misc",
        _       => null,
    };

    /// <summary>
    /// Per-action holdings/lot impact — the second (and last) per-action
    /// switch. Driven by the Ledger action since MD's txntype is already
    /// translated by the time we get here.
    /// <para>
    /// Cost-basis policy at import time: include commission per IRS
    /// convention on every share-acquiring action (buy / buyx /
    /// dividend_reinvest). The recompute function
    /// (<c>fn_recompute_holdings_cost_basis</c>, migration 056) ALWAYS
    /// re-derives lot <c>unit_cost</c> based on the brokerage's
    /// <c>is_trade_commission</c> flag — so the value written here is
    /// a placeholder that recompute either preserves (flag=TRUE) or
    /// strips commission from (flag=FALSE).
    /// </para>
    /// </summary>
    private static (HoldingDelta? Delta, LotRow? Lot) BuildHoldingsImpact(
        string action,
        MdSplit? sec,
        SecurityRef? secRef,
        decimal totalCommission,
        TransactionRow? holdingsRow,
        MapCtx ctx)
    {
        // Holdings only change when there's a real sec movement; the
        // sec-pair build above stamps holdingsRow non-null only when
        // it actually emitted the pair.
        if (sec is null || secRef is null || holdingsRow is null)
            return (null, null);

        var quantity = ToShareQuantity(sec.SplitAmount, secRef.ShareDecimals);
        var secPrice = Math.Abs(AccountMapper.MinorUnitsToDecimal(sec.ParentAmount));

        // Delegate the action × policy logic to the shared layer.
        var impact = InvestmentPostings.BuildHoldingsImpact(
            action:            action,
            holdingsAccountId: ctx.HoldingsAccountId,
            securityId:        secRef.Id,
            quantity:          quantity,
            sharePrice:        secPrice,
            totalCommission:   totalCommission,
            asOf:              ctx.Posted);

        if (impact is null) return (null, null);

        var delta = new HoldingDelta(
            impact.HoldingsAccountId, impact.SecurityId,
            impact.QuantityDelta, impact.CostBasisDelta, impact.AsOf);

        LotRow? lot = null;
        if (impact.NewLot is { } spec)
        {
            lot = new LotRow(
                Id:         Guid.NewGuid(),
                LedgerId:   ctx.LedgerId,
                HoldingId:  Guid.Empty,                  // resolved post-upsert
                LegId:      holdingsRow.Id,              // proposed; rebinds post-upsert
                Quantity:   spec.Quantity,
                UnitCost:   spec.UnitCost,
                AcquiredAt: spec.AcquiredAt,
                IsClosed:   false);
        }

        return (delta, lot);
    }

    /// <summary>
    /// Map a QIF-origin action (`qif_invst_action`) to its
    /// `invest.txntype` equivalent. Returns <c>null</c> when the
    /// QIF action is absent or unmapped — the caller falls through
    /// to structural classification. Mapping table is locked in
    /// <see href="../../../docs/decisions/0027-investment-action-catalog.md">ADR-0027</see>,
    /// grounded in cross-tab evidence from real-world MD exports
    /// (every observed QIF action maps deterministically; counts
    /// not reproduced in code).
    /// </summary>
    private static string? MapQifInvstActionToTxnType(string? qifAction)
    {
        if (string.IsNullOrEmpty(qifAction)) return null;
        return qifAction switch
        {
            "Buy"       => "buy",
            "BuyX"      => "buyx",
            "Sell"      => "sell",
            "SellX"     => "sellx",
            // Share-only transfers — basis-preserving moves with no
            // separate cash leg. Same Ledger shape as buyx/sellx.
            "ShrsIn"    => "buyx",
            "ShrsOut"   => "sellx",
            "Div"       => "div",
            "DivX"      => "divx",
            // Interest in QIF is a dividend-shape inc leg; MD's xfer_type
            // confirms (xfrtp_dividend). Same Ledger action as Div.
            "IntInc"    => "div",
            // IntIncX appears as xfrtp_dividend in MD but with both an
            // inc and an xfr split — the classifier's cross-validation
            // rule (xfrtp_dividend with xfr split → divx) catches it
            // regardless of what we return here; mapping to "divx"
            // makes the intent explicit at the call site too.
            "IntIncX"   => "divx",
            // All five reinvest variants collapse to divr; MD doesn't
            // distinguish dividend / interest / cap-gain reinvest at
            // the data layer. QIF text in `chk` / `desc` preserves
            // the original distinction for the user-visible label.
            "ReinvDiv"  => "divr",
            "ReinvInt"  => "divr",
            "ReinvLg"   => "divr",
            "ReinvMd"   => "divr",
            "ReinvSh"   => "divr",
            // Cash transfers — bank-shape with no security side.
            "XIn"       => "bank",
            "XOut"      => "bank",
            "ContribX"  => "bank",
            // MiscIncX in MD is a pure bank transfer (only an xfr
            // split, no inc split) — the QIF "MiscIncX" name is
            // misleading. Map to bank, not inc.
            "MiscIncX"  => "bank",
            // "Cash" is direction-ambiguous in QIF: most rows are
            // pure cash adjustments on the brokerage (xfrtp_bank),
            // but the data shows a small subset emitted with
            // xfrtp_miscincexp + an inc split. The dispatcher pairs
            // xfer_type with txntype, so returning "bank" here is
            // correct for the majority case; the xfrtp_miscincexp
            // case falls through to the structural classifier below
            // (which routes by splittype to inc/exp).
            "Cash"      => null,
            _           => null,
        };
    }

    /// <summary>
    /// Read the txn's intended <c>invest.txntype</c> from observable
    /// MD structure when neither <c>invest.txntype</c> nor
    /// <c>qif_invst_action</c> is present. Every signal is a field
    /// MD itself wrote — not an inference. Lookup table is locked
    /// in <see href="../../../docs/decisions/0027-investment-action-catalog.md">ADR-0027</see>.
    /// </summary>
    /// <remarks>
    /// Returning the empty string falls through to
    /// <see cref="SkipReason.UnknownShape"/>.
    /// </remarks>
    private static string ClassifyInvestTxnType(MapCtx ctx)
    {
        var sec = ctx.RequireSecSplit();

        if (ctx.Txn.XferType == "xfrtp_dividend")
        {
            // divr vs div: prefer the explicit reinvest flag MD sets
            // on OFX-imported reinvest dividends; fall back to share-
            // direction (sec.samt > 0 means shares were acquired).
            // Belt-and-suspenders — either signal alone is enough.
            var isReinvest = ctx.Txn.Reinvest == true
                          || (sec is not null && sec.SplitAmount > 0);
            return isReinvest ? "divr" : "div";
        }

        if (ctx.Txn.XferType == "xfrtp_buysell")
            return sec is not null && sec.SplitAmount < 0 ? "sell" : "buy";

        if (ctx.Txn.XferType == "xfrtp_buysellxfr")
            // sec.samt ≥ 0 → buyx (including zero-qty basis transfers,
            // whose OFX xferdir confirms IN); sec.samt < 0 → sellx.
            return sec is not null && sec.SplitAmount < 0 ? "sellx" : "buyx";

        if (ctx.Txn.XferType == "xfrtp_dividendxfr")
            return "divx";

        if (ctx.Txn.XferType == "xfrtp_miscincexp")
        {
            // Direction comes from the splittype of the category leg:
            // inc → income, exp → expense. Both map to Ledger action
            // 'misc' (via MapTxnTypeToLedgerAction) — but the MD-side
            // txntype `inc`/`exp` distinction stays meaningful here for
            // future fidelity (e.g. preserving the original direction
            // even when amount-sign analysis would also reveal it).
            var hasExp = ctx.Txn.Splits.Any(s => s.InvestSplitType == "exp");
            return hasExp ? "exp" : "inc";
        }

        if (ctx.Txn.XferType == "xfrtp_bank")
            return "bank";

        return string.Empty;
    }

    // -- pair builders --------------------------------------------------------
    //
    // Posting-shape rules live in `Coffer.Domain.Investment.InvestmentPostings`
    // (shared with the API editor). These importer-side wrappers thin-wrap
    // each shared builder, attaching MD-specific feed_* metadata (payee /
    // memo / posted_at / external_id / counterparty_id pairing) that the
    // shared layer doesn't carry.

    private static (TransactionRow Cash, TransactionRow Holdings) MakeSecPair(
        MapCtx ctx,
        MdSplit sec,
        string action,
        Guid securityId,
        decimal quantity,
        decimal unitPrice,
        decimal cashAmount,
        decimal holdingsAmount,
        Guid? groupId,
        int legIndex)
    {
        var posting = InvestmentPostings.BuildSecPair(
            brokerageAccountId: ctx.Brokerage.Id,
            holdingsAccountId:  ctx.HoldingsAccountId,
            securityId:         securityId,
            cashAmount:         cashAmount,
            holdingsAmount:     holdingsAmount,
            quantity:           quantity,
            unitPrice:          unitPrice);

        return MaterializePair(ctx, posting, action,
            sourceSplit: sec,
            // Sec pair never has a category-side memo override (the sec
            // split's `desc` is just the security name, redundant with
            // the header's primary payee).
            counterpartyPayeeOverride: null,
            legIndex: legIndex,
            groupId:  groupId);
    }

    private static (TransactionRow Cash, TransactionRow Category) MakeCategoryPair(
        MapCtx ctx,
        MdSplit leg,
        Guid categoryAccountId,
        string action,
        Guid? groupId,
        Guid? securityId,
        string postingRole)
    {
        var posting = InvestmentPostings.BuildCategoryPair(
            brokerageAccountId: ctx.Brokerage.Id,
            categoryAccountId:  categoryAccountId,
            cashAmount:         AccountMapper.MinorUnitsToDecimal(leg.ParentAmount),
            categoryAmount:     AccountMapper.MinorUnitsToDecimal(leg.SplitAmount),
            postingRole:        postingRole,
            securityId:         securityId);

        return MaterializePair(ctx, posting, action,
            sourceSplit: leg,
            // Category pair: the leg's `desc` (when present) labels the
            // counterparty side specifically (e.g. "Reinvest dividend
            // Q4 2024"), so it wins over the header's primary payee on
            // the category side only.
            counterpartyPayeeOverride: NullIfEmpty(leg.Description),
            legIndex: leg.Index,
            groupId:  groupId);
    }

    private static (TransactionRow Cash, TransactionRow Other) MakeXferPair(
        MapCtx ctx,
        MdSplit xfr,
        Guid otherAccountId,
        Guid? groupId)
    {
        var posting = InvestmentPostings.BuildXferPair(
            brokerageAccountId: ctx.Brokerage.Id,
            otherAccountId:     otherAccountId,
            brokerageAmount:    AccountMapper.MinorUnitsToDecimal(xfr.ParentAmount),
            otherAmount:        AccountMapper.MinorUnitsToDecimal(xfr.SplitAmount));

        return MaterializePair(ctx, posting, action: LedgerActions.Transfer,
            sourceSplit: xfr,
            counterpartyPayeeOverride: NullIfEmpty(xfr.Description),
            legIndex: xfr.Index,
            groupId:  groupId);
    }

    /// <summary>
    /// Translate a domain-level <see cref="InvestmentPosting"/> into a
    /// matched pair of importer-side <see cref="TransactionRow"/>
    /// records. Stamps the MD-specific metadata the domain layer
    /// doesn't carry: cash/counterparty id pairing, external_id from
    /// the source split index, feed payee/memo/status/posted_at from
    /// the txn context, group id for multi-posting events.
    /// </summary>
    private static (TransactionRow Cash, TransactionRow Other) MaterializePair(
        MapCtx ctx,
        InvestmentPosting posting,
        string action,
        MdSplit sourceSplit,
        string? counterpartyPayeeOverride,
        int legIndex,
        Guid? groupId)
    {
        var externalId = $"{ctx.Txn.Id}:{legIndex}";
        var cashId     = Guid.NewGuid();
        var otherId    = Guid.NewGuid();
        var status     = NullIfEmpty(sourceSplit.Status) ?? ctx.PrimaryStatus;
        // ADR-0082 per-leg reconciliation source. The brokerage cash leg
        // (posting.Cash) follows the txn's OWN parent stat; the counterparty
        // leg follows its own split stat — EXCEPT the Holdings/security leg
        // (counterparty posting_role 'security'), a position that is never
        // reconciled, so it gets NO source (=> no overlay row). A category
        // counterparty gets the split stat but is dropped at persist.
        var cashReconStat  = ctx.PrimaryStatus;
        var otherReconStat = posting.Counterparty.PostingRole == "security"
            ? null
            : NullIfEmpty(sourceSplit.Status);

        var cash = new TransactionRow(
            Id:               cashId,
            AccountId:        posting.Cash.AccountId,
            Origin:           ctx.OriginAndProviderKey.Origin,
            ProviderKey:      ctx.OriginAndProviderKey.ProviderKey,
            ExternalId:       externalId,
            IsPending:        false,
            InvestmentAction: action,
            FeedPayee:        ctx.PrimaryPayee,
            FeedMemo:         ctx.PrimaryMemo,
            FeedAmount:       posting.Cash.Amount,
            FeedPostedAt:     ctx.Posted,
            FeedTransactedAt: ctx.Transacted,
            FeedStatus:       status,
            ImportSource:     ctx.ImportSource,
            CounterpartyId:   otherId,
            TxnGroupId:       groupId,
            LegIndex:         legIndex,
            SecurityId:       posting.Cash.SecurityId,
            Quantity:         posting.Cash.Quantity,
            UnitPrice:        posting.Cash.UnitPrice,
            CheckNumber:      null,
            PostingRole:      posting.Cash.PostingRole,
            ReconStat:        cashReconStat,
            // Mig 109 / ADR-0035 §3: forward verbatim MD JSON so it
            // lands on txn_headers.provider_raw_payload via BuildHeader.
            ProviderRawPayload: ctx.Txn.RawJson);

        var other = new TransactionRow(
            Id:               otherId,
            AccountId:        posting.Counterparty.AccountId,
            Origin:           ctx.OriginAndProviderKey.Origin,
            ProviderKey:      ctx.OriginAndProviderKey.ProviderKey,
            ExternalId:       externalId,
            IsPending:        false,
            InvestmentAction: action,
            FeedPayee:        counterpartyPayeeOverride ?? ctx.PrimaryPayee,
            FeedMemo:         counterpartyPayeeOverride ?? ctx.PrimaryMemo,
            FeedAmount:       posting.Counterparty.Amount,
            FeedPostedAt:     ctx.Posted,
            FeedTransactedAt: ctx.Transacted,
            FeedStatus:       status,
            ImportSource:     ctx.ImportSource,
            CounterpartyId:   cashId,
            TxnGroupId:       null,
            LegIndex:         legIndex,
            SecurityId:       posting.Counterparty.SecurityId,
            Quantity:         posting.Counterparty.Quantity,
            UnitPrice:        posting.Counterparty.UnitPrice,
            CheckNumber:      null,
            PostingRole:      posting.Counterparty.PostingRole,
            ReconStat:        otherReconStat,
            // Mig 109 / ADR-0035 §3 (same as cash side above).
            ProviderRawPayload: ctx.Txn.RawJson);

        return (cash, other);
    }

    // -- helpers --------------------------------------------------------------

    private static MapResult Skip(SkipReason reason) =>
        new(Rows: [], HoldingDelta: null, NewLot: null, Skip: reason);

    private static decimal ToShareQuantity(long minorShares, int shareDecimals)
    {
        // 10^shareDecimals; bounded by the schema CHECK to [0,6] so a small loop is fine.
        long divisor = 1;
        for (var i = 0; i < shareDecimals; i++) divisor *= 10;
        return minorShares / (decimal)divisor;
    }

    /// <summary>
    /// Per-share price (always positive). MD stores share quantity as a
    /// signed value — negative on Sells — and we preserve that sign on
    /// <c>txn_legs.quantity</c>. But unit_price is a magnitude: it's
    /// "how many dollars one share is worth", which is positive whether
    /// shares are being acquired or disposed. The trade direction lives
    /// in the qty + amount signs, never in the price.
    ///
    /// HISTORICAL: this used to be <c>cash / qty</c> with cash forced
    /// positive but qty left signed, producing negative unit prices on
    /// every Sell row. The drill-in showed "-0.5873 sh × -$10.42 =
    /// -$6.12" which double-negatived: qty × price came out positive
    /// while amount was negative. Wired up rows on 2026-05-19 scrub.
    /// </summary>
    private static decimal ComputeUnitPrice(MdSplit secSplit, int shareDecimals)
    {
        var qty = Math.Abs(ToShareQuantity(secSplit.SplitAmount, shareDecimals));
        if (qty == 0m) return 0m;
        var cash = Math.Abs(AccountMapper.MinorUnitsToDecimal(secSplit.ParentAmount));
        return cash / qty;
    }

    private static DateTimeOffset ResolvePostedAt(MdTxn txn)
    {
        // See TransactionMapper.ResolvePostedAt for the rationale on
        // preferring `dt` over `dtentered`.
        return TransactionMapper.ParseMdDate(txn.Date)
            ?? throw new InvalidDataException(
                $"investment txn {txn.Id} has unparseable dt={txn.Date}");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record MapCtx(
        MdTxn Txn,
        AccountRef Brokerage,
        Guid HoldingsAccountId,
        Guid LedgerId,
        DateTimeOffset Posted,
        string ImportSource,
        IReadOnlyDictionary<string, AccountRef> AccountByMdId,
        IReadOnlyDictionary<string, SecurityRef> SecurityByMdSecAcctId)
    {
        public DateTimeOffset? Transacted => TransactionMapper.ParseMdDate(Txn.TransactedDate);
        // Precedence matches PR #38: Description / Memo (what the user
        // sees in MD) wins; OlOrigPayee / OlOrigMemo (raw OFX original)
        // is fallback only.
        public string? PrimaryPayee  => NullIfEmpty(Txn.Description) ?? NullIfEmpty(Txn.OlOrigPayee);
        public string? PrimaryMemo   => NullIfEmpty(Txn.Memo)        ?? NullIfEmpty(Txn.OlOrigMemo);
        public string? PrimaryStatus => NullIfEmpty(Txn.Status);

        // Mig 107: decompose MD per-row metadata into the canonical
        // (origin, provider_key) pair. Same value for the cash and
        // counterparty TransactionRow in a pair; both end-sites read
        // this property so they don't drift.
        // Mig 110: pass the brokerage AccountRef so the classifier
        // can read its `olbfi` / `ofx_import_acct_num` to discriminate
        // online OFX from QFX file imports.
        public (string Origin, string? ProviderKey) OriginAndProviderKey
            => TransactionMapper.DecomposeOrigin(Txn, Brokerage);

        public MdSplit? RequireSecSplit() => Txn.Splits.FirstOrDefault(s => s.InvestSplitType == "sec");
        public MdSplit? RequireIncSplit() => Txn.Splits.FirstOrDefault(s => s.InvestSplitType == "inc");
        public MdSplit? RequireXfrSplit() => Txn.Splits.FirstOrDefault(s => s.InvestSplitType == "xfr");
        public IReadOnlyList<MdSplit> FeeSplits() =>
            [.. Txn.Splits.Where(s => s.InvestSplitType == "fee")];
    }
}
