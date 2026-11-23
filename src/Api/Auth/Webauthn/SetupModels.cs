using Fido2NetLib;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// JSON response from <c>GET /api/auth/setup/{token}/info</c>. Returned when the
/// bootstrap token is still valid. The page fetches this on mount so an invalid
/// or expired token surfaces immediately rather than after the form is filled
/// and submitted.
/// </summary>
/// <remarks>
/// Deliberately empty (ADR-0088): no ledgers the new user could join, because a
/// fresh install has no real ledgers to offer (migration 186 drops migration
/// 055's empty placeholders). Setup asks nothing about ledgers (ADR-0088, amended by
/// ADR-0115 D2); ledgers are created afterwards from the hub. The endpoint
/// remains because token validation is its real job.
/// </remarks>
/// <param name="RestorePartSizeBytes">
/// The size the setup page must cut a restore artifact into when it is too
/// large to send in one request (ADR-0101), from <c>Api:Backup:PartSizeMb</c>.
/// Carried on this response because the pre-auth page has no other read to get
/// it from, and a hardcoded value would make the setting silently not apply to
/// the one restore path that has no fallback.
/// </param>
public sealed record SetupInfoResponse(long RestorePartSizeBytes);

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
/// <para>Setup asks nothing about ledgers and creates none (ADR-0088, amended by
/// ADR-0115 D2). It creates the user and passkey; the user then lands on the
/// ledger hub, which offers "Import from Moneydance", "Demo ledger" and "New
/// ledger". A user with zero ledgers is a supported state — the hub renders an
/// empty state with those choices, so it is not the dead end the old mandatory
/// ledger choice was guarding against.</para>
/// <para>A client built before that change may still send <c>includeDemo</c>. The
/// field is not read: System.Text.Json ignores a member the type does not declare,
/// so setup completes as usual, creates no ledger, and the user adds a Demo from
/// the hub.</para>
/// </remarks>
public sealed class SetupCompleteRequest
{
    public Guid ChallengeId { get; init; }
    public AuthenticatorAttestationRawResponse AttestationResponse { get; init; } = null!;
    public string CredentialNickname { get; init; } = string.Empty;
}

/// <summary>
/// JSON response from <c>/complete</c>: the user is now logged in (cookie
/// set on the response), and the recovery codes are returned exactly
/// once. Callers MUST surface the codes to the user — they will never
/// appear again.
/// </summary>
/// <remarks>
/// It names no ledger: setup creates none (ADR-0088, amended by ADR-0115 D2).
/// </remarks>
public sealed record SetupCompleteResponse(
    Guid UserId,
    string Username,
    Guid SessionId,
    DateTime SessionExpiresAt,
    IReadOnlyList<string> RecoveryCodes,
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
