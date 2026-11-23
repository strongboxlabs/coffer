using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Crypto;

/// <summary>
/// Finishes a <c>--adopt-new-kek</c> run (ADR-0092 D3): every secret the lost key sealed is
/// replaced or cleared under the newly minted key, an audit event records it in the same
/// transaction, the OAuth server keys are stored under the new key, and only then is that
/// key saved.
/// </summary>
/// <remarks>
/// <para><b>The issue.</b> Minting a key over wrapped material leaves that material sealed
/// under a key nobody has. Left in place it fails every use, and it aborts every later
/// rotation, which refuses anything that does not open. Reconciliation
/// (<see cref="KekReconciliationService"/>, ADR-0092 D5) is what removes it, so adoption runs it,
/// under the new key, which opens none of it.</para>
/// <para><b>Order: the clearing, the first sealed secret, then the key file.</b> The caller
/// holds <see cref="MasterKeyMintLock"/> throughout, and it is checked immediately before
/// the clearing (lost during the migrations, another start may have sealed its OAuth keys,
/// which the clearing would delete) and immediately before the key write. Reconciliation is
/// one transaction.</para>
/// <list type="bullet">
///   <item><description><b>The lock is lost before the clearing.</b> Nothing changes and no
///   key is saved.</description></item>
///   <item><description><b>Reconciliation fails.</b> No key is saved. If the failure came
///   before the commit, nothing changed. If the outcome of the commit itself is unknown (the
///   connection lost at that moment), the database may hold the cleared state, whose only
///   remaining sealed values are the fresh ledger keys wrapped under the unsaved key.
///   Either way the run can be repeated as it was.</description></item>
///   <item><description><b>Storing the OAuth keys, the lock check or saving the key fails
///   after the commit.</b> No key is saved. The database holds the cleared state plus what
///   this run sealed under the unsaved key: fresh ledger keys, if there are ledgers, and the
///   OAuth pair, if its store got that far. On an install with ledgers that is always sealed
///   material, so the next start without a key refuses; on one without, only when the OAuth
///   pair was stored, and otherwise nothing sealed is left and a start without a key mints as
///   a virgin install would, losing nothing because nothing is left for any key to open. A
///   repeated adopt run replaces whatever this one sealed and loses nothing more, since
///   nothing else was sealed under it yet.</description></item>
/// </list>
/// <para>The opposite order would leave a saved key over uncleared material after a failed
/// reconciliation; the next start would find the key, never enter the adopt path, and serve
/// with the leftovers. Saving the key before the OAuth store would release the lock, on an
/// install without ledgers, over a saved key with nothing sealed: a start on another key
/// volume would read it as virgin and mint a second key.</para>
/// </remarks>
public static class NewKekAdoption
{
    /// <summary>Reconcile under the new key and audit it in one transaction, seal the new
    /// key's first secret, then save the key to <paramref name="store"/>.</summary>
    /// <param name="reconciliation">Built over the NEW key, so nothing the lost one sealed
    /// opens.</param>
    /// <param name="mintLock">The lock the caller holds (<see cref="MasterKeyMintLock"/>);
    /// checked immediately before the clearing and again immediately before the key is
    /// written, and either step is refused when it was lost.</param>
    /// <param name="sealFirstSecret">Stores the OAuth server keys under the new key and says
    /// whether it did. Run before the key is saved, so the lock is never released over a
    /// saved key with nothing sealed under it.</param>
    /// <exception cref="NewKekAdoptionException">A step failed; the message says what
    /// state the install is in and what to do.</exception>
    public static async Task<KekReconciliationService.ReconciliationResult> CompleteAsync(
        KekReconciliationService reconciliation,
        MasterKeyStore store,
        string keyBase64,
        string keyId,
        TimeProvider clock,
        MasterKeyMintLock mintLock,
        Func<CancellationToken, Task<bool>> sealFirstSecret,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(mintLock);
        ArgumentNullException.ThrowIfNull(sealFirstSecret);

        // Lost during the migrations, the lock no longer keeps another start out: that start
        // may have minted and sealed its OAuth keys, and the clearing would delete them.
        if (!mintLock.IsStillHeld())
            throw new NewKekAdoptionException(
                "--adopt-new-kek FAILED before anything changed: the lock that keeps two starts from "
                + "minting at once was lost (its database session ended), so another start may be "
                + $"minting. Nothing was cleared and no key was saved to '{store.Path}'. "
                + LostLockAdvice);

        KekReconciliationService.ReconciliationResult result;
        try
        {
            result = await reconciliation.ReconcileAsync(
                (db, abandoned) => db.AdminAuditEvents.Add(new AdminAuditEventRow
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = clock.GetUtcNow().UtcDateTime,
                    Action = AdminAuditActions.MasterKeyAdoptedNew,
                    ActorUserId = null,
                    Detail = AuditDetail(keyId, abandoned),
                }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new NewKekAdoptionException(
                "--adopt-new-kek FAILED: clearing the secrets the lost master key sealed did not "
                + $"complete ({ex.GetBaseException().Message}). No key was saved to '{store.Path}'. If the failure "
                + "came before the database committed, nothing changed. Fix the cause and run with "
                + "--adopt-new-kek again; that is safe whichever way the commit went, because it "
                + "clears whatever is still there.", ex);
        }

        if (!await sealFirstSecret(ct).ConfigureAwait(false))
            throw new NewKekAdoptionException(
                "--adopt-new-kek FAILED after clearing the secrets the lost master key sealed: the "
                + "OAuth server keys could not be stored under the new key (the log above says why), "
                + $"so the new key was NOT saved to '{store.Path}'. Fix the cause and run with "
                + "--adopt-new-kek again; it replaces the ledger keys this run made, which hold "
                + "nothing yet, and loses nothing more.");

        if (!mintLock.IsStillHeld())
            throw new NewKekAdoptionException(
                "--adopt-new-kek FAILED after clearing the secrets the lost master key sealed: the "
                + "lock that keeps two starts from minting at once was lost (its database session "
                + "ended), so another start may be minting, and the new key was NOT saved to "
                + $"'{store.Path}'. " + LostLockAdvice);

        try
        {
            store.Write(keyBase64, keyId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NewKekAdoptionException(
                "--adopt-new-kek FAILED after clearing the secrets the lost master key sealed: "
                + $"the new key could not be saved to '{store.Path}' ({ex.Message}). Make that "
                + "path writable and run with --adopt-new-kek again; it replaces the ledger keys "
                + "this run made, which hold nothing yet, and loses nothing more.", ex);
        }

        return result;
    }

    /// <summary>What to do after the lock was lost, including when the other start won.</summary>
    private const string LostLockAdvice =
        "Once no other start is running, look at the key file. If none is there, run with "
        + "--adopt-new-kek again; it loses nothing more. If another start has saved one, that key "
        + "is the install's key now and --adopt-new-kek would refuse: start normally, or, if that "
        + "key is the wrong one, move it aside, keeping it, and run with --adopt-new-kek again.";

    /// <summary>The audit event's detail: the key id and the counts, never key material.</summary>
    internal static string AuditDetail(string keyId, KekReconciliationService.ReconciliationResult r) =>
        $"--adopt-new-kek minted '{keyId}' and abandoned what the lost key sealed: "
        + $"{r.LedgersRekeyed} ledger key(s) replaced, {r.FeedConnectionsNeedingReauth} feed(s) to "
        + $"reconnect, passphrase cleared={r.BackupPassphraseCleared}, drive disconnected="
        + $"{r.DriveDisconnected}, {r.NotificationTargetsDisabled} notification target(s) disabled, "
        + $"oauth keys discarded={r.OauthKeysDiscarded}, "
        + $"upgrade repository token discarded={r.UpgradeTokenDiscarded}";

    /// <summary>What <paramref name="result"/> abandoned, one kind per line.</summary>
    public static string Describe(KekReconciliationService.ReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        static string YesNo(bool value) => value ? "yes" : "no";
        return string.Join(Environment.NewLine,
            $"  ledger keys replaced:            {result.LedgersRekeyed}",
            $"  bank feeds to reconnect:         {result.FeedConnectionsNeedingReauth}",
            $"  backup passphrase cleared:       {YesNo(result.BackupPassphraseCleared)}",
            $"  Google Drive disconnected:       {YesNo(result.DriveDisconnected)}",
            $"  notification targets disabled:   {result.NotificationTargetsDisabled}",
            $"  OAuth server keys discarded:     {YesNo(result.OauthKeysDiscarded)}",
            $"  upgrade repository token:        {YesNo(result.UpgradeTokenDiscarded)}");
    }
}

/// <summary>A <c>--adopt-new-kek</c> run that could not finish; see
/// <see cref="NewKekAdoption.CompleteAsync"/>.</summary>
public sealed class NewKekAdoptionException : Exception
{
    public NewKekAdoptionException(string message) : base(message) { }

    public NewKekAdoptionException(string message, Exception inner) : base(message, inner) { }
}
