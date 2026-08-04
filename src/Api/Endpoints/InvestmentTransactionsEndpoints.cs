using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Endpoint group for investment-shape transaction lifecycle
/// (ADR-0029). Distinct from <see cref="TransactionsEndpoints"/>
/// (bank-shape) — each endpoint owns its shape; mixing them is
/// structurally wrong and rejected with a typed 422
/// (cross-topic protection).
/// </summary>
public static class InvestmentTransactionsEndpoints
{
    /// <summary>Max "possible matches" returned for the merge panel — same
    /// cap as the bank side.</summary>
    private const int MergeCandidatesLimit = 5;

    public static IEndpointRouteBuilder MapInvestmentTransactionsEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/ledgers/{ledgerId:guid}/investment-transactions")
            .RequireAuthorization()
            .RequireLedgerAccess();

        group.MapPost("/", CreateAsync);
        group.MapPatch("/{headerId:guid}", PatchAsync);
        group.MapDelete("/{headerId:guid}", DeleteAsync);
        // "Possible matches" for the editor's merge panel (mirrors the bank
        // /transactions/{id}/merge-candidates route).
        group.MapGet("/{headerId:guid}/merge-candidates", MergeCandidatesAsync);

        // In-kind transfer scrub (ADR-0065 D4): convert a detected sell+buy pair
        // into a single transfer_shares. Detection is the read-only MCP tool
        // find_in_kind_transfer_candidates; this is the explicit per-pair apply.
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/in-kind-transfers")
            .RequireAuthorization()
            .RequireLedgerAccess()
            .MapPost("/convert", ConvertInKindAsync);

        // FIFO lot preview for the editor's Sell / SellX popover.
        // Lives under /accounts/.../securities/.../lots — separate
        // route group from the main /investment-transactions URL.
        var lotsGroup = routes
            .MapGroup("/api/ledgers/{ledgerId:guid}/accounts/{accountId:guid}/securities/{securityId:guid}/lots")
            .RequireAuthorization()
            .RequireLedgerAccess();
        lotsGroup.MapGet("/", GetOpenLotsAsync);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/investment-transactions</c>.
    /// Validates the action × field matrix in ADR-0029, builds the
    /// multi-posting shape via <c>Coffer.Domain.Investment</c>'s
    /// builders, and inserts header + legs + holdings + lot in one
    /// Postgres transaction. Triggers
    /// <c>recompute_holdings_cost_basis</c> after commit.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateInvestmentTransactionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        ProviderSecurityMappingsRepository providerSecurityMappings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await investmentTxns.CreateAsync(
            ledgerId, request, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            var (code, message) = MapFailure(failure);
            return BusinessError.Problem(code, message);
        }

        // ADR-0031 Phase 3d.1: if the request carried a provider hint
        // alongside the resolved SecurityId, persist the mapping so
        // future syncs of the same ticker auto-resolve. Side-effect
        // only — no validation impact on the create result.
        if (request.ProviderSecurityHint is { } hint
            && request.SecurityId is { } sid)
        {
            await providerSecurityMappings.UpsertAsync(
                ledgerId, hint.ProviderKey, hint.ProviderSecurityId,
                sid, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);
        }

        return Results.Created(
            $"/api/ledgers/{ledgerId}/investment-transactions/{result.HeaderId}",
            new CreateInvestmentTransactionResponse(result.HeaderId));
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/investment-transactions/{headerId}</c>.
    /// Full postings-reshape per ADR-0025: the supplied body IS the
    /// new state of the world (no field-by-field merge with the
    /// existing row). Same validation as POST against ADR-0029's
    /// action × field matrix.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        Guid? account_id,
        PatchInvestmentTransactionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        RegisterRepository register,
        ProviderSecurityMappingsRepository providerSecurityMappings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await investmentTxns.PatchAsync(
            ledgerId, headerId, request, cancellationToken).ConfigureAwait(false);

        if (result.PatchFail is { } pf)
        {
            var (code, message) = MapPatchFailure(pf);
            return BusinessError.Problem(code, message);
        }

        // ADR-0031 Phase 3d.1: same provider-mapping upsert as on POST.
        // Recorded after a successful PATCH so a failed reshape (422)
        // doesn't pollute the mapping table with a half-resolution.
        if (request.ProviderSecurityHint is { } hint
            && request.SecurityId is { } sid)
        {
            await providerSecurityMappings.UpsertAsync(
                ledgerId, hint.ProviderKey, hint.ProviderSecurityId,
                sid, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (result.CreateFail is { } cf)
        {
            var (code, message) = MapFailure(cf);
            return BusinessError.Problem(code, message);
        }

        // PATCH succeeded. When the caller supplies an account_id
        // query param (the brokerage register the user is editing
        // in), return the freshly-resolved entry so the SPA can
        // patch it into the window via `mutateEntries` —
        // preserving scroll position + chronological row order vs.
        // a window refresh. Without the query param, default to
        // 204 No Content (the existing contract).
        if (account_id is { } rid)
        {
            // On a merge the SURVIVING row is the winner (MergeFromHeaderId);
            // the edited row (headerId) has folded into it. Re-resolve the
            // survivor so the SPA refocuses onto the row that's still there
            // (mirrors the bank endpoint).
            var survivingHeaderId = request.MergeFromHeaderId ?? headerId;
            var entry = await register.GetEntryForHeaderAsync(
                survivingHeaderId, rid, cancellationToken).ConfigureAwait(false);
            if (entry is not null) return Results.Ok(entry);
        }
        return Results.NoContent();
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/investment-transactions/{headerId}/merge-candidates</c>.
    /// Settled investment rows the edited (fresh, needs_review) row could fold
    /// into — same brokerage + security, matching principal (or quantity),
    /// within ±7 effective days. Probe-safe: an unknown / non-eligible headerId
    /// returns an empty list, not 404.
    /// </summary>
    private static async Task<IResult> MergeCandidatesAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var candidates = await investmentTxns.GetMergeCandidatesAsync(
            ledgerId, headerId, MergeCandidatesLimit, cancellationToken).ConfigureAwait(false);
        return Results.Ok(candidates);
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/investment-transactions/{headerId}</c>.
    /// Hard-deletes manual rows; soft-hides rows with an
    /// <c>external_id</c> (load-bearing for the queued SimpleFIN
    /// brokerage feed — see ADR-0029). Triggers recompute after
    /// the row state change.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await investmentTxns.DeleteAsync(
            ledgerId, headerId, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            InvestmentTransactionsRepository.DeleteOutcome.HeaderNotFound =>
                BusinessError.Problem(BusinessError.Codes.TransactionNotInLedger,
                    "Header not found in this ledger."),
            InvestmentTransactionsRepository.DeleteOutcome.HeaderNotInvestment =>
                BusinessError.Problem(BusinessError.Codes.InvestmentTxnHeaderNotInvestment,
                    "Use /transactions to delete bank-shape headers; this endpoint is investment-only."),
            InvestmentTransactionsRepository.DeleteOutcome.HardDeleted =>
                Results.Ok(new DeleteTransactionResponse("hard-deleted")),
            InvestmentTransactionsRepository.DeleteOutcome.SoftHidden =>
                Results.Ok(new DeleteTransactionResponse("soft-hidden")),
            _ => Results.StatusCode(500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/in-kind-transfers/convert</c> (ADR-0065 D4).
    /// Replace a detected sell+buy pair (really an in-kind transfer) with a single
    /// <c>transfer_shares</c> — zero realized gain, original cost basis carried.
    /// Atomic (deletes both, creates one). The pair comes from the read-only
    /// <c>find_in_kind_transfer_candidates</c> MCP detection; the user reviews each
    /// against a brokerage statement before calling this.
    /// </summary>
    private static async Task<IResult> ConvertInKindAsync(
        Guid ledgerId,
        ConvertInKindTransferRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await investmentTxns.ConvertInKindTransferAsync(
            ledgerId, request.SellHeaderId, request.BuyHeaderId, cancellationToken).ConfigureAwait(false);

        if (outcome.CreateFail is { } cf)
        {
            var (code, message) = MapFailure(cf);
            return BusinessError.Problem(code, message);
        }
        return outcome.Result switch
        {
            InvestmentTransactionsRepository.ConvertInKindResult.Ok =>
                Results.Created(
                    $"/api/ledgers/{ledgerId}/investment-transactions/{outcome.HeaderId}",
                    new CreateInvestmentTransactionResponse(outcome.HeaderId)),
            InvestmentTransactionsRepository.ConvertInKindResult.SellNotFound =>
                BusinessError.Problem(BusinessError.Codes.InKindTransferSellNotFound,
                    "sellHeaderId is not a live sell/sellx investment header in this ledger."),
            InvestmentTransactionsRepository.ConvertInKindResult.BuyNotFound =>
                BusinessError.Problem(BusinessError.Codes.InKindTransferBuyNotFound,
                    "buyHeaderId is not a live buy/buyx investment header in this ledger."),
            InvestmentTransactionsRepository.ConvertInKindResult.NotAValidPair =>
                BusinessError.Problem(BusinessError.Codes.InKindTransferNotAValidPair,
                    "The two headers are not a valid in-kind pair (need the same security, same date, equal quantity, and two distinct investment accounts)."),
            _ => Results.Problem("Unknown convert-in-kind result.", statusCode: 500),
        };
    }

    private static (string Code, string Message) MapPatchFailure(
        InvestmentTransactionsRepository.PatchFailure failure) => failure switch
    {
        InvestmentTransactionsRepository.PatchFailure.HeaderNotFound =>
            (BusinessError.Codes.TransactionNotInLedger,
             "Header not found in this ledger."),
        InvestmentTransactionsRepository.PatchFailure.HeaderNotInvestment =>
            (BusinessError.Codes.InvestmentTxnHeaderNotInvestment,
             "Use /transactions to edit bank-shape headers; this endpoint is investment-only."),
        InvestmentTransactionsRepository.PatchFailure.MergeSourceInvalid =>
            (BusinessError.Codes.MergeSourceInvalid,
             "The row you're merging is no longer a fresh review row, or the chosen match isn't a settled, visible transaction."),
        _ => (BusinessError.Codes.TransactionNotInLedger, "Patch failed."),
    };

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}/securities/{securityId}/lots</c>.
    /// Returns open lots ordered ascending by <c>acquired_at</c> —
    /// the order FIFO consumption walks. Used by the editor's Sell /
    /// SellX preview popover (ADR-0029).
    /// </summary>
    /// <remarks>
    /// <paramref name="accountId"/> is the user-visible brokerage's
    /// id; the repository resolves the Holdings sibling internally
    /// via the brokerage's <c>holdings_account_id</c>.
    /// </remarks>
    private static async Task<IResult> GetOpenLotsAsync(
        Guid ledgerId,
        Guid accountId,
        Guid securityId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        InvestmentTransactionsRepository investmentTxns,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await investmentTxns
            .GetOpenLotsAsync(ledgerId, accountId, securityId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            var (code, message) = MapLotsFailure(failure);
            return BusinessError.Problem(code, message);
        }

        return Results.Ok(result.Lots);
    }

    private static (string Code, string Message) MapLotsFailure(
        InvestmentTransactionsRepository.LotsLookupFailure failure) => failure switch
    {
        InvestmentTransactionsRepository.LotsLookupFailure.AccountNotInLedger =>
            (BusinessError.Codes.AccountNotInLedger,
             "Account does not belong to this ledger."),
        InvestmentTransactionsRepository.LotsLookupFailure.AccountNotInvestment =>
            (BusinessError.Codes.InvestmentTxnAccountNotInvestment,
             "Lots are only available on investment accounts."),
        InvestmentTransactionsRepository.LotsLookupFailure.AccountMissingHoldingsSibling =>
            (BusinessError.Codes.AccountMissingHoldingsSibling,
             "Investment account is missing its Holdings sibling."),
        InvestmentTransactionsRepository.LotsLookupFailure.SecurityNotInLedger =>
            (BusinessError.Codes.InvestmentTxnSecurityNotInLedger,
             "Security does not belong to this ledger."),
        _ => (BusinessError.Codes.AccountNotInLedger, "Lookup failed."),
    };

    /// <summary>
    /// Map a repository <c>CreateFailure</c> to its stable 422
    /// code + a human-readable message. Centralized so every
    /// failure path uses the same code vocabulary — shared with the
    /// reminders investment-template endpoint (ADR-0047), which reuses
    /// the same validation core (<c>InvestmentTransactionsRepository
    /// .BuildTemplateLegsAsync</c>).
    /// </summary>
    internal static (string Code, string Message) MapFailure(
        InvestmentTransactionsRepository.CreateFailure failure) => failure switch
    {
        InvestmentTransactionsRepository.CreateFailure.ActionInvalid =>
            (BusinessError.Codes.InvestmentTxnActionInvalid,
             "action must be a catalog value (ADR-0027 + transfer_shares per ADR-0065)."),
        InvestmentTransactionsRepository.CreateFailure.AccountNotInLedger =>
            (BusinessError.Codes.AccountNotInLedger,
             "Account does not belong to this ledger."),
        InvestmentTransactionsRepository.CreateFailure.AccountNotInvestment =>
            (BusinessError.Codes.InvestmentTxnAccountNotInvestment,
             "brokerageAccountId must reference an account of type 'investment'."),
        InvestmentTransactionsRepository.CreateFailure.AccountMissingHoldingsSibling =>
            (BusinessError.Codes.AccountMissingHoldingsSibling,
             "Investment account is missing its Holdings sibling."),
        InvestmentTransactionsRepository.CreateFailure.SecurityRequired =>
            (BusinessError.Codes.InvestmentTxnSecurityRequired,
             "securityId is required for this action."),
        InvestmentTransactionsRepository.CreateFailure.SecurityNotInLedger =>
            (BusinessError.Codes.InvestmentTxnSecurityNotInLedger,
             "Security does not belong to this ledger."),
        InvestmentTransactionsRepository.CreateFailure.SharesRequired =>
            (BusinessError.Codes.InvestmentTxnSharesRequired,
             "shares is required for buy / buyx / sell / sellx / dividend_reinvest."),
        InvestmentTransactionsRepository.CreateFailure.SharesNonZero =>
            (BusinessError.Codes.InvestmentTxnSharesNonZero,
             "shares must be non-zero."),
        InvestmentTransactionsRepository.CreateFailure.PriceRequired =>
            (BusinessError.Codes.InvestmentTxnPriceRequired,
             "price is required for buy / buyx / sell / sellx / dividend_reinvest."),
        InvestmentTransactionsRepository.CreateFailure.PricePositive =>
            (BusinessError.Codes.InvestmentTxnPricePositive,
             "price must be positive."),
        InvestmentTransactionsRepository.CreateFailure.AmountRequired =>
            (BusinessError.Codes.InvestmentTxnAmountRequired,
             "amount is required for dividend_cash / transfer / misc."),
        InvestmentTransactionsRepository.CreateFailure.CategoryRequired =>
            (BusinessError.Codes.InvestmentTxnCategoryRequired,
             "categoryAccountId is required for dividend_cash / dividend_reinvest / divx / misc."),
        InvestmentTransactionsRepository.CreateFailure.CategoryNotInLedger =>
            (BusinessError.Codes.AccountNotInLedger,
             "categoryAccountId does not belong to this ledger."),
        InvestmentTransactionsRepository.CreateFailure.TransferRequired =>
            (BusinessError.Codes.InvestmentTxnTransferRequired,
             "transferAccountId is required for buyx / sellx / divx / transfer."),
        InvestmentTransactionsRepository.CreateFailure.TransferNotInLedger =>
            (BusinessError.Codes.AccountNotInLedger,
             "transferAccountId does not belong to this ledger."),
        InvestmentTransactionsRepository.CreateFailure.FeeAmountRequired =>
            (BusinessError.Codes.InvestmentTxnFeeAmountRequired,
             "feeAmount is required when feeAccountId is set."),
        InvestmentTransactionsRepository.CreateFailure.FeeAmountPositive =>
            (BusinessError.Codes.InvestmentTxnFeeAmountPositive,
             "feeAmount must be positive."),
        InvestmentTransactionsRepository.CreateFailure.FeeWithoutAccount =>
            (BusinessError.Codes.InvestmentTxnFeeWithoutAccount,
             "feeAmount cannot be set without feeAccountId, and fees are not allowed on the transfer action."),
        InvestmentTransactionsRepository.CreateFailure.FeeAccountNotInLedger =>
            (BusinessError.Codes.AccountNotInLedger,
             "feeAccountId does not belong to this ledger."),
        // PR #132 inactive-account gate (per role).
        InvestmentTransactionsRepository.CreateFailure.BrokerageInactive =>
            (BusinessError.Codes.InvestmentTxnBrokerageInactive,
             "brokerageAccountId is inactive; reactivate the brokerage before posting new transactions to it."),
        InvestmentTransactionsRepository.CreateFailure.CategoryInactive =>
            (BusinessError.Codes.InvestmentTxnCategoryInactive,
             "categoryAccountId is inactive; reactivate the category before assigning it."),
        InvestmentTransactionsRepository.CreateFailure.TransferInactive =>
            (BusinessError.Codes.InvestmentTxnTransferInactive,
             "transferAccountId is inactive; reactivate the destination account before transferring to it."),
        InvestmentTransactionsRepository.CreateFailure.FeeAccountInactive =>
            (BusinessError.Codes.InvestmentTxnFeeAccountInactive,
             "feeAccountId is inactive; reactivate the fee category before assigning it."),
        // transfer_shares (in-kind, ADR-0065).
        InvestmentTransactionsRepository.CreateFailure.TransferSharesQtyPositive =>
            (BusinessError.Codes.InvestmentTxnTransferSharesQtyPositive,
             "shares must be a positive quantity to move for transfer_shares."),
        InvestmentTransactionsRepository.CreateFailure.TransferSharesToSelf =>
            (BusinessError.Codes.InvestmentTxnTransferSharesToSelf,
             "transferAccountId must be a different account than the source brokerage."),
        InvestmentTransactionsRepository.CreateFailure.TransferSharesDestNotInvestment =>
            (BusinessError.Codes.InvestmentTxnTransferSharesDestNotInvestment,
             "transferAccountId must reference an account of type 'investment' for transfer_shares."),
        InvestmentTransactionsRepository.CreateFailure.TransferSharesDestMissingHoldingsSibling =>
            (BusinessError.Codes.InvestmentTxnTransferSharesDestMissingHoldingsSibling,
             "The destination investment account is missing its Holdings sibling."),
        InvestmentTransactionsRepository.CreateFailure.TransferSharesInsufficientShares =>
            (BusinessError.Codes.InvestmentTxnTransferSharesInsufficient,
             "The source account does not hold that many shares to transfer."),
        _ => (BusinessError.Codes.InvestmentTxnActionInvalid,
              "Invalid investment transaction request."),
    };
}
