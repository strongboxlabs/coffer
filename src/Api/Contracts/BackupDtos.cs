namespace Coffer.Api.Contracts;

/// <summary>
/// Wire shapes for the admin backup surface (ADR-0060,
/// <c>/api/admin/backups</c>). The encrypted artifact bytes and the passphrase
/// never appear here — only metadata + a "configured" flag.
/// </summary>
public static class BackupContracts
{
    /// <summary>One stored backup artifact. <see cref="Id"/> is the opaque
    /// filename stem used in the download / delete / pin routes.
    /// <see cref="Pinned"/> = a "never delete" pin (ADR-0062): excluded from local
    /// and Drive retention.</summary>
    public sealed record BackupSummary(string Id, long SizeBytes, DateTime CreatedAtUtc, bool Pinned);

    /// <summary>Body for <c>PUT /api/admin/backups/passphrase</c>.</summary>
    public sealed record SetBackupPassphraseRequest(string Passphrase);

    /// <summary>Response for the backup schedule (<c>GET/PUT
    /// /api/admin/backups/schedule</c>): the daily schedule plus whether a
    /// passphrase has been set (the panel disables enabling until it is).</summary>
    public sealed record BackupScheduleResponse(
        bool Enabled,
        int HourLocal,
        int MinuteLocal,
        string? Timezone,
        DateTime? LastRunAt,
        DateTime? NextRunAt,
        bool PassphraseConfigured);

    /// <summary>Body for <c>PUT /api/admin/backups/schedule</c>.</summary>
    public sealed record SetBackupScheduleRequest(
        bool Enabled,
        int HourLocal,
        int MinuteLocal,
        string? Timezone = null);

    /// <summary>Response for the backup retention policy (<c>GET/PUT
    /// /api/admin/backups/retention</c>, ADR-0074). The GFS tiers — the single
    /// source of truth that governs local pruning AND the Google Drive mirror.</summary>
    public sealed record BackupRetentionResponse(
        int RetentionDaily,
        int RetentionWeekly,
        int RetentionMonthly);

    /// <summary>Body for <c>PUT /api/admin/backups/retention</c>.</summary>
    public sealed record SetBackupRetentionRequest(
        int RetentionDaily,
        int RetentionWeekly,
        int RetentionMonthly);

    /// <summary>Pre-flight KEK-compatibility check for a restore
    /// (<c>POST /api/admin/backups/restore/validate</c>, ADR-0074 / ADR-0071 D4).
    /// <see cref="HasFingerprint"/> is false for a v1 backup (no fingerprint —
    /// unverifiable). <see cref="Compatible"/> = the backup was sealed under THIS
    /// install's Master KEK, so its sealed secrets (backup passphrase, Drive
    /// token) will unseal after restore; when false, a cross-install restore
    /// still restores data + passkeys but those secrets need re-establishing.</summary>
    public sealed record BackupKekCheckResponse(bool HasFingerprint, bool Compatible);
}
