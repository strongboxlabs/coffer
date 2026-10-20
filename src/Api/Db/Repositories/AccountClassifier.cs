namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The single definition of which account types are assets vs liabilities
/// (ADR-0056). Shared by the overview, net worth, and accounts reporting so the
/// classification never diverges. Categories and the holdings-sibling shadow
/// accounts fall outside both sets ("none").
/// </summary>
public static class AccountClassifier
{
    public static readonly string[] AssetTypes = { "bank", "cash", "investment", "asset" };
    public static readonly string[] LiabilityTypes = { "credit_card", "liability", "loan" };

    /// <summary>"asset", "liability", or "none" (categories, siblings).</summary>
    public static string Classify(string accountType) =>
        AssetTypes.Contains(accountType) ? "asset"
        : LiabilityTypes.Contains(accountType) ? "liability"
        : "none";
}
