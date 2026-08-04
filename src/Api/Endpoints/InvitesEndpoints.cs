using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Invite links (ADR-0083 slice B). Three surfaces:
/// <list type="bullet">
///   <item><b>Issue / list / revoke</b> — an owner over their own ledger's invites
///   (<c>/api/ledgers/{id}/invites</c>), an admin over all
///   (<c>/api/admin/invites</c>).</item>
///   <item><b>Redeem</b> (<c>/api/auth/invite/{token}</c>, anonymous) — a NEW person runs
///   the WebAuthn registration ceremony (a scoped, repeatable clone of the first-user
///   bootstrap in <see cref="SetupEndpoints"/>: user + credential + recovery codes + the
///   invite's pre-scoped grant + token consume, one service-role transaction, then a
///   session cookie).</item>
///   <item><b>Accept</b> (signed-in) — apply the invite's grant to the current user.</item>
/// </list>
/// The token is the credential; the plaintext is shown to the issuer once and only its
/// SHA-256 is stored (<see cref="InvitesRepository"/>).
/// </summary>
public static class InvitesEndpoints
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);
    private static readonly string[] GrantRoles = ["owner", "editor", "viewer"];

    public static IEndpointRouteBuilder MapInvitesEndpoints(this IEndpointRouteBuilder routes)
    {
        // Owner — manage invites for their own ledger (owner-gated in each handler).
        routes.MapGet("/api/ledgers/{ledgerId:guid}/invites", ListLedgerInvitesAsync).RequireAuthorization();
        routes.MapPost("/api/ledgers/{ledgerId:guid}/invites", IssueLedgerInviteAsync).RequireAuthorization();
        routes.MapDelete("/api/ledgers/{ledgerId:guid}/invites/{inviteId:guid}", RevokeLedgerInviteAsync)
              .RequireAuthorization();

        // Admin — manage all invites.
        var admin = routes.MapGroup("/api/admin/invites").RequireAuthorization(AuthPolicies.RequireAdmin);
        admin.MapGet("/", ListAllInvitesAsync);
        admin.MapPost("/", IssueAdminInviteAsync);
        admin.MapDelete("/{inviteId:guid}", RevokeAdminInviteAsync);

        // Redeem — anonymous (the token authenticates).
        var redeem = routes.MapGroup("/api/auth/invite/{token}").AllowAnonymous();
        redeem.MapGet("/", PreviewAsync);
        redeem.MapPost("/begin", BeginAsync);
        redeem.MapPost("/complete", CompleteAsync);

        // Accept — signed-in user applies the grant to themselves.
        routes.MapPost("/api/auth/invite/{token}/accept", AcceptAsync).RequireAuthorization();

        return routes;
    }

    // ── Issue / list / revoke (owner) ─────────────────────────────────────────

    private static async Task<IResult> ListLedgerInvitesAsync(
        Guid ledgerId, LedgerAuthorizer authorizer, InvitesRepository invites, CancellationToken ct)
    {
        var gate = await authorizer.RequireOwnerAsync(ledgerId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;
        return Results.Ok(await invites.ListPendingForLedgerAsync(ledgerId, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> IssueLedgerInviteAsync(
        Guid ledgerId, IssueLedgerInviteRequest request,
        ICurrentUserAccessor currentUser, LedgerAuthorizer authorizer, InvitesRepository invites,
        CancellationToken ct)
    {
        var gate = await authorizer.RequireOwnerAsync(ledgerId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;
        if (!GrantRoles.Contains(request.Role))
            return BusinessError.Problem(BusinessError.Codes.InviteRoleInvalid,
                "Role must be owner, editor, or viewer.");

        var token = await invites
            .CreateAsync(currentUser.UserId, ledgerId, request.Role, grantsAdmin: false, ct)
            .ConfigureAwait(false);
        return Results.Ok(new InviteCreatedResponse(token, DateTime.UtcNow.Add(InvitesRepository.DefaultTtl)));
    }

    private static async Task<IResult> RevokeLedgerInviteAsync(
        Guid ledgerId, Guid inviteId, LedgerAuthorizer authorizer, InvitesRepository invites, CancellationToken ct)
    {
        var gate = await authorizer.RequireOwnerAsync(ledgerId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;
        return await invites.RevokeForLedgerAsync(ledgerId, inviteId, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.InviteNotFound, "No such pending invite on this ledger.");
    }

    // ── Issue / list / revoke (admin) ─────────────────────────────────────────

    private static async Task<IResult> ListAllInvitesAsync(InvitesRepository invites, CancellationToken ct) =>
        Results.Ok(await invites.ListPendingAllAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> IssueAdminInviteAsync(
        IssueAdminInviteRequest request, ICurrentUserAccessor currentUser, InvitesRepository invites,
        CancellationToken ct)
    {
        // A ledger invite carries a role; an instance-only invite carries neither
        // (the DB CHECK enforces both-or-neither too).
        var hasLedger = request.LedgerId.HasValue;
        var hasRole = !string.IsNullOrWhiteSpace(request.Role);
        if (hasLedger != hasRole)
            return BusinessError.Problem(BusinessError.Codes.InviteScopeInvalid,
                "Pass a ledgerId and role together (a ledger invite), or neither (an instance-only invite).");
        if (hasRole && !GrantRoles.Contains(request.Role!))
            return BusinessError.Problem(BusinessError.Codes.InviteRoleInvalid,
                "Role must be owner, editor, or viewer.");

        var token = await invites
            .CreateAsync(currentUser.UserId, request.LedgerId, hasRole ? request.Role : null, request.GrantsAdmin, ct)
            .ConfigureAwait(false);
        return Results.Ok(new InviteCreatedResponse(token, DateTime.UtcNow.Add(InvitesRepository.DefaultTtl)));
    }

    private static async Task<IResult> RevokeAdminInviteAsync(
        Guid inviteId, InvitesRepository invites, CancellationToken ct) =>
        await invites.RevokeAsync(inviteId, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.InviteNotFound, "No such pending invite.");

    // ── Redeem (anonymous) ────────────────────────────────────────────────────

    private static async Task<IResult> PreviewAsync(
        string token, InvitesRepository invites, CancellationToken ct)
    {
        var scope = await invites.GetValidScopeAsync(token, ct).ConfigureAwait(false);
        return scope is null
            ? BusinessError.Problem(BusinessError.Codes.InviteInvalid,
                "This invite link is invalid, already used, or expired.")
            : Results.Ok(new InvitePreviewResponse(scope.LedgerName, scope.Role, scope.GrantsAdmin));
    }

    private static async Task<IResult> BeginAsync(
        string token, InviteBeginRequest request,
        InvitesRepository invites, ChallengeStore challenges, ServiceDbContextFactory serviceFactory,
        IWebAuthnService webauthn, CancellationToken ct)
    {
        if (await invites.GetValidScopeAsync(token, ct).ConfigureAwait(false) is null)
            return Results.Problem("Invite link is invalid, used, or expired.",
                statusCode: StatusCodes.Status401Unauthorized);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BusinessError.Problem(BusinessError.Codes.InviteFieldRequired,
                "username and displayName are required.");

        // Same policy as setup (ADR-0089). This flow previously accepted ANY
        // non-empty username — including whitespace and bidi-override characters —
        // while setup enforced a client-side handle pattern. One rule now, applied
        // server-side, so the two entry points can't drift again.
        var username = Auth.UsernamePolicy.Normalize(request.Username);
        if (!Auth.UsernamePolicy.IsValid(username, out var usernameError))
            return BusinessError.Problem(BusinessError.Codes.InviteFieldRequired, usernameError!);

        await using var db = serviceFactory.Create();
        // Folds case via the username_ci collation (migration 187).
        if (await db.Users.AnyAsync(u => u.Username == username, ct).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.InviteUsernameTaken,
                $"Username '{username}' is already taken. Sign in and use Accept instead.");

        // Synthesise the user id now so the Fido2 credential binds to the row inserted
        // at /complete (mirrors setup). No row is left behind if /complete never arrives.
        var userId = Guid.NewGuid();
        var options = webauthn.BeginRegistration(
            new Fido2User { Id = userId.ToByteArray(), Name = username, DisplayName = request.DisplayName },
            Array.Empty<PublicKeyCredentialDescriptor>());

        var challengeId = await challenges.SaveAsync(
            ChallengeStore.InviteFlow,
            userId: null,
            optionsJson: options.ToJson(),
            metadataJson: JsonSerializer.Serialize(new InviteChallengeMetadata
            {
                UserId = userId,
                Username = username,
                DisplayName = request.DisplayName,
            }),
            ttl: ChallengeTtl,
            ct).ConfigureAwait(false);

        return Results.Ok(new InviteBeginResponse(challengeId, options));
    }

    private static async Task<IResult> CompleteAsync(
        string token, InviteCompleteRequest request,
        HttpContext http, InvitesRepository invites, ChallengeStore challenges, IWebAuthnService webauthn,
        CredentialsRepository credentials, SessionService sessions, ServiceDbContextFactory serviceFactory,
        IOptions<ApiOptions> apiOptions, CancellationToken ct)
    {
        var scope = await invites.GetValidScopeAsync(token, ct).ConfigureAwait(false);
        if (scope is null)
            return Results.Problem("Invite link is invalid, used, or expired.",
                statusCode: StatusCodes.Status401Unauthorized);
        if (string.IsNullOrWhiteSpace(request.CredentialNickname) || request.AttestationResponse is null)
            return BusinessError.Problem(BusinessError.Codes.InviteFieldRequired,
                "credentialNickname and attestationResponse are required.");

        var challenge = await challenges.ConsumeAsync(request.ChallengeId, ChallengeStore.InviteFlow, ct)
            .ConfigureAwait(false);
        if (challenge is null)
            return Results.Problem("Challenge is unknown, expired, or already consumed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var metadata = JsonSerializer.Deserialize<InviteChallengeMetadata>(challenge.MetadataJson!)
            ?? throw new InvalidOperationException("Invite challenge missing metadata.");

        WebAuthnRegistrationOutcome outcome;
        try
        {
            outcome = await webauthn.CompleteRegistrationAsync(
                request.AttestationResponse,
                CredentialCreateOptions.FromJson(challenge.OptionsJson),
                async (args, c) => await credentials.GetByCredentialIdAsync(args.CredentialId, c)
                    .ConfigureAwait(false) is null,
                ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            return BusinessError.Problem(BusinessError.Codes.InviteAttestationFailed, ex.Message,
                "WebAuthn attestation failed verification.");
        }

        await using var db = serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (recoveryPlaintext, recoveryHashes) = RecoveryCodes.Generate();

        db.Users.Add(new UserRow
        {
            Id = metadata.UserId,
            DisplayName = metadata.DisplayName,
            Username = metadata.Username,
            CreatedBy = "invite",
            IsAdmin = scope.GrantsAdmin,
        });
        db.WebAuthnCredentials.Add(new WebAuthnCredentialRow
        {
            Id = Guid.NewGuid(),
            UserId = metadata.UserId,
            CredentialId = outcome.CredentialId,
            PublicKey = outcome.PublicKey,
            SignatureCounter = outcome.SignatureCounter,
            Aaguid = outcome.Aaguid,
            Transports = outcome.Transports,
            Nickname = request.CredentialNickname,
            RpId = apiOptions.Value.Fido2.RpId,
        });
        foreach (var hash in recoveryHashes)
            db.RecoveryCodes.Add(new RecoveryCodeRow { Id = Guid.NewGuid(), UserId = metadata.UserId, CodeHash = hash });

        if (scope.LedgerId is not null)
            db.UserLedgerGrants.Add(new UserLedgerGrantRow
            {
                UserId = metadata.UserId,
                LedgerId = scope.LedgerId.Value,
                Role = scope.Role!,
            });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Consume the invite LAST (like setup) — kept alive for retry if an earlier
        // insert fails; the conditional single-statement update is race-safe.
        if (!await ConsumeInviteAsync(db, token, ct).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return BusinessError.Problem(BusinessError.Codes.InviteInvalid,
                "This invite was used by a concurrent request.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var session = await sessions.IssueAsync(metadata.UserId, http.Request.Headers.UserAgent.ToString(), ct)
            .ConfigureAwait(false);
        http.Response.Cookies.Append(sessions.CookieName, session.CookieValue,
            sessions.BuildCookieOptions(session.ExpiresAt));

        return Results.Ok(new InviteCompleteResponse(
            metadata.UserId, metadata.Username, recoveryPlaintext, scope.LedgerId, scope.LedgerName));
    }

    private static async Task<IResult> AcceptAsync(
        string token, ICurrentUserAccessor currentUser, InvitesRepository invites,
        ServiceDbContextFactory serviceFactory, CancellationToken ct)
    {
        var scope = await invites.GetValidScopeAsync(token, ct).ConfigureAwait(false);
        if (scope is null)
            return BusinessError.Problem(BusinessError.Codes.InviteInvalid,
                "This invite link is invalid, already used, or expired.");

        var userId = currentUser.UserId;
        await using var db = serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (scope.LedgerId is not null)
        {
            var alreadyMember = await db.UserLedgerGrants
                .AnyAsync(g => g.LedgerId == scope.LedgerId.Value && g.UserId == userId, ct)
                .ConfigureAwait(false);
            if (!alreadyMember)
                db.UserLedgerGrants.Add(new UserLedgerGrantRow
                {
                    UserId = userId,
                    LedgerId = scope.LedgerId.Value,
                    Role = scope.Role!,
                });
        }
        if (scope.GrantsAdmin)
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, true), ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!await ConsumeInviteAsync(db, token, ct).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return BusinessError.Problem(BusinessError.Codes.InviteInvalid,
                "This invite was used by a concurrent request.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return Results.Ok(new InviteAcceptResponse(scope.LedgerId, scope.LedgerName));
    }

    // Consume the invite inside the caller's transaction: race-safe conditional
    // single-statement update (mirrors the bootstrap-token consume).
    private static async Task<bool> ConsumeInviteAsync(AppDbContext db, string token, CancellationToken ct)
    {
        byte[] hash;
        try { hash = BootstrapTokenService.HashToken(token); }
        catch { return false; }
        var now = DateTime.UtcNow;
        return await db.Invites
            .Where(i => i.TokenHash == hash && i.ConsumedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConsumedAt, _ => (DateTime?)now), ct)
            .ConfigureAwait(false) > 0;
    }

    private sealed class InviteChallengeMetadata
    {
        public Guid UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }
}

// ── Request/response DTOs ─────────────────────────────────────────────────────

public sealed class IssueLedgerInviteRequest
{
    public string Role { get; init; } = string.Empty;
}

public sealed class IssueAdminInviteRequest
{
    public Guid? LedgerId { get; init; }
    public string? Role { get; init; }
    public bool GrantsAdmin { get; init; }
}

public sealed record InviteCreatedResponse(string Token, DateTime ExpiresAt);

public sealed record InvitePreviewResponse(string? LedgerName, string? Role, bool GrantsAdmin);

public sealed class InviteBeginRequest
{
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed record InviteBeginResponse(Guid ChallengeId, CredentialCreateOptions Options);

public sealed class InviteCompleteRequest
{
    public Guid ChallengeId { get; init; }
    public string CredentialNickname { get; init; } = string.Empty;
    public AuthenticatorAttestationRawResponse? AttestationResponse { get; init; }
}

public sealed record InviteCompleteResponse(
    Guid UserId, string Username, IReadOnlyList<string> RecoveryCodes, Guid? LedgerId, string? LedgerName);

public sealed record InviteAcceptResponse(Guid? LedgerId, string? LedgerName);
