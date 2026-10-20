namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over one split inside a <see cref="MdTxn"/>. Moneydance encodes
/// splits as flat keys with numeric prefixes on the parent transaction: the
/// first split's account-id appears as <c>0.acctid</c>, the second's as
/// <c>1.acctid</c>, and so on. <see cref="MdTxn.From"/> walks those prefixes
/// in order and produces one <c>MdSplit</c> per prefix.
/// </summary>
/// <remarks>
/// Sign convention (matches Moneydance): <see cref="ParentAmount"/> is the
/// amount applied to the txn's primary account (<see cref="MdTxn.AcctId"/>);
/// <see cref="SplitAmount"/> is the amount applied to <see cref="AcctId"/>.
/// The two are equal in magnitude and opposite in sign for a balanced
/// single-currency split. Investment txns can have additional splits
/// (commission/fee/security side); see <see cref="InvestSplitType"/>.
/// </remarks>
public sealed record MdSplit(
    int Index,
    string Id,
    string AcctId,
    long SplitAmount,
    long ParentAmount,
    string? Description,
    string? InvestSplitType,
    string? Status,
    string? Tags,
    string? OldId);
