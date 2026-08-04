using Fido2NetLib;

namespace Coffer.Api.Auth.Webauthn;

// DTOs for the authenticated account self-service surface (ADR-0013
// follow-through): adding/listing/removing passkeys and regenerating
// recovery codes. Distinct from the bootstrap setup ceremony — the user
// already exists and is signed in.

/// <summary>
/// JSON response from <c>POST /api/auth/register/begin</c>: the Fido2
/// options the browser feeds to <c>navigator.credentials.create()</c>,
/// plus the challenge id to echo at <c>/register/complete</c>.
/// </summary>
public sealed record RegisterBeginResponse(Guid ChallengeId, CredentialCreateOptions Options);

/// <summary>
/// JSON request body for <c>POST /api/auth/register/complete</c> — the
/// attestation for the new passkey plus a friendly label to store
/// alongside it (so the manage-passkeys list can disambiguate).
/// </summary>
public sealed class RegisterCompleteRequest
{
    public Guid ChallengeId { get; init; }
    public AuthenticatorAttestationRawResponse AttestationResponse { get; init; } = null!;
    public string CredentialNickname { get; init; } = string.Empty;
}

/// <summary>
/// One row in <c>GET /api/auth/credentials</c>: a passkey the current
/// user owns. No key material — just what the manage-passkeys UI needs to
/// list and disambiguate them.
/// </summary>
public sealed record CredentialSummary(
    Guid Id, string Nickname, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// JSON response from <c>GET /api/auth/recovery-codes</c>: how many
/// single-use codes remain (never the codes themselves — only hashes are
/// stored). Drives the "N of M remaining" hint and the regenerate prompt.
/// </summary>
public sealed record RecoveryCodesStatusResponse(int Remaining, int Total);

/// <summary>
/// JSON response from <c>POST /api/auth/recovery-codes/regenerate</c>:
/// the fresh plaintext codes, returned exactly once (the old set is
/// invalidated). The caller MUST surface them and require acknowledgement
/// before navigating away — they never appear again.
/// </summary>
public sealed record RegenerateRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
