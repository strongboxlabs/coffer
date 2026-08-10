using Fido2NetLib;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// JSON response from <c>GET /api/auth/setup/{token}/info</c>. Returned when the
/// bootstrap token is still valid. The page fetches this on mount so an invalid
/// or expired token surfaces immediately rather than after the form is filled
/// and submitted.
/// </summary>
/// <remarks>
/// Deliberately empty (ADR-0088). This used to carry the ledgers the new user
/// could join, but a fresh install has no real ledgers to offer — the rows it
/// listed were empty placeholders from migration 055, now dropped by migration
/// 186. Install shape is a single question on the form ("include a Demo
/// ledger?"), and ledgers are created afterwards from the hub. The endpoint
/// remains because token validation is its real job.
/// </remarks>
public sealed record SetupInfoResponse();

/// <summary>
/// JSON request body for <c>POST /api/auth/setup/{token}/begin</c>. The
/// caller proposes the username and human-readable display name they
/// want; the server creates them only on /complete to avoid orphan rows
/// from abandoned ceremonies.
/// </summary>
public sealed class SetupBeginRequest
{
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// JSON response from <c>/begin</c>: the Fido2 options the browser feeds
/// to <c>navigator.credentials.create()</c>, plus the challenge id to
/// echo at <c>/complete</c>.
/// </summary>
/// <remarks>
/// CredentialCreateOptions serialises to a sizeable blob; the response
/// matches what the Fido2NetLib browser shim expects so a vanilla
/// fetch + navigator.credentials.create is enough on the client.
/// </remarks>
public sealed record SetupBeginResponse(Guid ChallengeId, CredentialCreateOptions Options);

/// <summary>
/// JSON request body for <c>POST /api/auth/setup/{token}/complete</c>.
/// </summary>
/// <remarks>
/// <para>Setup no longer asks which ledger to use (ADR-0088). It creates the
/// user and passkey, and optionally a Demo ledger; the user then lands on the
/// ledger hub, which offers "New ledger" and "Import from Moneydance". A user
/// with zero ledgers is a supported state — the hub renders an empty state with
/// both calls to action, so it is not the dead end the old mandatory ledger
/// choice was guarding against.</para>
/// </remarks>
public sealed class SetupCompleteRequest
{
    public Guid ChallengeId { get; init; }
    public AuthenticatorAttestationRawResponse AttestationResponse { get; init; } = null!;
    public string CredentialNickname { get; init; } = string.Empty;

    /// <summary>
    /// Create a Demo ledger seeded with the bundled sample dataset, owned by the
    /// new user. Seeded post-commit and best-effort: setup has already succeeded
    /// by then, so a slow or failing import never costs the passkey registration.
    /// </summary>
    public bool IncludeDemo { get; init; }
}

/// <summary>
/// JSON response from <c>/complete</c>: the user is now logged in (cookie
/// set on the response), and the recovery codes are returned exactly
/// once. Callers MUST surface the codes to the user — they will never
/// appear again.
/// </summary>
/// <remarks>
/// <see cref="LedgerId"/> / <see cref="LedgerName"/> are null unless a Demo
/// ledger was requested AND its seed succeeded (ADR-0088) — setup itself no
/// longer creates a ledger. Callers must treat "no ledger" as the normal case.
/// </remarks>
public sealed record SetupCompleteResponse(
    Guid UserId,
    string Username,
    Guid SessionId,
    DateTime SessionExpiresAt,
    IReadOnlyList<string> RecoveryCodes,
    Guid? LedgerId,
    string? LedgerName,
    /// <summary>
    /// The install's master KEK, base64 (ADR-0092 D2). Startup minted it on this
    /// virgin install; setup is where the operator is first shown it, because it is
    /// the one moment they are present, verified, and paying attention.
    ///
    /// Returned here for the same reason <see cref="RecoveryCodes"/> is, and under
    /// the same gate: the bootstrap token only exists before the first user, so
    /// this is a one-time first-run payload. It is also the LESS sensitive of the
    /// two — recovery codes bypass passkey auth entirely, whereas losing this costs
    /// three re-establishable secrets. Requiring a second ceremony for the weaker
    /// secret would be incoherent, and would risk the operator never seeing it if
    /// that ceremony failed.
    ///
    /// Unlike the recovery codes, it CAN be seen again — System → Encryption reveals
    /// it behind a fresh assertion.
    /// </summary>
    string MasterKeyBase64);
