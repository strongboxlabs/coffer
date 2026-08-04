using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure translation from a Moneydance <see cref="MdCurr"/> security row into
/// a Coffer <see cref="SecurityRow"/>. No I/O. The MD <c>sec_type</c> values
/// observed in real-world exports are mapped to Coffer's <c>asset_class</c> enum
/// per ADR-0016.
/// </summary>
public static class SecurityMapper
{
    /// <summary>
    /// Map a single security-typed <see cref="MdCurr"/> to a <see cref="SecurityRow"/>.
    /// Returns <c>null</c> for non-security entries (plain currency rows like
    /// USD/EUR/KRW); callers are expected to filter on <see cref="MdCurr.IsSecurity"/>
    /// before invoking, but the null guard is defensive.
    /// </summary>
    public static SecurityRow? Map(MdCurr curr, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(curr);
        if (!curr.IsSecurity) return null;

        var (assetClass, vehicleType) = TranslateSecType(curr.SecType);
        var classified = !string.IsNullOrEmpty(curr.SecType);

        return new SecurityRow(
            Id: Guid.NewGuid(),
            LedgerId: ledgerId,                              // ADR-0020 Phase A: securities are per-ledger
            Ticker: NullIfEmpty(curr.Ticker),
            Cusip: NullIfEmpty(curr.Cusip),
            Name: string.IsNullOrWhiteSpace(curr.Name) ? "(unnamed)" : curr.Name,
            AssetClass: assetClass,
            VehicleType: vehicleType,
            // The MD sec_type gives the vehicle reliably but only a best-guess
            // class (a fund's class is unknown), so confidence is 'assumed'
            // (ADR-0067). Coffer owns it after seed (import-once) — the user
            // refines in the editor; re-import doesn't overwrite (the upsert
            // leaves classification on existing rows).
            ClassificationSource: classified ? "import" : null,
            ClassificationConfidence: classified ? "assumed" : null,
            Exchange: NullIfEmpty(curr.SecExchange),
            IsActive: !curr.IsHidden,
            ExternalId: curr.Id,
            ShareDecimals: ClampShareDecimals(curr.Decimals));
    }

    /// <summary>
    /// Coerce Moneydance's <c>dec</c> field into the bounded integer the
    /// schema accepts. The CHECK is [0,12] (migration 050, matching the
    /// NUMERIC(25,12) scale of quantity / unit_price / price columns
    /// post-migration 043). MD's values are typically 4 (stocks/ETFs),
    /// 5 (some mutual funds), or 9 (some fund families' admiral
    /// shares). Missing or out-of-range values fall back to 4 (the
    /// stock default) so the importer doesn't reject a security over
    /// metadata.
    ///
    /// HISTORICAL: until migration 050 the ceiling was 6. Real exports
    /// carry dec=9 for some mutual funds; the old clamp silently
    /// rewrote those to 4 and produced 100,000x-scaled quantities on
    /// every txn_leg. Affected rows on the dev DB were scrubbed
    /// 2026-05-19; this clamp + migration 050 ensure a future
    /// re-import doesn't recreate the problem.
    /// </summary>
    private static int ClampShareDecimals(int? mdDecimals) =>
        mdDecimals is { } d && d >= 0 && d <= 12 ? d : 4;

    /// <summary>
    /// Translate a Moneydance <c>sec_type</c> into the orthogonal (asset_class,
    /// vehicle_type) pair (ADR-0067). sec_type reliably gives the VEHICLE; the
    /// economic class is only derivable for single-instrument types (a fund's
    /// class is unknown → null, set later in the editor). Replaces the old
    /// vehicle-in-asset_class mapping (the core modeling bug this slice fixes).
    /// </summary>
    public static (string? AssetClass, string? VehicleType) TranslateSecType(string? secType) => secType switch
    {
        null or ""        => (null, null),
        "Mutual Fund"     => (null, "mutual_fund"),     // fund — class unknown
        "ETF"             => (null, "etf"),             // fund — class unknown
        "Stock"           => ("equity", "stock"),
        "Bond"            => ("fixed_income", "bond"),
        "CD"              => ("cash", "cd"),
        "Money Market"    => ("cash", "money_market"),
        "Option"          => ("alternative", "option"),
        _                 => (null, "other"),
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
