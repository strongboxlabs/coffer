namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>webauthn_credentials</c>. Mirrors the
/// schema column-for-column; the registration endpoint translates between
/// this and Fido2.AspNet's <c>StoredCredential</c> at the boundary so the
/// rest of the codebase doesn't depend on the library type.
/// </summary>
/// <remarks>
/// Class with init-only setters rather than a positional record because
/// the original Dapper-based code path needed property-setter
/// materialisation (constructor lookup struggled with nullable
/// annotations). EF Core also prefers properties; the shape stays the
/// same after the EF migration in PR 3.6.5.
/// </remarks>
public sealed class WebAuthnCredentialRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public byte[] CredentialId { get; init; } = Array.Empty<byte>();
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();
    public long SignatureCounter { get; init; }
    public Guid? Aaguid { get; init; }
    public string[]? Transports { get; init; }
    public string Nickname { get; init; } = string.Empty;
    /// <summary>
    /// WebAuthn Relying Party ID (the domain) this credential was registered
    /// against. NULL for rows created before migration 157 (unknown RP). A
    /// credential is only usable for login on the current RP, so registration
    /// excludes only same-RP credentials — one from a prior RP (domain rename /
    /// ADR-0061 restore) no longer blocks re-enrolling the same authenticator.
    /// </summary>
    public string? RpId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}
