using System.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Fido2NetLib;
using Fido2NetLib.Objects;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin master-KEK surface (ADR-0092 D2): show an operator the key that wraps
/// their sealed secrets, so it can be backed up and carried to another install.
/// </summary>
/// <remarks>
/// <para><b>Why revealing is safe, and why it is not show-once.</b> Recovery codes
/// are show-once because re-display is an <i>authentication</i> attack surface.
/// The master KEK is an encryption key, and the only caller who can reach this
/// endpoint is an admin who can already read every ledger in plaintext through the
/// normal UI — seeing the key grants them nothing they lacked. Meanwhile
/// show-once has a real failure mode: a browser that dies after the key is
/// persisted but before the human writes it down leaves an install whose key
/// nobody has. So reveal is repeatable.</para>
///
/// <para><b>What the extra ceremony is for.</b> The session cookie proves an admin
/// authenticated within the last 30 days (ADR-0013). A fresh assertion proves a
/// human with an enrolled authenticator is present <i>now</i>, which is the
/// property that matters for handing out key material — it turns a stolen
/// still-valid cookie into a dead end. The assertion is verified inline and
/// authorizes exactly this one response; no step-up session flag is minted, so
/// there is no elevated window to leak.</para>
/// </remarks>
public static class AdminMasterKeyEndpoints
{
    /// <summary>Assertion challenge lifetime. Matches the login ceremony's
    /// short window — the user is looking at the prompt.</summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapAdminMasterKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/master-key")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/", GetStatus);
        group.MapPost("/reveal/begin", BeginRevealAsync);
        // POST, not GET: key material must never sit in a URL, a referrer, or
        // browser history.
        group.MapPost("/reveal", RevealAsync);
        // Rotation (ADR-0092 D4). The ceremony endpoint is shared with reveal —
        // rotation both writes new key material and returns it, so it is at least
        // as sensitive.
        group.MapPost("/rotate", RotateAsync);
        return routes;
    }

    /// <summary>
    /// Metadata only — id, path, and a non-reversible fingerprint. Safe to render
    /// on panel load, and the fingerprint is what lets an operator confirm which
    /// key an install runs (and match it to a backup's) without revealing it.
    /// </summary>
    private static IResult GetStatus(
        MasterKey masterKey,
        MasterKeyStore store)
        => Results.Ok(new MasterKeyContracts.MasterKeyStatusResponse(
            KekId: masterKey.Id,
            Path: store.Path,
            Fingerprint: Convert.ToHexString(KekFingerprint.Compute(masterKey.KeyBytes))));

    /// <summary>
    /// Start the re-auth ceremony, scoped to the calling admin's own credentials.
    /// </summary>
    private static async Task<IResult> BeginRevealAsync(
        FreshAssertionGate gate,
        CancellationToken cancellationToken)
    {
        var begin = await gate
            .BeginAsync(ChallengeStore.MasterKeyRevealFlow, cancellationToken)
            .ConfigureAwait(false);
        if (begin.Failure is not null) return begin.Failure;

        return Results.Ok(new MasterKeyContracts.RevealBeginResponse(
            begin.ChallengeId, begin.Options!));
    }

    /// <summary>
    /// Verify the assertion and return the key. Every failure path returns 401 or
    /// a business error — never the key.
    /// </summary>
    private static async Task<IResult> RevealAsync(
        MasterKeyContracts.RevealRequest request,
        HttpContext http,
        ICurrentUserAccessor currentUser,
        FreshAssertionGate gate,
        MasterKey masterKey,
        AdminAuditRepository audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var verified = await gate.VerifyAsync(
            ChallengeStore.MasterKeyRevealFlow,
            request.ChallengeId, request.AssertionResponse, cancellationToken)
            .ConfigureAwait(false);
        if (verified.Failure is not null) return verified.Failure;

        var userId = currentUser.UserId;
        var credential = verified.Credential!;

        // Durable audit (ADR-0092 D2) plus the log line. The row is the record an
        // operator can query later; the log is what a live tail shows. Written BEFORE
        // the key goes out, so a failure to record means the key isn't handed over —
        // an unaudited reveal is worse than a failed one.
        await audit.AppendAsync(
            AdminAuditActions.MasterKeyRevealed, userId,
            $"credential {credential.Id}", cancellationToken).ConfigureAwait(false);

        loggerFactory.CreateLogger("Coffer.Api.MasterKey").LogWarning(
            "Master KEK revealed to admin {UserId} after a fresh passkey assertion "
            + "(credential {CredentialId}).",
            userId, credential.Id);

        // Never cached, never stored by an intermediary.
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";

        return Results.Ok(new MasterKeyContracts.RevealResponse(
            KeyBase64: Convert.ToBase64String(masterKey.KeyBytes),
            KekId: masterKey.Id));
    }

    // There is no separate dry-run endpoint. Rotation runs the dry run itself as its
    // first step and refuses before touching anything (MasterKeyRotationCoordinator),
    // so a preview only ever produced a list that didn't change the operator's
    // decision — while implying rotation might skip the check unless it was asked for.
    // An endpoint with no caller is surface nobody exercises and everybody maintains.

    /// <summary>
    /// Generate a new key, re-wrap everything onto it, swap the key file, and
    /// restart onto the new key.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this replaces the CLI.</b> <c>rotate-kek</c> re-wraps the
    /// database in one transaction, then tells the operator to edit <c>.env</c> and
    /// restart. Between those steps the live process holds the OLD key over a
    /// database wrapped with the NEW one, and a ledger created in that window is
    /// wrapped under the old key — leaving mixed <c>lek_kek_id</c> values. Here the
    /// window is bounded by an immediate restart rather than by however long the
    /// operator takes.</para>
    ///
    /// <para><b>Order is chosen for crash-safety.</b> Archive the old file, write
    /// the new one, THEN re-wrap. A crash between the write and the commit leaves
    /// the file ahead of the database — recoverable, because the old key is sitting
    /// in the archive. The reverse order would leave the database ahead of the file
    /// with the new key existing nowhere, which is not recoverable. If the re-wrap
    /// throws, the archive is rolled back explicitly.</para>
    /// </remarks>
    private static async Task<IResult> RotateAsync(
        MasterKeyContracts.RotateRequest request,
        HttpContext http,
        ICurrentUserAccessor currentUser,
        FreshAssertionGate gate,
        MasterKey currentKey,
        MasterKeyStore store,
        MasterKeyRotationCoordinator coordinator,
        IApplicationRestarter restarter,
        AdminAuditRepository audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var verified = await gate.VerifyAsync(
            ChallengeStore.MasterKeyRevealFlow,
            request.ChallengeId, request.AssertionResponse, cancellationToken)
            .ConfigureAwait(false);
        if (verified.Failure is not null) return verified.Failure;

        var newId = MasterKeyLoader.NextKekId(currentKey.Id);
        var newKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var newKey = MasterKeyLoader.LoadFromValueOrThrow(newKeyBase64, newId, "rotation");

        // The file/database ordering — and every refusal — lives in the coordinator
        // (ADR-0092 D4). This handler owns the ceremony, the response, and the restart.
        var outcome = await coordinator
            .RotateAsync(currentKey, newKey, newKeyBase64, store, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Refusal != MasterKeyRotationCoordinator.Refusal.None)
            return BusinessError.Problem(
                BusinessError.Codes.MasterKeyRotateBlocked, outcome.Message!);

        var result = outcome.Result!;

        await audit.AppendAsync(
            AdminAuditActions.MasterKeyRotated, currentUser.UserId,
            $"'{currentKey.Id}' -> '{newId}'; {result.LedgersRotated} ledger key(s), "
            + $"passphrase={result.PassphraseRotated}, driveToken={result.DriveTokenRotated}",
            cancellationToken).ConfigureAwait(false);

        loggerFactory.CreateLogger("Coffer.Api.MasterKey").LogWarning(
            "Master KEK rotated to '{NewId}' by admin {UserId}: {Ledgers} ledger key(s), "
            + "passphrase={Pass}, driveToken={Drive}. Previous key archived at {Archive}. "
            + "Restarting to load the new key.",
            newId, currentUser.UserId, result.LedgersRotated, result.PassphraseRotated,
            result.DriveTokenRotated, outcome.PreviousKeyArchivedAt ?? "(none)");

        // The MasterKey singleton is immutable by design (ADR-0092 D2), so the only
        // way to pick up the new key is a restart — the same mechanism the bootstrap
        // restore uses. Fires after this response flushes.
        restarter.RequestRestart();

        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";

        return Results.Ok(new MasterKeyContracts.RotateResponse(
            KeyBase64: newKeyBase64,
            KekId: newId,
            LedgersRotated: result.LedgersRotated,
            BackupPassphraseRotated: result.PassphraseRotated,
            DriveTokenRotated: result.DriveTokenRotated,
            PreviousKeyArchivedAt: outcome.PreviousKeyArchivedAt,
            RestartPending: true));
    }

}
