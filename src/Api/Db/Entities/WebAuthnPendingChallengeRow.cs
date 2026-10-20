namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>webauthn_pending_challenges</c>. Holds
/// the JSON-serialised options the browser ceremony returned at
/// <c>/begin</c> so <c>/complete</c> can verify the matching response.
/// </summary>
public sealed class WebAuthnPendingChallengeRow
{
    public Guid Id { get; init; }
    public string Flow { get; init; } = string.Empty;
    public Guid? UserId { get; init; }
    public string OptionsJson { get; init; } = string.Empty;
    public string? MetadataJson { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? ConsumedAt { get; init; }
}
