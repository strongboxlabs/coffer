using System.Globalization;

namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>txn</c> item. The transaction's primary
/// account is <see cref="AcctId"/>; the other-side accounts live on the
/// <see cref="Splits"/>. <see cref="InvestTxnType"/> is non-null for
/// investment transactions (buy/sell/div/divr/...) and informs the
/// downstream investment-mapper logic in PR 2.6.
/// </summary>
public sealed record MdTxn(
    string Id,
    string AcctId,
    string Description,
    string? Memo,
    int Date,
    int? TransactedDate,
    long? DateEnteredMillis,
    string? Status,
    string? CheckNumber,
    string? OlOrigPayee,
    string? OlOrigMemo,
    string? OlFiId,
    string? OlFitid,
    string? OlMatchStatus,
    string? OlMatchType,
    string? OlOrigTxn,
    string? InvestTxnType,
    /// <summary>
    /// QIF-origin action tag — verbatim QIF action name MD preserved
    /// when importing from QIF (`Buy`, `ReinvDiv`, `ShrsIn`, ...). Set
    /// only on QIF-imported txns; ADR-0027 documents the secondary
    /// classification source built on top of this.
    /// </summary>
    string? QifInvstAction,
    /// <summary>
    /// MD's serialized original QIF row, present on QIF-imported
    /// txns (bank as well as investment). Combined with
    /// <see cref="QifInvstAction"/> + <see cref="QifSn"/>, drives
    /// the mapper's QIF detection for the mig 107 origin decompose
    /// (any non-null QIF field → <c>file_import / qif</c>).
    /// </summary>
    string? QifOrigTxn,
    /// <summary>QIF source/serial-number tag. Same role as
    /// <see cref="QifOrigTxn"/> for QIF detection.</summary>
    string? QifSn,
    string? XferType,
    bool? Reinvest,
    string? Tags,
    IReadOnlyList<MdSplit> Splits,
    /// <summary>
    /// Verbatim per-row JSON from the MD export — captured at parse
    /// time via MdItem.RawJson. Persisted on
    /// `txn_headers.provider_raw_payload` so any future classifier
    /// refinement is a pure SQL migration over the JSONB column,
    /// instead of needing the original file (ADR-0035 §3, mig 109).
    /// Empty string when MdTxn is constructed by hand (test fixtures).
    /// </summary>
    string RawJson = "")
{
    public static MdTxn From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "txn")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'txn'.", nameof(item));

        var splits = ExtractSplits(item, prefix: string.Empty);

        return new MdTxn(
            Id: item.Id,
            AcctId: item.GetString("acctid") ?? throw new InvalidDataException(
                $"txn {item.Id}: missing required 'acctid'"),
            Description: item.GetString("desc") ?? string.Empty,
            Memo: item.GetString("memo"),
            Date: item.GetInt("dt") ?? throw new InvalidDataException(
                $"txn {item.Id}: missing required 'dt'"),
            TransactedDate: item.GetInt("td"),
            DateEnteredMillis: item.GetLong("dtentered"),
            Status: item.GetString("stat"),
            CheckNumber: NullIfEmpty(item.GetString("chk")),
            OlOrigPayee: item.GetString("ol.orig-payee"),
            OlOrigMemo: item.GetString("ol.orig-memo"),
            OlFiId: item.GetString("ol_fi_id"),
            OlFitid: item.GetString("ol_fitid_1"),
            OlMatchStatus: item.GetString("ol.match-status"),
            OlMatchType: item.GetString("ol.match-type"),
            OlOrigTxn: item.GetString("ol.orig-txn"),
            InvestTxnType: item.GetString("invest.txntype"),
            QifInvstAction: item.GetString("qif_invst_action"),
            QifOrigTxn: item.GetString("qif.orig-txn"),
            QifSn: item.GetString("qif_sn"),
            XferType: item.GetString("xfer_type"),
            Reinvest: ParseReinvest(item.GetString("reinvest")),
            Tags: item.GetString("tags"),
            Splits: splits,
            RawJson: item.RawJson);
    }

    /// <summary>
    /// Walk <c>{prefix}{N}.acctid</c> for N starting at 0 and produce one
    /// <see cref="MdSplit"/> per index until a gap is reached. Used by both
    /// <see cref="MdTxn.From"/> (prefix = empty) and the reminder reader
    /// (prefix = <c>"txn."</c>).
    /// </summary>
    internal static IReadOnlyList<MdSplit> ExtractSplits(MdItem item, string prefix)
    {
        var results = new List<MdSplit>();
        for (var index = 0; index < 32; index++)
        {
            var indexKey = string.Create(CultureInfo.InvariantCulture, $"{prefix}{index}.");
            var acctIdKey = indexKey + "acctid";
            var acctId = item.GetString(acctIdKey);
            if (acctId is null) break;

            var splitId = item.GetString(indexKey + "id") ?? string.Empty;
            var samt = item.GetLong(indexKey + "samt");
            var pamt = item.GetLong(indexKey + "pamt");

            results.Add(new MdSplit(
                Index: index,
                Id: splitId,
                AcctId: acctId,
                SplitAmount: samt ?? 0,
                ParentAmount: pamt ?? 0,
                Description: item.GetString(indexKey + "desc"),
                InvestSplitType: item.GetString(indexKey + "invest.splittype"),
                Status: NullIfEmpty(item.GetString(indexKey + "stat")),
                Tags: NullIfEmpty(item.GetString(indexKey + "tags")),
                OldId: item.GetString(indexKey + "oldid")));
        }
        return results;
    }

    public bool IsInvestmentTxn => !string.IsNullOrEmpty(InvestTxnType);

    /// <summary>
    /// True if this transaction belongs to the investment-mapper pipeline,
    /// either because its <see cref="InvestTxnType"/> tag is set or because
    /// its <see cref="XferType"/> names an investment-only transfer shape.
    /// Real exports include a population of buy/sell/dividend-shaped txns
    /// (likely manually-entered or migrated from another tool) that lack
    /// the txntype tag but still belong to the investment pipeline; the
    /// xfer_type fallback keeps them from being routed to the
    /// non-investment mapper, which can't translate sec splits.
    /// </summary>
    public bool IsInvestmentShape => IsInvestmentTxn || XferType is
        "xfrtp_buysell" or "xfrtp_buysellxfr" or
        "xfrtp_dividend" or "xfrtp_dividendxfr" or
        "xfrtp_miscincexp";

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static bool? ParseReinvest(string? value) => value switch
    {
        null or "" => null,
        "true" or "True" or "TRUE" or "y" or "yes" or "1" => true,
        "false" or "False" or "FALSE" or "n" or "no" or "0" => false,
        _ => null,
    };
}
