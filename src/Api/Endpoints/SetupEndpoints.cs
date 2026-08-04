using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Http.Features;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Provisioning;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// First-run WebAuthn setup ceremony per ADR-0013. Two endpoints, paired
/// across the bootstrap-token lifetime:
/// <list type="bullet">
///   <item><description><c>POST /api/auth/setup/{token}/begin</c> — verify
///   the bootstrap token (without consuming), generate a Fido2 challenge
///   for a brand-new credential, persist it to <c>webauthn_pending_challenges</c>,
///   return the options + challenge id to the browser.</description></item>
///   <item><description><c>POST /api/auth/setup/{token}/complete</c> —
///   consume the matching pending challenge, verify the attestation, then
///   in one transaction insert the new user, the credential, and the 10
///   recovery codes; flip the bootstrap token; issue a cookie session so
///   the browser is logged in.</description></item>
/// </list>
/// </summary>
public static class SetupEndpoints
{
    /// <summary>
    /// How long an in-flight setup challenge is valid. Short by design —
    /// a stale challenge must be retried, not replayed.
    /// </summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth/setup/{token}")
                          .AllowAnonymous();

        group.MapGet("/info",    InfoAsync);
        group.MapPost("/begin", BeginAsync);
        group.MapPost("/complete", CompleteAsync);
        // Restore-from-backup branch of the bootstrap UI (ADR-0061). Anonymous +
        // bootstrap-token-gated like the rest of setup; multipart upload, so opt
        // out of antiforgery (the token is the auth).
        group.MapPost("/restore", RestoreFromBackupAsync).DisableAntiforgery();

        return routes;
    }

    /// <summary>
    /// <c>POST /api/auth/setup/{token}/restore</c> (ADR-0061). Pre-auth, gated
    /// by the (unconsumed) bootstrap token — which only exists before the first
    /// user, so this is first-run only. Stages the uploaded <c>.cofferbak</c> +
    /// passphrase, verifies the passphrase actually opens it (so a bad one fails
    /// here, not in a post-restart boot loop), then restarts: the next boot
    /// applies the restore over the DB before serving (see Program.cs +
    /// <see cref="BootstrapRestoreStaging"/>). The SPA polls until the server is
    /// back, then lands on /login for the restored credentials.
    /// </summary>
    private static async Task<IResult> RestoreFromBackupAsync(
        string token,
        HttpRequest request,
        ServiceDbContextFactory serviceFactory,
        IApplicationRestarter restarter,
        CancellationToken cancellationToken)
    {
        await using (var db = serviceFactory.Create())
        {
            if (!await IsTokenValidAsync(db, token, cancellationToken).ConfigureAwait(false))
                return Results.Problem("Invalid or expired bootstrap token.",
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with an 'archive' file and a 'passphrase' field.");

        // Allow a large upload — a whole-DB backup can be sizeable. Lift Kestrel's
        // per-request cap for this request; the multipart length limit (~128 MB
        // default) is the practical ceiling for the UI path, with `coffer-api
        // restore` as the fallback for anything larger.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var passphrase = form["passphrase"].ToString();
        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0 || string.IsNullOrEmpty(passphrase))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Both a backup file ('archive') and a 'passphrase' are required.");

        // Stage the upload, then verify the passphrase opens it before committing
        // the request — a wrong passphrase must fail now, not after the restart.
        BootstrapRestoreStaging.EnsureDir();
        await using (var dest = File.Create(BootstrapRestoreStaging.ArchivePath))
            await file.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var verify = File.OpenRead(BootstrapRestoreStaging.ArchivePath);
            await BackupCrypto.DecryptAsync(verify, passphrase, Stream.Null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BackupDecryptException)
        {
            BootstrapRestoreStaging.Clear();
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseInvalid,
                "The passphrase didn't decrypt this backup (or the file is corrupt). Nothing was changed.");
        }

        await BootstrapRestoreStaging.CommitAsync(passphrase, cancellationToken).ConfigureAwait(false);

        // Restart so the next boot applies the staged restore before serving.
        // The SPA polls until the server is back, then lands on /login.
        restarter.RequestRestart();
        return Results.Ok(new { status = "restoring" });
    }

    /// <summary>
    /// <c>GET /api/auth/setup/{token}/info</c>. Pre-auth: validates the
    /// bootstrap token without consuming it and returns the list of
    /// ledgers the new user could join. The setup page calls this on
    /// mount so the token-invalid case surfaces immediately rather than
    /// after the form is filled and submitted, and so the ledger picker
    /// can be populated client-side without a separate round-trip.
    /// </summary>
    private static async Task<IResult> InfoAsync(
        string token,
        ServiceDbContextFactory serviceFactory,
        CancellationToken cancellationToken)
    {
        await using var db = serviceFactory.Create();

        if (!await IsTokenValidAsync(db, token, cancellationToken).ConfigureAwait(false))
            return Results.Problem("Invalid or expired bootstrap token.",
                statusCode: StatusCodes.Status401Unauthorized);

        // No ledger list any more (ADR-0088). This used to return every ledger on
        // the install so the form could offer "join an existing one" — but on a
        // fresh install the only rows were empty placeholders from migration 055,
        // which made the choice actively misleading. Migration 186 drops them, and
        // ledgers are now created after setup from the hub. Token validation above
        // is what this endpoint is actually for.
        return Results.Ok(new SetupInfoResponse());
    }

    private static async Task<IResult> BeginAsync(
        string token,
        SetupBeginRequest request,
        ChallengeStore challenges,
        ServiceDbContextFactory serviceFactory,
        IWebAuthnService webauthn,
        CancellationToken cancellationToken)
    {
        // Pre-auth bootstrap flow: every DB hit goes through the
        // service role because coffer_app would be RLS-denied on
        // bootstrap_tokens (REVOKE'd) and on the users existence check
        // (current_app_user_id is unset).
        await using var db = serviceFactory.Create();

        // ADR-0089: the server is the single source of truth for what a username
        // may be. Normalise first (NFC + trim) so the stored form is canonical and
        // the uniqueness check below compares canonical against canonical.
        var username = Auth.UsernamePolicy.Normalize(request.Username);
        if (!Auth.UsernamePolicy.IsValid(username, out var usernameError))
            return BusinessError.Problem(BusinessError.Codes.SetupUsernameRequired,
                usernameError!);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BusinessError.Problem(BusinessError.Codes.SetupDisplayNameRequired,
                "displayName is required.");

        // Verify (don't consume) the bootstrap token. /complete consumes
        // atomically with the credential insert so a failed registration
        // keeps the token alive for retry. The token-invalid case stays
        // 401 — bootstrap is the auth credential here, so failure is
        // an auth failure, not a business-rule violation.
        if (!await IsTokenValidAsync(db, token, cancellationToken).ConfigureAwait(false))
            return Results.Problem("Invalid or expired bootstrap token.",
                statusCode: StatusCodes.Status401Unauthorized);

        // Reject if the username is already taken — the post-bootstrap
        // case where additional credentials are added under an existing
        // account uses /api/auth/register/* (PR 3.5+), not setup.
        // Case-insensitive by storage, not by query: users.username carries the
        // ICU username_ci collation (migration 187), so this `==` folds case in
        // Postgres. `Ada` is correctly reported as taken when `ada` exists.
        if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.SetupUsernameTaken,
                $"Username '{username}' is already taken.");

        // Synthesise a user id now so the Fido2 ceremony has a stable
        // user.id to encode in the credential. The matching DB row gets
        // inserted at /complete; if /complete never arrives, no user row
        // is left behind.
        var userId = Guid.NewGuid();
        var fido2User = new Fido2User
        {
            Id = userId.ToByteArray(),
            // Normalised form, so the credential the authenticator stores matches
            // what lands in the DB (WebAuthn's user.name is the human-palatable
            // identifier — an email address is its canonical example).
            Name = username,
            DisplayName = request.DisplayName,
        };

        var options = webauthn.BeginRegistration(fido2User, Array.Empty<PublicKeyCredentialDescriptor>());

        var metadata = JsonSerializer.Serialize(new SetupChallengeMetadata
        {
            UserId = userId,
            Username = username,
            DisplayName = request.DisplayName,
        });

        var challengeId = await challenges.SaveAsync(
            ChallengeStore.SetupFlow,
            userId: null,    // user row not persisted yet
            optionsJson: options.ToJson(),
            metadataJson: metadata,
            ttl: ChallengeTtl,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new SetupBeginResponse(challengeId, options));
    }

    private static async Task<IResult> CompleteAsync(
        string token,
        SetupCompleteRequest request,
        HttpContext http,
        ChallengeStore challenges,
        IWebAuthnService webauthn,
        CredentialsRepository credentials,
        SessionService sessions,
        ServiceDbContextFactory serviceFactory,
        ProvisioningService provisioning,
        ILoggerFactory loggerFactory,
        IOptions<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        // Pre-auth bootstrap flow — service role; the user row itself
        // is created here so coffer_app couldn't satisfy the users
        // policy's WITH CHECK anyway.
        await using var db = serviceFactory.Create();

        if (string.IsNullOrWhiteSpace(request.CredentialNickname))
            return BusinessError.Problem(BusinessError.Codes.SetupNicknameRequired,
                "credentialNickname is required.");
        if (request.AttestationResponse is null)
            return BusinessError.Problem(BusinessError.Codes.SetupAttestationRequired,
                "attestationResponse is required.");

        // No ledger-choice gate any more (ADR-0088). Setup creates the user and
        // passkey; the ledger hub is the post-setup home and offers "New ledger"
        // and "Import from Moneydance". Zero ledgers is a supported landing —
        // the hub renders an empty state with both CTAs, so the dead end the old
        // mandatory choice guarded against no longer exists.

        var challenge = await challenges.ConsumeAsync(
            request.ChallengeId, ChallengeStore.SetupFlow, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
            // Challenge is the auth artefact at this point — unknown
            // means "you didn't bring valid auth," not "your input is
            // unprocessable." Stay 401.
            return Results.Problem("Challenge is unknown, expired, or already consumed.",
                statusCode: StatusCodes.Status401Unauthorized);

        var metadata = JsonSerializer.Deserialize<SetupChallengeMetadata>(challenge.MetadataJson!)
            ?? throw new InvalidOperationException(
                "Setup challenge missing metadata; this is a server-side invariant violation.");

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
            // Bootstrap token already authenticated the operator — a
            // failed attestation is "the credential you tried to
            // register didn't validate," which is a business-rule
            // rejection, not an auth failure.
            return BusinessError.Problem(
                BusinessError.Codes.SetupAttestationFailed,
                detail: ex.Message,
                title: "WebAuthn attestation failed verification.");
        }

        // Persist user + credential + recovery codes + flip the bootstrap
        // token in one transaction so a partial failure leaves the DB in
        // a consistent state. Bootstrap-token consumption is also where
        // the ceremony's success is committed; until COMMIT, retries
        // against the same token still work.
        var (recoveryPlaintext, recoveryHashes) = RecoveryCodes.Generate();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                         .ConfigureAwait(false);

        // First human user is the operator → admin (ADR-0060). Robust to
        // future flow changes: admin iff no admin exists yet (today this
        // handler only runs for the first user, since the bootstrap token
        // only mints when no credentials exist).
        var firstUserIsAdmin = !await db.Users
            .AnyAsync(u => u.IsAdmin, cancellationToken)
            .ConfigureAwait(false);

        // Insert the user row at the deferred id we promised the
        // ceremony at /begin so the FIDO2 user.id encoded in the
        // credential matches the row that owns it.
        db.Users.Add(new UserRow
        {
            Id = metadata.UserId,
            DisplayName = metadata.DisplayName,
            Username = metadata.Username,
            CreatedBy = "bootstrap-token",
            IsAdmin = firstUserIsAdmin,
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
        {
            db.RecoveryCodes.Add(new RecoveryCodeRow
            {
                Id = Guid.NewGuid(),
                UserId = metadata.UserId,
                CodeHash = hash,
            });
        }

        // No ledger is created here (ADR-0088), and therefore no grant — so
        // nothing in this transaction can trip the deferred ≥1-owner trigger. The
        // Demo ledger, if requested, is created post-commit by the import
        // pipeline, which owns ledger creation for imports (new-ledger-only,
        // ADR-0052) and grants ownership as part of that.

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Bootstrap-token consumption: deliberately the last step in
        // the transaction. If any earlier insert fails the token
        // stays alive for retry; once the transaction commits, the
        // token is dead and the system is bootstrapped. ExecuteUpdate
        // participates in the active transaction and is single-statement
        // — Postgres serialises the UPDATE so two concurrent /complete
        // calls can't both succeed.
        if (!TryHashToken(token, out var tokenHash))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return BusinessError.Problem(BusinessError.Codes.SetupBootstrapConsumed,
                "Bootstrap token was consumed by a concurrent request.");
        }

        var now = DateTime.UtcNow;
        var consumed = await db.BootstrapTokens
            .Where(t => t.TokenHash == tokenHash && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ConsumedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);

        if (consumed == 0)
        {
            // Lost the race against another /complete or the token
            // expired between /begin and /complete.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return BusinessError.Problem(BusinessError.Codes.SetupBootstrapConsumed,
                "Bootstrap token was consumed by a concurrent request.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Optional Demo ledger (ADR-0088). Best-effort, post-commit — setup has
        // already succeeded, so a slow or failing import costs the sample data,
        // never the passkey registration. The user simply lands on the hub with
        // no ledgers and can create or import one there.
        //
        // The seeder is deliberately NOT run here: the sample dataset carries its
        // own category tree, so layering the starter catalogue on top would
        // duplicate every row. Since ADR-0091 that tree and the starter catalogue
        // are the same set — the catalogue is generated from this very export —
        // so Demo and a hub-created ledger (which does run the seeder, via
        // LedgersEndpoints) end up with identical categories by different routes.
        Guid? ledgerId = null;
        string? ledgerName = null;
        if (request.IncludeDemo)
        {
            try
            {
                var demo = await provisioning
                    .ProvisionDemoAsync(metadata.UserId, cancellationToken)
                    .ConfigureAwait(false);
                ledgerId   = demo.LedgerId;
                ledgerName = demo.Name;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Coffer.Api.Provisioning")
                    .LogWarning(ex, "Demo ledger seed failed during setup for user {UserId}.", metadata.UserId);
            }
        }

        // Issue a cookie session so the browser ends up logged in as the
        // newly created user without an extra round-trip.
        var session = await sessions.IssueAsync(
            metadata.UserId,
            http.Request.Headers.UserAgent.ToString(),
            cancellationToken).ConfigureAwait(false);
        http.Response.Cookies.Append(
            sessions.CookieName, session.CookieValue,
            sessions.BuildCookieOptions(session.ExpiresAt));

        return Results.Ok(new SetupCompleteResponse(
            UserId: metadata.UserId,
            Username: metadata.Username,
            SessionId: session.SessionId,
            SessionExpiresAt: session.ExpiresAt,
            RecoveryCodes: recoveryPlaintext,
            LedgerId: ledgerId,
            LedgerName: ledgerName));
    }

    /// <summary>
    /// Lightweight read of <c>bootstrap_tokens</c> that mirrors
    /// <see cref="BootstrapTokenService.ConsumeAsync"/>'s WHERE clause but
    /// doesn't flip <c>consumed_at</c>. /begin only needs to confirm the
    /// token is still mintable; the actual consume happens at /complete.
    /// </summary>
    private static async Task<bool> IsTokenValidAsync(
        AppDbContext db, string token, CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var hash))
            return false;

        var now = DateTime.UtcNow;
        return await db.BootstrapTokens
            .AnyAsync(
                t => t.TokenHash == hash && t.ConsumedAt == null && t.ExpiresAt > now,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Wrap <see cref="BootstrapTokenService.HashToken"/> so a garbage
    /// (non-base64url) input is treated as "no such token" instead of
    /// bubbling the <see cref="FormatException"/> as a 500.
    /// </summary>
    private static bool TryHashToken(string token, out byte[] hash)
    {
        try
        {
            hash = BootstrapTokenService.HashToken(token);
            return true;
        }
        catch (FormatException)
        {
            hash = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// Build the Fido2 unique-credential-id callback. Returns true when
    /// the candidate id is not yet bound to any user (the global UNIQUE
    /// constraint on <c>webauthn_credentials.credential_id</c> is the
    /// source of truth; this just lets the library short-circuit before
    /// the INSERT would.)
    /// </summary>
    private static IsCredentialIdUniqueToUserAsyncDelegate IsCredentialIdUniqueAsync(
        CredentialsRepository credentials) =>
        async (args, ct) =>
        {
            var existing = await credentials.GetByCredentialIdAsync(args.CredentialId, ct).ConfigureAwait(false);
            return existing is null;
        };

    /// <summary>
    /// Internal-only metadata persisted alongside the in-flight challenge.
    /// </summary>
    private sealed class SetupChallengeMetadata
    {
        public Guid UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }
}
