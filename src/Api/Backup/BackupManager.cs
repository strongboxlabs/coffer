using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Backup;

/// <summary>
/// Business-layer orchestration for backups (ADR-0060), used by both the admin
/// endpoints and the scheduled job. Ties together the passphrase (the one
/// admin-set secret, sealed under the master KEK in <c>global_scheduled_jobs</c>),
/// the <see cref="BackupService"/> engine, and the <see cref="BackupStore"/>
/// filesystem. The plaintext passphrase only ever lives on the stack here —
/// the DB holds the KEK-sealed form, never the cleartext.
/// </summary>
public sealed class BackupManager
{
    private readonly GlobalSchedulesRepository _schedules;
    private readonly LedgerKeyService _keys;
    private readonly BackupService _engine;
    private readonly BackupStore _store;
    private readonly BackupPinsRepository _pins;
    private readonly BackupSettingsRepository _settings;
    private readonly IEnumerable<IBackupDestination> _destinations;
    private readonly ILogger<BackupManager> _logger;

    public BackupManager(
        GlobalSchedulesRepository schedules,
        LedgerKeyService keys,
        BackupService engine,
        BackupStore store,
        BackupPinsRepository pins,
        BackupSettingsRepository settings,
        IEnumerable<IBackupDestination> destinations,
        ILogger<BackupManager> logger)
    {
        _schedules = schedules;
        _keys = keys;
        _engine = engine;
        _store = store;
        _pins = pins;
        _settings = settings;
        _destinations = destinations;
        _logger = logger;
    }

    /// <summary>Set (or rotate) the backup passphrase: seal it under the master
    /// KEK and persist. Used for both manual and scheduled backups.</summary>
    public async Task SetPassphraseAsync(string passphrase, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        var sealed_ = _keys.SealWithMasterKey(Encoding.UTF8.GetBytes(passphrase));
        await _schedules.SetPassphraseCiphertextAsync(
            GlobalJobTypes.Backup, sealed_, actorUserId, DateTime.UtcNow, ct).ConfigureAwait(false);
        _logger.LogInformation("Backup passphrase set by user {UserId}.", actorUserId);
    }

    /// <summary>True when an admin has set a backup passphrase.</summary>
    public async Task<bool> IsPassphraseConfiguredAsync(CancellationToken ct = default)
    {
        var sealed_ = await _schedules.GetPassphraseCiphertextAsync(GlobalJobTypes.Backup, ct)
            .ConfigureAwait(false);
        return sealed_ is { Length: > 0 };
    }

    /// <summary>
    /// Create a backup now: resolve the passphrase, run pg_dump → encrypt into a
    /// freshly stored artifact, apply retention. Throws
    /// <see cref="BackupException"/> when no passphrase is configured or the
    /// sealed passphrase can't be opened (master KEK changed).
    /// </summary>
    public async Task<BackupFileInfo> CreateBackupAsync(CancellationToken ct = default)
    {
        var pinned = await _pins.GetPinnedIdsAsync(ct).ConfigureAwait(false);
        var retention = await _settings.GetRetentionAsync(ct).ConfigureAwait(false);
        var passphrase = await ResolvePassphraseAsync(ct).ConfigureAwait(false);
        BackupFileInfo info;
        try
        {
            info = await _store.CreateAsync(
                (stream, token) => _engine.CreateAsync(passphrase, stream, token),
                retention, pinned, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // best-effort: drop our reference promptly (string is immutable, so
            // this only helps GC, not zeroing — the engine handles its own).
            passphrase = string.Empty;
        }

        // Push to each enabled off-host destination + reconcile its retention
        // (ADR-0062 ④b+c). A push failure NEVER fails the backup — the local
        // artifact is already safe; the destination records its own last-sync error.
        await PushToDestinationsAsync(pinned, ct).ConfigureAwait(false);
        return info;
    }

    private async Task PushToDestinationsAsync(IReadOnlySet<string> pinned, CancellationToken ct)
    {
        foreach (var dest in _destinations)
        {
            try
            {
                if (await dest.IsEnabledAsync(ct).ConfigureAwait(false))
                    await dest.PushLatestAsync(pinned, DateTime.UtcNow, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Backup push to {Destination} failed (the local backup itself succeeded).", dest.Name);
            }
        }
    }

    /// <summary>Open the sealed passphrase, or throw <see cref="BackupException"/>
    /// when none is set / it can't be unsealed.</summary>
    /// <summary>
    /// The stored backup passphrase in cleartext (ADR-0092 D7). Callers MUST gate this
    /// behind <c>FreshAssertionGate</c> and audit it — it is the secret that decrypts
    /// every artifact this install has produced.
    /// </summary>
    /// <remarks>
    /// Exists because the alternative was worse. The passphrase is sealed under the
    /// master KEK and the server reads it on every scheduled backup, so it was always
    /// recoverable in principle; offering no way meant an operator who forgot it
    /// accumulated backups that all still succeeded and were all unrestorable, with
    /// nothing in the product saying so. Revealing it to an admin who can already read
    /// every ledger in plaintext — and who could mint a fresh backup under a known
    /// passphrase anyway — costs approximately nothing against that.
    /// </remarks>
    /// <exception cref="BackupException">No passphrase set, or it doesn't open under
    /// the current KEK.</exception>
    public Task<string> RevealPassphraseAsync(CancellationToken ct = default)
        => ResolvePassphraseAsync(ct);

    private async Task<string> ResolvePassphraseAsync(CancellationToken ct)
    {
        var sealed_ = await _schedules.GetPassphraseCiphertextAsync(GlobalJobTypes.Backup, ct)
            .ConfigureAwait(false);
        if (sealed_ is not { Length: > 0 })
            throw new BackupException("No backup passphrase is configured.");
        try
        {
            return Encoding.UTF8.GetString(_keys.OpenWithMasterKey(sealed_));
        }
        catch (CryptographicException ex)
        {
            throw new BackupException(
                "The stored backup passphrase could not be opened — has the master KEK changed?", ex);
        }
    }
}
