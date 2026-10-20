namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>user_preferences</c> (ADR-0057 / mig 134). One row per
/// (user, ledger, namespace); <see cref="ValueJson"/> is a namespace-typed JSON
/// document. The general per-(user, ledger) preference store.
/// </summary>
internal sealed class UserPreferenceRow
{
    public Guid UserId { get; init; }
    public Guid LedgerId { get; init; }
    public string Namespace { get; init; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
}
