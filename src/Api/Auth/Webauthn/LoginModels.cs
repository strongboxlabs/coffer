using Fido2NetLib;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// JSON request body for <c>POST /api/auth/login/begin</c>. Username
/// drives the credential lookup; the browser receives only the
/// credentials registered to that user so the authenticator picker
/// shows relevant entries.
/// </summary>
public sealed class LoginBeginRequest
{
    public string Username { get; init; } = string.Empty;
}

/// <summary>
/// JSON response from <c>/login/begin</c>: the Fido2 options the browser
/// feeds to <c>navigator.credentials.get()</c>, plus the challenge id to
/// echo back at <c>/complete</c>.
/// </summary>
public sealed record LoginBeginResponse(Guid ChallengeId, AssertionOptions Options);

/// <summary>
/// JSON request body for <c>POST /api/auth/login/complete</c>.
/// </summary>
public sealed class LoginCompleteRequest
{
    public Guid ChallengeId { get; init; }
    public AuthenticatorAssertionRawResponse AssertionResponse { get; init; } = null!;
}

/// <summary>
/// JSON response from <c>/login/complete</c>: the user is now logged in
/// (cookie set on the response). Returns the basic identity so the
/// browser can render "logged in as alice" without an extra round-trip.
/// </summary>
public sealed record LoginCompleteResponse(
    Guid UserId,
    string Username,
    Guid SessionId,
    DateTime SessionExpiresAt);

/// <summary>
/// JSON request body for <c>POST /api/auth/login/recovery</c> — the
/// account-recovery fallback when no passkey can be used (lost
/// authenticator, or a restored DB whose passkeys were bound to a
/// different RP id, per ADR-0061). The single-use recovery code stands
/// in for the assertion; success issues a session like a normal login.
/// </summary>
public sealed class RecoveryLoginRequest
{
    public string Username { get; init; } = string.Empty;
    public string RecoveryCode { get; init; } = string.Empty;
}
