using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// WebAuthn login (assertion) ceremony per ADR-0013. Two endpoints
/// paired across the assertion lifetime:
/// <list type="bullet">
///   <item><description><c>POST /api/auth/login/begin</c> — resolve the
///   user by username, generate an <see cref="AssertionOptions"/> scoped
///   to their credentials, persist the challenge, return the options +
///   challenge id.</description></item>
///   <item><description><c>POST /api/auth/login/complete</c> — consume
///   the matching pending challenge, look up the credential by id,
///   verify the assertion via Fido2, bump signature counter +
///   <c>last_used_at</c>, issue a cookie session.</description></item>
/// </list>
/// </summary>
public static class LoginEndpoints
{
    /// <summary>
    /// How long an in-flight login challenge is valid. Same envelope as
    /// the setup ceremony — short enough that a stale challenge must be
    /// retried rather than replayed.
    /// </summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapLoginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth/login")
                          .AllowAnonymous();

        group.MapPost("/begin", BeginAsync);
        group.MapPost("/complete", CompleteAsync);
        // Account-recovery fallback (ADR-0013 follow-through). Rate-limited:
        // unlike an assertion (unforgeable without the private key), a recovery
        // code is a bearer secret, and each attempt is an expensive Argon2id
        // verify — the limiter caps both brute-force and the memory-DoS the
        // verify cost would otherwise enable.
        group.MapPost("/recovery", RecoveryAsync)
             .RequireRateLimiting(RecoveryRateLimitPolicy);

        return routes;
    }

    /// <summary>
    /// Rate-limit policy name for <c>/login/recovery</c>; the policy is
    /// configured in Program.cs (fixed window, partitioned by client IP).
    /// </summary>
    public const string RecoveryRateLimitPolicy = "recovery-login";

    private static async Task<IResult> BeginAsync(
        LoginBeginRequest request,
        UsersRepository users,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BusinessError.Problem(BusinessError.Codes.LoginUsernameRequired,
                "username is required.");

        // The single-user / self-hosted threat model in ADR-0013 doesn't
        // include user enumeration, so we surface "no such user" /
        // "disabled" / "no credentials" as a 401 with a generic message
        // instead of returning a fake successful response. Keeps the
        // failure path readable without leaking interesting detail.
        var user = await users.GetByUsernameAsync(request.Username, cancellationToken)
                              .ConfigureAwait(false);
        if (user is null || user.IsDisabled)
            return Results.Problem("Authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var userCredentials = await credentials.GetByUserAsync(user.Id, cancellationToken)
                                                .ConfigureAwait(false);
        if (userCredentials.Count == 0)
            return Results.Problem("Authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var allowed = userCredentials
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();
        var options = webauthn.BeginAssertion(allowed);

        var challengeId = await challenges.SaveAsync(
            ChallengeStore.LoginFlow,
            userId: user.Id,
            optionsJson: options.ToJson(),
            metadataJson: null,
            ttl: ChallengeTtl,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new LoginBeginResponse(challengeId, options));
    }

    private static async Task<IResult> CompleteAsync(
        LoginCompleteRequest request,
        HttpContext http,
        ServiceDbContextFactory serviceFactory,
        CredentialsRepository credentials,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (request.AssertionResponse is null)
            return BusinessError.Problem(BusinessError.Codes.LoginAssertionRequired,
                "assertionResponse is required.");

        var challenge = await challenges.ConsumeAsync(
            request.ChallengeId, ChallengeStore.LoginFlow, cancellationToken).ConfigureAwait(false);
        if (challenge is null || challenge.UserId is null)
            return Results.Problem("Authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var credential = await credentials.GetByCredentialIdAsync(
            request.AssertionResponse.RawId, cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.UserId != challenge.UserId.Value)
            // Either the credential id doesn't exist, or it does but
            // belongs to a different user than /begin resolved — both
            // are auth failures, surface as 401.
            return Results.Problem("Authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var originalOptions = AssertionOptions.FromJson(challenge.OptionsJson);

        WebAuthnAssertionOutcome outcome;
        try
        {
            outcome = await webauthn.CompleteAssertionAsync(
                request.AssertionResponse,
                originalOptions,
                storedPublicKey: credential.PublicKey,
                storedSignatureCounter: (uint)credential.SignatureCounter,
                isUserHandleOwnerOfCredentialId: IsUserHandleOwnerCallback(credentials, challenge.UserId.Value),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "WebAuthn assertion failed verification.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Replay-attack guard: the WebAuthn spec requires the new counter
        // to be strictly greater than the stored one (unless the
        // authenticator returns 0, which means it doesn't track a
        // counter). Fido2NetLib already validates this; we trust it and
        // persist the new value.
        await credentials.UpdateAfterAssertionAsync(
            credential.Id, outcome.NewSignatureCounter, cancellationToken).ConfigureAwait(false);

        // Pre-cookie-issued: the request is still anonymous from the
        // RLS interceptor's perspective, so a runtime-AppDbContext
        // lookup would be denied. Service role bridges this last
        // pre-auth read.
        await using var serviceDb = serviceFactory.Create();
        var user = await serviceDb.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Credential's user_id resolved to no row — schema invariant violation.");

        var session = await sessions.IssueAsync(
            user.Id,
            http.Request.Headers.UserAgent.ToString(),
            cancellationToken).ConfigureAwait(false);
        http.Response.Cookies.Append(
            sessions.CookieName, session.CookieValue,
            sessions.BuildCookieOptions(session.ExpiresAt));

        return Results.Ok(new LoginCompleteResponse(
            UserId: user.Id,
            Username: user.Username ?? string.Empty,
            SessionId: session.SessionId,
            SessionExpiresAt: session.ExpiresAt));
    }

    private static async Task<IResult> RecoveryAsync(
        RecoveryLoginRequest request,
        HttpContext http,
        UsersRepository users,
        RecoveryCodesRepository recoveryCodes,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BusinessError.Problem(BusinessError.Codes.LoginUsernameRequired,
                "username is required.");
        if (string.IsNullOrWhiteSpace(request.RecoveryCode))
            return BusinessError.Problem(BusinessError.Codes.RecoveryCodeRequired,
                "recoveryCode is required.");

        // Same generic-401 posture as /begin: never distinguish unknown user,
        // disabled user, or wrong code (no enumeration; no "the username was
        // right" oracle).
        var user = await users.GetByUsernameAsync(request.Username, cancellationToken)
                              .ConfigureAwait(false);
        if (user is null || user.IsDisabled)
            return Results.Problem("Authentication failed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var unused = await recoveryCodes.GetUnusedByUserAsync(user.Id, cancellationToken)
                                        .ConfigureAwait(false);

        // Verify against every unused code (each a constant-time Argon2id
        // verify). On the first match, atomically consume that specific row;
        // if the consume loses a race (already used), fall through to 401.
        foreach (var (id, hash) in unused)
        {
            if (!RecoveryCodes.Verify(request.RecoveryCode, hash))
                continue;

            var consumed = await recoveryCodes.MarkUsedAsync(id, cancellationToken)
                                              .ConfigureAwait(false);
            if (!consumed)
                break;   // raced to used between read and update — treat as failure

            var session = await sessions.IssueAsync(
                user.Id,
                http.Request.Headers.UserAgent.ToString(),
                cancellationToken).ConfigureAwait(false);
            http.Response.Cookies.Append(
                sessions.CookieName, session.CookieValue,
                sessions.BuildCookieOptions(session.ExpiresAt));

            return Results.Ok(new LoginCompleteResponse(
                UserId: user.Id,
                Username: user.Username ?? string.Empty,
                SessionId: session.SessionId,
                SessionExpiresAt: session.ExpiresAt));
        }

        return Results.Problem("Authentication failed.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Build the Fido2 user-handle-owns-credential callback. Returns
    /// true when the handle the assertion claims to be from owns the
    /// credential id — a discoverable-credentials safeguard.
    /// </summary>
    private static IsUserHandleOwnerOfCredentialIdAsync IsUserHandleOwnerCallback(
        CredentialsRepository credentials, Guid expectedUserId) =>
        async (args, ct) =>
        {
            var credential = await credentials.GetByCredentialIdAsync(args.CredentialId, ct)
                                              .ConfigureAwait(false);
            return credential is not null && credential.UserId == expectedUserId;
        };
}
