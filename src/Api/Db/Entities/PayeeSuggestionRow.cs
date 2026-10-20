namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless query type for the <c>ledger_payee_suggestions(p_ledger_id,
/// p_limit)</c> TVF in migration 027. Materialises one row per
/// distinct resolved payee in the ledger, ranked count-desc then
/// last-used-desc by the function itself. The repository projects
/// straight to <see cref="Contracts.PayeeSuggestion"/>.
/// </summary>
internal sealed class PayeeSuggestionRow
{
    public string Name { get; init; } = string.Empty;
    public long Count { get; init; }
    public DateTime LastUsedAt { get; init; }
}
