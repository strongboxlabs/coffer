using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// Named accounts and the transaction shapes the annotation tests run against.
/// Every shape here is one the writers can produce; none carries money, only
/// structure.
/// </summary>
internal static class Scenarios
{
    public static readonly Guid Bank = Id("bank");
    public static readonly Guid Savings = Id("savings");
    public static readonly Guid Groceries = Id("groceries");
    public static readonly Guid Fuel = Id("fuel");
    public static readonly Guid Fees = Id("fees");
    public static readonly Guid Brokerage = Id("brokerage");
    public static readonly Guid Holdings = Id("holdings");
    public static readonly Guid OtherBrokerage = Id("other-brokerage");
    public static readonly Guid OtherHoldings = Id("other-holdings");

    /// <summary>A stable id per name, so a failure message names the same account every run.</summary>
    public static Guid Id(string name)
    {
        var bytes = new byte[16];
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    public static string NameOf(Guid account) =>
        Named.TryGetValue(account, out var name) ? name : account.ToString("N")[..8];

    private static readonly Dictionary<Guid, string> Named = new[]
    {
        ("bank", Bank), ("savings", Savings), ("groceries", Groceries), ("fuel", Fuel), ("fees", Fees),
        ("brokerage", Brokerage), ("holdings", Holdings),
        ("other-brokerage", OtherBrokerage), ("other-holdings", OtherHoldings),
    }.ToDictionary(p => p.Item2, p => p.Item1);

    /// <summary>A leg; whether its account is a brokerage is a fact about the account, so it is derived.</summary>
    public static ShapeLeg Leg(int posting, Guid account, string? role = null, bool investmentSide = false, Guid? legId = null,
        bool transferSource = false) =>
        new(new LegKey(posting, account), legId ?? Guid.NewGuid(), role, investmentSide,
            IsBrokerage: account == Brokerage || account == OtherBrokerage, IsTransferSource: transferSource);

    /// <summary>A buy with no fee: the brokerage and its holdings account on the one posting.</summary>
    public static TxnShape OnePostingBuy() =>
        new(Brokerage, LedgerActions.Buy,
            [Leg(0, Brokerage, PostingRoles.Security, investmentSide: true), Leg(0, Holdings, PostingRoles.Security, investmentSide: true)]);

    /// <summary>One posting between two plain accounts.</summary>
    public static TxnShape OneSplit(Guid originating, Guid a, Guid b) =>
        new(originating, null, [Leg(0, a), Leg(0, b)]);

    /// <summary>A bank split to several categories, entered from <paramref name="originating"/>.</summary>
    public static TxnShape MultiSplit(Guid originating, Guid source, params Guid[] targets) =>
        new(originating, null, [.. targets.SelectMany((t, i) => new[] { Leg(i, source), Leg(i, t) })]);

    /// <summary>
    /// Every line goes between the same two accounts, so both are on every posting
    /// and only the stored originating account can say which is the split side.
    /// </summary>
    public static TxnShape Symmetric(Guid originating) =>
        new(originating, null, [Leg(0, Bank), Leg(0, Groceries), Leg(1, Bank), Leg(1, Groceries)]);

    /// <summary>A one-posting cash transfer out of the brokerage (an Xfr).</summary>
    public static TxnShape XfrToBank() =>
        new(Brokerage, LedgerActions.Transfer,
            [Leg(0, Brokerage, PostingRoles.Transfer, investmentSide: true), Leg(0, Bank, PostingRoles.Transfer)]);

    /// <summary>
    /// A one-posting transfer between two brokerages. Only the one it was entered
    /// from is the investment side; the other sees a counterpart row.
    /// </summary>
    public static TxnShape BrokerageToBrokerage(Guid enteredFrom)
    {
        var other = enteredFrom == Brokerage ? OtherBrokerage : Brokerage;
        return new(enteredFrom, LedgerActions.Transfer,
            [Leg(0, enteredFrom, PostingRoles.Transfer, investmentSide: true), Leg(0, other, PostingRoles.Transfer)]);
    }

    /// <summary>A buy with a commission: the security pair and the fee pair.</summary>
    public static TxnShape BuyWithFee() =>
        new(Brokerage, LedgerActions.Buy,
        [
            Leg(0, Brokerage, PostingRoles.Security, investmentSide: true),
            Leg(0, Holdings, PostingRoles.Security, investmentSide: true),
            Leg(1, Brokerage, PostingRoles.Fee, investmentSide: true),
            Leg(1, Fees, PostingRoles.Fee),
        ]);

    /// <summary>A buy funded from another brokerage, which is on the transfer posting only.</summary>
    public static TxnShape BuyXfrFromOtherBrokerage() =>
        new(Brokerage, LedgerActions.BuyXfr,
        [
            Leg(0, Brokerage, PostingRoles.Security, investmentSide: true),
            Leg(0, Holdings, PostingRoles.Security, investmentSide: true),
            Leg(1, Brokerage, PostingRoles.Transfer, investmentSide: true),
            Leg(1, OtherBrokerage, PostingRoles.Transfer),
        ]);

    /// <summary>
    /// An in-kind transfer from the brokerage to the other one: no account is on every
    /// posting; all four are the investment side, and the brokerage is the source.
    /// </summary>
    public static TxnShape TransferShares() =>
        new(Brokerage, LedgerActions.TransferShares,
        [
            Leg(0, Brokerage, PostingRoles.Security, investmentSide: true, transferSource: true),
            Leg(0, Holdings, PostingRoles.Security, investmentSide: true),
            Leg(1, OtherBrokerage, PostingRoles.Security, investmentSide: true),
            Leg(1, OtherHoldings, PostingRoles.Security, investmentSide: true),
        ]);

    /// <summary>Every shape of the perspective matrix, named.</summary>
    public static IEnumerable<(string Name, TxnShape Shape)> All()
    {
        yield return ("one-split owned by the bank", OneSplit(Bank, Bank, Groceries));
        yield return ("one-split owned by the category", OneSplit(Groceries, Bank, Groceries));
        yield return ("real-to-real one-split owned by the bank", OneSplit(Bank, Bank, Savings));
        yield return ("real-to-real one-split owned by savings", OneSplit(Savings, Bank, Savings));
        yield return ("multi-split", MultiSplit(Bank, Bank, Groceries, Fuel));
        yield return ("multi-split owned by its other side", MultiSplit(Groceries, Groceries, Bank, Savings));
        yield return ("symmetric multi-split owned by the bank", Symmetric(Bank));
        yield return ("symmetric multi-split owned by the category", Symmetric(Groceries));
        yield return ("one-posting Xfr", XfrToBank());
        yield return ("one-posting brokerage-to-brokerage transfer", BrokerageToBrokerage(Brokerage));
        yield return ("the same transfer entered from the other brokerage", BrokerageToBrokerage(OtherBrokerage));
        yield return ("buy with a fee", BuyWithFee());
        yield return ("buy_xfr funded from another brokerage", BuyXfrFromOtherBrokerage());
        yield return ("transfer_shares", TransferShares());
    }
}
