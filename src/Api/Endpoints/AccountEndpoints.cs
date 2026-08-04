using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Authenticated account self-service (ADR-0013 follow-through): the
/// <c>/api/auth/register/*</c> passkey-add ceremony the setup code always
/// referenced, listing/removing passkeys, and regenerating recovery
/// codes. Every endpoint here operates on the current user only and
/// requires a session. Together with <c>POST /api/auth/login/recovery</c>
/// these make a lost/stale authenticator recoverable — notably after a
/// restore onto a new RP id (ADR-0061), which invalidates every passkey.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>
    /// How long an in-flight add-passkey challenge is valid. Matches the
    /// other ceremonies — short enough that a stale challenge must be
    /// retried, not replayed.
    /// </summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        // All authenticated, current-user-scoped.
        var register = routes.MapGroup("/api/auth/register").RequireAuthorization();
        register.MapPost("/begin", RegisterBeginAsync);
        register.MapPost("/complete", RegisterCompleteAsync);

        var credentials = routes.MapGroup("/api/auth/credentials").RequireAuthorization();
        credentials.MapGet("/", ListCredentialsAsync);
        credentials.MapDelete("/{id:guid}", DeleteCredentialAsync);

        var recovery = routes.MapGroup("/api/auth/recovery-codes").RequireAuthorization();
        recovery.MapGet("/", RecoveryCodesStatusAsync);
        recovery.MapPost("/regenerate", RegenerateRecoveryCodesAsync);

        return routes;
    }

    // --- Add a passkey ---------------------------------------------------

    private static async Task<IResult> RegisterBeginAsync(
        ICurrentUserAccessor currentUser,
        UsersRepository users,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        IOptions<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(currentUser.UserId, cancellationToken)
                              .ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();

        var fido2User = new Fido2User
        {
            Id = user.Id.ToByteArray(),
            Name = user.Username ?? user.Id.ToString(),
            DisplayName = user.DisplayName,
        };

        // Exclude the user's existing credentials so the same authenticator
        // can't enrol twice for one account — but ONLY those registered against
        // the current RP. A credential from a prior RP (domain rename / ADR-0061
        // restore) can't collide on the authenticator (different rpIdHash), and
        // excluding it wrongly makes the same key refuse re-enrolment.
        var rpId = apiOptions.Value.Fido2.RpId;
        var existing = await credentials.GetByUserAsync(user.Id, cancellationToken)
                                        .ConfigureAwait(false);
        var exclude = existing
            .Where(c => c.RpId == rpId)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        var options = webauthn.BeginRegistration(fido2User, exclude);

        var challengeId = await challenges.SaveAsync(
            ChallengeStore.RegisterFlow,
            userId: user.Id,
            optionsJson: options.ToJson(),
            metadataJson: null,
            ttl: ChallengeTtl,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new RegisterBeginResponse(challengeId, options));
    }

    private static async Task<IResult> RegisterCompleteAsync(
        RegisterCompleteRequest request,
        ICurrentUserAccessor currentUser,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        IOptions<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CredentialNickname))
            return BusinessError.Problem(BusinessError.Codes.RegisterNicknameRequired,
                "credentialNickname is required.");
        if (request.AttestationResponse is null)
            return BusinessError.Problem(BusinessError.Codes.RegisterAttestationRequired,
                "attestationResponse is required.");

        var challenge = await challenges.ConsumeAsync(
            request.ChallengeId, ChallengeStore.RegisterFlow, cancellationToken).ConfigureAwait(false);
        // The challenge is bound to the user it was minted for; a mismatch
        // (or unknown/expired) is an auth failure, not a business rejection.
        if (challenge is null || challenge.UserId != currentUser.UserId)
            return Results.Problem("Challenge is unknown, expired, or already consumed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var originalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson);

        WebAuthnRegistrationOutcome outcome;
        try
        {
            outcome = await webauthn.CompleteRegistrationAsync(
                request.AttestationResponse,
                originalOptions,
                IsCredentialIdUniqueAsync(credentials),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            return BusinessError.Problem(
                BusinessError.Codes.RegisterAttestationFailed,
                detail: ex.Message,
                title: "WebAuthn attestation failed verification.");
        }

        var row = await credentials.InsertAsync(
            userId: currentUser.UserId,
            credentialId: outcome.CredentialId,
            publicKey: outcome.PublicKey,
            signatureCounter: outcome.SignatureCounter,
            aaguid: outcome.Aaguid,
            transports: outcome.Transports,
            nickname: request.CredentialNickname,
            rpId: apiOptions.Value.Fido2.RpId,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new CredentialSummary(
            row.Id, row.Nickname, row.CreatedAt, row.LastUsedAt));
    }

    // --- List / remove passkeys -----------------------------------------

    private static async Task<IResult> ListCredentialsAsync(
        ICurrentUserAccessor currentUser,
        CredentialsRepository credentials,
        CancellationToken cancellationToken)
    {
        var rows = await credentials.GetByUserAsync(currentUser.UserId, cancellationToken)
                                    .ConfigureAwait(false);
        var summaries = rows
            .Select(c => new CredentialSummary(c.Id, c.Nickname, c.CreatedAt, c.LastUsedAt))
            .ToList();
        return Results.Ok(summaries);
    }

    private static async Task<IResult> DeleteCredentialAsync(
        Guid id,
        ICurrentUserAccessor currentUser,
        CredentialsRepository credentials,
        RecoveryCodesRepository recoveryCodes,
        CancellationToken cancellationToken)
    {
        // Removing the last passkey is a lockout UNLESS a fallback login path
        // exists — unused recovery codes. Allow the last one only then. This is
        // also what lets a user clear a now-dead passkey (e.g. one left over from
        // a previous RP after a domain rename) that they can no longer log in with.
        var unusedRecovery = await recoveryCodes.CountUnusedByUserAsync(currentUser.UserId, cancellationToken)
                                                .ConfigureAwait(false);
        var result = await credentials.DeleteOwnAsync(
            id, currentUser.UserId, allowLast: unusedRecovery > 0, cancellationToken)
                                      .ConfigureAwait(false);
        return result switch
        {
            CredentialDeleteResult.Deleted => Results.NoContent(),
            CredentialDeleteResult.NotFound => BusinessError.Problem(
                BusinessError.Codes.CredentialNotFound,
                "No such passkey for this account."),
            CredentialDeleteResult.WasLastCredential => BusinessError.Problem(
                BusinessError.Codes.CredentialLastRemaining,
                "You can't remove your only passkey with no fallback — add another passkey or generate recovery codes first, or you'd be locked out."),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    // --- Recovery codes --------------------------------------------------

    private static async Task<IResult> RecoveryCodesStatusAsync(
        ICurrentUserAccessor currentUser,
        RecoveryCodesRepository recoveryCodes,
        CancellationToken cancellationToken)
    {
        var remaining = await recoveryCodes.CountUnusedByUserAsync(currentUser.UserId, cancellationToken)
                                           .ConfigureAwait(false);
        return Results.Ok(new RecoveryCodesStatusResponse(remaining, RecoveryCodes.CodesPerSet));
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        ICurrentUserAccessor currentUser,
        RecoveryCodesRepository recoveryCodes,
        CancellationToken cancellationToken)
    {
        var (plaintext, hashes) = RecoveryCodes.Generate();
        await recoveryCodes.ReplaceAllAsync(currentUser.UserId, hashes, cancellationToken)
                           .ConfigureAwait(false);
        return Results.Ok(new RegenerateRecoveryCodesResponse(plaintext));
    }

    /// <summary>
    /// Build the Fido2 unique-credential-id callback. Returns true when
    /// the candidate id isn't already bound to any user (the global UNIQUE
    /// constraint on <c>webauthn_credentials.credential_id</c> is the
    /// source of truth; this short-circuits before the INSERT would).
    /// </summary>
    private static IsCredentialIdUniqueToUserAsyncDelegate IsCredentialIdUniqueAsync(
        CredentialsRepository credentials) =>
        async (args, ct) =>
        {
            var existing = await credentials.GetByCredentialIdAsync(args.CredentialId, ct).ConfigureAwait(false);
            return existing is null;
        };
}
