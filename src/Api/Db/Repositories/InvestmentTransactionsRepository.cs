using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Data-access surface for investment-shape transactions
/// (ADR-0029). Mirrors <see cref="TransactionsRepository"/>'s
/// lifecycle (Create / Patch / Delete) but speaks the
/// investment shape directly — action + security + shares + price
/// + optional fee — and translates internally via the shared
/// <c>Coffer.Domain.Investment</c> posting builders.
/// </summary>
/// <remarks>
/// <para>Cost-basis math: this repository writes lot UnitCost
/// values as placeholders (price + apportioned commission). The
/// recompute function (<c>recompute_holdings_cost_basis</c>,
/// migration 056) re-derives the authoritative value based on the
/// brokerage's <c>is_trade_commission</c> flag and overrides what
/// we wrote.</para>
///
/// <para><b>Both recomputes</b> (balance + holdings/lots) are handled
/// automatically by interceptors on this context's
/// <c>SaveChangesAsync</c>:
/// <list type="bullet">
///   <item><see cref="LegDerivedRecomputeInterceptor"/>
///   (mig 102 / ADR-0034 + mig 120 / ADR-0036) re-derives
///   <c>txn_header_account_balances</c> AND the denormalized posting
///   counts on <c>txn_legs</c>.</item>
///   <item><see cref="HoldingsRecomputeInterceptor"/>
///   (mig 104) re-derives <c>holdings</c> + <c>lots</c> for every
///   (account, security) pair whose investment-shape legs changed
///   in this save — including the BOTH-ends case where a leg moves
///   between holdings.</item>
/// </list>
/// Every mutation method here inserts legs as EF-tracked rows and
/// reaches <c>SaveChangesAsync</c>, so both interceptors see the leg
/// changes from the ChangeTracker and the recomputes are implicit. No
/// explicit recompute call is needed (the legs were once inserted via
/// the <c>insert_investment_legs</c> TVF, which bypassed the
/// ChangeTracker and forced hand-driven recomputes; that TVF was
/// retired once the txn_legs trigger family went away — ADR-0032
/// lineage).</para>
/// </remarks>
public sealed class InvestmentTransactionsRepository
{
    private readonly AppDbContext _db;

    public InvestmentTransactionsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Typed outcome of a validation / lookup step. The endpoint
    /// surfaces each kind as a 422 with a stable error code.
    /// </summary>
    public enum CreateFailure
    {
        ActionInvalid,
        AccountNotInLedger,
        AccountNotInvestment,
        AccountMissingHoldingsSibling,
        SecurityRequired,
        SecurityNotInLedger,
        SharesRequired,
        SharesNonZero,
        PriceRequired,
        PricePositive,
        AmountRequired,
        CategoryRequired,
        CategoryNotInLedger,
        TransferRequired,
        TransferNotInLedger,
        FeeAmountRequired,
        FeeAmountPositive,
        FeeWithoutAccount,
        FeeAccountNotInLedger,
        // transfer_shares (in-kind, ADR-0065): the destination must be a
        // distinct investment account with a holdings sibling, and the qty to
        // move must be a positive amount the source actually holds.
        TransferSharesQtyPositive,
        TransferSharesToSelf,
        TransferSharesDestNotInvestment,
        TransferSharesDestMissingHoldingsSibling,
        TransferSharesInsufficientShares,
        // Inactive-account gate (PR #132 follow-up). Per-role so the
        // SPA's editor can place the error message next to the right
        // field (the existing per-role "*NotInLedger" pattern).
        BrokerageInactive,
        CategoryInactive,
        TransferInactive,
        FeeAccountInactive,
    }

    /// <summary>
    /// Result of <see cref="CreateAsync"/>. On <see cref="Ok"/>,
    /// <see cref="HeaderId"/> is the new header's id. On any
    /// other value, <see cref="HeaderId"/> is unset and the
    /// failure maps to a 422 error code at the endpoint.
    /// </summary>
    public readonly record struct CreateResult(
        CreateFailure? Failure,
        Guid HeaderId)
    {
        public static CreateResult Ok(Guid id) => new(null, id);
        public static CreateResult Fail(CreateFailure f) => new(f, Guid.Empty);
    }

    /// <summary>
    /// Result of <see cref="BuildTemplateLegsAsync"/> (ADR-0047). On success,
    /// <see cref="Legs"/> are the investment-shape <c>txn_legs</c> for a
    /// recurring TEMPLATE header; on failure, the shared
    /// <see cref="CreateFailure"/> maps to the same 422 vocabulary as a live
    /// investment create.
    /// </summary>
    internal readonly record struct TemplateLegsResult(
        CreateFailure? Failure,
        IReadOnlyList<TxnLegRow> Legs)
    {
        public static TemplateLegsResult Ok(IReadOnlyList<TxnLegRow> legs) => new(null, legs);
        public static TemplateLegsResult Fail(CreateFailure f) => new(f, Array.Empty<TxnLegRow>());
    }

    /// <summary>
    /// Build the investment-shape <c>txn_legs</c> for a recurring-reminder
    /// TEMPLATE header (ADR-0047), reusing the EXACT validation + account/
    /// security resolution + posting construction as <see cref="CreateAsync"/>
    /// (via <see cref="ValidateAndResolveAsync"/>). Unlike a live create this
    /// builds NO holdings + NO lots: a template is invisible to the holdings
    /// walk (<c>recompute_holdings_cost_basis</c> reads <c>live_txn_headers</c>;
    /// <see cref="HoldingsRecomputeInterceptor"/> skips template legs), so a
    /// manual investment reminder never touches holdings/lots/balances — the
    /// same keystone the importer relies on.
    /// </summary>
    /// <remarks>
    /// The caller (<c>RemindersRepository</c>) adds the returned legs alongside
    /// the template header it inserts (flagged <c>is_recurring_template</c>) in
    /// one <c>SaveChanges</c>; this method performs NO writes itself.
    /// </remarks>
    internal async Task<TemplateLegsResult> BuildTemplateLegsAsync(
        Guid ledgerId,
        Guid templateHeaderId,
        CreateInvestmentTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // transfer_shares is a one-time in-kind move, never a recurring shape.
        if (request.Action == LedgerActions.TransferShares)
            return TemplateLegsResult.Fail(CreateFailure.ActionInvalid);

        var (failure, _, postings, _) =
            await ValidateAndResolveAsync(ledgerId, request, allowInactiveAccounts: false, cancellationToken).ConfigureAwait(false);
        if (failure is { } f) return TemplateLegsResult.Fail(f);

        var legs = new List<TxnLegRow>(postings.Count * 2);
        for (var i = 0; i < postings.Count; i++)
        {
            var p = postings[i];
            legs.Add(ToTxnLegRow(LegInsertSpec.From(Guid.NewGuid(), templateHeaderId, ledgerId, i, p.Cash)));
            legs.Add(ToTxnLegRow(LegInsertSpec.From(Guid.NewGuid(), templateHeaderId, ledgerId, i, p.Counterparty)));
        }
        return TemplateLegsResult.Ok(legs);
    }

    /// <summary>
    /// Create a manual investment transaction. Validates the
    /// request against the action × field matrix in ADR-0029,
    /// resolves all referenced accounts + security in this
    /// ledger, builds posting legs via
    /// <see cref="InvestmentPostings"/>, and saves header + legs
    /// + holdings + lot in one Postgres transaction. Triggers
    /// the holdings cost-basis recompute function after commit so
    /// basis math stays consistent with the brokerage's policy flag.
    /// </summary>
    /// <remarks>Balances, holdings/lots, and posting counts all
    /// recompute automatically via
    /// <see cref="LegDerivedRecomputeInterceptor"/> +
    /// <see cref="HoldingsRecomputeInterceptor"/> on the
    /// <c>SaveChangesAsync</c> that persists the EF-tracked legs — no
    /// explicit recompute call here.</remarks>
    public async Task<CreateResult> CreateAsync(
        Guid ledgerId,
        CreateInvestmentTransactionRequest request,
        // Adjust-at-post fire (ADR-0049): when set, the committed occurrence is
        // stamped to its series + slot. Null = a normal live investment create.
        Guid? recurringTransactionId = null,
        DateOnly? occurrenceDate = null,
        // Bypass the PR #132 inactive-account gate. Only convert_in_kind_transfer
        // sets this (a historical correction on since-closed accounts); every live
        // create keeps the guard. See ValidateAndResolveAsync.
        bool allowInactiveAccounts = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate the action × field matrix + resolve accounts/security +
        // build the posting pairs. Shared verbatim with PATCH and the
        // recurring-template path (ADR-0047) — see ValidateAndResolveAsync.
        var (failure, holdingsAccountId, postings, destHoldingsAccountId) =
            await ValidateAndResolveAsync(ledgerId, request, allowInactiveAccounts, cancellationToken).ConfigureAwait(false);
        if (failure is { } f) return CreateResult.Fail(f);

        // Resolved values for the header + lot side of the write.
        var headerId = Guid.NewGuid();
        var action = request.Action;
        var postedAt = request.PostedAt;
        var totalCommission = request.FeeAmount ?? 0m;

        // transfer_shares (in-kind, ADR-0065): compute the source FIFO lots to
        // move + build the per-lot postings. On a fresh create the source lots
        // already reflect the correct pre-transfer state (this transfer doesn't
        // exist yet), so the plan can be read before the write.
        var transferPlan = (IReadOnlyList<MovedLot>)Array.Empty<MovedLot>();
        if (action == LedgerActions.TransferShares)
        {
            var (planFail, plan) = await BuildTransferSharesPlanAsync(
                ledgerId, holdingsAccountId, request.SecurityId!.Value,
                request.Shares!.Value, cancellationToken).ConfigureAwait(false);
            if (planFail is { } pf) return CreateResult.Fail(pf);
            transferPlan = plan;
            postings = BuildTransferSharesPostings(
                request.BrokerageAccountId, holdingsAccountId,
                request.TransferAccountId!.Value, destHoldingsAccountId!.Value,
                request.SecurityId!.Value, transferPlan);
        }

        // Reuse-from-fire (ADR-0049): when the reminder fire path has already
        // opened a transaction (to make the committed occurrence + catch-up
        // atomic), JOIN it instead of nesting — and let that caller commit.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        _db.TxnHeaders.Add(new TxnHeaderRow
        {
            Id = headerId,
            LedgerId = ledgerId,
            Origin = "manual",
            Payee = request.Payee,
            Memo = request.Memo,
            CheckNumber = request.CheckNumber,
            PostedAt = postedAt,
            // NOT NULL since mig 189 — see TransactionsRepository.
            TransactedAt = request.TransactedAt ?? postedAt,
            Action = action,
            // Adjust-at-post fire stamps the occurrence to its series + slot
            // (null for a normal live create).
            RecurringTransactionId = recurringTransactionId,
            OccurrenceDate = occurrenceDate,
        });

        // Multi-posting headers share an implicit group via header_id;
        // the resolved_transactions view exposes that as `txn_group_id`
        // (computed CASE WHEN EXISTS (posting_index > 0)). No persistence
        // step needed beyond inserting all legs under the same header.
        var legs = new List<LegInsertSpec>(postings.Count * 2);
        var firstHoldingsLegId = Guid.Empty;
        // For transfer_shares: the destination leg id of each posting (= the
        // moved lot at the same index), so the per-lot destination rows bind to
        // the right leg after the insert flush.
        var transferDestLegIds = action == LedgerActions.TransferShares
            ? new List<Guid>(postings.Count)
            : null;

        for (var i = 0; i < postings.Count; i++)
        {
            var p = postings[i];
            var cashId = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            legs.Add(LegInsertSpec.From(cashId, headerId, ledgerId, i, p.Cash));
            legs.Add(LegInsertSpec.From(otherId, headerId, ledgerId, i, p.Counterparty));

            // transfer_shares: the destination posting's Counterparty is the
            // dest holdings leg (the side each moved-lot row binds to). Capture
            // those in plan order (source/dest postings alternate).
            if (transferDestLegIds is not null
                && p.Counterparty.AccountId == destHoldingsAccountId
                && p.Counterparty.PostingRole == PostingRoles.Security)
            {
                transferDestLegIds.Add(otherId);
            }

            if (p.Counterparty.AccountId == holdingsAccountId
                && p.Counterparty.PostingRole == PostingRoles.Security
                && firstHoldingsLegId == Guid.Empty)
            {
                firstHoldingsLegId = otherId;
            }
        }

        // Flush the header alone so its row exists when the leg
        // INSERT (next step) takes its FK reference: legs need the
        // header in the DB to satisfy txn_legs.header_id -> txn_headers(id).
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // EF-tracked multi-row leg insert. SaveChangesAsync fires both
        // LegDerivedRecomputeInterceptor (balances + posting counts)
        // and HoldingsRecomputeInterceptor (holdings + lots) from the
        // ChangeTracker entries — synchronously within this call — so
        // the holding row is created (HoldingsRecomputeService's
        // auto-create) by the time GetHoldingIdAsync runs below, and
        // the multi-posting counts are corrected from DEFAULT 1. No
        // explicit recompute call needed.
        _db.TxnLegs.AddRange(legs.Select(ToTxnLegRow));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (action == LedgerActions.TransferShares)
        {
            // In-kind: create the destination lots one-per-moved-lot with the
            // inherited acquired_at + unit_cost (ADR-0065). The source lots are
            // consumed FIFO by the recompute from the source −qty legs (no gain).
            await CreateTransferSharesLotsAsync(
                ledgerId, destHoldingsAccountId!.Value, request.SecurityId!.Value,
                transferPlan, transferDestLegIds!, cancellationToken).ConfigureAwait(false);

            // The destination lots are created out-of-band, AFTER the leg-save
            // above already fired HoldingsRecomputeInterceptor (which ran without
            // them), and a lots-only save does not re-trigger it (it keys on leg
            // changes). So explicitly re-derive BOTH holdings now that the
            // transfer-in lots exist — otherwise a destination sale dated AFTER the
            // transfer oversells against the not-yet-existing lots and books a
            // phantom gain instead of carrying basis (ADR-0065 D2).
            await new HoldingsRecomputeService(_db).RecomputeAsync(
                new[]
                {
                    (holdingsAccountId, request.SecurityId!.Value),
                    (destHoldingsAccountId!.Value, request.SecurityId!.Value),
                },
                cancellationToken).ConfigureAwait(false);
        }
        else if (LedgerActions.TouchesHoldings(action) && request.SecurityId is { } securityId)
        {
            // Acquisition actions (Buy / BuyXfr / DivReinvest) need a
            // matching lot row. The lot binds to the holding via
            // holding_id; query the (now-existing) holding for its id
            // and add the lot. Sell-side actions don't add lots — the
            // recompute's FIFO walk closes existing lots in place.
            var quantity = request.Shares ?? 0m;
            // Lot cost basis uses the authoritative amount (2dp), not a
            // re-derivation from the rounded price — same money the sec leg
            // carries, so basis == cash paid.
            var (sharePrice, _) = ResolveTradeMoney(request.Amount, request.Price ?? 0m, quantity);
            var impact = InvestmentPostings.BuildHoldingsImpact(
                action: action,
                holdingsAccountId: holdingsAccountId,
                securityId: securityId,
                quantity: quantity,
                sharePrice: sharePrice,
                totalCommission: totalCommission,
                asOf: postedAt);

            if (impact is { NewLot: { } lotSpec } && firstHoldingsLegId != Guid.Empty)
            {
                var holdingId = await GetHoldingIdAsync(
                    holdingsAccountId, securityId, ledgerId, cancellationToken)
                    .ConfigureAwait(false);

                _db.Lots.Add(new LotRow
                {
                    Id = Guid.NewGuid(),
                    HoldingId = holdingId,
                    LegId = firstHoldingsLegId,
                    LedgerId = ledgerId,
                    Quantity = lotSpec.Quantity,
                    UnitCost = lotSpec.UnitCost,
                    AcquiredAt = lotSpec.AcquiredAt.UtcDateTime,
                    IsClosed = false,
                });
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (ownsTransaction)
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CreateResult.Ok(headerId);
    }

    // ----------------------------------------------------------------
    // PATCH — full postings reshape (ADR-0025 / ADR-0029)
    // ----------------------------------------------------------------

    /// <summary>
    /// Typed outcome of <see cref="PatchAsync"/>. <see cref="HeaderNotFound"/>
    /// (header doesn't exist or isn't in this ledger) and
    /// <see cref="HeaderNotInvestment"/> (header exists but is a
    /// bank-shape txn — cross-topic protection) are the two PATCH-
    /// specific failures; everything else is shared with
    /// <see cref="CreateFailure"/> (same shape validation).
    /// </summary>
    public enum PatchFailure
    {
        HeaderNotFound,
        HeaderNotInvestment,
        // Merge (mirrors the bank side): the editor row is no longer a
        // fresh needs_review row, or the chosen candidate isn't a settled,
        // visible, non-loser header. One 422; the SPA never legitimately
        // produces it.
        MergeSourceInvalid,
    }

    public readonly record struct PatchResult(
        PatchFailure? PatchFail,
        CreateFailure? CreateFail)
    {
        public static PatchResult Ok() => new(null, null);
        public static PatchResult Fail(PatchFailure f) => new(f, null);
        public static PatchResult Fail(CreateFailure f) => new(null, f);
    }

    /// <summary>
    /// PATCH an existing investment txn. Per ADR-0025, postings
    /// replace wholesale — the supplied <paramref name="request"/>
    /// IS the new state of the world. Existing legs + lots are
    /// dropped; the new shape is built via the same per-action
    /// posting builders as <see cref="CreateAsync"/>. Recompute
    /// fires after commit.
    /// </summary>
    /// <remarks>
    /// <para>PATCH body fields are nullable at the wire
    /// (PATCH-flavored shape), but semantically the supplied set IS
    /// the full new shape — nulls mean "this field is null in the
    /// new state," not "leave the old value alone." The action ×
    /// field matrix is re-validated against the resulting
    /// (post-patch) shape.</para>
    ///
    /// <para>Legs KEEP their ids across an edit wherever the new shape still
    /// has a leg with the same (account, posting_role, security) — see the
    /// block that does the matching. Only what the new shape has no room for
    /// is deleted. This exists because anything keyed on leg_id used to die
    /// with the row, <c>txn_leg_recon</c> most damagingly.</para>
    ///
    /// <para>Balances and posting counts recompute via
    /// <see cref="LegDerivedRecomputeInterceptor"/>, whose Modified branch
    /// supplies both the OLD and the NEW pair from OriginalValues — so the
    /// two-flush split that used to carry that is no longer what makes it
    /// correct. Holdings and lots are reconciled EXPLICITLY at the end:
    /// <see cref="HoldingsRecomputeInterceptor"/> watches legs only, so a
    /// shape-identical edit produces nothing for it to see.</para>
    /// </remarks>
    public async Task<PatchResult> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        PatchInvestmentTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _db.TxnHeaders
            .FirstOrDefaultAsync(
                h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken).ConfigureAwait(false);
        if (existing is null) return PatchResult.Fail(PatchFailure.HeaderNotFound);

        // Investment-side merge (mirrors bank PatchAsync, slice
        // "investment-merge"). A merge-only PATCH carries just
        // MergeFromHeaderId: the editor row (existing) folds into the chosen
        // candidate. Validate both ends, stamp loser→winner + the winner's
        // adopted date, and return early — no leg reshape (the loser is
        // tombstoned, not rebuilt). The server re-enforces the same gates the
        // candidates query used, independently of the UI filter
        // (server-side-concurrency principle). Direction is inverted like
        // bank: the URL headerId is the LOSER, MergeFromHeaderId the WINNER.
        if (request.MergeFromHeaderId is { } mergeWinnerId)
        {
            // Editor row must still be a fresh, undecided, visible row —
            // merging an accepted/merged/hidden row would mutate a tombstone.
            // is_hidden is canonical since mig 230.
            if (!existing.NeedsReview
                || existing.IsMergedInto is not null
                || existing.IsHidden
                || mergeWinnerId == headerId)
                return PatchResult.Fail(PatchFailure.MergeSourceInvalid);

            var winner = await _db.TxnHeaders
                .FirstOrDefaultAsync(h => h.Id == mergeWinnerId && h.LedgerId == ledgerId,
                    cancellationToken).ConfigureAwait(false);
            // Candidate must be settled + visible + not itself a loser.
            // Winners ARE allowed (multi-source collapse keeps a one-hop graph).
            if (winner is null
                || winner.NeedsReview
                || winner.IsMergedInto is not null
                || winner.IsHidden)
                return PatchResult.Fail(PatchFailure.MergeSourceInvalid);

            // The loser's holdings-side (account, security) pairs — its shares
            // must stop counting once it's merged. Collected before the stamp
            // (the legs don't move).
            var loserPairs = await _db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId
                    && l.SecurityId != null
                    && l.Quantity != null)
                .Select(l => new { l.AccountId, SecurityId = l.SecurityId!.Value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            await using var mergeTx = await _db.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            existing.IsMergedInto = winner.Id;   // editor row → loser
            winner.IsMergeWinner = true;          // candidate → winner (idempotent)
            // ...and the loser is no longer awaiting review. The bank editor has
            // always sent `approve: true` alongside its merge stamp — "to keep
            // state coherent if the row is ever surfaced again" — and the
            // investment branch, which takes a merge-ONLY patch and returns before
            // any approve handling, never did. So investment losers alone stayed
            // flagged, and the sidebar's review dot stayed lit on an account whose
            // register had nothing left to review.
            //
            // Done HERE rather than by teaching the SPA to send the flag: the
            // server owns the end state, and a merge-only PATCH should not depend
            // on a caller remembering to pair it with an approve.
            existing.NeedsReview = false;
            // Winner adopts the imported (loser) row's date — the fresh feed
            // row's date is authoritative for the merged event. Written onto the
            // winner's canonical row since mig 230, with its feed date captured
            // to txn_header_originals first. Balance-relevant, so the balance
            // recompute interceptor rewalks the winner's account on save.
            var importedPostedAt = request.PostedAt ?? existing.PostedAt;
            await SetPostedAtAsync(winner, importedPostedAt, cancellationToken)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // Stamping is_merged_into on the header doesn't mutate txn_legs, so
            // the HoldingsRecomputeInterceptor never fires — trigger the holdings
            // recompute explicitly. Mig 163 makes the recompute exclude merged
            // legs, so the loser's shares drop out. Same transaction, so it's
            // atomic with the stamp.
            //
            // The WINNER's pairs go in too, and that became load-bearing with mig
            // 229. The walk orders by the date the line above just moved — so the
            // winner's lots, basis and realized gains can re-sort against
            // everything else in that holding, and recomputing only the loser
            // would leave the winner's figures derived from its pre-merge
            // position. (229 moved the walk onto the effective date; 230 made
            // that the only date there is.)
            var winnerPairs = await _db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == winner.Id
                    && l.SecurityId != null
                    && l.Quantity != null)
                .Select(l => new { l.AccountId, SecurityId = l.SecurityId!.Value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            await new HoldingsRecomputeService(_db)
                .RecomputeAsync(
                    loserPairs.Select(p => (p.AccountId, p.SecurityId))
                        .Concat(winnerPairs.Select(p => (p.AccountId, p.SecurityId)))
                        .Distinct(),
                    cancellationToken)
                .ConfigureAwait(false);

            // ADR-0082 merge → reconciling, as the bank branch has always done.
            // A feed match is the INSTITUTION acknowledging the transaction, and
            // nothing in that reasoning depends on the institution being a bank:
            // a brokerage confirming a trade is the same evidence in the same
            // shape. The survivor's leg becomes 'reconciling' unless it is
            // already 'cleared' — the stronger, user-affirmed state wins.
            //
            // THE BROKERAGE, not "the first non-category account" the bank picks.
            // An investment header also carries legs on the HOLDINGS sibling,
            // which is an account of type 'investment' and would satisfy that
            // heuristic; the recon status belongs on the cash sleeve the register
            // actually shows. A brokerage is the account that POINTS AT a
            // holdings account, which is how the rest of this file identifies
            // one.
            var brokerageAccountId = await _db.TxnLegs
                .Where(l => l.HeaderId == headerId)
                .Join(_db.Accounts, l => l.AccountId, a => a.Id, (l, a) => a)
                .Where(a => a.HoldingsAccountId != null)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (brokerageAccountId is { } brokerageId)
            {
                var winnerLegId = await _db.TxnLegs
                    .Where(l => l.HeaderId == winner.Id && l.AccountId == brokerageId)
                    .Select(l => (Guid?)l.Id)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (winnerLegId is { } legId)
                {
                    var current = await _db.TxnLegRecon
                        .FirstOrDefaultAsync(r => r.LegId == legId, cancellationToken)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        // 'reconciling' never carries clearedAt/clearedBy — those
                        // belong to 'cleared', which only a person sets.
                        _db.TxnLegRecon.Add(new TxnLegReconRow
                        {
                            LegId = legId,
                            LedgerId = ledgerId,
                            Status = "reconciling",
                        });
                    }
                    else if (current.Status != "cleared")
                    {
                        current.Status = "reconciling";
                    }
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await mergeTx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PatchResult.Ok();
        }

        // ADR-0031 Phase 3d.2 + gap-fix: PATCH allows converting a
        // bank-shape header into investment-shape when either:
        //   * Classifier hint is set (orchestrator detected a known
        //     pattern at sync time — the canonical hinted upgrade),
        //     OR
        //   * The header came from a feed source other than manual
        //     entry (origin != 'manual'). Real-world data shows the
        //     classifier doesn't match every institution's wire
        //     format (different brokerages use different description
        //     conventions), but the user still needs a path to
        //     upgrade these rows manually. Origin-gating keeps
        //     regular bank-shape manual entries from being
        //     accidentally converted — only feed-imported rows are
        //     upgradable without a classifier hit.
        // Header_id stays stable across the upgrade so the FITID
        // dedup link survives the next sync.
        if (existing.Action is null
            && existing.IngestActionHint is null
            && existing.Origin == "manual")
            return PatchResult.Fail(PatchFailure.HeaderNotInvestment);

        // Translate the PATCH-shape request into the create-shape
        // contract, then reuse the same validation + posting-build
        // pipeline. Wholesale-replace semantics: every field on the
        // PATCH body becomes the new state of the world.
        var asCreate = new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = request.BrokerageAccountId ?? Guid.Empty,
            PostedAt           = request.PostedAt           ?? existing.PostedAt,
            Action             = request.Action             ?? existing.Action ?? string.Empty,
            Payee              = request.Payee,
            Memo               = request.Memo,
            CheckNumber        = request.CheckNumber,
            TransactedAt       = request.TransactedAt,
            SecurityId         = request.SecurityId,
            Shares             = request.Shares,
            Price              = request.Price,
            Amount             = request.Amount,
            CategoryAccountId  = request.CategoryAccountId,
            TransferAccountId  = request.TransferAccountId,
            FeeAccountId       = request.FeeAccountId,
            FeeAmount          = request.FeeAmount,
        };

        // brokerageAccountId must be supplied (PATCH can move a txn
        // between brokerages, but the destination is required).
        if (asCreate.BrokerageAccountId == Guid.Empty)
            return PatchResult.Fail(CreateFailure.AccountNotInLedger);

        // Same validation + resolution + posting build as CreateAsync (the
        // PATCH body IS the new state of the world per ADR-0025). For
        // transfer_shares, postings are empty here and built after the leg-drop
        // flush below (the FIFO plan must read the RESTORED source lots).
        var (failure, holdingsAccountId, postings, destHoldingsAccountId) =
            await ValidateAndResolveAsync(ledgerId, asCreate, allowInactiveAccounts: false, cancellationToken).ConfigureAwait(false);
        if (failure is { } f) return PatchResult.Fail(f);

        var totalCommission = asCreate.FeeAmount ?? 0m;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Drop existing legs + lots tied to this header. CASCADE on
        // the lots FK (leg_id) handles per-lot cleanup; explicit
        // delete keeps the change tracker in sync. With the holdings
        // trigger family retired (mig 104), HoldingsRecomputeInterceptor
        // captures the OLD (account, security) pairs from the
        // tracked-delete entries AND the NEW pairs from the
        // subsequent INSERTs in the same SaveChanges, so both old +
        // new holdings reconcile in one post-save recompute call.
        var oldLegIds = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var oldLots = await _db.Lots
            .Where(lot => oldLegIds.Contains(lot.LegId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.Lots.RemoveRange(oldLots);
        var oldLegs = await _db.TxnLegs
            .Where(l => l.HeaderId == headerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // NOT removed yet — which legs survive is decided below, once the new
        // specs exist to match against.

        // Update header in place. The feed's values are captured to
        // txn_header_originals first (mig 230) — this path has always written
        // canonical and, until 230, captured nothing at all, so an investment
        // edit destroyed the imported values outright. Rows edited before 230
        // are past recovering; from here on both paths keep the original.
        await HeaderOriginals.CaptureAsync(_db, existing, cancellationToken)
            .ConfigureAwait(false);
        existing.Payee        = asCreate.Payee;
        existing.Memo         = asCreate.Memo;
        existing.CheckNumber  = asCreate.CheckNumber;
        existing.PostedAt     = asCreate.PostedAt;
        // NOT NULL since mig 189: a null request value means "no distinct tax
        // date", stored as the posted date. This is the in-place UPDATE path — the
        // one the create-path coalesce above does not cover.
        existing.TransactedAt = asCreate.TransactedAt ?? asCreate.PostedAt;
        existing.Action       = asCreate.Action;
        // Clear the needs-review flag — a successful investment PATCH
        // IS the user's act of approval. Unlike the bank-shape PATCH
        // (which uses an explicit Approve=true flag because the user
        // may also "save" without approving), the investment editor's
        // only Save-pressed exit IS Accept; there's no concept of
        // "save changes but leave it flagged for later." Aligns the
        // register's reconciliation indicator with the row's actual
        // upgraded state.
        existing.NeedsReview = false;

        // Multi-posting grouping is implicit via shared header_id —
        // see CreateAsync's note. Build leg specs first; insert as
        // EF-tracked rows after the leg-drop + header-update flush.
        // (transfer_shares: postings is empty here — the per-lot postings + legs
        // are built after the drop flush, once the source lots are restored.)
        var legs = new List<LegInsertSpec>(postings.Count * 2);
        var firstHoldingsLegId = Guid.Empty;
        var transferPlan = (IReadOnlyList<MovedLot>)Array.Empty<MovedLot>();
        List<Guid>? transferDestLegIds = null;

        for (var i = 0; i < postings.Count; i++)
        {
            var p = postings[i];
            var cashId  = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            legs.Add(LegInsertSpec.From(cashId, headerId, ledgerId, i, p.Cash));
            legs.Add(LegInsertSpec.From(otherId, headerId, ledgerId, i, p.Counterparty));

            if (p.Counterparty.AccountId == holdingsAccountId
                && p.Counterparty.PostingRole == PostingRoles.Security
                && firstHoldingsLegId == Guid.Empty)
            {
                firstHoldingsLegId = otherId;
            }
        }

        // ----------------------------------------------------------------
        // Keep leg IDENTITY across the edit.
        //
        // This used to delete every leg and rebuild with fresh ids. Anything
        // keyed on leg_id therefore died with the row — and txn_leg_recon.leg_id
        // cascades on delete (mig 171), so editing a CLEARED brokerage row, even
        // just its memo, silently reverted it to uncleared and destroyed
        // cleared_at / cleared_by_user_id. A reconciled brokerage account drifted
        // with nothing on screen to say why. The bank path never had this: it
        // keeps the legs a request names by legId and mutates them in place.
        //
        // The investment editor cannot name legs — they are DERIVED from the
        // action x field matrix, so the client never sees them — so identity is
        // re-established here instead, by matching old to new on
        // (account_id, posting_role, security_id).
        //
        // That key is load-bearing, not incidental: it is what makes a reused id
        // unable to migrate between (account, security) pairs. realized_gains
        // carries UNIQUE (sell_leg_id) (mig 148) and the recompute deletes and
        // re-inserts per pair, so an id that moved across pairs could race the two
        // per-pair recomputes and collide. Narrowing this key would open that.
        // ----------------------------------------------------------------
        var reusedLegIds = new List<Guid>();
        List<LegInsertSpec> legsToInsert;
        if (asCreate.Action == LedgerActions.TransferShares)
        {
            // transfer_shares is EXCLUDED from reuse. Its FIFO plan is computed
            // from committed lots after the drop flush precisely so it cannot see
            // this transfer's own effect (see below), which means its legs must
            // genuinely be gone by then. Its postings are also per-moved-lot, so
            // there is no stable (account, role, security) identity to match on —
            // every lot's leg looks alike under that key.
            _db.TxnLegs.RemoveRange(oldLegs);
            legsToInsert = legs;
        }
        else
        {
            var pool = new List<TxnLegRow>(oldLegs);
            legsToInsert = new List<LegInsertSpec>(legs.Count);
            for (var i = 0; i < legs.Count; i++)
            {
                var spec = legs[i];
                // Null-safe on security_id: cash-side legs carry NULL, and in SQL
                // semantics NULL never equals NULL — matching those by == would
                // silently rebuild every cash leg.
                var match = pool.FirstOrDefault(l =>
                    l.AccountId == spec.Leg.AccountId
                    && l.PostingRole == spec.Leg.PostingRole
                    && Nullable.Equals(l.SecurityId, spec.Leg.SecurityId));
                if (match is null)
                {
                    legsToInsert.Add(spec);
                    continue;
                }

                pool.Remove(match);
                // Mutate in place. PostingIndex is deliberately NOT set here — the
                // renumber happens after a shift flush below, so a kept leg cannot
                // collide with a doomed one on uq_txn_legs_posting.
                match.Amount      = spec.Leg.Amount;
                match.LegMemo     = spec.Leg.LegMemo;
                match.Quantity    = spec.Leg.Quantity;
                match.UnitPrice   = spec.Leg.UnitPrice;
                reusedLegIds.Add(match.Id);
                // The lot binds to the holdings leg by id, so the spec has to
                // carry the id that actually survived.
                legs[i] = spec with { Id = match.Id };
            }

            // Whatever the new shape had no room for.
            _db.TxnLegs.RemoveRange(pool);
        }

        // The holdings leg may now be a REUSED id rather than the freshly
        // generated one, and the acquisition lot binds to it. Recomputed here
        // rather than in the build loop, which ran before matching.
        if (asCreate.Action != LedgerActions.TransferShares)
        {
            firstHoldingsLegId = Guid.Empty;
            foreach (var spec in legs)
            {
                if (spec.Leg.AccountId == holdingsAccountId
                    && spec.Leg.PostingRole == PostingRoles.Security)
                {
                    firstHoldingsLegId = spec.Id;
                    break;
                }
            }
        }

        // A surviving leg keeps its txn_leg_recon row, and that is the POINT of
        // reusing the identity — the reconciled state is what an edit used to
        // destroy. It is deliberately not dropped here; nor does the bank path
        // drop it.
        //
        // This block also used to delete the leg's txn_leg_overrides row, on the
        // reasoning that a stale override would mask the amount just saved. That
        // table went away in migration 230 (zero rows in every database, never
        // written by the API), so only the index dance is left.
        if (reusedLegIds.Count > 0)
        {
            // Park kept legs at indices nothing else can claim, and flush, so the
            // final renumber below cannot transiently collide with a leg being
            // deleted in the same save. The bank calls the equivalent step
            // mandatory; EF gives no ordering guarantee between the delete and the
            // update that wants the vacated index.
            //
            // Only when something actually MOVES. Shifting unconditionally would
            // also make every reused leg Modified on every edit, which would mask
            // the holdings-recompute gap below behind an accident: the interceptor
            // watches legs, so it would fire for the wrong reason and stop firing
            // the day someone tightened this.
            var finalIndexById = legs.ToDictionary(x => x.Id, x => x.PostingIndex);
            var anyIndexMoves = oldLegs.Any(l =>
                finalIndexById.TryGetValue(l.Id, out var target) && target != l.PostingIndex);
            if (anyIndexMoves)
            {
                foreach (var leg in oldLegs)
                {
                    if (reusedLegIds.Contains(leg.Id)) leg.PostingIndex += PostingIndexShift;
                }
            }
        }

        // Flush: lot deletes + leg deletes + header update. Both
        // interceptors capture the OLD (account, security) +
        // (account, posted_at) pairs from the tracked deletes — that
        // covers the holding(s) the dropped legs belonged to AND any
        // accounts whose balance walks need to retrace past the now-
        // gone rows. Cross-(brokerage,security) PATCHes still need
        // the NEW side reconciled too; that's the explicit recompute
        // call after the TVF insert below.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // transfer_shares (in-kind, ADR-0065): now that the drop flush has
        // reconciled the source holding (the OLD transfer's −qty legs are gone,
        // so the source lots are restored to pre-transfer state), compute the
        // FIFO plan + build the per-lot postings + legs. Done here — not before
        // the drop — so the plan never double-counts THIS transfer's own effect.
        if (asCreate.Action == LedgerActions.TransferShares)
        {
            var (planFail, plan) = await BuildTransferSharesPlanAsync(
                ledgerId, holdingsAccountId, asCreate.SecurityId!.Value,
                asCreate.Shares!.Value, cancellationToken).ConfigureAwait(false);
            if (planFail is { } pf) return PatchResult.Fail(pf);  // await using rolls back

            transferPlan = plan;
            postings = BuildTransferSharesPostings(
                asCreate.BrokerageAccountId, holdingsAccountId,
                asCreate.TransferAccountId!.Value, destHoldingsAccountId!.Value,
                asCreate.SecurityId!.Value, transferPlan);

            transferDestLegIds = new List<Guid>(transferPlan.Count);
            for (var i = 0; i < postings.Count; i++)
            {
                var p = postings[i];
                var cashId  = Guid.NewGuid();
                var otherId = Guid.NewGuid();
                legs.Add(LegInsertSpec.From(cashId, headerId, ledgerId, i, p.Cash));
                legs.Add(LegInsertSpec.From(otherId, headerId, ledgerId, i, p.Counterparty));
                // Only the dest holdings legs bind per-lot rows.
                if (p.Counterparty.AccountId == destHoldingsAccountId.Value
                    && p.Counterparty.PostingRole == PostingRoles.Security)
                {
                    transferDestLegIds.Add(otherId);
                }
            }
        }

        // EF-tracked multi-row leg insert. This SaveChangesAsync fires
        // both interceptors from the ChangeTracker: HoldingsRecompute
        // (auto-creates the NEW (brokerage, security) holding row, so
        // GetHoldingIdAsync below finds it) and LegDerivedRecompute
        // (balances + posting counts). Legs KEPT from the old shape are
        // Modified rather than Added, and that branch reports both their old
        // and new pair, so the OLD side is covered without the delete.
        //
        // Now that the doomed legs are gone, the kept ones can take their final
        // indices without contending for one.
        if (reusedLegIds.Count > 0)
        {
            var specById = legs.ToDictionary(x => x.Id, x => x.PostingIndex);
            foreach (var leg in oldLegs)
            {
                if (specById.TryGetValue(leg.Id, out var finalIndex))
                    leg.PostingIndex = finalIndex;
            }
        }
        _db.TxnLegs.AddRange(legsToInsert.Select(ToTxnLegRow));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (asCreate.Action == LedgerActions.TransferShares)
        {
            // In-kind: re-create the destination lots one-per-moved-lot with the
            // inherited acquired_at + unit_cost (ADR-0065).
            await CreateTransferSharesLotsAsync(
                ledgerId, destHoldingsAccountId!.Value, asCreate.SecurityId!.Value,
                transferPlan, transferDestLegIds!, cancellationToken).ConfigureAwait(false);

            // Re-derive both holdings now that the transfer-in lots exist — the
            // leg-save recompute above ran without them, and a lots-only save does
            // not re-trigger it. Without this a later destination sale oversells
            // against the missing lots and books a phantom gain (see CreateAsync).
            await new HoldingsRecomputeService(_db).RecomputeAsync(
                new[]
                {
                    (holdingsAccountId, asCreate.SecurityId!.Value),
                    (destHoldingsAccountId!.Value, asCreate.SecurityId!.Value),
                },
                cancellationToken).ConfigureAwait(false);
        }
        // Add the acquisition lot in a second flush so it can bind
        // to the now-existing holding's id. Same shape as CreateAsync.
        else if (LedgerActions.TouchesHoldings(asCreate.Action) && asCreate.SecurityId is { } securityId)
        {
            var quantity = asCreate.Shares ?? 0m;
            // Lot cost basis uses the authoritative amount (2dp), not a
            // re-derivation from the rounded price — same money the sec leg
            // carries, so basis == cash paid.
            var (sharePrice, _) = ResolveTradeMoney(asCreate.Amount, asCreate.Price ?? 0m, quantity);
            var impact = InvestmentPostings.BuildHoldingsImpact(
                action: asCreate.Action,
                holdingsAccountId: holdingsAccountId,
                securityId: securityId,
                quantity: quantity,
                sharePrice: sharePrice,
                totalCommission: totalCommission,
                asOf: asCreate.PostedAt);

            if (impact is { NewLot: { } lotSpec } && firstHoldingsLegId != Guid.Empty)
            {
                var holdingId = await GetHoldingIdAsync(
                    holdingsAccountId, securityId, ledgerId, cancellationToken)
                    .ConfigureAwait(false);

                _db.Lots.Add(new LotRow
                {
                    Id = Guid.NewGuid(),
                    HoldingId = holdingId,
                    LegId = firstHoldingsLegId,
                    LedgerId = ledgerId,
                    Quantity = lotSpec.Quantity,
                    UnitCost = lotSpec.UnitCost,
                    AcquiredAt = lotSpec.AcquiredAt.UtcDateTime,
                    IsClosed = false,
                });
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // HoldingsRecomputeInterceptor watches txn_legs and nothing else, so it
        // used to fire on every PATCH only because every PATCH deleted every leg.
        // Now a shape-identical edit — a memo fix, or a DATE change, which reorders
        // the FIFO walk — can produce no leg diff at all and recompute nothing,
        // leaving lots, realized_gains and cost basis derived from the old order.
        // So the pairs this header touches, before and after, are reconciled
        // explicitly. Recompute is a full re-derivation, so doing it when the
        // interceptor already did is a no-op, not a double-count.
        var touchedPairs = oldLegs
            .Where(l => l.SecurityId is not null && l.Quantity is not null)
            .Select(l => (l.AccountId, SecurityId: l.SecurityId!.Value))
            .Concat(legs
                .Where(x => x.Leg.SecurityId is not null && x.Leg.Quantity is not null)
                .Select(x => (x.Leg.AccountId, SecurityId: x.Leg.SecurityId!.Value)))
            .Distinct()
            .ToList();
        if (touchedPairs.Count > 0)
        {
            await new HoldingsRecomputeService(_db)
                .RecomputeAsync(touchedPairs, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return PatchResult.Ok();
    }

    /// <summary>
    /// Offset kept legs are parked at while their doomed siblings are deleted.
    /// Larger than any plausible posting count, so a parked index can never be
    /// one a final renumber wants.
    /// </summary>
    private const int PostingIndexShift = 10_000;

    // ----------------------------------------------------------------
    // DELETE — hard-delete manual / soft-hide imported
    // ----------------------------------------------------------------

    public enum DeleteOutcome
    {
        HardDeleted,
        SoftHidden,
        HeaderNotFound,
        HeaderNotInvestment,
    }

    /// <summary>
    /// Remove an investment txn from the user-visible register.
    /// Same hard-delete vs soft-hide policy as
    /// <see cref="TransactionsRepository.DeleteAsync"/>: rows
    /// without <c>external_id</c> are hard-deleted (CASCADE drops
    /// legs + lots; recompute reconciles holdings); rows WITH
    /// <c>external_id</c> are soft-hidden (<c>is_hidden=true</c>)
    /// so re-source idempotency holds.
    /// </summary>
    /// <remarks>
    /// <para>Load-bearing for the queued SimpleFIN brokerage feed
    /// (ADR-0029): hard-deleting a sync-sourced row would let the
    /// next sync resurrect it; soft-hide preserves the user's
    /// delete intent across re-sync.</para>
    ///
    /// <para>Balance recompute on
    /// <c>txn_header_account_balances</c> is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// <c>SaveChangesAsync</c>. The hard-delete branch tracks the
    /// header DELETE via EF; the interceptor's pre-save snapshot
    /// reads the doomed header's legs from the DB so the
    /// affected-accounts set survives the cascade.</para>
    /// </remarks>
    public async Task<DeleteOutcome> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        var header = await _db.TxnHeaders
            .FirstOrDefaultAsync(
                h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken).ConfigureAwait(false);
        if (header is null) return DeleteOutcome.HeaderNotFound;
        if (header.Action is null) return DeleteOutcome.HeaderNotInvestment;

        // A fired reminder occurrence is NOT hard-deleted, exactly as on the bank
        // side. GetUpcomingAsync decides "already fired" from the (series, slot)
        // stamp on a live committed header, so removing the row returns the slot to
        // the agenda as un-acted and re-fireable — while the identical delete of a
        // BANK occurrence leaves it consumed. Soft-hiding keeps the stamp, takes the
        // row out of the register the same way a deleted feed row goes, and
        // UnhideAsync is already the undo.
        var isReminderOccurrence =
            header.RecurringTransactionId is not null
            && header.OccurrenceDate is not null
            && !header.IsRecurringTemplate;

        DeleteOutcome outcome;
        if (header.ExternalId is null && !isReminderOccurrence)
        {
            // Hard delete cascades through txn_legs. Both interceptors
            // (mig 102 balance + mig 104 holdings) read the doomed
            // legs from the live DB in SavingChangesAsync — before
            // the cascade — so the affected accounts + holdings set
            // survives the DB-side delete.
            _db.TxnHeaders.Remove(header);
            outcome = DeleteOutcome.HardDeleted;
        }
        else
        {
            // Soft-hide leaves the legs in place. Holdings state is
            // unchanged (no DML on txn_legs → no holdings recompute).
            // Balance state DOES change: mig 103 made is_hidden a
            // recompute filter, and LegDerivedRecomputeInterceptor picks
            // up the IsHidden flip on this SaveChanges.
            // Soft-delete also clears needs_review (ADR-0052 D3): a deleted row
            // is resolved, not awaiting acceptance — otherwise it strands as
            // is_hidden + needs_review (invisible but still queued).
            header.IsHidden = true;
            header.NeedsReview = false;
            outcome = DeleteOutcome.SoftHidden;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return outcome;
    }

    /// <summary>
    /// Investment merge candidates for the editor's "possible matches" panel
    /// (mirrors <see cref="TransactionsRepository.GetMergeCandidatesAsync"/> for
    /// the bank shape): settled investment rows the edited, still-unreviewed row
    /// could fold into.
    /// </summary>
    /// <remarks>
    /// <para><b>The anchor reads the ingest HINTS, not a security leg.</b> It used
    /// to require the edited row to carry <c>posting_role='security'</c> with a
    /// security_id, a quantity, and a non-null <c>action</c> — while also requiring
    /// <c>needs_review</c>. Those two demands are mutually exclusive in this
    /// product, so the panel could never return a single row: ingest writes a
    /// BANK-shaped two-leg posting (account + Uncategorized) and carries the
    /// investment detail in the <c>ingest_*</c> columns, and the investment shape
    /// only appears at Accept, which clears <c>needs_review</c> in the same
    /// transaction. The original predicate came from ADR-0031's sync-time write
    /// path, which specified action-bearing needs_review rows; migration 076
    /// records that the shipped Phase 3c deliberately did NOT do that, because
    /// <c>trg_validate_posting_role</c> (migration 057) makes posting_role and
    /// header.action inseparable and the invariant could not be relaxed for
    /// unreviewed rows. The anchor was written against the superseded design and
    /// was never reachable — the only writer of that state is a raw UPDATE in the
    /// old test fixtures, which is exactly why the gate stayed green.</para>
    ///
    /// <para>So the anchor is now defined over what an unreviewed row ACTUALLY
    /// has, the way the bank anchor always has been: its source leg (the
    /// non-category one) for the account and the effective date, and the
    /// <c>ingest_*</c> carriers for the money, the share count and the security.
    /// Reshaping ingest to suit the old predicate is not an option and never
    /// was — the trigger forbids it.</para>
    ///
    /// <para><b>Matching.</b> Candidates are settled, un-merged, effectively
    /// visible investment rows whose holdings leg sits on the source account's
    /// Holdings sibling (so: same brokerage, same ledger), within ±7 effective
    /// days, matched on MAGNITUDE:</para>
    /// <list type="number">
    ///   <item><description><c>ingest_amount</c> — the wire's own cash total. The
    ///   only usable number for a reinvest, whose net on the account is 0.00 by
    ///   construction (mig 228; before that column existed there was nothing to
    ///   match a reinvest on at all).</description></item>
    ///   <item><description>else the summed source-account amount — a plain buy or
    ///   sell moves real cash, so its own leg carries the principal.</description></item>
    ///   <item><description>else <c>ingest_shares</c> against the candidate's
    ///   quantity, for a basis-only move where no amount is informative.</description></item>
    /// </list>
    /// <para>Magnitude, not signed value: <c>ingest_amount</c> is stored as the
    /// wire's absolute total while a holdings leg is signed by direction, so a
    /// signed comparison would miss every match.</para>
    ///
    /// <para><b>Security.</b> Required only when OFX actually resolved it.
    /// <c>ingest_security_id</c> is set when the ticker hint matched an existing
    /// <c>provider_security_mappings</c> row and NULL when it did not (a security
    /// seen for the first time). Demanding a match in the NULL case would
    /// guarantee zero candidates for exactly the rows a person is most likely to
    /// be reviewing, so there the amount, account and date window carry the
    /// match on their own.</para>
    ///
    /// <para>All date/visibility reads resolve the override layer so matching
    /// agrees with what the register shows. Merge winners ARE eligible (folding
    /// into a prior winner keeps the merge graph one-hop).</para>
    /// </remarks>
    public async Task<IReadOnlyList<InvestmentMergeCandidateDto>> GetMergeCandidatesAsync(
        Guid ledgerId,
        Guid headerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // The edited row must be fresh, undecided and effectively visible. Anchored
        // through resolved_transactions for the override-aware date + hidden, and on
        // the NON-CATEGORY leg — the same negative test the bank anchor uses, which
        // is what makes it hold for the plain two-leg shape ingest writes.
        var anchor = await (
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where rv.HeaderId == headerId
                && rv.NeedsReview
                && rv.IsMergedInto == null
                && !rv.IsHidden
                && rv.AccountType != "category"
            join account in _db.Accounts.AsNoTracking() on rv.AccountId equals account.Id
            // The BROKERAGE leg specifically, identified as the account that owns a
            // Holdings sibling. "Not a category" alone is enough on the bank side,
            // where exactly one such leg exists; here it is not. An investment-shaped
            // row has a cash leg on the brokerage AND holdings legs on the sibling,
            // both non-category and both at posting_index 0, so the tie-break would
            // pick either one at the database's discretion — and picking the holdings
            // leg yields no sibling of its own and silently returns no candidates.
            where account.LedgerId == ledgerId && account.HoldingsAccountId != null
            orderby rv.LegIndex
            select new
            {
                SourceAccountId = rv.AccountId,
                rv.PostedAt,
                // The Holdings sibling hosts every candidate's security leg
                // (ADR-0019). NULL on a non-brokerage account, which then matches
                // nothing — correct, as there are no investment rows to fold into.
                account.HoldingsAccountId,
                rv.IngestAmount,
                rv.IngestShares,
                rv.IngestSecurityId,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (anchor is null || anchor.HoldingsAccountId is not { } holdingsAccountId)
            return Array.Empty<InvestmentMergeCandidateDto>();

        // The effective sum on the source account: this row's own cash movement.
        // Zero for a reinvest, which is why ingest_amount is preferred over it.
        var sourceAmount = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => rv.HeaderId == headerId && rv.AccountId == anchor.SourceAccountId)
            .SumAsync(rv => rv.Amount, cancellationToken)
            .ConfigureAwait(false);

        var principal = Math.Abs(anchor.IngestAmount ?? 0m);
        if (principal == 0m) principal = Math.Abs(sourceAmount);
        var shares = Math.Abs(anchor.IngestShares ?? 0m);
        // Nothing to match on: no cash figure and no share count. Offering every
        // row in the window would be worse than offering none.
        if (principal == 0m && shares == 0m)
            return Array.Empty<InvestmentMergeCandidateDto>();

        // Candidates read through resolved_transactions, like the bank side, so a
        // curated amount/date/payee/visibility is what gets matched — the raw-leg
        // read this replaces honoured overrides for the window but not for the
        // amount, so an edited figure matched on its pre-edit value.
        //
        // The view COALESCEs security_id and quantity across a posting's sibling
        // legs, so those two cannot tell a security posting's cash leg from its
        // holdings leg. Pinning account_id to the Holdings sibling can, and that
        // filter is needed anyway.
        var windowStart = anchor.PostedAt.AddDays(-7);
        var windowEnd = anchor.PostedAt.AddDays(7);

        var q =
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where rv.HeaderId != headerId
                && rv.AccountId == holdingsAccountId
                && rv.PostingRole == PostingRoles.Security
                && rv.SecurityId != null
                && rv.Quantity != null
                && rv.InvestmentAction != null
                && !rv.NeedsReview
                && rv.IsMergedInto == null
                && !rv.IsHidden
                && rv.PostedAt >= windowStart
                && rv.PostedAt <= windowEnd
            select rv;

        // Only when OFX resolved the ticker to a security already mapped.
        if (anchor.IngestSecurityId is { } knownSecurityId)
            q = q.Where(rv => rv.SecurityId == knownSecurityId);

        q = principal != 0m
            ? q.Where(rv => (rv.Amount < 0 ? -rv.Amount : rv.Amount) == principal)
            : q.Where(rv => (rv.Quantity < 0 ? -rv.Quantity : rv.Quantity) == shares);

        var rows = await q
            .Select(rv => new
            {
                rv.HeaderId,
                rv.InvestmentAction,
                rv.SecurityTicker,
                rv.Quantity,
                rv.UnitPrice,
                rv.Amount,
                rv.PostedAt,
                rv.Payee,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            // One row per HEADER. The view is per-leg, so a header carrying more
            // than one matching holdings leg would otherwise be offered twice and
            // spend two of the caller's limit slots on one candidate.
            .GroupBy(r => r.HeaderId)
            .Select(g => g.First())
            .OrderBy(r => Math.Abs((r.PostedAt - anchor.PostedAt).TotalDays))
            .ThenByDescending(r => r.PostedAt)
            .Take(limit)
            .Select(r => new InvestmentMergeCandidateDto(
                r.HeaderId,
                r.PostedAt,
                (int)Math.Round((r.PostedAt - anchor.PostedAt).TotalDays),
                r.InvestmentAction,
                r.SecurityTicker,
                r.Quantity,
                r.UnitPrice,
                r.Amount,
                r.Payee))
            .ToList();
    }

    /// <summary>
    /// Set ONLY <c>posted_at</c> on a header, capturing its original first
    /// (migration 230). Used by the merge branch of <see cref="PatchAsync"/> so
    /// the surviving winner adopts the imported loser's date.
    /// </summary>
    /// <remarks>
    /// Identical to the bank repository's helper of the same name, and both
    /// delegate the capture to <see cref="HeaderOriginals"/> — the two paths
    /// share one rule rather than two that happen to agree.
    /// </remarks>
    private async Task SetPostedAtAsync(
        TxnHeaderRow header,
        DateTime postedAt,
        CancellationToken cancellationToken)
    {
        await HeaderOriginals.CaptureAsync(_db, header, cancellationToken)
            .ConfigureAwait(false);
        header.PostedAt = postedAt;
    }

    // ----------------------------------------------------------------
    // Convert a sell+buy pair to an in-kind transfer_shares (ADR-0065 D4)
    // ----------------------------------------------------------------

    public enum ConvertInKindResult
    {
        Ok,
        SellNotFound,
        BuyNotFound,
        NotAValidPair,
    }

    public readonly record struct ConvertInKindOutcome(
        ConvertInKindResult Result,
        CreateFailure? CreateFail,
        Guid HeaderId)
    {
        public static ConvertInKindOutcome Ok(Guid id) => new(ConvertInKindResult.Ok, null, id);
        public static ConvertInKindOutcome Fail(ConvertInKindResult r) => new(r, null, Guid.Empty);
        public static ConvertInKindOutcome Fail(CreateFailure f) => new(ConvertInKindResult.NotAValidPair, f, Guid.Empty);
    }

    private sealed record HoldingsLeg(Guid SecurityId, decimal Quantity, Guid SiblingAccountId, DateTime PostedAt);

    /// <summary>
    /// Convert a (sell/sellx) + (buy/buyx) pair that is really an in-kind transfer
    /// into a single <c>transfer_shares</c> (ADR-0065 D4). Validates the pair (same
    /// security, same calendar date, equal qty, distinct investment accounts), then
    /// in ONE transaction deletes both headers and creates the transfer. The delete
    /// flush restores the source holding's lots (the sell's consumption undone), so
    /// <see cref="CreateAsync"/>'s FIFO plan reads the correct pre-transfer state;
    /// CreateAsync joins this open transaction (does not commit) since it sees a
    /// current transaction. Any fee/cash legs on the original pair are dropped (an
    /// in-kind transfer moves no cash) — the candidate review surfaces that.
    /// </summary>
    public async Task<ConvertInKindOutcome> ConvertInKindTransferAsync(
        Guid ledgerId,
        Guid sellHeaderId,
        Guid buyHeaderId,
        CancellationToken cancellationToken = default)
    {
        var sell = await LoadDisposalOrAcquisitionLegAsync(
            ledgerId, sellHeaderId, disposal: true, cancellationToken).ConfigureAwait(false);
        if (sell is null) return ConvertInKindOutcome.Fail(ConvertInKindResult.SellNotFound);

        var buy = await LoadDisposalOrAcquisitionLegAsync(
            ledgerId, buyHeaderId, disposal: false, cancellationToken).ConfigureAwait(false);
        if (buy is null) return ConvertInKindOutcome.Fail(ConvertInKindResult.BuyNotFound);

        // Same security, same calendar date, equal share count.
        if (sell.SecurityId != buy.SecurityId
            || sell.PostedAt.Date != buy.PostedAt.Date
            || buy.Quantity != -sell.Quantity)
            return ConvertInKindOutcome.Fail(ConvertInKindResult.NotAValidPair);

        // Resolve each holdings sibling back to its (distinct) brokerage.
        var sourceBrokerage = await BrokerageForSiblingAsync(ledgerId, sell.SiblingAccountId, cancellationToken).ConfigureAwait(false);
        var destBrokerage = await BrokerageForSiblingAsync(ledgerId, buy.SiblingAccountId, cancellationToken).ConfigureAwait(false);
        if (sourceBrokerage is not { } sourceId || destBrokerage is not { } destId || sourceId == destId)
            return ConvertInKindOutcome.Fail(ConvertInKindResult.NotAValidPair);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Hard-delete both headers. The DB cascades header → legs → lots
        // (mig 123), and HoldingsRecomputeInterceptor snapshots the doomed
        // legs' (account, security) pairs before the cascade, so both holdings
        // reconcile on this save — restoring the source lots the sell consumed.
        var headers = await _db.TxnHeaders
            .Where(h => h.LedgerId == ledgerId && (h.Id == sellHeaderId || h.Id == buyHeaderId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.TxnHeaders.RemoveRange(headers);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Create the in-kind transfer. CreateAsync sees the open transaction and
        // joins it (no nested commit); its FIFO plan reads the restored source lots.
        var createResult = await CreateAsync(
            ledgerId,
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = sourceId,
                Action = LedgerActions.TransferShares,
                SecurityId = sell.SecurityId,
                Shares = -sell.Quantity,          // positive qty to move
                TransferAccountId = destId,
                PostedAt = sell.PostedAt,
            },
            // A historical in-kind correction operates on the already-existing
            // sell+buy — the source/destination brokerage is often since-closed
            // (e.g. a rolled-over 401k). Bypass the inactive-account write gate.
            allowInactiveAccounts: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (createResult.Failure is { } f)
            return ConvertInKindOutcome.Fail(f);   // await using rolls back

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ConvertInKindOutcome.Ok(createResult.HeaderId);
    }

    /// <summary>
    /// Load the single holdings-side security leg of a sell/sellx (when
    /// <paramref name="disposal"/>) or buy/buyx (otherwise) header, verifying it's
    /// a live (non-hidden, non-merged) investment header with the matching qty
    /// sign. Returns null when the header isn't that shape.
    /// </summary>
    private async Task<HoldingsLeg?> LoadDisposalOrAcquisitionLegAsync(
        Guid ledgerId, Guid headerId, bool disposal, CancellationToken cancellationToken)
    {
        var actions = disposal
            ? new[] { LedgerActions.Sell, LedgerActions.SellXfr }
            : new[] { LedgerActions.Buy, LedgerActions.BuyXfr };

        // Date and visibility come from the OVERRIDE LAYER, not the raw header.
        // This read decides whether a pair is convertible, and the caller compares
        // the two sides' dates — but the detector that OFFERS the pair
        // (FindInKindCandidates) reads resolved_transactions. This side used to
        // read the raw posted_at while that view resolved the override, so the two
        // disagreed about the same pair in both directions: a pair whose displayed
        // dates matched was offered and then rejected as NotAValidPair, and a pair
        // a user had deliberately dated apart could still convert. Since mig 230
        // there is one date and they cannot drift again.
        //
        // The leg columns come off txn_legs on purpose: resolved_transactions
        // COALESCEs security_id and quantity across a posting's sibling legs, so
        // reading them from the view would make the cash leg indistinguishable
        // from the holdings leg this must find.
        var row = await (
            from l in _db.TxnLegs.AsNoTracking()
            join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
            where l.HeaderId == headerId
                  && l.LedgerId == ledgerId
                  && l.PostingRole == PostingRoles.Security
                  && l.SecurityId != null
                  && l.Quantity != null
                  && h.Action != null && actions.Contains(h.Action)
                  && h.IsMergedInto == null
            select new
            {
                SecurityId = l.SecurityId!.Value,
                Quantity = l.Quantity!.Value,
                l.AccountId,
                h.IsHidden,
                h.PostedAt,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null || row.IsHidden) return null;
        if (disposal && row.Quantity >= 0m) return null;
        if (!disposal && row.Quantity <= 0m) return null;
        return new HoldingsLeg(
            row.SecurityId, row.Quantity, row.AccountId, row.PostedAt);
    }

    private Task<Guid?> BrokerageForSiblingAsync(
        Guid ledgerId, Guid siblingAccountId, CancellationToken cancellationToken) =>
        _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId == siblingAccountId)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // ----------------------------------------------------------------
    // Validation
    // ----------------------------------------------------------------

    /// <summary>
    /// Shared validation + account/security resolution + posting construction
    /// for the create / PATCH / recurring-template paths. Validates the
    /// action × field matrix (ADR-0029), resolves every referenced account +
    /// the security in this ledger (single account round-trip), and builds the
    /// per-action <see cref="InvestmentPosting"/> pairs. Returns the first
    /// failure (the caller maps it to a 422) OR the resolved holdings-sibling
    /// account id + the posting list. Performs NO writes.
    /// </summary>
    private async Task<(CreateFailure? Failure, Guid HoldingsAccountId, IReadOnlyList<InvestmentPosting> Postings, Guid? DestHoldingsAccountId)>
        ValidateAndResolveAsync(
            Guid ledgerId,
            CreateInvestmentTransactionRequest request,
            bool allowInactiveAccounts,
            CancellationToken cancellationToken)
    {
        var empty = Array.Empty<InvestmentPosting>();
        var action = request.Action;
        if (!IsCatalogAction(action))
            return (CreateFailure.ActionInvalid, Guid.Empty, empty, null);

        var (shapeFailure, _) = ValidateActionShape(request, action);
        if (shapeFailure is { } sf) return (sf, Guid.Empty, empty, null);

        // Account lookups (single round-trip; one query covers brokerage +
        // every referenced counterparty).
        var referenced = CollectReferencedAccountIds(request);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && referenced.Contains(a.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = accounts.ToDictionary(a => a.Id);

        if (!byId.TryGetValue(request.BrokerageAccountId, out var brokerage))
            return (CreateFailure.AccountNotInLedger, Guid.Empty, empty, null);
        if (brokerage.AccountType != "investment")
            return (CreateFailure.AccountNotInvestment, Guid.Empty, empty, null);
        if (brokerage.HoldingsAccountId is not { } holdingsAccountId)
            return (CreateFailure.AccountMissingHoldingsSibling, Guid.Empty, empty, null);

        foreach (var (id, code) in EnumerateCounterpartyIds(request))
            if (!byId.ContainsKey(id))
                return (code, Guid.Empty, empty, null);

        // PR #132 inactive-account gate: a brokerage / counterparty must be active
        // to receive a write. It blocks NEW activity being posted to a closed
        // account. allowInactiveAccounts bypasses it for convert_in_kind_transfer,
        // which re-represents transactions that ALREADY exist on those (often since-
        // closed) accounts — a historical data-hygiene correction, not new activity
        // (the ADR-0085 presentation-vs-correctness distinction: is_active gates the
        // UI, not a legitimate historical write).
        if (!allowInactiveAccounts)
        {
            if (!brokerage.IsActive)
                return (CreateFailure.BrokerageInactive, Guid.Empty, empty, null);

            // Runs after the "not-in-ledger" pass, so the more specific error wins
            // when both apply.
            foreach (var (id, code) in EnumerateCounterpartyIdsForInactiveCheck(request))
                if (byId.TryGetValue(id, out var account) && !account.IsActive)
                    return (code, Guid.Empty, empty, null);
        }

        // Security lookup (per-ledger).
        if (request.SecurityId is { } secId)
        {
            var secExists = await _db.Securities.AsNoTracking()
                .AnyAsync(s => s.Id == secId && s.LedgerId == ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (!secExists) return (CreateFailure.SecurityNotInLedger, Guid.Empty, empty, null);
        }

        // transfer_shares (in-kind, ADR-0065): the destination is another
        // brokerage; resolve its holdings sibling here. Postings + lots are
        // built on a dedicated path by the caller (the FIFO lots to move are
        // read at create/scrub time — see BuildTransferSharesPlanAsync), so we
        // return EMPTY postings + the resolved destination holdings sibling.
        if (action == LedgerActions.TransferShares)
        {
            // TransferAccountId existence + active already covered by the
            // counterparty passes above.
            var dest = byId[request.TransferAccountId!.Value];
            if (dest.AccountType != "investment")
                return (CreateFailure.TransferSharesDestNotInvestment, Guid.Empty, empty, null);
            if (dest.HoldingsAccountId is not { } destHoldings)
                return (CreateFailure.TransferSharesDestMissingHoldingsSibling, Guid.Empty, empty, null);
            if (destHoldings == holdingsAccountId)
                return (CreateFailure.TransferSharesToSelf, Guid.Empty, empty, null);
            return (null, holdingsAccountId, empty, destHoldings);
        }

        var totalCommission = request.FeeAmount ?? 0m;
        var postings = BuildPostings(request, action, holdingsAccountId, totalCommission);
        return (null, holdingsAccountId, postings, null);
    }

    private static bool IsCatalogAction(string action) => action is
        LedgerActions.Buy or LedgerActions.BuyXfr or
        LedgerActions.Sell or LedgerActions.SellXfr or
        LedgerActions.DividendCash or LedgerActions.DividendReinvest or
        LedgerActions.DivXfr or LedgerActions.Transfer or LedgerActions.Misc or
        LedgerActions.TransferShares;

    /// <summary>
    /// Validates the action × field matrix from ADR-0029. Returns
    /// the first failing required-field as a CreateFailure code, or
    /// (null, true) when every required field is present + within
    /// its expected bounds.
    /// </summary>
    private static (CreateFailure? Failure, bool Ok) ValidateActionShape(
        CreateInvestmentTransactionRequest r, string action)
    {
        // transfer_shares (in-kind, ADR-0065): security + a positive qty to move
        // + a destination account; NO price (unit cost carries per-lot from the
        // source), NO amount/category, and NO fee (in-kind moves no cash).
        if (action == LedgerActions.TransferShares)
        {
            if (r.SecurityId is null) return (CreateFailure.SecurityRequired, false);
            if (r.Shares is null) return (CreateFailure.SharesRequired, false);
            if (r.Shares <= 0m) return (CreateFailure.TransferSharesQtyPositive, false);
            if (r.TransferAccountId is null) return (CreateFailure.TransferRequired, false);
            if (r.FeeAccountId is not null || r.FeeAmount is not null)
                return (CreateFailure.FeeWithoutAccount, false);
            return (null, true);
        }

        var needsSecurity = action is not LedgerActions.Transfer;
        if (needsSecurity && r.SecurityId is null && action != LedgerActions.Misc)
            return (CreateFailure.SecurityRequired, false);

        var needsShares = action is
            LedgerActions.Buy or LedgerActions.BuyXfr or
            LedgerActions.Sell or LedgerActions.SellXfr or
            LedgerActions.DividendReinvest;
        if (needsShares)
        {
            if (r.Shares is null) return (CreateFailure.SharesRequired, false);
            if (r.Shares == 0m) return (CreateFailure.SharesNonZero, false);
            if (r.Price is null) return (CreateFailure.PriceRequired, false);
            if (r.Price <= 0m) return (CreateFailure.PricePositive, false);
        }

        var needsAmount = action is
            LedgerActions.DividendCash or LedgerActions.Transfer or LedgerActions.Misc;
        if (needsAmount && r.Amount is null)
            return (CreateFailure.AmountRequired, false);

        var needsCategory = action is
            LedgerActions.DividendCash or LedgerActions.DividendReinvest or
            LedgerActions.DivXfr or LedgerActions.Misc;
        if (needsCategory && r.CategoryAccountId is null)
            return (CreateFailure.CategoryRequired, false);

        var needsTransfer = action is
            LedgerActions.BuyXfr or LedgerActions.SellXfr or
            LedgerActions.DivXfr or LedgerActions.Transfer;
        if (needsTransfer && r.TransferAccountId is null)
            return (CreateFailure.TransferRequired, false);

        // Fee fields: account ⇔ amount; positive amount required.
        if (r.FeeAccountId is not null)
        {
            if (r.FeeAmount is null)
                return (CreateFailure.FeeAmountRequired, false);
            if (r.FeeAmount <= 0m)
                return (CreateFailure.FeeAmountPositive, false);
        }
        else if (r.FeeAmount is not null)
        {
            return (CreateFailure.FeeWithoutAccount, false);
        }

        // Transfer action never accepts a fee leg (ADR-0027).
        if (action == LedgerActions.Transfer && r.FeeAccountId is not null)
            return (CreateFailure.FeeWithoutAccount, false);

        return (null, true);
    }

    private static HashSet<Guid> CollectReferencedAccountIds(
        CreateInvestmentTransactionRequest r)
    {
        var ids = new HashSet<Guid> { r.BrokerageAccountId };
        if (r.CategoryAccountId is { } c) ids.Add(c);
        if (r.TransferAccountId is { } t) ids.Add(t);
        if (r.FeeAccountId is { } f) ids.Add(f);
        return ids;
    }

    private static IEnumerable<(Guid Id, CreateFailure Code)> EnumerateCounterpartyIds(
        CreateInvestmentTransactionRequest r)
    {
        if (r.CategoryAccountId is { } c) yield return (c, CreateFailure.CategoryNotInLedger);
        if (r.TransferAccountId is { } t) yield return (t, CreateFailure.TransferNotInLedger);
        if (r.FeeAccountId is { } f) yield return (f, CreateFailure.FeeAccountNotInLedger);
    }

    /// <summary>
    /// PR #132 inactive-account gate per role. Same shape as
    /// <see cref="EnumerateCounterpartyIds"/>; emits the role-specific
    /// "inactive" failure when the referenced account exists in the
    /// ledger but has <c>is_active=false</c>.
    /// </summary>
    private static IEnumerable<(Guid Id, CreateFailure Code)> EnumerateCounterpartyIdsForInactiveCheck(
        CreateInvestmentTransactionRequest r)
    {
        if (r.CategoryAccountId is { } c) yield return (c, CreateFailure.CategoryInactive);
        if (r.TransferAccountId is { } t) yield return (t, CreateFailure.TransferInactive);
        if (r.FeeAccountId is { } f) yield return (f, CreateFailure.FeeAccountInactive);
    }

    // ----------------------------------------------------------------
    // Posting-shape construction
    // ----------------------------------------------------------------

    /// <summary>
    /// Convert the request + resolved holdings sibling into an
    /// ordered list of <see cref="InvestmentPosting"/>s — one per
    /// (account-pair) on the brokerage. The list's order determines
    /// the legs' posting_index sequence.
    /// </summary>
    private static IReadOnlyList<InvestmentPosting> BuildPostings(
        CreateInvestmentTransactionRequest r,
        string action,
        Guid holdingsAccountId,
        decimal totalCommission)
    {
        var list = new List<InvestmentPosting>();
        var brokerageId = r.BrokerageAccountId;
        var securityId = r.SecurityId;

        switch (action)
        {
            case LedgerActions.Buy:
            {
                // Cash side -X (cash leaving for shares), holdings +Y.
                var qty = r.Shares!.Value;
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty);
                list.Add(InvestmentPostings.BuildSecPair(
                    brokerageId, holdingsAccountId, securityId!.Value,
                    cashAmount: -principal, holdingsAmount: principal,
                    quantity: qty, unitPrice: unitPrice));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.Sell:
            {
                var qty = r.Shares!.Value;             // negative input → expected
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty);
                list.Add(InvestmentPostings.BuildSecPair(
                    brokerageId, holdingsAccountId, securityId!.Value,
                    cashAmount: principal, holdingsAmount: -principal,
                    quantity: qty, unitPrice: unitPrice));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.BuyXfr:
            case LedgerActions.SellXfr:
            {
                var qty = r.Shares!.Value;
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty);
                var sign = action == LedgerActions.BuyXfr ? -1m : 1m;
                list.Add(InvestmentPostings.BuildSecPair(
                    brokerageId, holdingsAccountId, securityId!.Value,
                    cashAmount: sign * principal, holdingsAmount: -sign * principal,
                    quantity: qty, unitPrice: unitPrice));
                // Xfr pair offsets brokerage cash (cash nets to zero on the brokerage).
                list.Add(InvestmentPostings.BuildXferPair(
                    brokerageAccountId: brokerageId,
                    otherAccountId: r.TransferAccountId!.Value,
                    brokerageAmount: -sign * principal,
                    otherAmount: sign * principal));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.DividendCash:
            {
                var amount = r.Amount!.Value;          // positive
                list.Add(InvestmentPostings.BuildCategoryPair(
                    brokerageAccountId: brokerageId,
                    categoryAccountId: r.CategoryAccountId!.Value,
                    cashAmount: amount,
                    categoryAmount: -amount,
                    postingRole: PostingRoles.Income,
                    securityId: securityId));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.DividendReinvest:
            {
                var qty = r.Shares!.Value;
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty);
                // Inc pair: cash IN from category.
                list.Add(InvestmentPostings.BuildCategoryPair(
                    brokerageAccountId: brokerageId,
                    categoryAccountId: r.CategoryAccountId!.Value,
                    cashAmount: principal,
                    categoryAmount: -principal,
                    postingRole: PostingRoles.Income,
                    securityId: securityId));
                // Sec pair: cash OUT to holdings (reinvest purchase).
                list.Add(InvestmentPostings.BuildSecPair(
                    brokerageId, holdingsAccountId, securityId!.Value,
                    cashAmount: -principal, holdingsAmount: principal,
                    quantity: qty, unitPrice: unitPrice));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.DivXfr:
            {
                var amount = r.Amount!.Value;
                list.Add(InvestmentPostings.BuildCategoryPair(
                    brokerageAccountId: brokerageId,
                    categoryAccountId: r.CategoryAccountId!.Value,
                    cashAmount: amount,
                    categoryAmount: -amount,
                    postingRole: PostingRoles.Income,
                    securityId: securityId));
                list.Add(InvestmentPostings.BuildXferPair(
                    brokerageAccountId: brokerageId,
                    otherAccountId: r.TransferAccountId!.Value,
                    brokerageAmount: -amount,
                    otherAmount: amount));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
            case LedgerActions.Transfer:
            {
                var amount = r.Amount!.Value;
                list.Add(InvestmentPostings.BuildXferPair(
                    brokerageAccountId: brokerageId,
                    otherAccountId: r.TransferAccountId!.Value,
                    brokerageAmount: amount,
                    otherAmount: -amount));
                // Transfer never carries a fee (ADR-0027); validation
                // already rejected the combination.
                break;
            }
            case LedgerActions.Misc:
            {
                var amount = r.Amount!.Value;
                list.Add(InvestmentPostings.BuildCategoryPair(
                    brokerageAccountId: brokerageId,
                    categoryAccountId: r.CategoryAccountId!.Value,
                    cashAmount: amount,
                    categoryAmount: -amount,
                    postingRole: PostingRoles.Income,
                    securityId: securityId));
                AddOptionalFee(list, r, brokerageId, securityId, action);
                break;
            }
        }

        return list;
    }

    private static void AddOptionalFee(
        List<InvestmentPosting> postings,
        CreateInvestmentTransactionRequest r,
        Guid brokerageId,
        Guid? securityId,
        string action)
    {
        if (r.FeeAccountId is not { } feeAccountId) return;
        if (r.FeeAmount is not { } feeAmount) return;

        postings.Add(InvestmentPostings.BuildCategoryPair(
            brokerageAccountId: brokerageId,
            categoryAccountId: feeAccountId,
            cashAmount: -feeAmount,
            categoryAmount: feeAmount,
            postingRole: PostingRoles.Fee,
            securityId: securityId));
    }

    /// <summary>
    /// Money + derived unit price for a share-trade leg (buy / sell /
    /// buyx / sellx / dividend_reinvest). The AMOUNT paid/received is
    /// authoritative and carries exactly 2 decimals — it is the actual
    /// settled cash, which for an imported trade is the wire total, not a
    /// re-derivation from a rounded per-share price (ADR-0073). The
    /// per-share <c>unit_price</c> is DERIVED metadata (<c>amount ÷
    /// |shares|</c>) rounded to 6 decimals — the register's max display
    /// precision (<c>formatPrice</c>) — so what is stored is exactly what
    /// is shown (never more digits in the DB than on screen), and no
    /// sub-cent money ever lands on a leg (which is what produced
    /// fractional amounts + "-$0.00" balances).
    ///
    /// Falls back to <c>round(price × |shares|, 2)</c> only when the request
    /// omits an amount — a defensive path for callers predating the
    /// amount-authoritative model (e.g. a fixture that sets shares+price
    /// but no amount). Rounds half away from zero to match Postgres
    /// <c>round(numeric, n)</c> so the scrub migration and this code agree.
    /// </summary>
    private static (decimal Principal, decimal UnitPrice) ResolveTradeMoney(
        decimal? requestAmount, decimal price, decimal quantity)
    {
        var absQty = Math.Abs(quantity);
        var principal = Math.Round(
            requestAmount ?? price * absQty, 2, MidpointRounding.AwayFromZero);
        var unitPrice = absQty != 0m
            ? Math.Round(principal / absQty, 6, MidpointRounding.AwayFromZero)
            : price;
        return (principal, unitPrice);
    }

    // ----------------------------------------------------------------
    // transfer_shares (in-kind) — per-lot carry (ADR-0065)
    // ----------------------------------------------------------------

    /// <summary>
    /// One source FIFO lot (slice) being moved by a transfer_shares: the
    /// quantity taken from it, its inherited per-share <see cref="UnitCost"/>,
    /// and its original <see cref="AcquiredAt"/> (UTC). The destination
    /// re-creates a lot with exactly these values so holding period + basis
    /// carry across the move (ADR-0065 D2).
    /// </summary>
    private readonly record struct MovedLot(decimal Quantity, decimal UnitCost, DateTime AcquiredAt);

    /// <summary>
    /// Compute the FIFO consumption plan on the SOURCE holding for an in-kind
    /// transfer of <paramref name="qtyToMove"/> shares: walk the source's open
    /// lots oldest-first and slice off lots until the quantity is satisfied.
    /// Fails with <see cref="CreateFailure.TransferSharesInsufficientShares"/>
    /// when the source doesn't currently hold that many. Performs NO writes.
    /// </summary>
    private async Task<(CreateFailure? Failure, IReadOnlyList<MovedLot> Plan)>
        BuildTransferSharesPlanAsync(
            Guid ledgerId,
            Guid sourceHoldingsAccountId,
            Guid securityId,
            decimal qtyToMove,
            CancellationToken cancellationToken)
    {
        var lots = await _db.Lots.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId
                     && !l.IsClosed
                     && l.Quantity > 0
                     && _db.Holdings.Any(h => h.Id == l.HoldingId
                                           && h.AccountId == sourceHoldingsAccountId
                                           && h.SecurityId == securityId
                                           && h.LedgerId == ledgerId))
            .OrderBy(l => l.AcquiredAt)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Quantity, l.UnitCost, l.AcquiredAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var available = lots.Sum(l => l.Quantity);
        if (available < qtyToMove)
            return (CreateFailure.TransferSharesInsufficientShares, Array.Empty<MovedLot>());

        var plan = new List<MovedLot>();
        var remaining = qtyToMove;
        foreach (var lot in lots)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(remaining, lot.Quantity);
            plan.Add(new MovedLot(take, lot.UnitCost, lot.AcquiredAt));
            remaining -= take;
        }
        return (null, plan);
    }

    /// <summary>
    /// Build the in-kind transfer postings: per moved lot, TWO sec postings —
    /// a source one (brokerage cash $0 ↔ source holdings −lot) and a destination
    /// one (brokerage cash $0 ↔ destination holdings +lot). The zero-cash sec
    /// pair is the canonical Coffer shape for a one-sided share event (mig 065:
    /// legitimate cash=0 share-side events like a share-class exchange); it keeps
    /// the 2-leg-per-posting cardinality, makes the transfer visible in BOTH
    /// brokerage registers, and makes every destination lot leg-derived 1:1
    /// (leg amount = lot cost, leg qty = lot qty) so the recompute's lot-reset
    /// re-derives the inherited unit cost with no transfer-specific branch
    /// (ADR-0065 D2/D3). Postings come out in [source₀, dest₀, source₁, dest₁, …]
    /// order; each destination holding leg (the <c>Counterparty</c> whose
    /// <c>AccountId == destHoldingsAccountId</c>) is the side a per-lot row binds to.
    /// </summary>
    private static IReadOnlyList<InvestmentPosting> BuildTransferSharesPostings(
        Guid sourceBrokerageId,
        Guid sourceHoldingsAccountId,
        Guid destBrokerageId,
        Guid destHoldingsAccountId,
        Guid securityId,
        IReadOnlyList<MovedLot> plan)
    {
        var list = new List<InvestmentPosting>(plan.Count * 2);
        foreach (var lot in plan)
        {
            // Leg money is authoritative at 2dp (ADR-0073, ck_txn_legs_amount_scale_2):
            // producers round before insert. quantity × unit_cost (unit_cost stored at
            // NUMERIC(25,12), mig 180) is generally NOT 2dp when the lot's basis didn't
            // divide evenly by its quantity (e.g. $100 / 3 sh → 33.333… × 3 = 99.999…),
            // so round here — same convention as the buy/sell principal (see
            // PrincipalAndUnitPrice). At 12dp the rounded basis is penny-exact vs the
            // source lot; the pre-mig-180 (19,4) unit_cost drifted ~$4 on a $570k move.
            var cost = Math.Round(lot.Quantity * lot.UnitCost, 2, MidpointRounding.AwayFromZero);
            // Source: brokerage cash $0 (in-kind) ↔ source holdings −lot.
            list.Add(InvestmentPostings.BuildSecPair(
                sourceBrokerageId, sourceHoldingsAccountId, securityId,
                cashAmount: 0m, holdingsAmount: -cost,
                quantity: -lot.Quantity, unitPrice: lot.UnitCost));
            // Destination: brokerage cash $0 (in-kind) ↔ dest holdings +lot.
            list.Add(InvestmentPostings.BuildSecPair(
                destBrokerageId, destHoldingsAccountId, securityId,
                cashAmount: 0m, holdingsAmount: cost,
                quantity: lot.Quantity, unitPrice: lot.UnitCost));
        }
        return list;
    }

    /// <summary>
    /// Create the destination lot rows for an in-kind transfer — one per moved
    /// lot, each bound to its destination leg, carrying the inherited
    /// <see cref="MovedLot.AcquiredAt"/> + <see cref="MovedLot.UnitCost"/> so the
    /// holding period + basis carry (ADR-0065 D2). The source lots are NOT
    /// touched here; the recompute consumes them FIFO from the source −qty legs.
    /// </summary>
    private async Task CreateTransferSharesLotsAsync(
        Guid ledgerId,
        Guid destHoldingsAccountId,
        Guid securityId,
        IReadOnlyList<MovedLot> plan,
        IReadOnlyList<Guid> destLegIds,
        CancellationToken cancellationToken)
    {
        if (plan.Count == 0) return;

        var holdingId = await GetHoldingIdAsync(
            destHoldingsAccountId, securityId, ledgerId, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < plan.Count; i++)
        {
            _db.Lots.Add(new LotRow
            {
                Id = Guid.NewGuid(),
                HoldingId = holdingId,
                LegId = destLegIds[i],
                LedgerId = ledgerId,
                Quantity = plan[i].Quantity,
                UnitCost = plan[i].UnitCost,
                AcquiredAt = plan[i].AcquiredAt,
                IsClosed = false,
            });
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------
    // Holdings / lots
    // ----------------------------------------------------------------

    /// <summary>
    /// Look up the holdings row id for (account, security) in this
    /// ledger. The row is created by <c>recompute_holdings_cost_basis</c>'s
    /// auto-create path (mig 068) the first time the recompute runs
    /// for that (account, security). Mig 104 moved the recompute
    /// trigger to <see cref="HoldingsRecomputeInterceptor"/>, but the
    /// auto-create path is unchanged — the interceptor fires the
    /// recompute in the same SaveChanges as the leg INSERT, so the
    /// holdings row exists by the time this lookup runs.
    /// </summary>
    private Task<Guid> GetHoldingIdAsync(
        Guid holdingsAccountId,
        Guid securityId,
        Guid ledgerId,
        CancellationToken ct)
        => _db.Holdings
            .Where(h => h.AccountId == holdingsAccountId
                     && h.SecurityId == securityId
                     && h.LedgerId == ledgerId)
            .Select(h => h.Id)
            .SingleAsync(ct);

    // ----------------------------------------------------------------
    // Leg insert (EF ChangeTracker)
    // ----------------------------------------------------------------

    /// <summary>
    /// Per-leg insert spec: an <see cref="InvestmentLegSpec"/> (the
    /// persistence-agnostic domain shape) plus the resolved row id and
    /// the header / ledger / posting-index coordinates the caller
    /// assigns once the full pair list is built. <see cref="ToTxnLegRow"/>
    /// maps it onto the EF entity.
    /// </summary>
    private sealed record LegInsertSpec(
        Guid Id,
        Guid HeaderId,
        Guid LedgerId,
        int PostingIndex,
        InvestmentLegSpec Leg)
    {
        public static LegInsertSpec From(
            Guid id, Guid headerId, Guid ledgerId, int postingIndex,
            InvestmentLegSpec leg) =>
            new(id, headerId, ledgerId, postingIndex, leg);
    }

    /// <summary>
    /// Map a <see cref="LegInsertSpec"/> onto a tracked
    /// <see cref="TxnLegRow"/>. <c>created_at</c> is omitted — it's
    /// <c>ValueGeneratedOnAdd</c> (DB default), matching the old TVF's
    /// per-row <c>clock_timestamp()</c>.
    /// </summary>
    private static TxnLegRow ToTxnLegRow(LegInsertSpec spec) => new()
    {
        Id = spec.Id,
        HeaderId = spec.HeaderId,
        LedgerId = spec.LedgerId,
        AccountId = spec.Leg.AccountId,
        PostingIndex = spec.PostingIndex,
        Amount = spec.Leg.Amount,
        SecurityId = spec.Leg.SecurityId,
        Quantity = spec.Leg.Quantity,
        UnitPrice = spec.Leg.UnitPrice,
        LegMemo = spec.Leg.LegMemo,
        PostingRole = spec.Leg.PostingRole,
    };

    // ----------------------------------------------------------------
    // Reads — lots for FIFO preview (ADR-0029)
    // ----------------------------------------------------------------

    /// <summary>
    /// Lookup outcome for open lots on a (brokerage, security).
    /// Mirrors the create-side typed failures so endpoint mapping
    /// stays uniform.
    /// </summary>
    public enum LotsLookupFailure
    {
        AccountNotInLedger,
        AccountNotInvestment,
        AccountMissingHoldingsSibling,
        SecurityNotInLedger,
    }

    public readonly record struct LotsLookupResult(
        LotsLookupFailure? Failure,
        IReadOnlyList<InvestmentLotDto> Lots)
    {
        public static LotsLookupResult Ok(IReadOnlyList<InvestmentLotDto> lots) =>
            new(null, lots);
        public static LotsLookupResult Fail(LotsLookupFailure f) =>
            new(f, Array.Empty<InvestmentLotDto>());
    }

    /// <summary>
    /// Returns open lots (is_closed=false) for the given user-visible
    /// brokerage and security, ordered ascending by
    /// <c>acquired_at</c> (FIFO consumption order). Drives the
    /// editor's Sell / SellX preview popover.
    /// </summary>
    /// <remarks>
    /// <paramref name="brokerageAccountId"/> is the user-visible
    /// brokerage; the repository resolves the Holdings sibling via
    /// <c>accounts.holdings_account_id</c> and joins lots through
    /// the holding row.
    /// </remarks>
    public async Task<LotsLookupResult> GetOpenLotsAsync(
        Guid ledgerId,
        Guid brokerageAccountId,
        Guid securityId,
        CancellationToken cancellationToken = default)
    {
        var brokerage = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == brokerageAccountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.HoldingsAccountId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (brokerage is null)
            return LotsLookupResult.Fail(LotsLookupFailure.AccountNotInLedger);
        if (brokerage.AccountType != "investment")
            return LotsLookupResult.Fail(LotsLookupFailure.AccountNotInvestment);
        if (brokerage.HoldingsAccountId is not { } holdingsAccountId)
            return LotsLookupResult.Fail(LotsLookupFailure.AccountMissingHoldingsSibling);

        var securityExists = await _db.Securities.AsNoTracking()
            .AnyAsync(s => s.Id == securityId && s.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (!securityExists)
            return LotsLookupResult.Fail(LotsLookupFailure.SecurityNotInLedger);

        var lots = await _db.Lots.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId
                     && !l.IsClosed
                     && _db.Holdings.Any(h => h.Id == l.HoldingId
                                           && h.AccountId == holdingsAccountId
                                           && h.SecurityId == securityId
                                           && h.LedgerId == ledgerId))
            .OrderBy(l => l.AcquiredAt)
            .ThenBy(l => l.Id)
            .Select(l => new InvestmentLotDto(
                l.Id, l.AcquiredAt, l.Quantity, l.UnitCost))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return LotsLookupResult.Ok(lots);
    }

}
