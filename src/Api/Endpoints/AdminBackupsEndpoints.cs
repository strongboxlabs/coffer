using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Coffer.Api.Auth;
using Coffer.Api.Backup;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
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
/// (pre-auth) restore and <c>coffer-api restore</c> remain for their cases.
///
///   * POST   /api/admin/backups/restore    — restore from an uploaded .cofferbak
///   * POST   /api/admin/backups/restore/validate — pre-flight KEK-compat check (ADR-0074)
///   * POST   /api/admin/backups            — create one now (stored passphrase)
///   * GET    /api/admin/backups            — list stored artifacts
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

        group.MapPost("/", CreateAsync);
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
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with 'archive', 'passphrase', and 'confirm'.");

        // Lift Kestrel's per-request cap; a whole-DB backup can be sizeable (the
        // ~128 MB multipart limit is the UI ceiling, CLI restore for anything larger).
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var passphrase = form["passphrase"].ToString();
        var confirm = form["confirm"].ToString();
        var acknowledgeKekMismatch =
            string.Equals(form["acknowledgeKekMismatch"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();

        if (file is null || file.Length == 0 || string.IsNullOrEmpty(passphrase))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Both a backup file ('archive') and a 'passphrase' are required.");

        // Blunt typed-confirmation gate — a restore replaces everything.
        if (!string.Equals(confirm.Trim(), RestoreConfirmPhrase, StringComparison.OrdinalIgnoreCase))
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreConfirmRequired,
                $"Type “{RestoreConfirmPhrase}” to confirm — a restore replaces ALL users, ledgers, " +
                "and data across the deployment, and signs everyone out.");

        BootstrapRestoreStaging.EnsureDir();
        await using (var dest = File.Create(BootstrapRestoreStaging.ArchivePath))
            await file.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);

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
                    "This backup was sealed under a different Master KEK. For a clean migration set "
                    + "COFFER_MASTER_KEK_BASE64 to the source install's value and re-upload; or proceed "
                    + "anyway — your data and passkeys restore, but you'll need to re-set the backup "
                    + "passphrase and reconnect Google Drive afterward.");
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

        await BootstrapRestoreStaging.CommitAsync(passphrase, cancellationToken).ConfigureAwait(false);

        // Restart so the next boot applies the staged restore before serving. All
        // sessions (including this admin's) die with the old DB; the SPA polls
        // until the server is back, then lands on /login (or /setup).
        restarter.RequestRestart();
        return Results.Json(new { restarting = true }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> CreateAsync(
        BackupManager manager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!await manager.IsPassphraseConfiguredAsync(cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseNotSet,
                "Set a backup passphrase before creating a backup.");

        try
        {
            var info = await manager.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
            // A freshly-created artifact is never pinned yet.
            return Results.Created($"/api/admin/backups/{info.Id}", ToSummary(info, pinned: false));
        }
        catch (BackupException ex)
        {
            // Operational failure (pg_dump missing/failed, or the sealed
            // passphrase can't be opened) — a 500, not a business rejection.
            // Log the detail; don't leak internals to the client.
            loggerFactory.CreateLogger("AdminBackups")
                .LogError(ex, "Manual backup failed.");
            return Results.Problem("Backup failed. Check the server logs.", statusCode: 500);
        }
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
        string id, BackupStore store, BackupPinsRepository pins, CancellationToken cancellationToken)
    {
        // Idempotent: an unknown id still returns 204 (no info leak about which
        // artifacts exist), matching the snapshot delete convention.
        store.Delete(id);
        // Drop any pin so deleting a pinned artifact doesn't orphan the pin row.
        await pins.UnpinAsync(id, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPassphraseAsync(
        SetBackupPassphraseRequest? request,
        ICurrentUserAccessor currentUser,
        BackupManager manager,
        CancellationToken cancellationToken)
    {
        var passphrase = request?.Passphrase ?? string.Empty;
        if (passphrase.Length < MinPassphraseLength)
            return BusinessError.Problem(BusinessError.Codes.BackupPassphraseInvalid,
                $"Passphrase must be at least {MinPassphraseLength} characters.");

        await manager.SetPassphraseAsync(passphrase, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
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
            currentUser.UserId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToScheduleResponse(state));
    }

    private static async Task<IResult> ValidateRestoreAsync(
        HttpRequest request,
        MasterKey masterKey,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "Send multipart/form-data with 'archive' (the backup file or its leading bytes).");

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "A backup file ('archive') is required.");
        try
        {
            // Reads only the header (no passphrase needed) — the UI uploads just a
            // small leading slice, so this never streams a whole backup.
            await using var stream = file.OpenReadStream();
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
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.RetentionDaily is < 0 or > MaxRetention
            || request.RetentionWeekly is < 0 or > MaxRetention
            || request.RetentionMonthly is < 0 or > MaxRetention)
            return BusinessError.Problem(BusinessError.Codes.BackupRetentionInvalid,
                $"Each retention tier must be between 0 and {MaxRetention}.");

        await settings.SetRetentionAsync(
            (short)request.RetentionDaily, (short)request.RetentionWeekly, (short)request.RetentionMonthly,
            currentUser.UserId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BackupRetentionResponse(
            request.RetentionDaily, request.RetentionWeekly, request.RetentionMonthly));
    }

    private static BackupSummary ToSummary(BackupFileInfo info, bool pinned) =>
        new(info.Id, info.SizeBytes, info.CreatedAtUtc, pinned);

    private static BackupScheduleResponse ToScheduleResponse(GlobalScheduleState state) =>
        new(
            state.Schedule.Enabled,
            state.Schedule.HourLocal,
            state.Schedule.MinuteLocal,
            state.Schedule.Timezone,
            state.Schedule.LastRunAt,
            state.Schedule.NextRunAt,
            state.PassphraseConfigured);
}
