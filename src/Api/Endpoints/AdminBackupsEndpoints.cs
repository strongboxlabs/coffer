using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Errors;
using Coffer.Api.Scheduling;

using static Coffer.Api.Contracts.BackupContracts;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin-only whole-DB backup surface (ADR-0060). Every route is gated by the
/// <see cref="AuthPolicies.RequireAdmin"/> policy — backups are a
/// deployment-global capability, not per-ledger.
///
/// Restore (ADR-0071 D3, amending ADR-0060) is now also an authenticated-admin
/// action here: it reuses the ADR-0061 stage → restart → apply-at-boot machinery
/// behind a typed-confirmation gate + the ADR-0071 D4 KEK check. The bootstrap
/// (pre-auth) restore remains for a fresh install with no admin to authenticate as;
/// the <c>coffer-api restore</c> CLI was removed by ADR-0094.
///
///   * POST   /api/admin/backups/restore    — restore from an uploaded .cofferbak
///   * POST   /api/admin/backups/restore/validate — pre-flight KEK-compat check (ADR-0074)
///   * POST   /api/admin/backups            — create one now (stored passphrase)
///   * GET    /api/admin/backups            — list stored artifacts
///   * POST   /api/admin/backups/restore/parts — upload one part of a large restore
///   * GET    /api/admin/backups/restore/limits — the configured part size
///   * GET    /api/admin/backups/{id}       — download a .cofferbak
///   * DELETE /api/admin/backups/{id}       — delete one
///   * PUT    /api/admin/backups/passphrase — set / rotate the backup passphrase
///   * GET    /api/admin/backups/schedule   — read the daily schedule
///   * PUT    /api/admin/backups/schedule   — set the daily schedule
///   * GET    /api/admin/backups/retention  — read the retention policy (ADR-0074)
///   * PUT    /api/admin/backups/retention  — set the retention policy (ADR-0074)
/// </summary>
public static class AdminBackupsEndpoints
{
    /// <summary>Minimum backup passphrase length. The passphrase is the only
    /// thing protecting an exported artifact at rest, so reject trivially-short
    /// ones; Argon2id (ADR-0037 params) absorbs the rest.</summary>
    public const int MinPassphraseLength = 8;

    /// <summary>Upper bound per retention tier — guards a fat-finger that would
    /// keep everything (3650 ≈ 10 years of daily).</summary>
    public const short MaxRetention = 3650;

    public static IEndpointRouteBuilder MapAdminBackupsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/backups")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        // Literal sub-routes before the {id} catch-all (literals win in routing
        // precedence regardless, but declaring them first reads clearly).
        group.MapPut("/passphrase", SetPassphraseAsync);
        // Reveal the stored passphrase (ADR-0092 D7). POST, not GET, so the secret
        // never lands in a URL, a referrer, or history — same shape and same step-up
        // as the master-KEK reveal.
        group.MapPost("/passphrase/reveal/begin", BeginRevealPassphraseAsync);
        group.MapPost("/passphrase/reveal", RevealPassphraseAsync);
        group.MapGet("/schedule", GetScheduleAsync);
        group.MapPut("/schedule", PutScheduleAsync);
        group.MapGet("/retention", GetRetentionAsync);
        group.MapPut("/retention", PutRetentionAsync);
        // Authenticated-admin restore (ADR-0071 D3). Multipart upload, so opt out
        // of antiforgery (the admin session cookie is the auth).
        group.MapPost("/restore", RestoreAsync).DisableAntiforgery();
        // Pre-flight KEK-compatibility check (ADR-0074): the UI uploads just the
        // backup's header to learn, before committing, whether it was sealed under
        // this install's Master KEK.
        group.MapPost("/restore/validate", ValidateRestoreAsync).DisableAntiforgery();
        group.MapPost("/restore/parts", UploadRestorePartAsync).DisableAntiforgery();
        group.MapGet("/restore/limits", (BackupStore store) =>
            Results.Ok(new { partSizeBytes = store.PartSizeBytes }));

        group.MapPost("/", CreateAsync);
        group.MapGet("/runs/{runId:guid}", GetRunAsync);
        group.MapGet("/", ListAsync);
        group.MapPost("/{id}/pin", PinAsync);
        group.MapDelete("/{id}/pin", UnpinAsync);
        group.MapGet("/{id}", DownloadAsync);
        group.MapDelete("/{id}", DeleteAsync);
        return routes;
    }

    /// <summary>The exact phrase an admin must type to confirm a restore.</summary>
    public const string RestoreConfirmPhrase = "yes i agree";

    /// <summary>
    /// <c>POST /api/admin/backups/restore</c> (ADR-0071 D3). Restore the whole
    /// database from an uploaded <c>.cofferbak</c>. Reuses the ADR-0061
    /// stage → restart → apply-at-boot machinery: stages the archive + passphrase,
    /// pre-flights the KEK fingerprint (D4) and the passphrase, then restarts so
    /// the next boot applies the restore before serving. Destructive: replaces
    /// ALL users, ledgers, and data, and signs everyone out — gated by an exact
    /// typed-confirmation phrase.
    /// </summary>
    private static async Task<IResult> RestoreAsync(
        HttpRequest request,
        MasterKey masterKey,
        IApplicationRestarter restarter,
        BackupStore store,
        BackupService backups,
        StepUpGuard stepUp,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        // A restore replaces every row and can install a master key: a passkey tap, not
        // just a cookie (2026-10-12 audit, M2) — checked before anything is staged.
        var approval = await stepUp.RequireAsync(StepUpActions.Restore, cancellationToken)
                                   .ConfigureAwait(false);
        if (approval.Failure is not null)
            return approval.Failure;

        // A restore builds the backup as a new database and swaps it in, which the
        // service role may only do once provisioned (migration 246). Refuse now,
        // before the upload is read, with the fix — not after a restart, from a log.
        var readiness = await backups.CheckRestoreReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (!readiness.IsReady)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreNotProvisioned, readiness.Explain());

        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with 'archive', 'passphrase', and 'confirm'.");

        // Lift Kestrel's per-request cap; a whole-DB backup can be sizeable. The
        // multipart form limit is raised to 4 GiB in Program.cs (ADR-0094) — this is
        // the only restore path now, so it cannot be the narrower one.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var passphrase = form["passphrase"].ToString();
        var confirm = form["confirm"].ToString();
        var acknowledgeKekMismatch =
            string.Equals(form["acknowledgeKekMismatch"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();

        // A stored backup is restored by NAME, not by sending it back. The
        // artifact is already on this disk — Coffer wrote it there — so uploading
        // it is a round trip of the whole archive to reach a file the server is
        // sitting on. It is also the round trip that fails: a large .cofferbak
        // exceeds Cloudflare's 100 MB request-body cap, which is not a setting on
        // Free or Pro, so the documented "raise your proxy's limit" remedy does
        // not exist for that deployment shape.
        //
        // Upload stays for the case that genuinely needs it: a fresh install, or
        // an artifact kept somewhere else, where the file is not on this disk.
        var storedId = form["backupId"].ToString().Trim();
        var hasUpload = file is not null && file.Length > 0;
        // The third source: an artifact sent ahead of this call in pieces, because
        // it was too big to send in one (ADR-0101).
        var useParts = string.Equals(
            form["useUploadedParts"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var sources = (storedId.Length > 0 ? 1 : 0) + (hasUpload ? 1 : 0) + (useParts ? 1 : 0);
        if (sources > 1)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Name one backup only — 'archive', 'backupId', or 'useUploadedParts' — "
                + "otherwise which one is being restored is ambiguous.");

        if (sources == 0)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "A backup is required: upload one ('archive'), name a stored one "
                + "('backupId'), or finish a part upload ('useUploadedParts').");

        if (string.IsNullOrEmpty(passphrase))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "A 'passphrase' is required.");

        // Blunt typed-confirmation gate — a restore replaces everything.
        if (!string.Equals(confirm.Trim(), RestoreConfirmPhrase, StringComparison.OrdinalIgnoreCase))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreConfirmRequired,
                $"Type “{RestoreConfirmPhrase}” to confirm — a restore replaces ALL users, ledgers, " +
                "and data across the deployment, and signs everyone out.");

        BootstrapRestoreStaging.EnsureDir();
        // A source key staged by an earlier attempt that never finished must not be adopted
        // by this one, which may have supplied none (FU-073): this restore's own key, if it
        // has one, is staged below.
        BootstrapRestoreStaging.ClearSourceKey();
        if (useParts)
        {
            // The parts were checked for order and size as they arrived; all that
            // is left is that something arrived at all. If the set is short, the
            // passphrase check below fails on a truncated archive — before
            // anything destructive, which is where every other bad input lands too.
            if (!BootstrapRestoreStaging.PromoteUpload())
                return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                    "No uploaded parts to restore — send the parts first.");
        }
        else if (storedId.Length > 0)
        {
            // Resolved through the store rather than by pasting the id into a path:
            // the id comes from a request, and building a path out of it invites
            // traversal. OpenRead returns null for anything the store does not own.
            await using var src = store.OpenRead(storedId);
            if (src is null)
                return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                    "No stored backup with that id. It may have aged out under the "
                    + "retention policy since the list was loaded.");
            // A copy of a stored backup is the same bytes again on the same volume (FU-072).
            if (BootstrapRestoreStaging.WouldFillTheVolume(
                    src.Length, options.Value.Backup.RestoreFreeSpaceReserveBytes) is { } full)
                return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid, full);
            await using var dest = File.Create(BootstrapRestoreStaging.ArchivePath);
            await src.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (BootstrapRestoreStaging.WouldFillTheVolume(
                    file!.Length, options.Value.Backup.RestoreFreeSpaceReserveBytes) is { } full)
                return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid, full);
            await using var dest = File.Create(BootstrapRestoreStaging.ArchivePath);
            await file.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }

        // Adopt path (ADR-0092 D4): the operator has the SOURCE install's key and
        // wants its sealed secrets carried over rather than re-established. Checked here —
        // before anything destructive — and staged for the boot that adopts it, by the
        // same rules as the setup-screen restore. An empty field means "reconcile instead"
        // (ADR-0092 D5).
        var (keyFailure, keyStaged) = await RestoreSourceKey
            .StageIfSuppliedAsync(form[RestoreSourceKey.FormField].ToString(), cancellationToken)
            .ConfigureAwait(false);
        if (keyFailure is not null)
            return keyFailure;
        // The mismatch acknowledgement below is about restoring WITHOUT the source key. A
        // verified source key makes it moot.
        if (keyStaged)
            acknowledgeKekMismatch = true;

        // Pre-flight 1 (ADR-0071 D4): KEK fingerprint. A mismatch means the
        // backup's sealed secrets (backup passphrase, Drive token) won't unseal
        // under this install's KEK — a cross-install migration. The data +
        // passkeys still restore, so it's not a hard block: warn + require an
        // explicit acknowledgement. A v1 backup carries no fingerprint (empty) —
        // can't verify, so allow.
        try
        {
            await using var fpStream = File.OpenRead(BootstrapRestoreStaging.ArchivePath);
            var backupFingerprint = await BackupCrypto.ReadKekFingerprintAsync(fpStream, cancellationToken)
                .ConfigureAwait(false);
            var currentFingerprint = KekFingerprint.Compute(masterKey.KeyBytes);
            if (backupFingerprint.Length > 0
                && !KekFingerprint.Matches(backupFingerprint, currentFingerprint)
                && !acknowledgeKekMismatch)
            {
                BootstrapRestoreStaging.Clear();
                return BusinessError.Problem(BusinessError.Codes.BackupKekMismatch,
                    "This backup was sealed under a different Master KEK. For a clean migration, put "
                    + "the source install's key file in place (Api:MasterKey:Path) and re-upload; or "
                    + "proceed anyway — your data and passkeys restore intact, but the secrets sealed "
                    + "under the old key are cleared afterward: re-link your bank feeds, set a new "
                    + "backup passphrase, and reconnect Google Drive.");
            }
        }
        catch (BackupDecryptException)
        {
            BootstrapRestoreStaging.Clear();
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "That file isn't a Coffer backup. Nothing was changed.");
        }

        // Pre-flight 2: the passphrase actually opens it — fail now, not in a
        // post-restart boot loop.
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

        var attemptId = await BootstrapRestoreStaging.CommitAsync(passphrase, cancellationToken)
            .ConfigureAwait(false);

        // Restart so the next boot applies the staged restore before serving. The
        // SPA reads that boot's recorded outcome by attemptId once the server is
        // back (RestoreOutcomeEndpoints) — never inferring success from a restart.
        restarter.RequestRestart();
        return Results.Json(new { restarting = true, attemptId }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// <c>POST /api/admin/backups</c>. Accepts a manual backup and answers 202 with a run
    /// id; the backup runs on the host, not in this request (FU-065). It used to run inside
    /// the request and die with it: a proxy cancels a request at its timeout (Cloudflare's is
    /// 100 seconds), and a backup of a growing ledger outlasts that.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        BackupManager manager,
        BackupRunCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!await manager.IsPassphraseConfiguredAsync(cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet,
                "Set a backup passphrase before creating a backup.");

        var run = coordinator.TryStartManual();
        if (run is null)
            return BusinessError.Problem(BusinessError.Codes.BackupInProgress,
                "A backup is already running. Wait for it to finish; it will appear in the list.");

        return Results.Json(new BackupRunAccepted(run.RunId), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary><c>GET /api/admin/backups/runs/{runId}</c>: how a manual backup is going.
    /// 404 for a run this process does not know (never issued, aged out, or the server
    /// restarted since: the list is then the place to look).</summary>
    private static async Task<IResult> GetRunAsync(
        Guid runId, BackupRunCoordinator coordinator, BackupStore store, BackupPinsRepository pins,
        CancellationToken cancellationToken)
    {
        var run = coordinator.Get(runId);
        if (run is null) return Results.NotFound();

        BackupSummary? backup = null;
        if (run.BackupId is { } id && store.List().FirstOrDefault(b => b.Id == id) is { } info)
        {
            var pinned = await pins.GetPinnedIdsAsync(cancellationToken).ConfigureAwait(false);
            backup = ToSummary(info, pinned.Contains(id));
        }
        return Results.Ok(new BackupRunResponse(
            run.RunId, run.State, run.StartedAtUtc, run.FinishedAtUtc, backup, run.Error));
    }

    private static async Task<IResult> ListAsync(
        BackupStore store, BackupPinsRepository pins, CancellationToken cancellationToken)
    {
        var pinned = await pins.GetPinnedIdsAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(store.List().Select(b => ToSummary(b, pinned.Contains(b.Id))).ToList());
    }

    private static async Task<IResult> PinAsync(
        string id, ICurrentUserAccessor currentUser, BackupStore store, BackupPinsRepository pins,
        CancellationToken cancellationToken)
    {
        // Only pin a real artifact (avoids orphan pins for typo'd ids).
        if (!store.Exists(id)) return Results.NotFound();
        await pins.PinAsync(id, currentUser.UserId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> UnpinAsync(
        string id, BackupPinsRepository pins, CancellationToken cancellationToken)
    {
        // Idempotent: unpinning an unknown/unpinned id is a no-op 204.
        await pins.UnpinAsync(id, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult DownloadAsync(string id, BackupStore store)
    {
        var stream = store.OpenRead(id);
        return stream is null
            ? Results.NotFound()
            // Results.File disposes the stream once the response is written.
            : Results.File(stream, "application/octet-stream", fileDownloadName: id + ".cofferbak");
    }

    private static async Task<IResult> DeleteAsync(
        string id, BackupStore store, BackupPinsRepository pins, DriveSyncRepository drive,
        TimeProvider clock, StepUpGuard stepUp, CancellationToken cancellationToken)
    {
        // Deleting backups is how a stolen session would destroy the recovery points
        // before damaging the data: a tap, not just a cookie (2026-10-12 audit, M2).
        var approval = await stepUp.RequireAsync(StepUpActions.BackupDelete, cancellationToken)
                                   .ConfigureAwait(false);
        if (approval.Failure is not null)
            return approval.Failure;

        // Idempotent: an unknown id still returns 204 (no info leak about which
        // artifacts exist), matching the snapshot delete convention.
        store.Delete(id);
        // Drop any pin so deleting a pinned artifact doesn't orphan the pin row.
        await pins.UnpinAsync(id, cancellationToken).ConfigureAwait(false);
        // This is the operator saying they do not want it, so the Drive copy may go at the
        // next mirror without waiting for retention (FU-064). A backup that merely VANISHED
        // from the local folder carries no such mark, and is held to retention instead.
        await drive.MarkDeletedAsync(id, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPassphraseAsync(
        SetBackupPassphraseRequest? request,
        ICurrentUserAccessor currentUser,
        BackupManager manager,
        StepUpGuard stepUp,
        CancellationToken cancellationToken)
    {
        // Setting a passphrase the caller knows, then taking and downloading a backup,
        // used to get round the tap that guards REVEALING the passphrase (2026-10-12
        // audit, M2). The old passphrase is not asked for: the tap proves the same
        // person, and asking would strand an operator who has forgotten it.
        var approval = await stepUp.RequireAsync(StepUpActions.BackupPassphrase, cancellationToken)
                                   .ConfigureAwait(false);
        if (approval.Failure is not null)
            return approval.Failure;

        var passphrase = request?.Passphrase ?? string.Empty;
        if (passphrase.Length < MinPassphraseLength)
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseInvalid,
                $"Passphrase must be at least {MinPassphraseLength} characters.");

        await manager.SetPassphraseAsync(passphrase, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Start the step-up ceremony for a passphrase reveal (ADR-0092 D7).
    /// </summary>
    private static async Task<IResult> BeginRevealPassphraseAsync(
        FreshAssertionGate gate,
        BackupManager manager,
        CancellationToken cancellationToken)
    {
        // Fail before the ceremony when there is nothing to reveal — otherwise the
        // operator taps their authenticator only to be told "not configured".
        if (!await manager.IsPassphraseConfiguredAsync(cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet,
                "No backup passphrase is set, so there is nothing to show.");

        var begin = await gate
            .BeginAsync(ChallengeStore.BackupPassphraseRevealFlow, cancellationToken)
            .ConfigureAwait(false);
        if (begin.Failure is not null) return begin.Failure;

        return Results.Ok(new MasterKeyContracts.RevealBeginResponse(
            begin.ChallengeId, begin.Options!));
    }

    /// <summary>
    /// Receive one part of a restore artifact too large to send in one request
    /// (ADR-0101). Parts arrive in order and accumulate in staging; the restore
    /// itself is a separate, confirmed call with <c>useUploadedParts=true</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT gated by the typed confirmation: nothing here is
    /// destructive — an upload never followed by a confirmed restore leaves a
    /// file in staging and no more. Putting the gate on the upload instead would
    /// mean typing the phrase before a transfer that can take minutes, and then
    /// acting on a confirmation given long before the thing it confirmed existed.
    /// <para>The assembly rules live in <see cref="RestorePartProtocol"/>,
    /// shared with the pre-auth bootstrap restore: the two paths must agree on
    /// how bytes are stitched together, and nothing would catch them drifting
    /// short of a restore that fails to decrypt.</para>
    /// </remarks>
    private static async Task<IResult> UploadRestorePartAsync(
        HttpRequest request, IOptions<ApiOptions> options, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with 'archive', 'part', and 'partCount'.");

        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        return await RestorePartProtocol.AcceptAsync(
            form, options.Value.Backup.RestoreFreeSpaceReserveBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verify the assertion and return the stored backup passphrase (ADR-0092 D7).
    /// </summary>
    private static async Task<IResult> RevealPassphraseAsync(
        MasterKeyContracts.RevealRequest request,
        HttpContext http,
        FreshAssertionGate gate,
        BackupManager manager,
        ICurrentUserAccessor currentUser,
        AdminAuditRepository audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var verified = await gate.VerifyAsync(
            ChallengeStore.BackupPassphraseRevealFlow,
            request.ChallengeId, request.AssertionResponse, cancellationToken)
            .ConfigureAwait(false);
        if (verified.Failure is not null) return verified.Failure;

        string passphrase;
        try
        {
            passphrase = await manager.RevealPassphraseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BackupException ex)
        {
            // Most likely a cross-KEK restore that left the ciphertext unopenable —
            // BackupManager already translates that into a readable message.
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet, ex.Message);
        }

        // Audited BEFORE the secret goes out, same as the master-KEK reveal: an
        // unaudited disclosure is worse than a failed one.
        await audit.AppendAsync(
            AdminAuditActions.BackupPassphraseRevealed, currentUser.UserId,
            $"credential {verified.Credential!.Id}", cancellationToken).ConfigureAwait(false);

        loggerFactory.CreateLogger("Coffer.Api.Backup").LogWarning(
            "Backup passphrase revealed to admin {UserId} after a fresh passkey assertion.",
            currentUser.UserId);

        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";

        return Results.Ok(new BackupContracts.RevealPassphraseResponse(passphrase));
    }

    private static async Task<IResult> GetScheduleAsync(
        GlobalSchedulesRepository schedules,
        CancellationToken cancellationToken)
    {
        var state = await schedules.GetAsync(GlobalJobTypes.Backup, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
            // Never configured: backups off, default 3am, no passphrase yet.
            return Results.Ok(new BackupScheduleResponse(
                Enabled: false, HourLocal: 3, MinuteLocal: 0, Timezone: null,
                LastRunAt: null, NextRunAt: null, PassphraseConfigured: false));
        return Results.Ok(ToScheduleResponse(state));
    }

    private static async Task<IResult> PutScheduleAsync(
        SetBackupScheduleRequest? request,
        ICurrentUserAccessor currentUser,
        GlobalSchedulesRepository schedules,
        BackupManager manager,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || request.HourLocal is < 0 or > 23 || request.MinuteLocal is < 0 or > 59)
            return BusinessError.Problem(BusinessError.Codes.ScheduleInvalid,
                "Hour must be 0–23 and minute 0–59.");

        // Can't run an unattended backup with no passphrase to encrypt under.
        if (request.Enabled
            && !await manager.IsPassphraseConfiguredAsync(cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet,
                "Set a backup passphrase before enabling the schedule.");

        var state = await schedules.UpsertScheduleAsync(
            GlobalJobTypes.Backup, request.Enabled,
            (short)request.HourLocal, (short)request.MinuteLocal, request.Timezone,
            currentUser.UserId, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToScheduleResponse(state));
    }

    private static async Task<IResult> ValidateRestoreAsync(
        HttpRequest request,
        MasterKey masterKey,
        BackupStore store,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with 'archive' (the backup file or its "
                + "leading bytes) or 'backupId' (a stored backup).");

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();
        // A stored backup is checked in place. Without this the pre-flight would be
        // the one step still demanding an upload, and the KEK answer would be
        // missing from exactly the path that exists to avoid uploading.
        var storedId = form["backupId"].ToString().Trim();
        if (storedId.Length == 0 && (file is null || file.Length == 0))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "A backup is required: either 'archive' or 'backupId'.");
        try
        {
            // Reads only the header (no passphrase needed) — the UI uploads just a
            // small leading slice, so this never streams a whole backup.
            await using var stream = storedId.Length > 0
                ? store.OpenRead(storedId)
                : file!.OpenReadStream();
            if (stream is null)
                return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                    "No stored backup with that id.");
            var backupFingerprint = await BackupCrypto.ReadKekFingerprintAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            var hasFingerprint = backupFingerprint.Length > 0;
            var compatible = hasFingerprint
                && KekFingerprint.Matches(backupFingerprint, KekFingerprint.Compute(masterKey.KeyBytes));
            return Results.Ok(new BackupKekCheckResponse(hasFingerprint, compatible));
        }
        catch (BackupDecryptException)
        {
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "That file isn't a valid .cofferbak backup.");
        }
    }

    private static async Task<IResult> GetRetentionAsync(
        BackupSettingsRepository settings, CancellationToken cancellationToken)
    {
        var policy = await settings.GetRetentionAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BackupRetentionResponse(
            policy.DailyDays, policy.WeeklyWeeks, policy.MonthlyMonths));
    }

    private static async Task<IResult> PutRetentionAsync(
        SetBackupRetentionRequest? request,
        ICurrentUserAccessor currentUser,
        BackupSettingsRepository settings,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.RetentionDaily is < 1 or > MaxRetention
            || request.RetentionWeekly is < 0 or > MaxRetention
            || request.RetentionMonthly is < 0 or > MaxRetention)
            return BusinessError.Problem(BusinessError.Codes.BackupRetentionInvalid,
                $"The daily tier must be between 1 and {MaxRetention} days (at least one day of "
                + $"backups is always kept); the weekly and monthly tiers between 0 and {MaxRetention}.");

        await settings.SetRetentionAsync(
            (short)request.RetentionDaily, (short)request.RetentionWeekly, (short)request.RetentionMonthly,
            currentUser.UserId, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BackupRetentionResponse(
            request.RetentionDaily, request.RetentionWeekly, request.RetentionMonthly));
    }

    private static BackupSummary ToSummary(BackupFileInfo info, bool pinned) =>
        new(info.Id, info.SizeBytes, info.CreatedAtUtc, pinned);

    // Named, for the reason ScheduleDto's projection is: this record now has optional
    // trailing parameters, and positional construction would map the wrong values the
    // first time one is inserted rather than appended.
    private static BackupScheduleResponse ToScheduleResponse(GlobalScheduleState state) =>
        new(
            Enabled: state.Schedule.Enabled,
            HourLocal: state.Schedule.HourLocal,
            MinuteLocal: state.Schedule.MinuteLocal,
            Timezone: state.Schedule.Timezone,
            LastRunAt: state.Schedule.LastRunAt,
            NextRunAt: state.Schedule.NextRunAt,
            PassphraseConfigured: state.PassphraseConfigured,
            ConsecutiveFailures: state.Schedule.ConsecutiveFailures,
            LastError: state.Schedule.LastError,
            LastFailureAt: state.Schedule.LastFailureAt,
            DisabledReason: state.Schedule.DisabledReason,
            LastSuccessAt: state.Schedule.LastSuccessAt,
            NothingDoneSince: state.Schedule.NothingDoneSince);
}
