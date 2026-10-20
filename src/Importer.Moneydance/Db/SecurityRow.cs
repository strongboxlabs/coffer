namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>securities</c>. Pure data record;
/// translation from a Moneydance <c>MdCurr</c> happens in
/// <see cref="Mappers.SecurityMapper"/>. <see cref="ShareDecimals"/> is
/// the per-security precision for share quantities (Moneydance's
/// <c>dec</c> field) — stocks typically use 4, mutual funds 5; the
/// investment mapper looks it up to scale raw share-quantity integers
/// from the export.
/// </summary>
public sealed record SecurityRow(
    Guid Id,
    Guid LedgerId,
    string? Ticker,
    string? Cusip,
    string Name,
    string? AssetClass,
    string? VehicleType,
    string? ClassificationSource,
    string? ClassificationConfidence,
    string? Exchange,
    bool IsActive,
    string? ExternalId,
    int ShareDecimals);
