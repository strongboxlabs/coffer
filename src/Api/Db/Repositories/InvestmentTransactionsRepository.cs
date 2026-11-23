using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;
using Coffer.Domain.Money;

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
///   between holdings — and for every investment leg of a header
///   whose date, action, hidden or merged flag changed.</item>
/// </list>
/// Every mutation method here inserts legs as EF-tracked rows and
/// reaches <c>SaveChangesAsync</c>, so both interceptors see the leg
/// and header changes from the ChangeTracker and the recomputes are
/// implicit (the legs were once inserted via the
/// <c>insert_investment_legs</c> TVF, which bypassed the ChangeTracker
/// and forced hand-driven recomputes; that TVF was retired once the
/// txn_legs trigger family went away — ADR-0032 lineage). The explicit
/// holdings recomputes left here cover what no save the interceptor
/// sees carries: lots added in a save of their own, after the legs, and
/// a fee leg's amount (see <see cref="PatchAsync"/>).</para>
///
/// <para><b>The ledger write lock</b> (<see cref="LedgerWriteLock"/>, migration 275).
/// Every mutation method that recomputes (create, patch, delete, the in-kind
/// conversion) opens its transaction with <c>BeginLedgerWriteAsync</c> as its first
/// statement, before the reads that decide what it writes: the database refuses a
/// recompute in a transaction that wrote or row-locked without the lock, and two
/// writers of one ledger take turns. Approving writes only <c>needs_review</c>, which
/// no recompute reads, and takes no lock.</para>
/// </remarks>
public sealed class InvestmentTransactionsRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    /// <param name="db">The request's context.</param>
    /// <param name="clock">The injected server clock, which decides the day the lot-basis
    /// move between currencies reads a rate for (ADR-0114 D13, the one rate rule).
    /// Required: a caller that left it out would read the wall clock instead of the
    /// injected one.</param>
    public InvestmentTransactionsRepository(AppDbContext db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _clock = clock;
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
        // Inactive-account gate. Per-role so the
        // SPA's editor can place the error message next to the right
        // field (the existing per-role "*NotInLedger" pattern).
        BrokerageInactive,
        CategoryInactive,
        TransferInactive,
        FeeAccountInactive,
        // Migration 256: the save would leave some sale taking more shares than
        // the account held at that point — or deepen a shortfall already there.
        // Checked against the FIFO walk itself, so it holds for a sale in 2014 as
        // much as for one today, and for a BUY edited down under a later sale.
        InsufficientShares,
        // A transfer-shape action between the brokerage and a real account in another
        // currency (ADR-0114 D13, ADR-0029): transferAmount, the other account's own
        // leg, is required, nonzero and opposite in sign to the brokerage's leg of the
        // transfer pair.
        TransferAmountRequired,
        TransferAmountsNonzero,
        TransferAmountsSign,
        // A typed amount or fee with more places than the brokerage's currency allows
        // (ADR-0114 D15). Its FailureDetail names the currency and the request field.
        AmountPlacesExceedCurrency,
        // A share move between brokerages in different currencies with a lot whose
        // acquisition day has no derived rate (ADR-0114 D13). Its FailureDetail names
        // each missing stored leg and its day.
        RateUnavailable,
    }

    /// <summary>
    /// What a refusal names beyond its <see cref="CreateFailure"/> code, for the two
    /// refusals whose reason is data: the currency and request field of
    /// <see cref="CreateFailure.AmountPlacesExceedCurrency"/> (ADR-0114 D15), and each
    /// missing stored leg of <see cref="CreateFailure.RateUnavailable"/> (ADR-0114 D13).
    /// </summary>
    /// <param name="CurrencyCode">The currency the refused figure lands in.</param>
    /// <param name="Field">The request field's wire name.</param>
    /// <param name="MissingRates">Each missing stored leg, in the direction it is stored
    /// (account currency into the reporting currency), with its day.</param>
    public sealed record FailureDetail(
        string? CurrencyCode = null,
        string? Field = null,
        IReadOnlyList<RateUnavailable>? MissingRates = null)
    {
        /// <summary>The places refusal of a figure in <paramref name="currencyCode"/> sent
        /// as <paramref name="field"/>.</summary>
        public static FailureDetail Places(string currencyCode, string field) => new(currencyCode, field);
    }

    /// <summary>
    /// Result of <see cref="CreateAsync"/>. On <see cref="Ok"/>,
    /// <see cref="HeaderId"/> is the new header's id. On any
    /// other value, <see cref="HeaderId"/> is unset and the
    /// failure maps to a 422 error code at the endpoint, with
    /// <see cref="Detail"/> when the refusal names data.
    /// </summary>
    public readonly record struct CreateResult(
        CreateFailure? Failure,
        Guid HeaderId,
        AnnotationViolation? Refusal = null,
        FailureDetail? Detail = null)
    {
        public static CreateResult Ok(Guid id) => new(null, id);
        public static CreateResult Fail(CreateFailure f, FailureDetail? detail = null) => new(f, Guid.Empty, null, detail);
        /// <summary>The write primitive refused the event's notes; nothing is written.</summary>
        public static CreateResult Refused(AnnotationViolation v) => new(null, Guid.Empty, v);
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
        IReadOnlyList<TxnLegRow> Legs,
        FailureDetail? Detail = null)
    {
        public static TemplateLegsResult Ok(IReadOnlyList<TxnLegRow> legs) => new(null, legs);
        public static TemplateLegsResult Fail(CreateFailure f, FailureDetail? detail = null) =>
            new(f, Array.Empty<TxnLegRow>(), detail);
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
        // The accounts the template's legs already sit on, when this rebuilds an
        // existing template: those may stay inactive, and the template's stored
        // figures exempt an unchanged one from the checks. Null on a create.
        IReadOnlySet<Guid>? alreadyPresent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // transfer_shares is a one-time in-kind move, never a recurring shape.
        if (request.Action == LedgerActions.TransferShares)
            return TemplateLegsResult.Fail(CreateFailure.ActionInvalid);

        // A rebuild of an existing template checks only the figures it changes, as a
        // PATCH does (ADR-0114 D13, ADR-0114 D15): an edit of the memo or the schedule
        // resends the stored amounts, and one that no longer fits is refused at fire,
        // not here. The stored legs are read before the caller replaces them.
        StoredFigures? stored = null;
        if (alreadyPresent is not null)
            stored = StoredFigures.Of(
                await _db.TxnLegs.AsNoTracking()
                    .Where(l => l.HeaderId == templateHeaderId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false),
                request.BrokerageAccountId);

        var resolved = await ValidateAndResolveAsync(
            ledgerId, request, allowInactiveAccounts: false, alreadyPresent, stored, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } f) return TemplateLegsResult.Fail(f, resolved.Detail);
        var postings = resolved.Postings;

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
    /// + holdings + lot in one Postgres transaction. Calls
    /// the holdings cost-basis recompute function after the save's
    /// statements, inside its transaction, so basis math stays
    /// consistent with the brokerage's policy flag.
    /// </summary>
    /// <remarks>Balances, holdings/lots, and posting counts all
    /// recompute automatically via
    /// <see cref="LegDerivedRecomputeInterceptor"/> +
    /// <see cref="HoldingsRecomputeInterceptor"/> on the
    /// <c>SaveChangesAsync</c> that persists the EF-tracked legs — no
    /// explicit recompute call here, but for an in-kind transfer's two positions.
    /// The ledger write lock (<see cref="LedgerWriteLock"/>, migration 275) is the
    /// first statement, before the validation reads.</remarks>
    public async Task<CreateResult> CreateAsync(
        Guid ledgerId,
        CreateInvestmentTransactionRequest request,
        // Adjust-at-post fire (ADR-0049): when set, the committed occurrence is
        // stamped to its series + slot. Null = a normal live investment create.
        Guid? recurringTransactionId = null,
        DateOnly? occurrenceDate = null,
        // Bypass the inactive-account gate. Only convert_in_kind_transfer
        // sets this (a historical correction on since-closed accounts); every live
        // create keeps the guard. See ValidateAndResolveAsync.
        bool allowInactiveAccounts = false,
        CancellationToken cancellationToken = default,
        // The event's notes when they come from somewhere other than the body: the
        // in-kind transfer that replaces a sell and a buy joins theirs. Given the
        // event's shape; null plans the body's memo and tags.
        Func<TxnShape, AnnotationPlan<Guid>>? notesFor = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ledger write lock (migration 275), first. The saves below recompute
        // balances and holdings, which the database refuses in a transaction that wrote
        // without it, and every read from here on (the validation, the in-kind FIFO
        // plan, the shortfall baseline) decides what is written, so they read under it
        // too. Inside a caller's transaction (a reminder fire, an in-kind conversion),
        // which took it first, this does nothing and the caller commits; a refusal
        // returns without committing, so in both cases nothing is kept.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // Validate the action × field matrix + resolve accounts/security +
        // build the posting pairs. Shared verbatim with PATCH and the
        // recurring-template path (ADR-0047) — see ValidateAndResolveAsync.
        var resolved = await ValidateAndResolveAsync(
            ledgerId, request, allowInactiveAccounts, alreadyPresent: null, stored: null, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } f) return CreateResult.Fail(f, resolved.Detail);
        var (holdingsAccountId, postings, destHoldingsAccountId) =
            (resolved.HoldingsAccountId, resolved.Postings, resolved.DestHoldingsAccountId);

        // Resolved values for the header + lot side of the write.
        var headerId = Guid.NewGuid();
        var action = request.Action;
        var postedAt = request.PostedAt;
        var totalCommission = request.FeeAmount ?? 0m;

        // transfer_shares (in-kind, ADR-0065): compute the source FIFO lots to
        // move + build the per-lot postings. On a fresh create the source lots
        // already reflect the correct pre-transfer state (this transfer doesn't
        // exist yet), so the plan can be read before the write. Each lot's basis is
        // priced for the destination before anything is written, so a lot with no
        // rate refuses the whole move (ADR-0114 D13).
        var transferPlan = (IReadOnlyList<MovedLot>)Array.Empty<MovedLot>();
        if (action == LedgerActions.TransferShares)
        {
            var (planFail, plan) = await BuildTransferSharesPlanAsync(
                ledgerId, holdingsAccountId, request.SecurityId!.Value,
                request.Shares!.Value, cancellationToken).ConfigureAwait(false);
            if (planFail is { } pf) return CreateResult.Fail(pf);
            var (missing, priced) = await PriceMovedLotsAsync(
                ledgerId, plan, resolved.BrokerageCurrency, resolved.DestCurrency!, cancellationToken).ConfigureAwait(false);
            if (missing is not null) return CreateResult.Fail(CreateFailure.RateUnavailable, missing);
            transferPlan = priced;
            postings = BuildTransferSharesPostings(
                request.BrokerageAccountId, holdingsAccountId,
                request.TransferAccountId!.Value, destHoldingsAccountId!.Value,
                request.SecurityId!.Value, transferPlan);
        }

        // The event's one memo and tag set, planned from its brokerage before any
        // write, so a refusal stores nothing: on the brokerage leg of a one-posting
        // event, on the header of a multi-posting one. The brokerage owns it (for
        // transfer_shares, the source; TransferAccountId is the destination).
        var shape = AnnotationWriter.Shape(request.BrokerageAccountId, action, PlannedLegs(postings),
            await AnnotationWriter.AccountFactsAsync(
                    _db, postings.SelectMany(p => new[] { p.Cash.AccountId, p.Counterparty.AccountId }), cancellationToken)
                .ConfigureAwait(false));
        AnnotationPlan<Guid> notesPlan;
        if (notesFor is not null)
        {
            notesPlan = notesFor(shape);
        }
        else
        {
            var notes = await AnnotationWriter.ResolveAsync(
                    _db, ledgerId, NotesIntent(request.BrokerageAccountId, request), cancellationToken)
                .ConfigureAwait(false);
            notesPlan = AnnotationPlanner.Seed(shape, notes);
        }
        if (!notesPlan.IsValid) return CreateResult.Refused(notesPlan.Violations[0]);

        // Inside the transaction and before the first flush, so this is the
        // position the save starts from. See ShortfallWorsenedAsync.
        var shortfallBefore = await ShortfallsAsync(
            ShortfallPairs([], holdingsAccountId, destHoldingsAccountId, request.SecurityId),
            cancellationToken).ConfigureAwait(false);

        // Owned by the request's brokerage (mig 263): the one on every posting, and
        // for transfer_shares the SOURCE brokerage (TransferAccountId is the
        // destination).
        var header = TxnHeaderRow.NewTransaction(headerId, ledgerId, request.BrokerageAccountId);
        header.Payee = HeaderText.Normalize(request.Payee);
        header.CheckNumber = HeaderText.Normalize(request.CheckNumber);
        header.PostedAt = postedAt;
        // NOT NULL since mig 189 — see TransactionsRepository.
        header.TransactedAt = request.TransactedAt ?? postedAt;
        header.Action = action;
        // Adjust-at-post fire stamps the occurrence to its series + slot
        // (null for a normal live create).
        header.RecurringTransactionId = recurringTransactionId;
        header.OccurrenceDate = occurrenceDate;
        _db.TxnHeaders.Add(header);

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
        // explicit recompute call needed. The planned notes land in the same save.
        var legRows = legs.Select(ToTxnLegRow).ToList();
        _db.TxnLegs.AddRange(legRows);
        await AnnotationWriter.ApplyAsync(_db, header, legRows, notesPlan, cancellationToken).ConfigureAwait(false);
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
            // Acquisition actions (Buy / BuyXfr / Reinvest) need a
            // matching lot row. The lot binds to the holding via
            // holding_id; query the (now-existing) holding for its id
            // and add the lot. Sell-side actions don't add lots — the
            // recompute's FIFO walk closes existing lots in place.
            var quantity = request.Shares ?? 0m;
            // Lot cost basis uses the authoritative amount, at the brokerage's
            // places (ADR-0114 D15), not a re-derivation from the rounded price —
            // same money the sec leg carries, so basis == cash paid.
            var (sharePrice, _) = ResolveTradeMoney(
                request.Amount, request.Price ?? 0m, quantity, resolved.BrokerageCurrency);
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

        // Refuse BEFORE committing. When the lock opened the transaction, returning
        // disposes it and rolls everything back; inside a caller's (a reminder fire,
        // an in-kind conversion), that caller returns without committing, same effect.
        if (await ShortfallWorsenedAsync(shortfallBefore, cancellationToken).ConfigureAwait(false))
            return CreateResult.Fail(CreateFailure.InsufficientShares);

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);

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
        CreateFailure? CreateFail,
        AnnotationViolation? Refusal = null,
        FailureDetail? Detail = null)
    {
        public static PatchResult Ok() => new(null, null);
        public static PatchResult Fail(PatchFailure f) => new(f, null);
        public static PatchResult Fail(CreateFailure f, FailureDetail? detail = null) => new(null, f, null, detail);
        /// <summary>The write primitive refused the event's notes; nothing is written.</summary>
        public static PatchResult Refused(AnnotationViolation v) => new(null, null, v);
    }

    /// <summary>
    /// PATCH an existing investment txn. Per ADR-0025, postings
    /// replace wholesale — the supplied <paramref name="request"/>
    /// IS the new state of the world. The new shape is built via the
    /// same per-action posting builders as <see cref="CreateAsync"/>,
    /// and existing legs keep their identity where it has room for
    /// them. Recompute runs after the save's statements, inside its
    /// transaction.
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
    /// is deleted, because anything keyed on leg_id dies with the row,
    /// <c>txn_leg_recon</c> most damagingly.</para>
    ///
    /// <para>Balances and posting counts recompute via
    /// <see cref="LegDerivedRecomputeInterceptor"/>, whose Modified branch
    /// supplies both the OLD and the NEW pair from OriginalValues — so no
    /// two-flush split is needed to make it correct. Holdings and lots
    /// recompute via <see cref="HoldingsRecomputeInterceptor"/> for every
    /// investment leg the edit writes and for a changed date or action, and
    /// are reconciled once more EXPLICITLY at the end, for what that
    /// interceptor does not see: the acquisition lot added after the leg
    /// save, and a fee leg's amount, which the walk folds into basis.</para>
    /// </remarks>
    public async Task<PatchResult> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        PatchInvestmentTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ONE transaction for the whole method, opened before the first read —
        // the boundary the bank PATCH has always had, and for the reason it
        // documents: the ledger-membership guard has to run inside the
        // transaction that later writes, or there is a TOCTOU window between
        // the endpoint's cross-ledger check and these writes.
        //
        // Opened here, not further down (one inside the merge branch, one after
        // validation), where every read above them — including the membership
        // guard and the whole action x field matrix validation — would run
        // unprotected. `await using` rolls back on any early
        // return, so the failure paths below need no commit.
        //
        // Opened by the ledger write lock (migration 275), its first statement: the
        // saves below recompute balances and holdings, in several steps, and the
        // database refuses a recompute in a transaction that wrote without the lock.
        // Held from here to the commit, it keeps every other writer of the ledger out
        // between the steps.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // A merged-away header is not found by an edit (MergedAway). The merge reads it as
        // it is: its own gate below refuses a tombstone at either end.
        var headers = request.MergeFromHeaderId is null ? _db.TxnHeaders.NotMergedAway() : _db.TxnHeaders;
        var existing = await headers
            .FirstOrDefaultAsync(
                h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken).ConfigureAwait(false);
        if (existing is null) return PatchResult.Fail(PatchFailure.HeaderNotFound);

        // Investment-side merge (mirrors bank PatchAsync, slice
        // "investment-merge"). Only the merge route sets MergeFromHeaderId,
        // on a request carrying nothing else: the editor row (existing)
        // folds into the chosen candidate, which keeps its own fields. Validate both ends, stamp loser→winner + the winner's
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
            // A reminder's template is no transaction (ADR-0047), as on the bank
            // merge: the candidates never offer it, and a merge into it would fold
            // a real row away into one no register shows.
            if (winner is null
                || winner.NeedsReview
                || winner.IsMergedInto is not null
                || winner.IsHidden
                || winner.IsRecurringTemplate)
                return PatchResult.Fail(PatchFailure.MergeSourceInvalid);

            existing.IsMergedInto = winner.Id;   // editor row → loser
            winner.IsMergeWinner = true;          // candidate → winner (idempotent)
            // ...and the loser is no longer awaiting review. This branch takes a
            // merge-ONLY patch and returns before any approve handling, so without
            // this an investment loser would stay flagged, and the sidebar's review
            // dot would stay lit on an account whose register has nothing left to
            // review.
            //
            // Done HERE rather than by teaching the SPA to send the flag: the
            // server owns the end state, and a merge-only PATCH should not depend
            // on a caller remembering to pair it with an approve.
            existing.NeedsReview = false;
            // Winner adopts the imported (loser) row's date — the fresh feed
            // row's date is authoritative for the merged event. Written onto the
            // winner's canonical row since mig 230, with its feed date captured
            // to txn_header_originals first.
            var importedPostedAt = request.PostedAt ?? existing.PostedAt;
            await SetPostedAtAsync(winner, importedPostedAt, cancellationToken)
                .ConfigureAwait(false);
            // Both stamps are header changes the walks read, so this save
            // recomputes them, in this transaction, with no leg written: the
            // loser's is_merged_into takes its shares out of its positions, its
            // amounts out of its accounts' balances (mig 163) and its trades off the
            // day they priced (mig 273), and the winner's moved date re-sorts its
            // lots, basis and realized gains against the rest of each position it
            // holds, re-walks its accounts, and re-derives the trade price of the day
            // it now sits on (LegDerivedRecomputeInterceptor,
            // HoldingsRecomputeInterceptor, TradePriceFromLegInterceptor).
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PatchResult.Ok();
        }

        // ADR-0031 Phase 3d.2 + gap-fix: PATCH allows converting a
        // bank-shape header into investment-shape when either:
        //   * Classifier hint is set (orchestrator detected a known
        //     pattern at sync time — the canonical hinted upgrade),
        //     OR
        //   * The header came from a feed source other than manual
        //     entry (origin != 'manual'). The classifier cannot
        //     match every institution's wire
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
        // The memo and tags are not part of the shape and are planned from the PATCH
        // body itself (presence-tracked), so they are not carried over here.
        // A date on the stored day keeps the stored timestamp (HeaderDates): the
        // editor sends midnight UTC of the picked day, and the FIFO walk orders by
        // posted_at, so overwriting the time of day on an untouched date would
        // re-sort the event among its same-day neighbours.
        var asCreate = new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = request.BrokerageAccountId ?? Guid.Empty,
            PostedAt           = request.PostedAt is { } requestedPostedAt
                                     ? HeaderDates.Resolve(existing.PostedAt, requestedPostedAt)
                                     : existing.PostedAt,
            Action             = request.Action             ?? existing.Action ?? string.Empty,
            Payee              = request.Payee,
            CheckNumber        = request.CheckNumber,
            TransactedAt       = request.TransactedAt is { } requestedTransactedAt
                                     ? HeaderDates.Resolve(existing.TransactedAt, requestedTransactedAt)
                                     : null,
            SecurityId         = request.SecurityId,
            Shares             = request.Shares,
            Price              = request.Price,
            Amount             = request.Amount,
            CategoryAccountId  = request.CategoryAccountId,
            TransferAccountId  = request.TransferAccountId,
            TransferAmount     = request.TransferAmount,
            FeeAccountId       = request.FeeAccountId,
            FeeAmount          = request.FeeAmount,
        };

        // brokerageAccountId must be supplied (PATCH can move a txn
        // between brokerages, but the destination is required).
        if (asCreate.BrokerageAccountId == Guid.Empty)
            return PatchResult.Fail(CreateFailure.AccountNotInLedger);

        // The figures the edit would replace, so the validation checks only what it
        // changes (ADR-0114 D13, D15): read before the leg rebuild below changes them.
        var storedFigures = StoredFigures.Of(
            await _db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId)
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            asCreate.BrokerageAccountId);

        // Same validation + resolution + posting build as CreateAsync (the
        // PATCH body IS the new state of the world per ADR-0025). For
        // transfer_shares, postings are empty here and built after the leg-drop
        // flush below (the FIFO plan must read the RESTORED source lots). The
        // accounts the event already sits on may stay inactive; read inside this
        // transaction, before the leg rebuild below changes them.
        var alreadyPresent = await TransactionsRepository
            .LegAccountIdsAsync(_db, ledgerId, headerId, cancellationToken).ConfigureAwait(false);
        var resolved = await ValidateAndResolveAsync(
            ledgerId, asCreate, allowInactiveAccounts: false, alreadyPresent, storedFigures, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } f) return PatchResult.Fail(f, resolved.Detail);
        var (holdingsAccountId, postings, destHoldingsAccountId) =
            (resolved.HoldingsAccountId, resolved.Postings, resolved.DestHoldingsAccountId);

        // The event's notes and owner, planned before the first write so a refusal
        // stores nothing. The request's brokerage writes and owns the rebuilt event
        // (the SOURCE brokerage for transfer_shares): the hand-over when a feed or
        // Moneydance bank-shape row becomes an event, and when an event moves to
        // another brokerage. The event's value survives the rebuild (the planner moves
        // it between the brokerage leg and the header when the posting count changes);
        // a counterpart's own value follows its (account, role). An in-kind transfer's
        // postings exist only after the drop flush below, so it is planned there.
        var stored = await AnnotationWriter.LoadAsync(_db, existing, cancellationToken,
                postings.SelectMany(p => new[] { p.Cash.AccountId, p.Counterparty.AccountId })
                    .Append(asCreate.BrokerageAccountId)
                    .Concat(asCreate.TransferAccountId is { } dest ? [dest] : []))
            .ConfigureAwait(false);
        var notesIntent = await AnnotationWriter.ResolveAsync(
                _db, ledgerId, NotesIntent(asCreate.BrokerageAccountId, request), cancellationToken)
            .ConfigureAwait(false);
        AnnotationPlan<Guid>? notesPlan = null;
        if (asCreate.Action != LedgerActions.TransferShares)
        {
            notesPlan = PlanEventNotes(stored, asCreate, postings, notesIntent);
            if (!notesPlan.IsValid) return PatchResult.Refused(notesPlan.Violations[0]);
        }

        // Read before the first write (the leg-drop flush below): wherever this
        // header held shares, and wherever the edit puts them — a changed
        // security moves the sale onto a pair it never touched.
        var priorPairs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.SecurityId != null && l.Quantity != null)
            .Select(l => new { l.AccountId, SecurityId = l.SecurityId!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var shortfallBefore = await ShortfallsAsync(
            ShortfallPairs(
                priorPairs.Select(p => (p.AccountId, p.SecurityId)),
                holdingsAccountId, destHoldingsAccountId, asCreate.SecurityId),
            cancellationToken).ConfigureAwait(false);

        var totalCommission = asCreate.FeeAmount ?? 0m;

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
        await AnnotationWriter.CaptureOriginalsAsync(_db, existing, cancellationToken)
            .ConfigureAwait(false);
        existing.Payee        = HeaderText.Normalize(asCreate.Payee);
        existing.CheckNumber  = HeaderText.Normalize(asCreate.CheckNumber);
        existing.PostedAt     = asCreate.PostedAt;
        // NOT NULL since mig 189: a null request value means "no distinct tax
        // date", stored as the posted date. This is the in-place UPDATE path — the
        // one the create-path coalesce above does not cover.
        existing.TransactedAt = asCreate.TransactedAt ?? asCreate.PostedAt;
        existing.Action       = asCreate.Action;
        // Approve only when ASKED to, matching the bank PATCH.
        //
        // Not unconditionally: "the investment editor's only Save-pressed exit IS
        // Accept" is a claim about one client, not the endpoint. Clearing it on
        // every PATCH would make every other caller — MCP, a direct PATCH, a
        // script correcting a typo across a ledger — accept every row it touches,
        // and nobody could fix an imported row and leave it queued. The editor
        // sends approve: true on a needs-review row.
        if (request.Approve == true) existing.NeedsReview = false;

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
        // Deleting every leg and rebuilding with fresh ids would kill anything
        // keyed on leg_id with the row — and txn_leg_recon.leg_id cascades on
        // delete (mig 171), so editing a CLEARED brokerage row, even just its
        // memo, would silently revert it to uncleared and destroy cleared_at /
        // cleared_by_user_id, and a reconciled brokerage account would drift with
        // nothing on screen to say why. The bank path keeps the legs a request
        // names by legId and mutates them in place.
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
                // collide with a doomed one on uq_txn_legs_posting. Its memo and tags
                // are the plan's, applied once every leg is in place.
                match.Amount      = spec.Leg.Amount;
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
        // reusing the identity — the reconciled state is what a rebuild would
        // destroy. It is deliberately not dropped here; nor does the bank path
        // drop it. (There is no txn_leg_overrides row to clear: migration 230
        // drops that table.) Only the index dance is left.
        if (reusedLegIds.Count > 0)
        {
            // Park kept legs at indices nothing else can claim, and flush, so the
            // final renumber below cannot transiently collide with a leg being
            // deleted in the same save. The bank calls the equivalent step
            // mandatory; EF gives no ordering guarantee between the delete and the
            // update that wants the vacated index.
            //
            // Only when something actually MOVES. Shifting unconditionally would
            // also make every reused leg Modified on every edit, and every
            // interceptor that watches legs would recompute for a change that is
            // not one.
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
            // Priced as the create prices them, so a PATCH of the move and the create
            // store the same figures (ADR-0114 D13).
            var (missing, priced) = await PriceMovedLotsAsync(
                ledgerId, plan, resolved.BrokerageCurrency, resolved.DestCurrency!, cancellationToken).ConfigureAwait(false);
            if (missing is not null) return PatchResult.Fail(CreateFailure.RateUnavailable, missing);  // await using rolls back

            transferPlan = priced;
            postings = BuildTransferSharesPostings(
                asCreate.BrokerageAccountId, holdingsAccountId,
                asCreate.TransferAccountId!.Value, destHoldingsAccountId!.Value,
                asCreate.SecurityId!.Value, transferPlan);

            notesPlan = PlanEventNotes(stored, asCreate, postings, notesIntent);
            if (!notesPlan.IsValid) return PatchResult.Refused(notesPlan.Violations[0]);  // await using rolls back

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
        // Every leg is in place at its final posting: store the planned notes and
        // owner with them.
        await AnnotationWriter.ApplyAsync(
                _db, existing, _db.TxnLegs.Local.Where(l => l.HeaderId == headerId).ToList(),
                notesPlan!, cancellationToken)
            .ConfigureAwait(false);
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
            // Lot cost basis uses the authoritative amount, at the brokerage's
            // places (ADR-0114 D15), not a re-derivation from the rounded price —
            // same money the sec leg carries, so basis == cash paid.
            var (sharePrice, _) = ResolveTradeMoney(
                asCreate.Amount, asCreate.Price ?? 0m, quantity, resolved.BrokerageCurrency);
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

        // HoldingsRecomputeInterceptor recomputes the positions of the investment
        // legs a save writes, and of every investment leg of a header whose date or
        // action changed, so a date-only edit that keeps every leg as it was still
        // re-sorts the walk. Two inputs reach it in no save it sees: the
        // acquisition lot added above, saved after the legs (a lots-only save
        // recomputes nothing), which the walk has to bring to its derived state;
        // and a fee leg's amount, which the walk folds into basis when the
        // brokerage treats commissions as basis, on a leg that is not
        // investment-shaped. So the pairs this header touches, before and after,
        // are reconciled explicitly. Recompute is a full re-derivation, so doing it
        // when the interceptor already did is a no-op, not a double-count.
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

        // `await using` rolls the whole edit back on this return.
        if (await ShortfallWorsenedAsync(shortfallBefore, cancellationToken).ConfigureAwait(false))
            return PatchResult.Fail(CreateFailure.InsufficientShares);

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);

        return PatchResult.Ok();
    }

    // ----------------------------------------------------------------
    // Shortfall guard (migration 256)
    // ----------------------------------------------------------------

    /// <summary>
    /// The holdings-side pairs a save can change the shortfall of: wherever the
    /// header held shares before, plus wherever the request puts them (and, for
    /// an in-kind transfer, its destination).
    /// </summary>
    private static List<(Guid AccountId, Guid SecurityId)> ShortfallPairs(
        IEnumerable<(Guid AccountId, Guid SecurityId)> prior,
        Guid holdingsAccountId, Guid? destHoldingsAccountId, Guid? securityId)
    {
        var pairs = prior.ToList();
        if (securityId is { } s)
        {
            pairs.Add((holdingsAccountId, s));
            if (destHoldingsAccountId is { } d) pairs.Add((d, s));
        }
        return pairs.Distinct().ToList();
    }

    private async Task<Dictionary<(Guid AccountId, Guid SecurityId), decimal>> ShortfallsAsync(
        IReadOnlyCollection<(Guid AccountId, Guid SecurityId)> pairs,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(Guid AccountId, Guid SecurityId), decimal>(pairs.Count);
        foreach (var pair in pairs)
        {
            result[pair] = await _db.HoldingsFifoShortfall(pair.AccountId, pair.SecurityId)
                .Select(r => r.Shortfall)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// True when the save left any pair shorter than it found it.
    /// </summary>
    /// <remarks>
    /// <para>"WORSE", NOT "SHORT". Imported data can already carry small
    /// shortfalls — share residues from the source system — and refusing any
    /// save on a short position would lock the user out of exactly the rows
    /// they are cleaning up. A save may leave a shortfall where it was; it may
    /// not create one or deepen one.</para>
    ///
    /// <para>Per pair, not summed: improving one position must not buy
    /// permission to oversell another.</para>
    ///
    /// <para>Asked of the FIFO walk rather than of the <c>lots</c> table,
    /// because the table is drained in place and only knows today. The walk is
    /// the writer's own algorithm, so this is exactly the shortfall the save is
    /// about to persist — including the indirect case, a BUY edited down until a
    /// LATER sale no longer fits.</para>
    /// </remarks>
    private async Task<bool> ShortfallWorsenedAsync(
        IReadOnlyDictionary<(Guid AccountId, Guid SecurityId), decimal> before,
        CancellationToken cancellationToken)
    {
        var after = await ShortfallsAsync(before.Keys.ToList(), cancellationToken)
            .ConfigureAwait(false);
        return after.Any(kv => kv.Value > before[kv.Key]);
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
    /// <para>Balances (<see cref="LegDerivedRecomputeInterceptor"/>) and
    /// holdings, lots and realized gains
    /// (<see cref="HoldingsRecomputeInterceptor"/>) recompute on this
    /// method's <c>SaveChangesAsync</c>, in both branches. The hard-delete
    /// branch tracks the header DELETE via EF, and each interceptor reads the
    /// doomed header's legs from the DB before the save, so the affected
    /// accounts and positions survive the cascade; the rows merged into the
    /// header, which the delete un-merges, are tracked first so the save
    /// recomputes them too (<see cref="MergeLosers"/>). The soft-hide branch flips
    /// <c>is_hidden</c>, which both walks read, so each interceptor treats the
    /// change as one to every leg of the header, and a hidden buy's shares
    /// leave its position (ADR-0032, 2026-11-03 amendment).</para>
    ///
    /// <para>The save runs in a transaction this method opens with the ledger write
    /// lock (<see cref="LedgerWriteLock"/>, migration 275) as its first statement, so the
    /// header read, the interceptors' reads of its legs and the cascade all come after
    /// it.</para>
    /// </remarks>
    public async Task<DeleteOutcome> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275), first. Without a transaction of its own
        // the save would run in the one the stored-notes interceptor opens, after the
        // recompute interceptors' reads and with the cascade's row locks taken before any
        // recompute asked for the lock, which the database refuses.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // A merged-away header is not found (MergedAway). Deleting a winner still
        // un-merges its losers below.
        var header = await _db.TxnHeaders.NotMergedAway()
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
        // the bulk unhide is the undo. The rule is TxnHeaderRow.IsDeletedOutright,
        // shared with the bank delete.
        DeleteOutcome outcome;
        if (header.DeletesOutright())
        {
            // Hard delete cascades through txn_legs. Both interceptors
            // (mig 102 balance + mig 104 holdings) read the doomed
            // legs from the live DB in SavingChangesAsync — before
            // the cascade — so the affected accounts + holdings set
            // survives the DB-side delete. A manual row can be a merge
            // winner, and deleting it un-merges its losers; tracked first,
            // the save recomputes them (MergeLosers).
            await MergeLosers.TrackAsync(_db, [header.Id], cancellationToken).ConfigureAwait(false);
            _db.TxnHeaders.Remove(header);
            outcome = DeleteOutcome.HardDeleted;
        }
        else
        {
            // Soft-hide leaves the legs in place, but both walks leave a
            // hidden header out (balances since mig 103, holdings since mig
            // 117, ADR-0040), so the IsHidden flip on this SaveChanges
            // recomputes the header's accounts and positions through
            // LegDerivedRecomputeInterceptor and HoldingsRecomputeInterceptor.
            // Soft-delete also clears needs_review (ADR-0052 D3): a deleted row
            // is resolved, not awaiting acceptance — otherwise it strands as
            // is_hidden + needs_review (invisible but still queued).
            header.IsHidden = true;
            header.NeedsReview = false;
            outcome = DeleteOutcome.SoftHidden;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);

        return outcome;
    }

    /// <summary>
    /// Investment merge candidates for the editor's "Merge candidates" strip
    /// (mirrors <see cref="TransactionsRepository.GetMergeCandidatesAsync"/> for
    /// the bank shape): settled investment rows the edited, still-unreviewed row
    /// could fold into.
    /// </summary>
    /// <remarks>
    /// <para><b>The anchor reads the ingest HINTS, not a security leg.</b> Requiring
    /// the edited row to carry <c>posting_role='security'</c> with a security_id, a
    /// quantity, and a non-null <c>action</c> — while also requiring
    /// <c>needs_review</c> — asks for two things that are mutually exclusive in this
    /// product, so the panel would never return a single row: ingest writes a
    /// BANK-shaped two-leg posting (account + Uncategorized) and carries the
    /// investment detail in the <c>ingest_*</c> columns, and the investment shape
    /// only appears at Accept, which clears <c>needs_review</c> in the same
    /// transaction. The invariant <c>posting_role IS NOT NULL ⇔ txn_headers.action
    /// IS NOT NULL</c> holds for every row, unreviewed ones included: no trigger
    /// enforces it (migration 084 dropped <c>trg_validate_posting_role</c>), so
    /// every leg writer upholds it — <c>CreateAsync</c> / <c>PatchAsync</c> stamp
    /// a role on every investment leg, and ingest writes <c>action = NULL</c> with
    /// <c>posting_role = NULL</c> (ADR-0031's action-bearing needs_review rows are
    /// not what ingest writes).</para>
    ///
    /// <para>So the anchor is defined over what an unreviewed row ACTUALLY has,
    /// the way the bank anchor is: its source leg (the non-category one) for the
    /// account and the effective date, and the <c>ingest_*</c> carriers for the
    /// money, the share count and the security. Reshaping ingest to suit a
    /// security-leg predicate is not an option — a role-bearing leg on an
    /// action-less header breaks the invariant above.</para>
    ///
    /// <para><b>Matching.</b> Candidates are settled, un-merged, effectively
    /// visible investment rows whose holdings leg sits on the source account's
    /// Holdings sibling (so: same brokerage, same ledger), within ±7 effective
    /// days, matched on MAGNITUDE:</para>
    /// <list type="number">
    ///   <item><description><c>ingest_amount</c> — the wire's own cash total, kept
    ///   as sent and read at the brokerage currency's places (ADR-0114 D15). The
    ///   only usable number for a reinvest, whose net on the account is zero by
    ///   construction, so no leg amount identifies it (column added by
    ///   mig 228).</description></item>
    ///   <item><description>else the summed source-account amount — a plain buy or
    ///   sell moves real cash, so its own leg carries the principal.</description></item>
    ///   <item><description>else <c>ingest_shares</c> against the candidate's
    ///   quantity, for a basis-only move where no amount is informative.</description></item>
    ///   <item><description>and the same principal on a candidate's holdings leg,
    ///   which is the only place a dividend reinvest carries it: its brokerage
    ///   cash nets to zero.</description></item>
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
    /// <param name="register">Reads each candidate's row in the brokerage's
    /// register, which is what the chip's figures are.</param>
    public async Task<IReadOnlyList<InvestmentMergeCandidateDto>> GetMergeCandidatesAsync(
        Guid ledgerId,
        Guid headerId,
        int limit,
        RegisterRepository register,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(register);
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
                account.CurrencyCode,
                rv.IngestAmount,
                rv.IngestShares,
                rv.IngestSecurityId,
                rv.IngestActionHint,
                rv.InvestmentAction,
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

        // ingest_amount keeps the wire's total as sent, so it is read once at the
        // brokerage currency's places, half away from zero, as the ingest write fits a
        // leg and the editor's prefill reads it (ADR-0114 D14, D15): the candidates'
        // legs are at those places. A total under half a minor unit is none.
        var principal = anchor.IngestAmount is { } total
            ? CurrencyMinorUnits.RoundAwayFromZero(Math.Abs(total), anchor.CurrencyCode)
            : 0m;
        if (principal == 0m) principal = Math.Abs(sourceAmount);
        var shares = Math.Abs(anchor.IngestShares ?? 0m);
        // Whether the anchor's own cash direction is a classification's, not just
        // a feed's sign: it carries an action or an ingest action hint.
        var directionKnown = anchor.IngestActionHint is not null
            || anchor.InvestmentAction is not null;
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

        // Settledness, and nothing else — the whole of the bank candidate rule.
        // An accepted, un-merged, effectively-visible row inside the window.
        // Merge-winners ARE valid (folding into a prior winner keeps the graph
        // one-hop), which is why is_merge_winner is not tested here.
        var settledInWindow =
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where rv.HeaderId != headerId
                && !rv.NeedsReview
                && rv.IsMergedInto == null
                && !rv.IsHidden
                && rv.PostedAt >= windowStart
                && rv.PostedAt <= windowEnd
            select rv;

        // (A) Bank's rule, applied to the brokerage: the same effective cash on
        // the same account. Matching on the SECURITY leg's amount instead would
        // silently add a SHAPE constraint to a rule that is supposed to constrain
        // settledness only — a candidate would have to already be
        // investment-shaped, with a security leg on the Holdings sibling.
        //
        // The commonest duplicate on a brokerage has no such leg. A dividend
        // recorded as cash is (brokerage cash, income category); a feed-delivered
        // INVBANKTRAN row is (brokerage cash, category) with no action at all.
        // Neither could be offered, so the merge panel would come up empty on
        // exactly the rows it exists for.
        var byAmount = principal == 0m
            ? new List<Guid>()
            : await settledInWindow
                .Where(rv => rv.AccountId == anchor.SourceAccountId)
                .GroupBy(rv => rv.HeaderId)
                // Sign-insensitive: the anchor's principal is a magnitude, and a
                // buy's cash leg is negative where a dividend's is positive.
                // Written as two equalities rather than Math.Abs so it translates
                // to SQL over the aggregate.
                .Where(g => g.Sum(x => x.Amount) == principal
                         || g.Sum(x => x.Amount) == -principal)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        // (B) The cash-neutral case, which bank has no analogue for: a reinvest
        // moves no cash on the brokerage, so (A) can never see it. Match the
        // share count on the Holdings sibling instead.
        //
        // The view COALESCEs security_id and quantity across a posting's sibling
        // legs, so those two cannot tell a security posting's cash leg from its
        // holdings leg. Pinning account_id to the Holdings sibling can.
        var byShares = shares == 0m
            ? new List<Guid>()
            : await settledInWindow
                .Where(rv => rv.AccountId == holdingsAccountId
                    && rv.PostingRole == PostingRoles.Security
                    && rv.Quantity != null
                    && (rv.Quantity < 0 ? -rv.Quantity : rv.Quantity) == shares)
                .Select(rv => rv.HeaderId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        // (C) A reinvest moves no net cash on the brokerage either: its income leg
        // and its purchase leg cancel, so (A) reads it as zero and never offers it
        // for a cash row of its principal. The principal sits on the holdings leg,
        // so match the figure there, in magnitude as a holdings leg is signed by
        // direction. Only the amount is compared: the direction check below still
        // separates the two sides of a trade, and it passes a zero-cash reinvest.
        var byHoldingsAmount = principal == 0m
            ? new List<Guid>()
            : await settledInWindow
                .Where(rv => rv.AccountId == holdingsAccountId
                    && rv.PostingRole == PostingRoles.Security
                    && (rv.Amount == principal || rv.Amount == -principal))
                .Select(rv => rv.HeaderId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var matched = byAmount.Union(byShares).Union(byHoldingsAmount).ToList();
        if (matched.Count == 0) return Array.Empty<InvestmentMergeCandidateDto>();

        // The security leg, for the candidates that have one: the security the
        // narrowing below compares, and the ticker and price the chip shows.
        var securityLegs = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => matched.Contains(rv.HeaderId)
                && rv.AccountId == holdingsAccountId
                && rv.PostingRole == PostingRoles.Security
                && rv.Quantity != null)
            .Select(rv => new
            {
                rv.HeaderId,
                rv.PostedAt,
                rv.SecurityId,
                rv.SecurityTicker,
                rv.UnitPrice,
                rv.InvestmentAction,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // One per header: a header with two matching holdings legs would
        // otherwise be offered twice and spend two of the caller's limit slots.
        var securityByHeader = securityLegs
            .GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => g.First());

        // The brokerage leg, for every candidate: date and payee come from here,
        // and the cash direction the filter below compares.
        var cashLegs = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => matched.Contains(rv.HeaderId)
                && rv.AccountId == anchor.SourceAccountId)
            .GroupBy(rv => new { rv.HeaderId, rv.PostedAt, rv.Payee, rv.InvestmentAction })
            .Select(g => new
            {
                g.Key.HeaderId,
                g.Key.PostedAt,
                g.Key.Payee,
                g.Key.InvestmentAction,
                Amount = g.Sum(x => x.Amount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var cashByHeader = cashLegs
            .GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => g.First());

        var chosen = matched
            // A candidate whose cash moved the OPPOSITE way is not a duplicate
            // — it is the other side of the trade. A sell that brought in some
            // amount and a buy that spent that same amount on the same security,
            // the same day, are the same magnitude and opposite events, and
            // offering one as the other's twin invites a merge that tombstones
            // a real transaction.
            //
            // An unsigned amount match (Math.Abs on the security leg) accepts
            // that pair. The bank rule compares the SIGNED sum
            // (`g.Sum(...) == targetSourceAmount`); this direction check is what
            // gives the investment side the same parity.
            //
            // Skipped when the anchor moved no cash: a reinvest nets to zero on
            // the brokerage, so it has no direction for a candidate to
            // contradict, and matching there is by share count or by the feed's
            // stated total instead.
            //
            // Also skipped when the anchor has no classification (no action and
            // no ingest action hint). Its sign is then only the feed's claim: an
            // aggregator that cannot tell a fund purchase from a sale reports the
            // cash with its own convention, so a buy can arrive as money IN. The
            // guard would then hide the row's true twin, which is exactly the row
            // the panel exists for. Nothing is lost by offering it: the chip
            // shows the twin's own direction, the merge is the person's pick, and
            // same-direction twins are listed first (see the ordering below).
            .Where(id => !directionKnown
                || sourceAmount == 0m
                || !cashByHeader.TryGetValue(id, out var cash)
                || cash.Amount == 0m
                || Math.Sign(cash.Amount) == Math.Sign(sourceAmount))
            // Narrow by security ONLY where the candidate has one to contradict.
            // A cash-shape candidate carries no security because it has not been
            // classified yet — which is what makes it the duplicate worth
            // offering — so excluding it here would re-close the gap that
            // widening (A) opened.
            .Where(id => anchor.IngestSecurityId is not { } known
                || !securityByHeader.TryGetValue(id, out var leg)
                || leg.SecurityId == known)
            .Select(id =>
            {
                securityByHeader.TryGetValue(id, out var sec);
                cashByHeader.TryGetValue(id, out var cash);
                // Every investment event has a brokerage cash leg, so `cash`
                // is the normal source; fall back rather than assume.
                var postedAt = cash?.PostedAt ?? sec?.PostedAt ?? anchor.PostedAt;
                return new
                {
                    HeaderId = id,
                    PostedAt = postedAt,
                    Action = sec?.InvestmentAction ?? cash?.InvestmentAction,
                    Ticker = sec?.SecurityTicker,
                    sec?.UnitPrice,
                    Payee = cash?.Payee,
                    // Only an unclassified anchor can reach here with a twin that
                    // moved cash the other way; those sort after the rest.
                    Opposes = cash is not null
                        && sourceAmount != 0m
                        && cash.Amount != 0m
                        && Math.Sign(cash.Amount) != Math.Sign(sourceAmount),
                };
            })
            .OrderBy(r => r.Opposes)
            .ThenBy(r => Math.Abs((r.PostedAt - anchor.PostedAt).TotalDays))
            .ThenByDescending(r => r.PostedAt)
            .ThenBy(r => r.HeaderId)
            .Take(limit)
            .ToList();
        if (chosen.Count == 0) return Array.Empty<InvestmentMergeCandidateDto>();

        // The chip reads like the candidate's row in this brokerage's register, so
        // its amount, shares, action and other account are that row's, as the
        // register reads and projects it. The legs above say something else: the
        // holdings leg of a sell is negative where its row is cash coming in, the
        // principal leaves out a fee the row includes, and a cash transfer's Xfr
        // exists only on the row.
        var rows = await register.GetRowsForHeadersAsync(
                chosen.Select(c => c.HeaderId).ToList(), anchor.SourceAccountId, cancellationToken)
            .ConfigureAwait(false);

        // Every candidate has a visible leg on the brokerage (a cash match is made
        // on one, and a security posting pairs one with its holdings leg), so each
        // has a row; one without would have nothing to read like and is not
        // offered.
        return chosen
            .Where(c => rows.TryGetValue(c.HeaderId, out var r) && r is InvestmentRowDto)
            .Select(c =>
            {
                var row = (InvestmentRowDto)rows[c.HeaderId];
                return new InvestmentMergeCandidateDto(
                    c.HeaderId,
                    c.PostedAt,
                    (int)Math.Round((c.PostedAt - anchor.PostedAt).TotalDays),
                    c.Action,
                    c.Ticker,
                    row.Quantity,
                    c.UnitPrice,
                    row.Amount,
                    c.Payee,
                    row.DerivedAction,
                    row.CounterpartyAccountId,
                    row.CounterpartyAccountName,
                    row.Origin);
            })
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
        await AnnotationWriter.CaptureOriginalsAsync(_db, header, cancellationToken)
            .ConfigureAwait(false);
        header.PostedAt = postedAt;
    }

    /// <summary>Outcome of <see cref="ApproveAsync"/>.</summary>
    public enum ApproveOutcome { Ok, HeaderNotFound }

    /// <summary>
    /// Clear <c>needs_review</c> on an investment header, changing nothing else.
    /// </summary>
    /// <remarks>
    /// Idempotent: an already-accepted row is Ok, not an error — accepting twice
    /// is the same request, and a 422 on the second click of a button that
    /// looked enabled is a worse answer than doing nothing.
    ///
    /// <para>No shape gate. Accepting touches one boolean that exists on every
    /// header, so refusing a bank-shape row here would buy nothing and would
    /// make the two registers disagree about a row they can both display.</para>
    /// </remarks>
    public async Task<ApproveOutcome> ApproveAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        // A merged-away header is not found (MergedAway).
        var header = await _db.TxnHeaders.NotMergedAway()
            .FirstOrDefaultAsync(h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null) return ApproveOutcome.HeaderNotFound;

        if (header.NeedsReview)
        {
            header.NeedsReview = false;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return ApproveOutcome.Ok;
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
        Guid HeaderId,
        FailureDetail? Detail = null)
    {
        public static ConvertInKindOutcome Ok(Guid id) => new(ConvertInKindResult.Ok, null, id);
        public static ConvertInKindOutcome Fail(ConvertInKindResult r) => new(r, null, Guid.Empty);
        public static ConvertInKindOutcome Fail(CreateFailure f, FailureDetail? detail = null) =>
            new(ConvertInKindResult.NotAValidPair, f, Guid.Empty, detail);
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
    /// in-kind transfer moves no cash) — the candidate review surfaces that. The
    /// transaction is opened by the ledger write lock (<see cref="LedgerWriteLock"/>,
    /// migration 275), before the pair is read and validated.
    /// </summary>
    public async Task<ConvertInKindOutcome> ConvertInKindTransferAsync(
        Guid ledgerId,
        Guid sellHeaderId,
        Guid buyHeaderId,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275), first: the delete save and the
        // transfer's saves recompute balances and holdings, which the database refuses
        // in a transaction that wrote without it, and the pair read here is the pair
        // deleted below. CreateAsync takes it again inside this transaction, which does
        // nothing.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

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

        // Hard-delete both headers. The DB cascades header → legs → lots
        // (mig 123), and HoldingsRecomputeInterceptor snapshots the doomed
        // legs' (account, security) pairs before the cascade, so both holdings
        // reconcile on this save — restoring the source lots the sell consumed.
        var headers = await _db.TxnHeaders
            .Where(h => h.LedgerId == ledgerId && (h.Id == sellHeaderId || h.Id == buyHeaderId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Read both events' notes before they go: the transfer joins them.
        var sellNotes = await AnnotationWriter.LoadAsync(_db, headers.Single(h => h.Id == sellHeaderId), cancellationToken)
            .ConfigureAwait(false);
        var buyNotes = await AnnotationWriter.LoadAsync(_db, headers.Single(h => h.Id == buyHeaderId), cancellationToken)
            .ConfigureAwait(false);
        // Either can be a merge winner, and deleting it un-merges its losers; tracked
        // first, the save recomputes them (MergeLosers).
        await MergeLosers.TrackAsync(_db, [.. headers.Select(h => h.Id)], cancellationToken).ConfigureAwait(false);
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
            cancellationToken: cancellationToken,
            // The transfer's notes: what each event's register showed, the sell's
            // first, joined, and their tags unioned. A system merge: never capped.
            notesFor: transfer => AnnotationPlanner.ConvertInKind(
                sellNotes.Shape, sellNotes.Notes, buyNotes.Shape, buyNotes.Notes, transfer)).ConfigureAwait(false);
        if (createResult.Failure is { } f)
            return ConvertInKindOutcome.Fail(f, createResult.Detail);   // await using rolls back
        if (createResult.Refusal is not null)
            throw new InvalidOperationException("An in-kind transfer's joined notes were refused.");

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        // Date and visibility must be the ones the detector that OFFERS the pair
        // (FindInKindCandidates, over resolved_transactions) sees. This read decides
        // whether a pair is convertible, and the caller compares the two sides'
        // dates; if the two read different dates they disagree about the same pair
        // in both directions — a pair whose displayed dates match is offered and
        // then rejected as NotAValidPair, and a pair a user deliberately dated apart
        // can still convert. Since mig 230 the header holds the one date, so they
        // cannot drift.
        //
        // The leg columns come off txn_legs on purpose: resolved_transactions
        // COALESCEs security_id and quantity across a posting's sibling legs, so
        // reading them from the view would make the cash leg indistinguishable
        // from the holdings leg this must find.
        var row = await (
            from l in _db.TxnLegs.AsNoTracking()
            // A merged-away header is not found (MergedAway).
            join h in _db.TxnHeaders.AsNoTracking().NotMergedAway() on l.HeaderId equals h.Id
            where l.HeaderId == headerId
                  && l.LedgerId == ledgerId
                  && l.PostingRole == PostingRoles.Security
                  && l.SecurityId != null
                  && l.Quantity != null
                  && h.Action != null && actions.Contains(h.Action)
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
    /// <remarks>
    /// <para><b>Places</b> (ADR-0114 D15). The amount and the fee are typed figures in
    /// the brokerage's currency, so each is checked at its places once the brokerage is
    /// loaded, and one with more places is refused, never rounded. An edit passes the
    /// figures it replaces as <paramref name="stored"/>: a figure resent equal to one of
    /// them passes, because a stored figure that does not fit is kept.</para>
    /// <para><b>The transfer pair</b> (ADR-0114 D13, ADR-0029). A transfer-shape action
    /// whose transfer account is a real account in another currency carries
    /// <c>transferAmount</c> as that account's leg, checked by the one second-amount
    /// rule (<see cref="PostingLegPair.CheckSecondAmount"/>) against the brokerage's leg
    /// of the transfer pair. Every other transfer mirrors, and a
    /// <c>transferAmount</c> sent on it is dropped.</para>
    /// </remarks>
    private async Task<Resolution> ValidateAndResolveAsync(
            Guid ledgerId,
            CreateInvestmentTransactionRequest request,
            bool allowInactiveAccounts,
            IReadOnlySet<Guid>? alreadyPresent,
            StoredFigures? stored,
            CancellationToken cancellationToken)
    {
        var action = request.Action;
        if (!IsCatalogAction(action))
            return Resolution.Fail(CreateFailure.ActionInvalid);

        var (shapeFailure, _) = ValidateActionShape(request, action);
        if (shapeFailure is { } sf) return Resolution.Fail(sf);

        // Account lookups (single round-trip; one query covers brokerage +
        // every referenced counterparty).
        var referenced = CollectReferencedAccountIds(request);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && referenced.Contains(a.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = accounts.ToDictionary(a => a.Id);

        if (!byId.TryGetValue(request.BrokerageAccountId, out var brokerage))
            return Resolution.Fail(CreateFailure.AccountNotInLedger);
        if (brokerage.AccountType != "investment")
            return Resolution.Fail(CreateFailure.AccountNotInvestment);
        if (brokerage.HoldingsAccountId is not { } holdingsAccountId)
            return Resolution.Fail(CreateFailure.AccountMissingHoldingsSibling);

        foreach (var (id, code) in EnumerateCounterpartyIds(request))
            if (!byId.ContainsKey(id))
                return Resolution.Fail(code);

        // Inactive-account gate: a brokerage / counterparty must be active
        // to receive a write. It blocks NEW activity being posted to a closed
        // account. allowInactiveAccounts bypasses it for convert_in_kind_transfer,
        // which re-represents transactions that ALREADY exist on those (often since-
        // closed) accounts — a historical data-hygiene correction, not new activity
        // (the ADR-0085 presentation-vs-correctness distinction: is_active gates the
        // UI, not a legitimate historical write).
        //
        // alreadyPresent is what an edit's legs already sit on, as the bank edits
        // pass theirs: an inactive account left in place is not new activity, one
        // newly chosen is. An edit resends the whole shape, so without it an
        // unchanged account looks exactly like one moved onto, and an event on a
        // deactivated account could not be edited at all. Null (creates and fires)
        // gates every account.
        bool IsNew(Guid id) => alreadyPresent is null || !alreadyPresent.Contains(id);
        if (!allowInactiveAccounts)
        {
            if (!brokerage.IsActive && IsNew(brokerage.Id))
                return Resolution.Fail(CreateFailure.BrokerageInactive);

            // Runs after the "not-in-ledger" pass, so the more specific error wins
            // when both apply.
            foreach (var (id, code) in EnumerateCounterpartyIdsForInactiveCheck(request))
                if (byId.TryGetValue(id, out var account) && !account.IsActive && IsNew(id))
                    return Resolution.Fail(code);
        }

        // Security lookup (per-ledger).
        if (request.SecurityId is { } secId)
        {
            var secExists = await _db.Securities.AsNoTracking()
                .AnyAsync(s => s.Id == secId && s.LedgerId == ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (!secExists) return Resolution.Fail(CreateFailure.SecurityNotInLedger);
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
                return Resolution.Fail(CreateFailure.TransferSharesDestNotInvestment);
            if (dest.HoldingsAccountId is not { } destHoldings)
                return Resolution.Fail(CreateFailure.TransferSharesDestMissingHoldingsSibling);
            if (destHoldings == holdingsAccountId)
                return Resolution.Fail(CreateFailure.TransferSharesToSelf);
            return new Resolution(null, null, holdingsAccountId, [], destHoldings,
                brokerage.CurrencyCode, dest.CurrencyCode);
        }

        // The typed figures, at the brokerage's places (ADR-0114 D15). A figure equal
        // to one the edit replaces passes: stored amounts that do not fit are kept.
        var currency = brokerage.CurrencyCode;
        if (request.Amount is { } typedAmount
            && !CurrencyMinorUnits.Fits(typedAmount, currency)
            && !(stored?.HoldsAmount(request.BrokerageAccountId, typedAmount) ?? false))
            return Resolution.Fail(CreateFailure.AmountPlacesExceedCurrency, FailureDetail.Places(currency, "amount"));
        if (request.FeeAmount is { } typedFee
            && !CurrencyMinorUnits.Fits(typedFee, currency)
            && !(stored?.HoldsFee(request.BrokerageAccountId, typedFee) ?? false))
            return Resolution.Fail(CreateFailure.AmountPlacesExceedCurrency, FailureDetail.Places(currency, "feeAmount"));

        // The transfer account's own leg (ADR-0114 D13, ADR-0029): required only across
        // currencies between real accounts; everywhere else the leg mirrors and a sent
        // transferAmount is dropped.
        decimal? transferLeg = null;
        if (TakesTransferAmount(action)
            && byId[request.TransferAccountId!.Value] is var transferAccount
            && new PostingLegPair.AccountSide(transferAccount.AccountType, transferAccount.CurrencyCode).IsReal
            && !string.Equals(transferAccount.CurrencyCode, currency, StringComparison.Ordinal))
        {
            if (request.TransferAmount is not { } sent)
                return Resolution.Fail(CreateFailure.TransferAmountRequired);
            transferLeg = sent;

            // At the transfer account's own places (ADR-0114 D15), like every typed figure;
            // one resent equal to the stored leg on the same account is unchanged and passes.
            if (!CurrencyMinorUnits.Fits(sent, transferAccount.CurrencyCode)
                && !(stored?.TransferPair is { } storedPair
                     && storedPair.Second.AccountId == transferAccount.Id
                     && storedPair.Second.Amount == sent))
                return Resolution.Fail(CreateFailure.AmountPlacesExceedCurrency,
                    FailureDetail.Places(transferAccount.CurrencyCode, "transferAmount"));
        }

        var totalCommission = request.FeeAmount ?? 0m;
        var postings = BuildPostings(request, action, holdingsAccountId, totalCommission, currency, transferLeg);

        if (transferLeg is not null)
        {
            var pair = postings.Single(p => p.Cash.PostingRole == PostingRoles.Transfer);
            var fault = PostingLegPair.CheckSecondAmount(
                new PostingLegPair.Leg(pair.Cash.AccountId, pair.Cash.Amount),
                new PostingLegPair.Leg(pair.Counterparty.AccountId, pair.Counterparty.Amount),
                stored?.TransferPair);
            if (fault != PostingLegPair.SecondAmountFault.None)
                return Resolution.Fail(fault == PostingLegPair.SecondAmountFault.Zero
                    ? CreateFailure.TransferAmountsNonzero
                    : CreateFailure.TransferAmountsSign);
        }

        return new Resolution(null, null, holdingsAccountId, postings, null, currency, null);
    }

    /// <summary>
    /// What <see cref="ValidateAndResolveAsync"/> resolved: the first failure and what
    /// it names, or the brokerage's holdings sibling, the postings, the destination's
    /// holdings sibling for an in-kind move, and the two brokerages' currencies.
    /// </summary>
    private readonly record struct Resolution(
        CreateFailure? Failure,
        FailureDetail? Detail,
        Guid HoldingsAccountId,
        IReadOnlyList<InvestmentPosting> Postings,
        Guid? DestHoldingsAccountId,
        string BrokerageCurrency,
        string? DestCurrency)
    {
        public static Resolution Fail(CreateFailure failure, FailureDetail? detail = null) =>
            new(failure, detail, Guid.Empty, [], null, string.Empty, null);
    }

    /// <summary>
    /// The figures a PATCH would replace, read from the stored legs before the rebuild:
    /// the amounts and fees on the edited brokerage, compared by magnitude since each
    /// action signs them its own way, and the stored transfer pair. A figure resent
    /// equal to one of them is unchanged, so it passes the places rule (ADR-0114 D15),
    /// and a transfer pair resent equal to its two stored legs passes the nonzero and
    /// sign rules (ADR-0114 D13).
    /// </summary>
    /// <remarks>Only legs on the request's brokerage count: an edit that moves the event
    /// to another brokerage changes every figure's currency, so each is checked as
    /// typed.</remarks>
    private sealed record StoredFigures(
        Guid BrokerageAccountId,
        IReadOnlySet<decimal> Amounts,
        IReadOnlySet<decimal> Fees,
        PostingLegPair.StoredPair? TransferPair)
    {
        public static StoredFigures Of(IReadOnlyList<TxnLegRow> legs, Guid brokerageAccountId)
        {
            var onBrokerage = legs.Where(l => l.AccountId == brokerageAccountId).ToList();
            var transferLeg = onBrokerage.FirstOrDefault(l => l.PostingRole == PostingRoles.Transfer);
            var transferPair = transferLeg is not null
                               && PostingLegPair.StoredPairs(legs, brokerageAccountId)
                                   .TryGetValue(transferLeg.Id, out var pair)
                ? pair
                : (PostingLegPair.StoredPair?)null;
            return new StoredFigures(
                brokerageAccountId,
                onBrokerage.Where(l => l.PostingRole != PostingRoles.Fee).Select(l => Math.Abs(l.Amount)).ToHashSet(),
                onBrokerage.Where(l => l.PostingRole == PostingRoles.Fee).Select(l => Math.Abs(l.Amount)).ToHashSet(),
                transferPair);
        }

        public bool HoldsAmount(Guid brokerageAccountId, decimal value) =>
            brokerageAccountId == BrokerageAccountId && Amounts.Contains(Math.Abs(value));

        public bool HoldsFee(Guid brokerageAccountId, decimal value) =>
            brokerageAccountId == BrokerageAccountId && Fees.Contains(Math.Abs(value));
    }

    /// <summary>The transfer-shape actions, whose transfer pair carries
    /// <c>transferAmount</c> across currencies (ADR-0114 D13, ADR-0029): on buyx, sellx
    /// and divx it pairs with the trade amount, never the fee.</summary>
    private static bool TakesTransferAmount(string action) => action is
        LedgerActions.Transfer or LedgerActions.BuyXfr or LedgerActions.SellXfr or LedgerActions.DivXfr;

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
    /// Inactive-account gate per role. Same shape as
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
    /// <param name="currencyCode">The brokerage's currency, whose places the
    /// price-times-shares fallback rounds at (ADR-0114 D15).</param>
    /// <param name="transferLeg">The transfer account's own leg when the transfer pair
    /// is between currencies (ADR-0114 D13); null mirrors the brokerage's leg.</param>
    private static IReadOnlyList<InvestmentPosting> BuildPostings(
        CreateInvestmentTransactionRequest r,
        string action,
        Guid holdingsAccountId,
        decimal totalCommission,
        string currencyCode,
        decimal? transferLeg)
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
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty, currencyCode);
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
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty, currencyCode);
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
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty, currencyCode);
                var sign = action == LedgerActions.BuyXfr ? -1m : 1m;
                list.Add(InvestmentPostings.BuildSecPair(
                    brokerageId, holdingsAccountId, securityId!.Value,
                    cashAmount: sign * principal, holdingsAmount: -sign * principal,
                    quantity: qty, unitPrice: unitPrice));
                // Xfr pair offsets brokerage cash (cash nets to zero on the brokerage).
                // Across currencies the other leg is its own figure, which pairs with
                // the trade amount, not the fee (ADR-0114 D13).
                list.Add(InvestmentPostings.BuildXferPair(
                    brokerageAccountId: brokerageId,
                    otherAccountId: r.TransferAccountId!.Value,
                    brokerageAmount: -sign * principal,
                    otherAmount: transferLeg ?? sign * principal));
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
                var (principal, unitPrice) = ResolveTradeMoney(r.Amount, r.Price!.Value, qty, currencyCode);
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
                    otherAmount: transferLeg ?? amount));
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
                    otherAmount: transferLeg ?? -amount));
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
    /// authoritative — it is the actual settled cash, which for an imported
    /// trade is the wire total, not a re-derivation from a rounded per-share
    /// price (ADR-0073). A typed amount is used as sent: the validation has
    /// already refused one finer than the brokerage's currency (ADR-0114
    /// D15), and one resent equal to a stored figure is kept as stored. The
    /// per-share <c>unit_price</c> is DERIVED metadata (<c>amount ÷
    /// |shares|</c>) rounded to 6 decimals — the register's max display
    /// precision (<c>formatPrice</c>) — so what is stored is exactly what
    /// is shown (never more digits in the DB than on screen), and no
    /// money finer than the currency ever lands on a leg (which is what
    /// produced fractional amounts + negative-zero balances).
    ///
    /// Falls back to price × |shares| only when the request omits an
    /// amount — a defensive path for callers predating the
    /// amount-authoritative model (e.g. a fixture that sets shares+price
    /// but no amount). That figure is computed, so it is rounded once at
    /// the brokerage's places, half away from zero (ADR-0114 D15).
    /// </summary>
    private static (decimal Principal, decimal UnitPrice) ResolveTradeMoney(
        decimal? requestAmount, decimal price, decimal quantity, string currencyCode)
    {
        var absQty = Math.Abs(quantity);
        var principal = requestAmount
                        ?? CurrencyMinorUnits.RoundAwayFromZero(price * absQty, currencyCode);
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
    /// re-creates a lot with its quantity and acquisition date so holding period
    /// + basis carry across the move (ADR-0065 D2), at the basis
    /// <see cref="PriceMovedLotsAsync"/> sets.
    /// </summary>
    private readonly record struct MovedLot(decimal Quantity, decimal UnitCost, DateTime AcquiredAt)
    {
        /// <summary>The lot's basis leaving the source, at the source brokerage's
        /// places.</summary>
        public decimal SourceCost { get; init; }

        /// <summary>The lot's basis arriving at the destination, in the destination
        /// brokerage's currency, at its places: the source basis itself between two
        /// brokerages in one currency, converted otherwise (ADR-0114 D13).</summary>
        public decimal DestCost { get; init; }

        /// <summary>The destination lot's per-share cost: the inherited
        /// <see cref="UnitCost"/> in one currency, <see cref="DestCost"/> over the
        /// quantity across currencies.</summary>
        public decimal DestUnitCost { get; init; }
    }

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
    /// Price each moved lot's basis on both sides of an in-kind move. The source
    /// basis is the lot's quantity times its unit cost, rounded once at the source
    /// brokerage's places (ADR-0114 D15). Between two brokerages in one currency the
    /// destination carries that basis and the lot's unit cost unchanged, and no rate
    /// is read. Across currencies each lot converts at the derived rate for its own
    /// acquisition day (ADR-0114 D13), which an earlier move keeps: computed at full
    /// precision from the unrounded basis and rounded once at the destination's
    /// places, with the destination unit cost that figure over the quantity.
    /// </summary>
    /// <returns>Every lot priced, or, when any lot's day has no derived rate, the
    /// missing stored legs (account currency into the reporting currency) and days,
    /// each once, and no lot: the whole move is refused.</returns>
    private async Task<(FailureDetail? Missing, IReadOnlyList<MovedLot> Priced)> PriceMovedLotsAsync(
        Guid ledgerId,
        IReadOnlyList<MovedLot> plan,
        string sourceCurrency,
        string destCurrency,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sourceCurrency, destCurrency, StringComparison.Ordinal))
        {
            return (null, [.. plan.Select(lot =>
            {
                var cost = CurrencyMinorUnits.RoundAwayFromZero(lot.Quantity * lot.UnitCost, sourceCurrency);
                return lot with { SourceCost = cost, DestCost = cost, DestUnitCost = lot.UnitCost };
            })]);
        }

        var reporting = await _db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var lot in plan)
            needs.AddDerived(sourceCurrency, destCurrency, reporting, AcquiredDay(lot));
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var priced = new List<MovedLot>(plan.Count);
        var missing = new List<RateUnavailable>();
        foreach (var lot in plan)
        {
            var basis = lot.Quantity * lot.UnitCost;
            var rate = rates.Derived(sourceCurrency, destCurrency, reporting, AcquiredDay(lot));
            if (rate.Convert(basis) is not { } converted)
            {
                foreach (var leg in rate.Unavailable)
                    if (!missing.Contains(leg)) missing.Add(leg);
                continue;
            }
            priced.Add(lot with
            {
                SourceCost = CurrencyMinorUnits.RoundAwayFromZero(basis, sourceCurrency),
                DestCost = converted,
                DestUnitCost = converted / lot.Quantity,
            });
        }
        return missing.Count > 0
            ? (new FailureDetail(MissingRates: missing), [])
            : (null, priced);

        static DateOnly AcquiredDay(MovedLot lot) => DateOnly.FromDateTime(lot.AcquiredAt);
    }

    /// <summary>
    /// Build the in-kind transfer postings: per moved lot, TWO sec postings —
    /// a source one (zero brokerage cash ↔ source holdings −lot) and a destination
    /// one (zero brokerage cash ↔ destination holdings +lot). The zero-cash sec
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
            // Leg money is a computed figure, rounded once at its brokerage's places
            // (ADR-0114 D15) by PriceMovedLotsAsync. quantity × unit_cost (unit_cost
            // stored at NUMERIC(25,12), mig 180) generally does not fit the currency
            // when the lot's basis didn't divide evenly by its quantity (e.g. a
            // whole-unit basis over three shares gives a repeating per-share cost, and
            // that cost times three falls just short of the basis), so it is rounded
            // there, as the price-times-shares fallback is (see ResolveTradeMoney). At
            // 12dp the rounded basis is exact to the minor unit vs the source lot; the
            // pre-mig-180 (19,4) unit_cost drifted by several whole units of currency
            // on a large in-kind move.
            // Source: zero brokerage cash (in-kind) ↔ source holdings −lot.
            list.Add(InvestmentPostings.BuildSecPair(
                sourceBrokerageId, sourceHoldingsAccountId, securityId,
                cashAmount: 0m, holdingsAmount: -lot.SourceCost,
                quantity: -lot.Quantity, unitPrice: lot.UnitCost));
            // Destination: zero brokerage cash (in-kind) ↔ dest holdings +lot, in the
            // destination's currency, its unit price the destination lot's unit cost.
            list.Add(InvestmentPostings.BuildSecPair(
                destBrokerageId, destHoldingsAccountId, securityId,
                cashAmount: 0m, holdingsAmount: lot.DestCost,
                quantity: lot.Quantity, unitPrice: lot.DestUnitCost));
        }
        return list;
    }

    /// <summary>
    /// Create the destination lot rows for an in-kind transfer — one per moved
    /// lot, each bound to its destination leg, carrying the inherited
    /// <see cref="MovedLot.AcquiredAt"/> + its <see cref="MovedLot.DestUnitCost"/> so the
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
                UnitCost = plan[i].DestUnitCost,
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
        // No memo: the write primitive places the event's one memo.
        PostingRole = spec.Leg.PostingRole,
    };

    /// <summary>
    /// Plan an edited event's notes against its stored ones: the rebuilt event,
    /// owned by the request's brokerage, its legs matched to the stored ones by
    /// (account, role).
    /// </summary>
    private static AnnotationPlan<Guid> PlanEventNotes(
        AnnotationWriter.Stored stored, CreateInvestmentTransactionRequest asCreate,
        IReadOnlyList<InvestmentPosting> postings, AnnotationIntent<Guid> intent)
    {
        var after = AnnotationWriter.Shape(asCreate.BrokerageAccountId, asCreate.Action, PlannedLegs(postings), stored.Facts);
        var change = new ShapeChange<Guid>(stored.Shape, stored.Notes, after,
            ShapeChange<Guid>.ByAccountAndRole(stored.Shape, after));
        return AnnotationPlanner.Plan(change, intent);
    }

    /// <summary>The planner's legs of an event built from <paramref name="postings"/>.</summary>
    private static IEnumerable<AnnotationWriter.PlannedLeg> PlannedLegs(IReadOnlyList<InvestmentPosting> postings) =>
        postings.SelectMany((p, i) => new[]
        {
            new AnnotationWriter.PlannedLeg(i, p.Cash.AccountId, null, p.Cash.PostingRole),
            new AnnotationWriter.PlannedLeg(i, p.Counterparty.AccountId, null, p.Counterparty.PostingRole),
        });

    /// <summary>An investment body's notes as the planner reads them, written from its brokerage.</summary>
    internal static AnnotationIntent<string> NotesIntent(Guid brokerageAccountId, IInvestmentWriteNotes notes) =>
        new(brokerageAccountId, InvestmentNotesEdits.Memo(notes.HasMemo, notes.Memo), InvestmentNotesEdits.Tags(notes.Tags));

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
    /// Returns open lots for the given user-visible brokerage and security,
    /// ordered ascending by <c>acquired_at</c> (FIFO consumption order). Drives
    /// the editor's Sell / SellX preview popover.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="brokerageAccountId"/> is the user-visible
    /// brokerage; the repository resolves the Holdings sibling via
    /// <c>accounts.holdings_account_id</c>.</para>
    ///
    /// <para>WITH <paramref name="asOf"/>, the answer is the position at that
    /// instant, from the FIFO walk (migration 256), optionally without
    /// <paramref name="excludeHeaderId"/> — what was available to a sale being
    /// edited. WITHOUT it, today's <c>lots</c> table, as before. The table is
    /// drained in place, so it can only ever answer "now": asked about a 2014
    /// sale on a since-closed account it said "no open lots", and the preview
    /// warned on every historical edit. The editor always sends the sale's
    /// date.</para>
    /// </remarks>
    public async Task<LotsLookupResult> GetOpenLotsAsync(
        Guid ledgerId,
        Guid brokerageAccountId,
        Guid securityId,
        DateTime? asOf = null,
        Guid? excludeHeaderId = null,
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

        if (asOf is { } instant)
        {
            // Keyed by the acquiring leg: a derived lot has no lots-row id, and
            // the caller only uses it to tell lots apart.
            var walked = await _db.HoldingsFifoOpenLots(
                    holdingsAccountId, securityId,
                    DateTime.SpecifyKind(instant.ToUniversalTime(), DateTimeKind.Utc),
                    excludeHeaderId)
                // Re-stated here: an ORDER BY inside a function is not a promise
                // the outer query keeps, and the preview's FIFO walk is
                // order-dependent. Same tie-break the walk itself uses.
                .OrderBy(l => l.AcquiredAt)
                .ThenBy(l => l.LegId)
                .Select(l => new InvestmentLotDto(l.LegId, l.AcquiredAt, l.Quantity, l.UnitCost))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return LotsLookupResult.Ok(walked);
        }

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
