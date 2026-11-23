using System.Globalization;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// The plain-language sentence each failed validation check gives the person who ran
/// the import. The check's <see cref="ImportValidator.CheckResult.Message"/> names
/// what failed in the importer's own terms, for whoever diagnoses it; the person
/// importing has none of those terms, so the import wizard shows this instead. Each
/// sentence says what is wrong in the new ledger and, where the check knows it, how
/// many and which.
/// </summary>
internal static class CheckSummaries
{
    internal static string DuplicateAccountNames(IReadOnlyList<string> names, bool more) =>
        $"{AtLeast(more)}{Count(names.Count, "account name is", "account names are")} used by more than "
        + $"one account: {string.Join(", ", names)}. Rename the accounts that share a name so you can "
        + "tell them apart.";

    internal static string UnusableActions(IReadOnlyList<string> actions, bool more) =>
        "Some investment transactions were stored with "
        + $"{(actions.Count == 1 && !more ? "an action" : "actions")} Coffer does not use: "
        + $"{string.Join(", ", actions)}{(more ? ", and others" : "")}. They may not show correctly in "
        + "their register.";

    /// <summary>The header-balance check's sentence: lines that do not add up within a
    /// currency. A transfer between currencies keeps each side's own amount and is not
    /// counted here when its two amounts are nonzero and opposite in sign
    /// (ADR-0114 D13). One that fails that test is counted: a one-sided or same-sign
    /// pair, whose lines do not add up, and a pair with both sides zero, whose lines do
    /// add up but which lacks the two nonzero amounts the rule requires. The sentence is
    /// kept for the SPA copy, so for that last shape it overstates.</summary>
    internal static string Unbalanced(int transactions) =>
        $"{Count(transactions, "transaction has", "transactions have")} lines that do not add up to zero, "
        + $"so the balances of the accounts {(transactions == 1 ? "it touches" : "they touch")} may be off.";

    internal static string WithoutLines(int transactions) =>
        $"{Count(transactions, "transaction was", "transactions were")} imported with no lines, so "
        + $"{(transactions == 1 ? "it has" : "they have")} no amount.";

    internal static string WithoutOwner(int transactions) =>
        $"{Count(transactions, "transaction is", "transactions are")} not owned by an account that can "
        + $"hold {(transactions == 1 ? "it" : "them")}, so {(transactions == 1 ? "it" : "they")} may show "
        + "in the wrong register.";

    internal static string LotsWithoutLine(int lots) =>
        $"{Count(lots, "tax lot points", "tax lots point")} at a transaction line that does not exist, so "
        + $"{(lots == 1 ? "its" : "their")} cost basis may be wrong.";

    internal static string AccountCount(int inExport, int inLedger) =>
        inLedger < inExport
            ? $"{Count(inExport - inLedger, "account", "accounts")} of the {inExport.ToString("N0", CultureInfo.InvariantCulture)} "
              + $"in the export {(inExport - inLedger == 1 ? "is" : "are")} not in the ledger."
            : $"The ledger has {Count(inLedger, "account", "accounts")} from the export, which has "
              + $"{inExport.ToString("N0", CultureInfo.InvariantCulture)}.";

    internal static string NotImported(int transactions) =>
        $"{Count(transactions, "transaction was", "transactions were")} not imported.";

    internal static string UnrecognisedSecurityTypes(int securities, IReadOnlyList<string> codes) =>
        $"{Count(securities, "security has", "securities have")} a Moneydance security type Coffer does "
        + $"not recognise ({(codes.Count == 1 ? "code" : "codes")} {string.Join(", ", codes)}), so "
        + $"{(securities == 1 ? "it was" : "they were")} imported with vehicle type Other. Set the vehicle "
        + "with Edit security on the security's page.";

    private static string AtLeast(bool more) => more ? "At least " : "";

    private static string Count(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {many}";
}
