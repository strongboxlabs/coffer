using Microsoft.AspNetCore.Http;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// "Prove a human with an enrolled authenticator is here right now" — the step-up
/// every deployment-secret disclosure, and every action that grants lasting access or
/// replaces data, sits behind (ADR-0092 D2, D7; 2026-10-12 audit, M2).
/// </summary>
/// <remarks>
/// <para>The session cookie only proves an admin authenticated some time in the
/// last 30 days (ADR-0013). Acting on that alone would make a stolen still-valid
/// cookie enough; a fresh assertion turns it into a dead end.</para>
///
/// <para>Shared rather than duplicated per endpoint <b>because</b> it is a security
/// check: two copies drift, and a weaker copy on any one surface becomes the way to
/// reach a secret with only a cookie. Every caller gets the same gates — the
/// challenge is single-use, scoped to its own flow, owned by the caller; the
/// asserting credential belongs to the caller, <b>predates the session</b>, and the
/// authenticator <b>verified the user</b> (PIN or biometric).</para>
///
/// <para><b>Why "predates the session".</b> Enrolling a passkey used to need only the
/// cookie, so a stolen cookie could enrol a software key and then pass every step-up
/// with it — the gate checked that the credential was the caller's, and it was.
/// Accepting only passkeys enrolled at or before the session began means a cookie
/// can never manufacture its own way through. A passkey added during this session
/// works from the next sign-in made with it. (<c>&lt;=</c>, not <c>&lt;</c>: setup and
/// invite stamp the passkey and then issue the session, so the passkey is earlier, but
/// storage rounds both to microseconds and a test clock can stamp both with one
/// instant. Rounding never reorders them, so <c>&lt;=</c> keeps the rule exact.)</para>
///
/// <para><b>Why user verification.</b> "Preferred" let a security key with no PIN
/// approve on touch alone, so a stolen key could approve a restore. Ordinary sign-in
/// keeps "preferred"; only these taps require it. The library enforces it from the
/// options, and the gate also reads the UV bit from the authenticator data itself, so
/// the rule survives a library or options change.</para>
///
/// <para>The assertion authorizes exactly ONE response. No step-up session flag is
/// minted, so there is no elevated window to leak.</para>
/// </remarks>
public sealed class FreshAssertionGate
{
    /// <summary>Challenge lifetime. Short — the user is looking at the prompt.</summary>
    public static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    /// <summary>The User Verified bit in the authenticator data's flags byte (WebAuthn §6.1).</summary>
    private const byte UserVerifiedFlag = 0x04;

    /// <summary>Offset of the flags byte: it follows the 32-byte rpIdHash.</summary>
    private const int FlagsOffset = 32;

    private readonly ICurrentUserAccessor _currentUser;
    private readonly IHttpContextAccessor _http;
    private readonly CredentialsRepository _credentials;
    private readonly ChallengeStore _challenges;
    private readonly IWebAuthnService _webauthn;
    private readonly IOptions<ApiOptions> _apiOptions;

    public FreshAssertionGate(
        ICurrentUserAccessor currentUser,
        IHttpContextAccessor http,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        IOptions<ApiOptions> apiOptions)
    {
        _currentUser = currentUser;
        _http = http;
        _credentials = credentials;
        _challenges = challenges;
        _webauthn = webauthn;
        _apiOptions = apiOptions;
    }

    /// <summary>Either a failure to return, or the ceremony payload to hand the client.</summary>
    public readonly record struct BeginResult(
        IResult? Failure,
        Guid ChallengeId,
        AssertionOptions? Options);

    /// <summary>Either a failure to return, or the verified credential.</summary>
    public readonly record struct VerifyResult(
        IResult? Failure,
        WebAuthnCredentialRow? Credential);

    /// <summary>
    /// When the caller's session began, or null for a principal that is not a cookie
    /// session (which the age rule then cannot apply to).
    /// </summary>
    private DateTime? SessionStart =>
        _http.HttpContext is { } context ? SessionClaims.CreatedAtOf(context.User) : null;

    private static bool PredatesSession(WebAuthnCredentialRow credential, DateTime? sessionStart) =>
        sessionStart is null || credential.CreatedAt.ToUniversalTime() <= sessionStart.Value;

    /// <summary>
    /// Start a ceremony for <paramref name="flow"/>, scoped to the calling user's own
    /// credentials that predate the session.
    /// </summary>
    public async Task<BeginResult> BeginAsync(string flow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        var userId = _currentUser.UserId;
        var sessionStart = SessionStart;

        // Offer credentials for the current RP id, PLUS any with a null one. Null
        // means "enrolled before the column existed", not "wrong domain" — filtering
        // those out would make step-up permanently unreachable on an older install
        // whose passkeys all predate it. A credential from a KNOWN different RP
        // (domain rename, cross-install restore) stays excluded: its rpIdHash can't
        // match, so offering it would only produce an unexplainable prompt failure.
        var rpId = _apiOptions.Value.Fido2.RpId;
        var forThisDomain = (await _credentials.GetByUserAsync(userId, cancellationToken).ConfigureAwait(false))
            .Where(c => c.RpId == rpId || c.RpId is null)
            .ToList();

        if (forThisDomain.Count == 0)
            // Reachable after a recovery-code login on an install whose passkeys all
            // predate a domain change. Say so plainly rather than issuing a ceremony
            // that cannot succeed.
            return new(BusinessError.Problem(BusinessError.Codes.MasterKeyNoCredentials,
                "No passkey registered for this domain is available to confirm your identity. "
                + "Add a passkey for this domain first, then retry."), Guid.Empty, null);

        var usable = forThisDomain
            .Where(c => PredatesSession(c, sessionStart))
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        if (usable.Count == 0)
            // Every passkey for this domain was added during this session.
            return new(BusinessError.Problem(BusinessError.Codes.StepUpNoEligiblePasskey,
                "This needs a passkey you had before you signed in. Sign out, sign in again "
                + "with the passkey you just added, then retry."), Guid.Empty, null);

        var options = _webauthn.BeginAssertion(usable, requireUserVerification: true);
        var challengeId = await _challenges.SaveAsync(
            flow,
            userId: userId,
            optionsJson: options.ToJson(),
            metadataJson: null,
            ttl: ChallengeTtl,
            cancellationToken).ConfigureAwait(false);

        return new(null, challengeId, options);
    }

    /// <summary>
    /// Consume the challenge, confirm the credential belongs to the caller and predates
    /// the session, verify the signature and user verification, and persist the new
    /// counter. Every failure is a 401 or a business error — never a partial success.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(
        string flow,
        Guid challengeId,
        AuthenticatorAssertionRawResponse? assertionResponse,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);

        if (assertionResponse is null)
            return new(BusinessError.Problem(BusinessError.Codes.MasterKeyAssertionRequired,
                "assertionResponse is required."), null);

        var userId = _currentUser.UserId;

        // Single-use, flow-scoped, TTL-bounded. The flow check is what stops a
        // challenge minted by /login/begin — or by a DIFFERENT step-up ceremony —
        // being redeemed here.
        var challenge = await _challenges
            .ConsumeAsync(challengeId, flow, cancellationToken).ConfigureAwait(false);
        if (challenge is null || challenge.UserId != userId)
            // Includes the cross-user case: a challenge minted for someone else,
            // replayed by this admin. Both are auth failures.
            return new(Results.Problem("Re-authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized), null);

        var credential = await _credentials
            .GetByCredentialIdAsync(assertionResponse.RawId, cancellationToken)
            .ConfigureAwait(false);
        if (credential is null || credential.UserId != userId)
            // The credential must belong to the CALLER, not merely exist. Without
            // this, any enrolled user's authenticator would unlock an admin's secret.
            return new(Results.Problem("Re-authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized), null);

        if (!PredatesSession(credential, SessionStart))
            // The ceremony only OFFERED older passkeys, but the browser can answer with
            // any credential the user holds; the rule is enforced here, not trusted.
            return new(BusinessError.Problem(BusinessError.Codes.StepUpNoEligiblePasskey,
                "This needs a passkey you had before you signed in. Sign out, sign in again "
                + "with the passkey you just added, then retry."), null);

        try
        {
            var outcome = await _webauthn.CompleteAssertionAsync(
                assertionResponse,
                AssertionOptions.FromJson(challenge.OptionsJson),
                storedPublicKey: credential.PublicKey,
                storedSignatureCounter: (uint)credential.SignatureCounter,
                isUserHandleOwnerOfCredentialId: (args, ct) =>
                    Task.FromResult(credential.UserId.ToByteArray().SequenceEqual(args.UserHandle)),
                cancellationToken).ConfigureAwait(false);

            if (!UserWasVerified(assertionResponse))
                return new(BusinessError.Problem(BusinessError.Codes.StepUpNotVerified,
                    "This action needs your passkey's PIN or fingerprint/face check, not just a "
                    + "touch. Use a passkey that supports it."), null);

            if (!await _credentials.UpdateAfterAssertionAsync(
                    credential.Id, credential.SignatureCounter, outcome.NewSignatureCounter, cancellationToken)
                .ConfigureAwait(false))
                return new(Results.Problem(
                    detail: WebAuthnFailures.Assertion,
                    title: "WebAuthn assertion failed verification.",
                    statusCode: StatusCodes.Status401Unauthorized), null);
        }
        catch (Fido2VerificationException ex)
        {
            WebAuthnFailures.Log(_http.HttpContext!.RequestServices, ex, "step-up assertion");
            return new(Results.Problem(
                detail: WebAuthnFailures.Assertion,
                title: "WebAuthn assertion failed verification.",
                statusCode: StatusCodes.Status401Unauthorized), null);
        }

        return new(null, credential);
    }

    /// <summary>
    /// The UV bit of the (now signature-verified) authenticator data. Read directly so
    /// the requirement does not rest on the options alone.
    /// </summary>
    private static bool UserWasVerified(AuthenticatorAssertionRawResponse assertion)
    {
        var authenticatorData = assertion.Response?.AuthenticatorData;
        return authenticatorData is { Length: > FlagsOffset }
            && (authenticatorData[FlagsOffset] & UserVerifiedFlag) != 0;
    }
}
