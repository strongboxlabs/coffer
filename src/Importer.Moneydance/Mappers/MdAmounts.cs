using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// How a Moneydance amount becomes Coffer money. Moneydance stores every amount as an
/// integer of its currency's minor units, at that currency's own <c>dec</c> places: a
/// split's <c>pamt</c> in the parent account's currency, its <c>samt</c> in the split
/// account's, an account's <c>sbal</c> in its own. A 0-place currency's integer is the
/// amount; a 2-place one's is the amount times a hundred, a 3-place one's the amount
/// times a thousand.
/// </summary>
/// <remarks>
/// <para>A category holds no money of its own (ADR-0114 D7): Coffer reads a category's
/// line as being in the currency of the real account on the posting's other side. So a
/// category leg carries the real side's amount negated, never the split's
/// <c>samt</c>, which Moneydance keeps in the category's own currency. For a posting
/// whose two sides share a currency the two are the same number.</para>
/// <para>A posting between two real accounts keeps each side's own amount, in its own
/// currency. When those differ (a transfer between currencies) the posting is a
/// two-amount pair (ADR-0114 D13): it does not sum to zero, and the header-balance
/// check leaves it out of its header's sum and passes it when its two amounts are
/// nonzero and opposite in sign.</para>
/// </remarks>
public static class MdAmounts
{
    /// <summary><paramref name="minorUnits"/> at <paramref name="decimals"/> places. The
    /// ceiling is the database's 3 places on leg money (ADR-0114 D15); each account's
    /// own limit, its code's places, is held by <see cref="MdCurrencies.ResolveAccount"/>.</summary>
    public static decimal ToMoney(long minorUnits, int decimals)
    {
        if (decimals is < 0 or > MdCurrencies.MaxAmountDecimals)
            throw new ArgumentOutOfRangeException(nameof(decimals), decimals,
                $"A currency's amounts are kept at 0 to {MdCurrencies.MaxAmountDecimals} decimal places.");
        decimal divisor = 1m;
        for (var i = 0; i < decimals; i++) divisor *= 10m;
        return minorUnits / divisor;
    }

    /// <summary><paramref name="minorUnits"/> in <paramref name="account"/>'s currency;
    /// throws when the account's decimal places were never resolved.</summary>
    public static decimal ToMoney(long minorUnits, AccountRef account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var decimals = account.AmountDecimals
            ?? throw new InvalidOperationException(
                $"Account {account.Id} has no currency decimal places; its Moneydance amounts cannot be scaled.");
        return ToMoney(minorUnits, decimals);
    }

    /// <summary>
    /// The two legs of one Moneydance split: the origin on the transaction's primary
    /// account, the counterpart on the split's account. A category side carries the
    /// real side's amount negated; two real accounts each keep their own; two
    /// categories mirror the primary's <c>pamt</c>.
    /// </summary>
    public static (decimal Origin, decimal Counterpart) Posting(MdSplit split, AccountRef primary, AccountRef target)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(target);

        if (primary.IsCategory && !target.IsCategory)
        {
            var real = ToMoney(split.SplitAmount, target);
            return (-real, real);
        }
        if (target.IsCategory)
        {
            var origin = ToMoney(split.ParentAmount, primary);
            return (origin, -origin);
        }
        return (ToMoney(split.ParentAmount, primary), ToMoney(split.SplitAmount, target));
    }
}
