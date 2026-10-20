using Microsoft.AspNetCore.Http;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Shared bank-shape posting validation (ADR-0025), used by both
/// <see cref="TransactionsEndpoints"/> (live create/PATCH) and
/// <see cref="RemindersEndpoints"/> (reminder template create/edit, ADR-0047).
/// One copy so the two surfaces can never diverge on the per-posting rules
/// (counterparty present + distinct from source; every referenced account
/// in-ledger + active).
/// </summary>
internal static class PostingValidation
{
    /// <summary>
    /// Shared per-posting shape validation. Returns a 422 IResult on
    /// rejection or null on a clean posting list.
    ///
    /// <para>Zero-amount postings are intentionally allowed: paycheck splits
    /// routinely carry zero-amount line items (a payroll surtax, a
    /// retirement-contribution overflow, accrual placeholders) that are
    /// positive in some pay periods and zero in others. The DB places no zero constraint.</para>
    /// </summary>
    public static IResult? ValidatePostings(
        IReadOnlyList<TransactionPosting> postings,
        Guid sourceAccountId)
    {
        if (postings is null || postings.Count == 0)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionPostingsEmpty,
                "postings must contain at least one item.");
        foreach (var p in postings)
        {
            if (p.CounterpartyAccountId == Guid.Empty)
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionPostingCounterpartyRequired,
                    "every posting requires a counterpartyAccountId.");
            if (p.CounterpartyAccountId == sourceAccountId)
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionPostingSelf,
                    "a posting's counterparty must differ from the source account.");
        }
        return null;
    }

    /// <summary>
    /// Verify every account referenced by the postings (source + every
    /// counterparty) belongs to the supplied ledger, and that none of them is
    /// a deactivated account the posting was not ALREADY on. Single batch
    /// lookup: dictionary miss → 422 <c>account-not-in-ledger</c>; a newly
    /// introduced inactive account → 422 <c>account-inactive</c>.
    /// </summary>
    /// <param name="alreadyReferenced">
    /// Accounts this header's legs already sit on, on a PATCH. Null on create,
    /// where nothing is already referenced.
    ///
    /// <para>Without it this gate contradicted its own documented rule: "new
    /// transactions and re-targeting PATCHes cannot land on accounts that have
    /// been deactivated. Editing other fields on existing legs whose account is
    /// already inactive is still allowed (historical preservation)." Editing a
    /// memo resends the unchanged counterparty, the validator saw an inactive
    /// account in the payload and refused — so deactivating an account froze
    /// its whole history, and a typo in a ten-year-old memo became
    /// uncorrectable.</para>
    /// </param>
    public static async Task<IResult?> ValidatePostingAccountsAsync(
        Guid ledgerId,
        Guid sourceAccountId,
        IReadOnlyList<TransactionPosting> postings,
        AccountsRepository accounts,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? alreadyReferenced = null)
    {
        var referenced = new HashSet<Guid> { sourceAccountId };
        foreach (var p in postings)
            referenced.Add(p.CounterpartyAccountId);

        var byId = await accounts.LookupAccountActivityAsync(
            ledgerId, referenced, cancellationToken).ConfigureAwait(false);

        if (!byId.TryGetValue(sourceAccountId, out var sourceActivity))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Source account does not belong to this ledger.");
        if (!sourceActivity.IsActive && alreadyReferenced?.Contains(sourceAccountId) != true)
            return BusinessError.Problem(BusinessError.Codes.AccountInactive,
                "Source account is inactive; reactivate it before posting new transactions to it.");

        foreach (var counterparty in postings
            .Select(p => p.CounterpartyAccountId)
            .Distinct()
            .Where(id => id != sourceAccountId))
        {
            if (!byId.TryGetValue(counterparty, out var cpActivity))
                return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "A posting's counterparty account does not belong to this ledger.");
            if (!cpActivity.IsActive && alreadyReferenced?.Contains(counterparty) != true)
                return BusinessError.Problem(BusinessError.Codes.AccountInactive,
                    "A posting's counterparty account is inactive; reactivate it before posting to it.");
        }
        return null;
    }
}
