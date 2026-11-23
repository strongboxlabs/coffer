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
/// in-ledger + active; each typed amount at its currency's places).
/// </summary>
/// <remarks>
/// The second-amount rule of a posting between real accounts in different currencies
/// (ADR-0114 D13: required, nonzero, opposite in sign; dropped on any other posting) is not
/// here: it is <see cref="PostingLegPair"/>'s, which every bank writer builds its legs
/// through, so an internal caller that skips this validation is refused too. This class
/// holds the places rule (ADR-0114 D15) for the posting's amount and its second amount.
/// </remarks>
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
    /// <para>Without it this gate contradicts its own documented rule: "new
    /// transactions and re-targeting PATCHes cannot land on accounts that have
    /// been deactivated. Editing other fields on existing legs whose account is
    /// already inactive is still allowed (historical preservation)." Editing a
    /// memo resends the unchanged counterparty, so a validator that refuses any
    /// inactive account in the payload freezes a deactivated account's whole
    /// history, and a typo in a ten-year-old memo becomes uncorrectable.</para>
    /// </param>
    /// <param name="stored">The stored pair of each posting an edit keeps, keyed by the id
    /// of its source-side leg (<see cref="PostingLegPair.StoredPairs"/>), read before the
    /// edit. Null on a create and on a reminder's fire, which are new writes. A posting
    /// whose <c>legId</c> names a stored leg with the same amount, read in the same
    /// currency, is not checked for places: stored amounts that do not fit are kept, and an
    /// edit that leaves one as it was is not refused for it (ADR-0114 D15).</param>
    /// <param name="postingsField">The request's wire path to the postings list, for the
    /// places refusal's <c>field</c>: <c>postings</c> on a create or a fire,
    /// <c>postings.items</c> on an edit.</param>
    /// <remarks>
    /// <para><b>Places</b> (ADR-0114 D15). Each posting's <c>amount</c> is a typed figure,
    /// checked at the places of the currency it lands in: the source's when the source is
    /// a real account, or the counterparty's when the source is a category, because a
    /// category's leg mirrors the real side and is checked at its currency, never at the
    /// category's own stored code (ADR-0114 D13). A posting between two categories takes
    /// the category's own code. The <c>counterpartyAmount</c> of a posting between real
    /// accounts in different currencies is typed too, and is checked at the counterparty's
    /// places; one resent equal to the stored counterparty leg of the posting its
    /// <c>legId</c> names, on the same account, passes. On any other posting it is dropped,
    /// so it is not checked. A figure that does not fit is refused with
    /// <c>amount-places-exceed-currency</c>, naming the currency and the field.</para>
    /// </remarks>
    public static async Task<IResult?> ValidatePostingAccountsAsync(
        Guid ledgerId,
        Guid sourceAccountId,
        IReadOnlyList<TransactionPosting> postings,
        AccountsRepository accounts,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? alreadyReferenced = null,
        IReadOnlyDictionary<Guid, PostingLegPair.StoredPair>? stored = null,
        string postingsField = "postings")
    {
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(accounts);
        var referenced = new HashSet<Guid> { sourceAccountId };
        foreach (var p in postings)
            referenced.Add(p.CounterpartyAccountId);
        // The other side of each stored pair too, so the currency a stored figure was read
        // in is known (a category-register edit that repoints the paying account).
        if (stored is not null)
            foreach (var pair in stored.Values)
                referenced.Add(pair.Second.AccountId);

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

        for (var i = 0; i < postings.Count; i++)
        {
            var posting = postings[i];
            var counterpartyActivity = byId[posting.CounterpartyAccountId];
            var currency = PlacesCurrency(sourceActivity, counterpartyActivity);
            var amountUnchanged = posting.LegId is { } legId
                && stored is not null
                && stored.TryGetValue(legId, out var pair)
                && MoneyPlaces.Unchanged(posting.Amount, pair.First.Amount)
                && byId.TryGetValue(pair.First.AccountId, out var storedSource)
                && byId.TryGetValue(pair.Second.AccountId, out var storedOther)
                && string.Equals(PlacesCurrency(storedSource, storedOther), currency, StringComparison.Ordinal);
            if (!amountUnchanged
                && MoneyPlaces.Check(posting.Amount, currency, $"{postingsField}[{i}].amount") is { } unfit)
                return unfit;

            // The second amount of a posting between real accounts in different currencies is
            // typed in the counterparty's currency, so it is held to that currency's places.
            if (TwoAmounts(sourceActivity, counterpartyActivity)
                && posting.CounterpartyAmount is { } second
                && MoneyPlaces.Check(second, counterpartyActivity.CurrencyCode,
                    $"{postingsField}[{i}].counterpartyAmount", StoredSecond(posting, stored)) is { } unfitSecond)
                return unfitSecond;
        }
        return null;
    }

    /// <summary>True when a posting between <paramref name="source"/> and
    /// <paramref name="counterparty"/> carries two amounts (ADR-0114 D13): both are real
    /// accounts and their currencies differ, as <see cref="PostingLegPair"/> decides.</summary>
    private static bool TwoAmounts(AccountsRepository.AccountActivity source, AccountsRepository.AccountActivity counterparty) =>
        source.IsReal && counterparty.IsReal
        && !string.Equals(source.CurrencyCode, counterparty.CurrencyCode, StringComparison.Ordinal);

    /// <summary>The stored counterparty leg's amount a kept posting's
    /// <c>counterpartyAmount</c> would replace: that of the stored pair its <c>legId</c>
    /// names, when the pair's other leg is on the same counterparty account. Null on a new
    /// posting, or one repointed to another counterparty, whose figure is checked as typed.</summary>
    private static decimal? StoredSecond(
        TransactionPosting posting, IReadOnlyDictionary<Guid, PostingLegPair.StoredPair>? stored) =>
        posting.LegId is { } legId
        && stored is not null
        && stored.TryGetValue(legId, out var pair)
        && pair.Second.AccountId == posting.CounterpartyAccountId
            ? pair.Second.Amount
            : null;

    /// <summary>
    /// The currency whose places a posting's amount is checked at (ADR-0114 D13, D15): the
    /// source's when it is a real account; otherwise the counterparty's when that is real,
    /// since a category's leg mirrors the real side; and a category's own code for a
    /// posting between two categories, which has no real side.
    /// </summary>
    internal static string PlacesCurrency(AccountsRepository.AccountActivity source, AccountsRepository.AccountActivity counterparty) =>
        source.IsReal ? source.CurrencyCode
        : counterparty.IsReal ? counterparty.CurrencyCode
        : source.CurrencyCode;
}
