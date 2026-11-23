using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Errors;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The two legs of a posting between real accounts in different currencies (ADR-0114 D13),
/// each in its own account's currency: a bank posting's <c>amount</c> and
/// <c>counterpartyAmount</c>, or an investment transfer's brokerage leg and its
/// <c>transferAmount</c>. Holds the one second-amount rule every writer of such a pair
/// applies, and the one builder of a bank posting's two legs.
/// </summary>
/// <remarks>
/// <para><b>Which posting carries two amounts.</b> Only a posting between two real
/// accounts (neither a category) whose currencies differ. Its counterparty leg is the
/// posting's <c>counterpartyAmount</c>, which is required, nonzero and opposite in sign to
/// the amount (<see cref="CheckSecondAmount"/>). Every other posting mirrors: its
/// counterparty leg is the amount negated (ADR-0022 Rule 2), and a
/// <c>counterpartyAmount</c> sent on it is dropped, as an investment request's transfer
/// amount is where it does not apply. A category leg therefore always carries the real
/// side's amount negated (ADR-0114 D13).</para>
/// <para><b>Fails closed.</b> Every bank writer (the create, the PATCH reshape, a bank
/// reminder's create, edit and adjust-at-post fire) builds its legs here, whoever its
/// caller is, so an internal caller that skips the endpoint's validation and sends a
/// posting across currencies without the second amount is refused with a
/// <see cref="Refusal"/>: it is never written as a mirror, which would read one figure in
/// two currencies.</para>
/// </remarks>
public static class PostingLegPair
{
    /// <summary>The facts about one account the builder decides by: whether it is a
    /// category, and its currency.</summary>
    internal readonly record struct AccountSide(string AccountType, string CurrencyCode)
    {
        /// <summary>A real account: anything but a category, which holds no money of its
        /// own.</summary>
        public bool IsReal => !string.Equals(AccountType, "category", StringComparison.Ordinal);
    }

    /// <summary>The two leg amounts of one posting as the builder writes them: the source
    /// side's, and the counterparty side's in its own account's currency.</summary>
    internal readonly record struct LegAmounts(decimal Source, decimal Counterparty);

    /// <summary>A posting the builder refuses: the 422 code, its detail, and the posting's
    /// place in the request (0-based).</summary>
    public sealed record Refusal(string Code, string Detail, int PostingIndex);

    /// <summary>
    /// Every account <paramref name="accountIds"/> names in <paramref name="ledgerId"/>,
    /// with its type and currency, in one read on the write's own context (and so inside
    /// its transaction and under its row-level security).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, AccountSide>> LoadSidesAsync(
        AppDbContext db, Guid ledgerId, IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accountIds);
        var ids = accountIds.Distinct().ToList();
        var rows = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountType, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => new AccountSide(r.AccountType, r.CurrencyCode));
    }

    /// <summary>
    /// The stored pair of each posting of a stored transaction, by the id of its leg on
    /// <paramref name="sourceAccountId"/>: that leg, and the other leg of its posting.
    /// What an edit's resent pair is compared with (<see cref="CheckSecondAmount"/>).
    /// </summary>
    internal static IReadOnlyDictionary<Guid, StoredPair> StoredPairs(
        IEnumerable<TxnLegRow> legs, Guid sourceAccountId)
    {
        ArgumentNullException.ThrowIfNull(legs);
        var all = legs.ToList();
        var pairs = new Dictionary<Guid, StoredPair>();
        foreach (var source in all.Where(l => l.AccountId == sourceAccountId))
        {
            var other = all.FirstOrDefault(l => l.PostingIndex == source.PostingIndex && l.Id != source.Id);
            if (other is null) continue;
            pairs[source.Id] = new StoredPair(new Leg(source.AccountId, source.Amount), new Leg(other.AccountId, other.Amount));
        }
        return pairs;
    }

    /// <summary>
    /// The two legs of <paramref name="posting"/>, entered from
    /// <paramref name="sourceAccountId"/>, or why it is refused (ADR-0114 D13).
    /// </summary>
    /// <param name="sourceAccountId">The account the posting is entered from.</param>
    /// <param name="posting">The posting as the write sends it.</param>
    /// <param name="postingIndex">Its place in the request, named by a refusal.</param>
    /// <param name="sides">Both accounts' facts (<see cref="LoadSidesAsync"/>).</param>
    /// <param name="stored">The pair the posting replaces, when the write is an edit of a
    /// kept posting; a pair resent equal to it passes the nonzero and sign rules.</param>
    /// <exception cref="InvalidOperationException">An account is not among
    /// <paramref name="sides"/>: the caller did not load it, or it is not in the
    /// ledger, which the caller validates first.</exception>
    internal static (LegAmounts Amounts, Refusal? Refused) Build(
        Guid sourceAccountId,
        TransactionPosting posting,
        int postingIndex,
        IReadOnlyDictionary<Guid, AccountSide> sides,
        StoredPair? stored = null)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(sides);
        var source = SideOf(sides, sourceAccountId);
        var counterparty = SideOf(sides, posting.CounterpartyAccountId);

        var twoAmounts = source.IsReal && counterparty.IsReal
                         && !string.Equals(source.CurrencyCode, counterparty.CurrencyCode, StringComparison.Ordinal);
        if (!twoAmounts) return (new LegAmounts(posting.Amount, -posting.Amount), null);

        if (posting.CounterpartyAmount is not { } second)
            return (default, new Refusal(
                BusinessError.Codes.TransactionPostingCounterpartyAmountRequired,
                $"Posting {postingIndex + 1} moves money between accounts in {source.CurrencyCode} and "
                + $"{counterparty.CurrencyCode}: send counterpartyAmount, the amount in {counterparty.CurrencyCode}.",
                postingIndex));

        var fault = CheckSecondAmount(
            new Leg(sourceAccountId, posting.Amount), new Leg(posting.CounterpartyAccountId, second), stored);
        return BankCode(fault) is { } code
            ? (default, new Refusal(code, FaultDetail(fault, postingIndex), postingIndex))
            : (new LegAmounts(posting.Amount, second), null);
    }

    private static AccountSide SideOf(IReadOnlyDictionary<Guid, AccountSide> sides, Guid accountId) =>
        sides.TryGetValue(accountId, out var side)
            ? side
            : throw new InvalidOperationException(
                $"Account {accountId} was not loaded for the posting's legs; the caller validates every account first.");

    private static string FaultDetail(SecondAmountFault fault, int postingIndex) => fault switch
    {
        SecondAmountFault.Zero =>
            $"Posting {postingIndex + 1}: money that leaves one account arrives in the other, so neither amount can be zero.",
        _ =>
            $"Posting {postingIndex + 1}: what leaves one account arrives in the other, so the two amounts have opposite signs.",
    };
    /// <summary>One leg of a pair: its account and its signed amount, in that account's
    /// currency.</summary>
    internal readonly record struct Leg(Guid AccountId, decimal Amount);

    /// <summary>The pair as stored, for an edit of a posting that already has one.</summary>
    internal readonly record struct StoredPair(Leg First, Leg Second);

    /// <summary>What is wrong with a pair's two amounts, if anything.</summary>
    internal enum SecondAmountFault
    {
        /// <summary>The pair passes.</summary>
        None,
        /// <summary>One side or both is zero.</summary>
        Zero,
        /// <summary>The two sides have the same sign.</summary>
        SameSign,
    }

    /// <summary>
    /// The second-amount rule (ADR-0114 D13): money that leaves one account arrives in the
    /// other, so neither side is zero and their signs are opposite.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Coffer.Api.Endpoints.MoneyPlaces"/>'s places rule, it checks only a pair the
    /// write changes. A pair resent equal to its two stored legs, on the same accounts,
    /// passes, whichever side the edit was opened from: a mirrored zero pair that a move or
    /// a currency edit left across currencies is representable and is kept, and an edit
    /// that leaves it alone (a payee or a date) must not be refused for it. Amounts are
    /// compared by value, so a resent figure passes whatever its scale.
    /// </remarks>
    /// <param name="first">The source side's leg, as the write sends it.</param>
    /// <param name="second">The other side's leg, as the write sends it.</param>
    /// <param name="stored">The pair the write would replace, when there is one.</param>
    internal static SecondAmountFault CheckSecondAmount(Leg first, Leg second, StoredPair? stored = null)
    {
        if (Unchanged(first, second, stored)) return SecondAmountFault.None;
        if (first.Amount == 0m || second.Amount == 0m) return SecondAmountFault.Zero;
        if (Math.Sign(first.Amount) == Math.Sign(second.Amount)) return SecondAmountFault.SameSign;
        return SecondAmountFault.None;
    }

    /// <summary>True when the two legs equal the stored pair's, in either order: the same
    /// accounts, each with its stored amount.</summary>
    internal static bool Unchanged(Leg first, Leg second, StoredPair? stored) =>
        stored is { } s
        && ((first == s.First && second == s.Second) || (first == s.Second && second == s.First));

    /// <summary>The bank posting's refusal code for <paramref name="fault"/>, or null when
    /// the pair passes.</summary>
    internal static string? BankCode(SecondAmountFault fault) => fault switch
    {
        SecondAmountFault.Zero => BusinessError.Codes.TransactionPostingTwoAmountsNonzero,
        SecondAmountFault.SameSign => BusinessError.Codes.TransactionPostingTwoAmountsSign,
        _ => null,
    };

    /// <summary>The investment transfer's refusal code for <paramref name="fault"/>, or null
    /// when the pair passes.</summary>
    internal static string? InvestmentCode(SecondAmountFault fault) => fault switch
    {
        SecondAmountFault.Zero => BusinessError.Codes.InvestmentTxnTransferAmountsNonzero,
        SecondAmountFault.SameSign => BusinessError.Codes.InvestmentTxnTransferAmountsSign,
        _ => null,
    };
}
