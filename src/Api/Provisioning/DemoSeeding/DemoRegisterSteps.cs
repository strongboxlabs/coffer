using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The examples step that leaves the register as a ledger in use has it (ADR-0115 D3): a
/// few rows renamed beside the bank's own text, the copies the bank sent twice hidden, and
/// the checking account's last closed statement half way through reconciling.
/// </summary>
/// <remarks>
/// Each write goes through the repository the register's own endpoint writes through, after
/// the body checks that endpoint makes and its repository leaves to it. Every check runs
/// before the first write: a step reports added or failed as a whole, so a refusal after
/// some of its writes would list the register examples missing while most of them exist.
/// Any refusal, and any outcome but the one the register's action gives, is the step's
/// failure. Every row comes from the first step's match of the manifest.
/// </remarks>
internal static class DemoRegisterSteps
{
    /// <summary>The status the statement's marked rows are given.</summary>
    internal const string Reconciling = "reconciling";

    /// <summary>
    /// Step 15. (a) Each edit candidate renamed to its new payee, as the register's edit
    /// does: the first edit of an imported row keeps the bank's values beside it. (b) Each
    /// copy the bank re-sent removed, as the register's delete does: an imported row is
    /// hidden, never deleted. (c) The manifest's half of checking's last closed statement
    /// marked reconciling on checking, as the register's bulk status action does; the rest of
    /// that statement was imported uncleared and stays so.
    /// </summary>
    internal static async Task RegisterAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var manifest = run.Context.Manifest;
        if (manifest.EditCandidates.Count == 0) throw new DemoSeedStepException("the manifest names no row to rename");
        if (manifest.DuplicateTxnIds.Count == 0) throw new DemoSeedStepException("the manifest names no re-sent row");
        if (manifest.CheckingReconcile is not { ReconcilingTxnIds.Count: > 0 } statement)
            throw new DemoSeedStepException("the manifest names no checking row to mark reconciling");

        var renames = manifest.EditCandidates
            .Select(c => (
                What: $"the rename of the row with the Moneydance id {c.TxnId}",
                HeaderId: run.Ids.Transactions[c.TxnId],
                Request: new PatchTransactionRequest { Payee = c.NewPayee }))
            .ToList();
        foreach (var (what, _, request) in renames) Refused(what, TransactionsEndpoints.ValidatePatchBody(request));

        var checking = run.Ids.Accounts[DemoAccountRoles.JointChecking];
        var marked = statement.ReconcilingTxnIds.Select(id => run.Ids.Transactions[id]).ToList();
        var reconcile = new BulkReconStatusRequest
        {
            Selection = new SelectionRequest { Kind = "explicit", HeaderIds = marked, AccountId = checking },
            Status = Reconciling,
            AccountId = checking,
        };
        Refused("the checking statement's reconciling rows", TransactionsEndpoints.ValidateBulkReconStatus(reconcile));
        await EachHasACheckingLegAsync(db, run.LedgerId, checking, marked, cancellationToken).ConfigureAwait(false);

        var transactions = new TransactionsRepository(db, run.Clock);
        foreach (var (what, headerId, request) in renames)
        {
            var outcome = await transactions.PatchAsync(run.LedgerId, headerId, request, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Refusal is { } refusal) Refused(what, AnnotationProblem.Of(refusal));
            if (outcome.Result != TransactionsRepository.PatchResult.Ok)
                throw new DemoSeedStepException($"{what} was refused: {outcome.Result}");
        }

        foreach (var mdId in manifest.DuplicateTxnIds)
        {
            var outcome = await transactions.DeleteAsync(run.LedgerId, run.Ids.Transactions[mdId], cancellationToken)
                .ConfigureAwait(false);
            // An imported row keeps its Moneydance id, so the delete hides it; one deleted
            // outright would be a row the import did not key, not the copy the bank sent.
            if (outcome != TransactionsRepository.DeleteOutcome.SoftHidden)
                throw new DemoSeedStepException($"the re-sent row with the Moneydance id {mdId} was not hidden: {outcome}");
        }

        // The repository sets every leg the rows have on checking, one per posting; each row
        // was checked above to have one.
        var bulk = new BulkTransactionsRepository(
            db, new LegDerivedRecomputeService(db), new HoldingsRecomputeService(db), run.Clock);
        await bulk.BulkSetReconStatusAsync(
                run.LedgerId, reconcile.Selection, reconcile.AccountId, reconcile.Status, run.Context.OwnerUserId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every row to mark has a leg on <paramref name="checking"/>: the status is set on
    /// those legs, so a row with none would be passed over without a word.
    /// </summary>
    /// <exception cref="DemoSeedStepException">A row to mark has no leg on checking.</exception>
    private static async Task EachHasACheckingLegAsync(
        AppDbContext db, Guid ledgerId, Guid checking, IReadOnlyList<Guid> marked, CancellationToken cancellationToken)
    {
        var onChecking = await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && l.AccountId == checking && marked.Contains(l.HeaderId))
            .Select(l => l.HeaderId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var without = marked.Except(onChecking).Count();
        if (without > 0)
            throw new DemoSeedStepException(
                $"{without} of the {marked.Count} checking rows the manifest marks reconciling have no leg on checking");
    }

    /// <summary>A shared validator's refusal, as the step's failure.</summary>
    private static void Refused(string what, IResult? refusal)
    {
        if (refusal is not null)
            throw new DemoSeedStepException($"{what} was refused: {BusinessError.Describe(refusal)}");
    }
}
