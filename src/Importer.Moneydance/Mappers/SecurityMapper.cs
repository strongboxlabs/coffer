using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure translation from a Moneydance <see cref="MdCurr"/> security row, and the
/// security-type parameters of the account that holds it, into a Coffer
/// <see cref="SecurityRow"/>. No I/O. The MD <c>sec_type</c> code is mapped to
/// Coffer's (<c>asset_class</c>, <c>vehicle_type</c>) pair by
/// <see cref="MdSecurityType"/> (ADR-0016, ADR-0067).
/// </summary>
public static class SecurityMapper
{
    /// <summary>
    /// Map a single security-typed <see cref="MdCurr"/> to a <see cref="SecurityRow"/>.
    /// Returns <c>null</c> for non-security entries (plain currency rows like
    /// USD/EUR/KRW); callers are expected to filter on <see cref="MdCurr.IsSecurity"/>
    /// before invoking, but the null guard is defensive.
    /// </summary>
    /// <param name="curr">The security's <c>curr</c> item.</param>
    /// <param name="fields">The <c>sec_type</c>, <c>sec_subtype</c> and
    /// <c>sec_exchange</c> of the type-<c>s</c> account holding it, where Moneydance
    /// writes them; <see cref="MdSecurityFields.None"/> (or null) when no account
    /// carries them, which leaves the security unclassified.</param>
    /// <param name="ledgerId">The ledger the row belongs to.</param>
    public static SecurityRow? Map(MdCurr curr, MdSecurityFields? fields, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(curr);
        if (!curr.IsSecurity) return null;

        fields ??= MdSecurityFields.None;
        var classification = MdSecurityType.Translate(fields.SecType, fields.SecSubtype);

        return new SecurityRow(
            Id: Guid.NewGuid(),
            LedgerId: ledgerId,                              // ADR-0020 Phase A: securities are per-ledger
            Ticker: NullIfEmpty(curr.Ticker),
            Cusip: NullIfEmpty(curr.Cusip),
            Name: string.IsNullOrWhiteSpace(curr.Name) ? "(unnamed)" : curr.Name,
            AssetClass: classification.AssetClass,
            VehicleType: classification.VehicleType,
            // The MD sec_type gives the vehicle reliably but only a best-guess
            // class (a fund's class is unknown), so confidence is 'assumed'
            // (ADR-0067). Coffer owns it after seed (import-once) — the user
            // refines in the editor; re-import doesn't overwrite (the upsert
            // leaves classification on existing rows).
            ClassificationSource: classification.Classified ? "import" : null,
            ClassificationConfidence: classification.Classified ? "assumed" : null,
            Exchange: NullIfEmpty(fields.SecExchange),
            IsActive: !curr.IsHidden,
            ExternalId: curr.Id,
            ShareDecimals: ClampShareDecimals(curr.Decimals));
    }

    /// <summary>
    /// Coerce Moneydance's <c>dec</c> field into the bounded integer the
    /// schema accepts. The CHECK is [0,12] (migration 050, matching the
    /// NUMERIC(25,12) scale of quantity / unit_price / price columns
    /// post-migration 043). MD's value is the security's share precision:
    /// 4 is the stock default, and a fund can carry more (5, 9). Missing
    /// or out-of-range values fall back to 4 (the stock default) so the
    /// importer doesn't reject a security over metadata.
    ///
    /// Why the ceiling is 12, not lower: MD can carry dec=9 for a mutual
    /// fund, and a clamp below that silently rewrites it to 4, which
    /// scales quantities by 10^5 (the five lost decimal places) on every
    /// txn_leg. This clamp and migration 050's CHECK keep the two in step.
    /// </summary>
    private static int ClampShareDecimals(int? mdDecimals) =>
        mdDecimals is { } d && d >= 0 && d <= 12 ? d : 4;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
