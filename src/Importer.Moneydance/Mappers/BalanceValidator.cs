using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure logic that reconstructs each Moneydance account's closing balance
/// from the export. The validator drives PR 2.8: after an import, every
/// account's persisted Coffer <c>balance_after</c> should match the value
/// MD itself would compute by summing flows on top of <c>sbal</c>. A drift
/// flags either an importer bug (mapper drops a flow) or a corrupted
/// export (rare).
/// </summary>
/// <remarks>
/// <para>Algorithm matches Moneydance's own running-balance derivation:
/// closing balance = <c>sbal</c> + every flow into the account. The
/// flow-aggregation rules:</para>
/// <list type="bullet">
///   <item><description>Where the txn's primary <c>acctid</c> is X, add the
///   <em>signed sum of every split's <c>parent_amount</c></em>. <c>parent_amount</c>
///   is MD's cash impact on the primary account; summing across splits
///   captures the txn's net cash effect.</description></item>
///   <item><description>Where a split's <c>acctid</c> is X (and not the primary,
///   to avoid double-count on self-referential splits), add the
///   split's <c>split_amount</c>. <c>split_amount</c> is cash on the
///   target except for investment <c>sec</c> splits, where it's a
///   share count — but those targets are <c>type='s'</c> sub-accounts
///   that never become Coffer accounts, so they're naturally excluded
///   from the per-account map.</description></item>
/// </list>
///
/// <para>Categories, system Holdings siblings, and security sub-accounts
/// are intentionally absent from the result map. Categories don't carry
/// a meaningful closing balance in MD; system rows don't exist in the
/// MD export; security sub-accounts get translated to <c>holdings</c>,
/// not <c>accounts</c>.</para>
/// </remarks>
public static class BalanceValidator
{
    /// <summary>
    /// Walk the export and return, for every <em>real</em> Moneydance account
    /// (bank/credit/investment/asset/liability/loan), the closing balance MD
    /// itself would compute. Keyed by MD account id (the export UUID), so
    /// callers cross-walk via <c>accounts.external_id</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> ComputeExpectedByMdAccountId(MdExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        // Seed with sbal for every non-category, non-security-sub, non-root account.
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            var acct = MdAcct.From(item);
            if (acct.IsRoot || acct.IsSecuritySubAccount) continue;

            // Categories (`type` ∈ {i, e}) have no meaningful closing balance;
            // skip them here so the caller doesn't need to second-guess.
            if (acct.TypeCode is "i" or "e") continue;

            result[acct.Id] = AccountMapper.MinorUnitsToDecimal(acct.StartingBalance ?? 0);
        }

        // Apply every txn's flows on top of sbal.
        foreach (var item in export.AllItems)
        {
            if (item.ObjType != "txn") continue;
            var txn = MdTxn.From(item);

            // Primary side: sum of parent_amount across all splits = net cash
            // on the primary account.
            if (result.ContainsKey(txn.AcctId))
            {
                var pamtSum = txn.Splits.Sum(s => s.ParentAmount);
                result[txn.AcctId] += AccountMapper.MinorUnitsToDecimal(pamtSum);
            }

            // Target side: split_amount on every split whose target is a
            // tracked account other than the primary. The same-as-primary
            // skip prevents double-counting; sec-split targets are
            // type='s' sub-accounts and aren't in `result`, so they fall
            // through automatically.
            foreach (var split in txn.Splits)
            {
                if (split.AcctId == txn.AcctId) continue;
                if (!result.ContainsKey(split.AcctId)) continue;
                result[split.AcctId] += AccountMapper.MinorUnitsToDecimal(split.SplitAmount);
            }
        }

        return result;
    }
}
