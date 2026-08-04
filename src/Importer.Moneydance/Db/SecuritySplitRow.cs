namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>security_splits</c>. One row per
/// stock-split corporate action. <see cref="Ratio"/> is the multiplier
/// applied to running quantity at <see cref="SplitAt"/>;
/// <see cref="OldShares"/> / <see cref="NewShares"/> are audit fields
/// preserved for round-trip with Moneydance's <c>csplit</c> object.
/// </summary>
public sealed record SecuritySplitRow(
    Guid Id,
    Guid LedgerId,
    Guid SecurityId,
    DateTimeOffset SplitAt,
    decimal Ratio,
    decimal? OldShares,
    decimal? NewShares,
    string? ExternalId);
