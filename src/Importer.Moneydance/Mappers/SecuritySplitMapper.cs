using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure translation from a Moneydance <see cref="MdCsplit"/> stock-split event
/// into a Coffer <see cref="SecuritySplitRow"/>. csplit items reference a
/// security via <see cref="MdCsplit.CurrId"/>; events whose curr doesn't
/// resolve to a known security in the current ledger are skipped (the security
/// import step would have skipped the parent security too).
/// </summary>
public static class SecuritySplitMapper
{
    public enum SkipReason
    {
        UnknownSecurity,        // CurrId doesn't resolve to a known security
        UnparseableDate,        // dt + ts both unusable
        InvalidRatio,           // ratio <= 0
    }

    public sealed record MapResult(SecuritySplitRow? Row, SkipReason? Skip);

    public static MapResult Map(
        MdCsplit csplit,
        IReadOnlyDictionary<string, SecurityRef> securityByMdId,
        Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(csplit);
        ArgumentNullException.ThrowIfNull(securityByMdId);

        if (!securityByMdId.TryGetValue(csplit.CurrId, out var securityRef))
            return new MapResult(null, SkipReason.UnknownSecurity);

        if (csplit.Ratio <= 0m)
            return new MapResult(null, SkipReason.InvalidRatio);

        var splitAt = ResolveSplitAt(csplit);
        if (splitAt is null) return new MapResult(null, SkipReason.UnparseableDate);

        return new MapResult(new SecuritySplitRow(
            Id:         Guid.NewGuid(),
            LedgerId:   ledgerId,
            SecurityId: securityRef.Id,
            SplitAt:    splitAt.Value,
            Ratio:      csplit.Ratio,
            OldShares:  csplit.OldShares,
            NewShares:  csplit.NewShares,
            ExternalId: csplit.Id), Skip: null);
    }

    /// <summary>
    /// Prefer the precise <c>ts</c> millis (sub-day ordering) when present;
    /// fall back to the integer <c>dt</c> field (yyyymmdd, midnight UTC).
    /// </summary>
    private static DateTimeOffset? ResolveSplitAt(MdCsplit csplit)
    {
        if (csplit.TimestampMillis is { } millis && millis > 0)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(millis); }
            catch (ArgumentOutOfRangeException) { /* fall through */ }
        }
        return TransactionMapper.ParseMdDate(csplit.Date == 0 ? null : csplit.Date);
    }
}
