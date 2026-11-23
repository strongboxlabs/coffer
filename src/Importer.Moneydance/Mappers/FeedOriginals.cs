using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Decides whether a mapped header gets a <c>txn_header_originals</c> seed
/// (FU-024), for both the bank and the investment route.
/// </summary>
/// <remarks>
/// <para>Both mappers write the user's curated <c>desc</c> / <c>memo</c> and
/// fall back to Moneydance's <c>ol.orig-payee</c> / <c>ol.orig-memo</c> only
/// when the curated value is blank. When a non-blank feed value differs from
/// what was written, the transaction is in the state the API's first edit
/// leaves behind — curated on the canonical row, feed in the sidecar — so the
/// sidecar is seeded to match. When nothing differs no row is written: an
/// originals row is what <c>resolved_transactions.has_overrides</c> reports,
/// and a row whose feed agrees must not read as one that differs.</para>
///
/// <para>The payee is compared with the header's. The memo is compared with
/// the transaction's memo wherever it was written: the header of several
/// postings, the home account's leg of one. Memos are stored trimmed, so the
/// memo comparison is over trimmed text; the seed keeps the feed's value as
/// Moneydance holds it. The comparison is ordinal and against the values
/// ACTUALLY written, so the blank-curated fallback is "no difference" by
/// construction.</para>
/// </remarks>
public static class FeedOriginals
{
    /// <summary>
    /// The seed for <paramref name="header"/>, mapped from
    /// <paramref name="txn"/>, or <c>null</c> when neither feed value differs
    /// from the written one. A side whose feed value is blank carries the
    /// written value, as the API's whole-row capture would. The amount is the
    /// transaction's amount on its originating account as written, the sum of
    /// <paramref name="legs"/> there (ADR-0114 D15): a split's several lines on
    /// that account add up, and the other side of each posting, a Holdings
    /// sibling's legs included, is not counted.
    /// </summary>
    /// <param name="writtenMemo">The transaction's memo where it was written
    /// (<see cref="Db.StampedNotes.TransactionMemo"/>).</param>
    /// <param name="legs">The header's legs as stamped.</param>
    public static HeaderOriginalsSeed? SeedFor(
        MdTxn txn, TxnHeaderRow header, string? writtenMemo, IReadOnlyList<TxnLegRow> legs)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(legs);

        var feedPayee = NullIfEmpty(txn.OlOrigPayee);
        var feedMemo  = NullIfEmpty(txn.OlOrigMemo);

        var payeeDiffers = feedPayee is not null && !string.Equals(feedPayee, header.Payee, StringComparison.Ordinal);
        var memoDiffers  = feedMemo  is not null
            && !string.Equals(AnnotationText.NormalizeMemo(feedMemo), AnnotationText.NormalizeMemo(writtenMemo), StringComparison.Ordinal);
        if (!payeeDiffers && !memoDiffers) return null;

        return new HeaderOriginalsSeed(
            HeaderId: header.Id,
            Payee:    feedPayee ?? header.Payee,
            Memo:     feedMemo  ?? writtenMemo,
            Amount:   AmountOnOriginatingAccount(header, legs));
    }

    /// <summary>The sum of <paramref name="legs"/> on the header's originating
    /// account; null when it has none, or no leg there.</summary>
    private static decimal? AmountOnOriginatingAccount(TxnHeaderRow header, IReadOnlyList<TxnLegRow> legs)
    {
        if (header.OriginatingAccountId is not { } home) return null;
        decimal? sum = null;
        foreach (var leg in legs)
        {
            if (leg.HeaderId != header.Id || leg.AccountId != home) continue;
            sum = (sum ?? 0m) + leg.Amount;
        }
        return sum;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
