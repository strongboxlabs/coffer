namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>security_components</c> (migration 150, ADR-0067) — multi-asset
/// look-through. One row per (security, sleeve): the percent (0-100) of a wrapper
/// in a given asset class + region. Allocation decomposes <c>asset_class =
/// 'multi_asset'</c> securities through these instead of counting them 100% as
/// multi-asset (mig 153 retired the separate needs_look_through flag).
/// </summary>
internal sealed class SecurityComponentRow
{
    public Guid Id { get; init; }
    public Guid SecurityId { get; set; }
    public string ComponentAssetClass { get; set; } = string.Empty;
    public string? ComponentRegion { get; set; }
    public decimal Weight { get; set; }
    public DateTime CreatedAt { get; init; }
}
