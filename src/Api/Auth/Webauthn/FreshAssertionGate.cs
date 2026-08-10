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
/// every deployment-secret disclosure sits behind (ADR-0092 D2, D7).
/// </summary>
/// <remarks>
/// <para>The session cookie only proves an admin authenticated some time in the
/// last 30 days (ADR-0013). Handing out key material on that alone would make a
/// stolen still-valid cookie enough; a fresh assertion turns it into a dead
/// end.</para>
///
/// <para>Shared rather than duplicated per endpoint <b>because</b> it is a security
/// check: two copies drift, and a weaker copy on any one surface becomes the way to
/// reach a secret with only a cookie. Every caller gets the same four gates — the
/// challenge is single-use, scoped to its own flow, owned by the caller, and the
/// asserting credential belongs to the caller too.</para>
///
/// <para>The assertion authorizes exactly ONE response. No step-up session flag is
/// minted, so there is no elevated window to leak.</para>
/// </remarks>
public sealed class FreshAssertionGate
{
    /// <summary>Challenge lifetime. Short — the user is looking at the prompt.</summary>
    public static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    private readonly ICurrentUserAccessor _currentUser;
    private readonly CredentialsRepository _credentials;
    private readonly ChallengeStore _challenges;
    private readonly IWebAuthnService _webauthn;
    private readonly IOptions<ApiOptions> _apiOptions;

    public FreshAssertionGate(
        ICurrentUserAccessor currentUser,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        IOptions<ApiOptions> apiOptions)
    {
        _currentUser = currentUser;
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
    /// Start a ceremony for <paramref name="flow"/>, scoped to the calling user's own
    /// credentials.
    /// </summary>
    public async Task<BeginResult> BeginAsync(string flow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        var userId = _currentUser.UserId;

        // Offer credentials for the current RP id, PLUS any with a null one. Null
        // means "enrolled before the column existed", not "wrong domain" — filtering
        // those out would make step-up permanently unreachable on an older install
        // whose passkeys all predate it. A credential from a KNOWN different RP
        // (domain rename, cross-install restore) stays excluded: its rpIdHash can't
        // match, so offering it would only produce an unexplainable prompt failure.
        var rpId = _apiOptions.Value.Fido2.RpId;
        var usable = (await _credentials.GetByUserAsync(userId, cancellationToken).ConfigureAwait(false))
            .Where(c => c.RpId == rpId || c.RpId is null)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        if (usable.Count == 0)
            // Reachable after a recovery-code login on an install whose passkeys all
            // predate a domain change. Say so plainly rather than issuing a ceremony
            // that cannot succeed.
            return new(BusinessError.Problem(BusinessError.Codes.MasterKeyNoCredentials,
                "No passkey registered for this domain is available to confirm your identity. "
                + "Add a passkey for this domain first, then retry."), Guid.Empty, null);

        var options = _webauthn.BeginAssertion(usable);
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
    /// Consume the challenge, confirm the credential belongs to the caller, verify the
    /// signature, and persist the new counter. Every failure is a 401 or a business
    /// error — never a partial success.
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

            await _credentials.UpdateAfterAssertionAsync(
                credential.Id, outcome.NewSignatureCounter, cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            return new(Results.Problem(
                detail: ex.Message,
                title: "WebAuthn assertion failed verification.",
                statusCode: StatusCodes.Status401Unauthorized), null);
        }

        return new(null, credential);
    }
}
