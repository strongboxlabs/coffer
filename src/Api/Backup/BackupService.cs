using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Coffer.Api.Configuration;
using Coffer.Api.Crypto;

namespace Coffer.Api.Backup;

/// <summary>
/// Whole-DB backup + restore engine (ADR-0060). Shells out to
/// <c>pg_dump</c> / <c>pg_restore</c> (postgresql-client in the image) and
/// pipes the archive through <see cref="BackupCrypto"/>, so the encrypted
/// artifact is a passphrase-sealed custom-format dump. Connects as the service
/// role (BYPASSRLS) — a backup must see every row.
///
/// Backup = <c>pg_dump -Fc</c> → encrypt → output stream.
/// Restore = input stream → decrypt → <c>pg_restore --clean --if-exists</c>.
/// Restore is destructive (it drops + recreates objects); the caller (the
/// operator CLI) gates it behind an explicit confirmation.
/// </summary>
public sealed class BackupService
{
    private readonly string _connectionString;
    private readonly string _compress;
    private readonly ILogger<BackupService> _logger;
    private readonly MasterKey _masterKey;

    public BackupService(IOptions<ApiOptions> options, ILogger<BackupService> logger, MasterKey masterKey)
    {
        _connectionString = options.Value.ServiceConnectionString;
        _compress = options.Value.Backup.Compress?.Trim() ?? string.Empty;
        _logger = logger;
        _masterKey = masterKey;
    }

    public async Task CreateAsync(string passphrase, Stream output, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(output);

        var (env, _) = BuildPgEnvironment(_connectionString);
        // --no-owner so objects restore as the restoring role; KEEP privileges
        // (no --no-privileges) so the GRANTs to coffer_app/coffer_service +
        // REVOKEs (e.g. users.is_admin) come back — without them a restored DB
        // is inaccessible to the request-time role.
        // --compress: zstd by default (ADR-0062, ~10% smaller than zlib); the
        // custom format is self-describing so pg_restore picks the codec up.
        // --exclude-table-data=ledger_snapshots: the ADR-0037 in-place snapshots are
        // LOCAL restore points — up to 5 full (~20 MB+) already-compressed copies of
        // the ledger. They have no value in an off-host DR backup (ADR-0060) and they
        // dominate the dump (already-compressed blobs, so the zstd pass can't shrink
        // them — they pass through ~1:1, ballooning every backup as the daily auto-snap
        // accumulates). Exclude their DATA only — the table schema stays in the dump, so
        // a restored DB still has an (empty) ledger_snapshots; new snapshots regenerate.
        string[] dumpArgs = string.IsNullOrEmpty(_compress)
            ? ["--format=custom", "--no-owner", "--exclude-table-data=public.ledger_snapshots"]
            : ["--format=custom", "--no-owner", "--exclude-table-data=public.ledger_snapshots", $"--compress={_compress}"];
        using var proc = StartProcess(
            "pg_dump",
            dumpArgs,
            env,
            redirectStdout: true,
            redirectStdin: false);

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        // pg_dump streams the archive to stdout; encrypt it straight into the
        // output without buffering. The KEK fingerprint (ADR-0071 D4) rides in
        // the header for the restore-time cross-install check.
        var kekFingerprint = KekFingerprint.Compute(_masterKey.KeyBytes);
        await BackupCrypto.EncryptAsync(
            proc.StandardOutput.BaseStream, passphrase, output, kekFingerprint, ct)
            .ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new BackupException($"pg_dump failed (exit {proc.ExitCode}): {stderr.Trim()}");
        _logger.LogInformation("Backup created.");
    }

    /// <param name="clean">When true, wipe the target database to an empty
    /// schema BEFORE restoring — for the bootstrap-UI restore over an
    /// already-migrated DB (ADR-0061). The CLI path (fresh install, empty DB)
    /// leaves it false. See <see cref="WipeServiceOwnedObjectsAsync"/> for why
    /// this is a targeted object drop rather than <c>pg_restore --clean</c>.</param>
    public async Task RestoreAsync(
        Stream input, string passphrase, bool clean = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(input);

        var (env, database) = BuildPgEnvironment(_connectionString);

        // Restore is only deterministic INTO AN EMPTY SCHEMA. The old approach —
        // pg_restore --clean --if-exists — was fatally fragile: the archive's
        // DROP list only covers objects that existed at the backup's schema
        // version, can't CASCADE, and can't touch the superuser-owned
        // extensions. Restoring over a live (especially cross-version) schema
        // left dependents that blocked those drops, so the CREATE/COPY phase
        // then collided (relation already exists, FK violations) and the restore
        // failed HALF-APPLIED. Instead, wipe the schema to empty first — dropping
        // only what the service role owns, leaving the install-managed extensions
        // intact — then pg_restore into the clean schema with no --clean guesswork.
        if (clean)
            await WipeServiceOwnedObjectsAsync(env, database, ct).ConfigureAwait(false);

        // --no-owner restores objects as the connecting role; KEEP privileges so
        // the GRANTs come back.
        using var proc = StartProcess(
            "pg_restore",
            ["--no-owner", $"--dbname={database}"],
            env,
            redirectStdout: false,
            redirectStdin: true);

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            // Decrypt the artifact straight into pg_restore's stdin. A wrong
            // passphrase / tamper throws BackupDecryptException here, before
            // pg_restore can apply a partial archive.
            await BackupCrypto.DecryptAsync(input, passphrase, proc.StandardInput.BaseStream, ct)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // "Pipe is broken": pg_restore exited before consuming all input —
            // it couldn't connect (missing roles, wrong password), couldn't read
            // the archive, etc. The real reason is on its stderr; swallow the
            // broken-pipe write error and fall through to surface that instead
            // of an opaque IOException stack. (A wrong passphrase is a
            // BackupDecryptException, NOT IOException, so it still propagates.)
        }
        finally
        {
            try { proc.StandardInput.Close(); }
            catch (IOException) { /* already broken — nothing to flush */ }
        }
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            // pg_restore exits non-zero if ANY statement errored. The only
            // benign case is extension DDL it can't own (extensions are
            // install-managed by db/init); everything else is a real failure.
            if (!AllErrorsAreBenignExtensionOwnership(stderr))
                throw new BackupException(
                    $"pg_restore failed (exit {proc.ExitCode}): {stderr.Trim()}");
            _logger.LogWarning(
                "pg_restore ignored benign extension-ownership errors (extensions are install-managed): {Stderr}",
                stderr.Trim());
        }

        // Reset the install-specific Google Drive identity carried in the backup
        // (ADR-0074). The restored drive_sync holds the SOURCE install's
        // install_id + folder + sealed OAuth; left intact, the restored install
        // resolves to the source's Drive folder name — and, once reconnected,
        // MIRRORS into (and deletes from) that folder. Clear the connection so
        // this install reconnects with a fresh install_id → its own folder. (The
        // sealed OAuth blob is KEK-unusable across installs anyway.)
        await ResetInstallDriveStateAsync(env, database, ct).ConfigureAwait(false);

        _logger.LogInformation("Restore complete.");
    }

    /// <summary>
    /// Post-restore reset of install-specific Drive state (ADR-0074). Runs after
    /// pg_restore so the restored install doesn't inherit the SOURCE install's
    /// Google Drive identity — which would collide with, and (under the mirror
    /// model) delete from, the source's Drive folder once reconnected. Guarded by
    /// a table-exists check so restoring a pre-<c>drive_sync</c> (mig 142) archive
    /// is a no-op.
    /// </summary>
    private async Task ResetInstallDriveStateAsync(
        Dictionary<string, string?> env, string database, CancellationToken ct)
    {
        using var proc = StartProcess(
            "psql",
            [$"--dbname={database}", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "-f", "-"],
            env,
            redirectStdout: false,
            redirectStdin: true);

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardInput.WriteAsync(ResetDriveStateSql.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new BackupException(
                $"post-restore Drive-state reset failed (exit {proc.ExitCode}): {stderr.Trim()}");
        _logger.LogInformation("Reset install-specific Drive state after restore (fresh install identity).");
    }

    /// <summary>Clears the restored (source-install) Google Drive connection so the
    /// restored install starts Drive-disconnected and mints a fresh install_id on
    /// reconnect. Table-guarded for pre-mig-142 archives. See ADR-0074.</summary>
    private const string ResetDriveStateSql =
        """
        DO $reset$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'drive_sync')
            THEN
                UPDATE drive_sync SET
                    oauth_ciphertext = NULL,
                    folder_id        = NULL,
                    folder_name      = NULL,
                    connected_email  = NULL,
                    install_id       = NULL,
                    enabled          = false,
                    last_sync_at     = NULL,
                    last_sync_status = NULL,
                    last_sync_error  = NULL;
            END IF;
        END
        $reset$;
        """;

    /// <summary>
    /// Empty the public schema before a restore-over-populated-DB, dropping
    /// only what the connecting service role (coffer_service) owns — tables,
    /// views, sequences, and its own (non-extension) functions — with CASCADE,
    /// which dissolves the inter-table foreign keys that ordering-blind DROPs
    /// choke on (the exact failure mode of the old <c>--clean</c> path). The
    /// schema itself and the superuser-owned, install-managed extensions
    /// (pgcrypto / pg_trgm / plpgsql, owned by <c>coffer</c>) are left in place,
    /// so the archive's <c>CREATE EXTENSION IF NOT EXISTS</c> is a no-op.
    ///
    /// Deliberately NOT <c>DROP OWNED BY coffer_service</c>: that would also
    /// revoke the role's own <c>CREATE ON SCHEMA public</c> grant, which a
    /// non-superuser can't grant back to itself — breaking the pg_restore that
    /// follows. Runs as the service role, so it stays within least privilege.
    /// Idempotent: a no-op on an already-empty schema (the fresh-install path).
    /// </summary>
    private async Task WipeServiceOwnedObjectsAsync(
        Dictionary<string, string?> env, string database, CancellationToken ct)
    {
        using var proc = StartProcess(
            "psql",
            [$"--dbname={database}", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "-f", "-"],
            env,
            redirectStdout: false,
            redirectStdin: true);

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardInput.WriteAsync(WipeSchemaSql.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new BackupException(
                $"schema wipe before restore failed (exit {proc.ExitCode}): {stderr.Trim()}");
        _logger.LogInformation("Wiped service-owned objects; schema is empty for restore.");
    }

    /// <summary>
    /// Drops every table / view / sequence / non-extension function owned by
    /// the connecting role in the public schema. An extension-provided function
    /// is skipped via its pg_depend 'e' dependency; everything else the role
    /// owns goes with CASCADE. See <see cref="WipeServiceOwnedObjectsAsync"/>.
    /// </summary>
    private const string WipeSchemaSql =
        """
        DO $wipe$
        DECLARE cmd text;
        BEGIN
            FOR cmd IN
                SELECT format('DROP TABLE IF EXISTS %I.%I CASCADE', schemaname, tablename)
                FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user
            LOOP EXECUTE cmd; END LOOP;

            FOR cmd IN
                SELECT format('DROP VIEW IF EXISTS %I.%I CASCADE', schemaname, viewname)
                FROM pg_views WHERE schemaname = 'public' AND viewowner = current_user
            LOOP EXECUTE cmd; END LOOP;

            FOR cmd IN
                SELECT format('DROP SEQUENCE IF EXISTS %I.%I CASCADE', schemaname, sequencename)
                FROM pg_sequences WHERE schemaname = 'public' AND sequenceowner = current_user
            LOOP EXECUTE cmd; END LOOP;

            FOR cmd IN
                SELECT format('DROP FUNCTION IF EXISTS %s CASCADE', p.oid::regprocedure)
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN pg_roles r   ON r.oid = p.proowner
                WHERE n.nspname = 'public'
                  AND r.rolname = current_user
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_depend d
                      WHERE d.objid = p.oid AND d.deptype = 'e')
            LOOP EXECUTE cmd; END LOOP;
        END
        $wipe$;
        """;

    /// <summary>
    /// True when every pg_restore error line is an "must be owner of extension"
    /// — benign, because extensions (pgcrypto / pg_trgm) are created by db/init
    /// as the superuser on every install and aren't ours to recreate.
    /// </summary>
    private static bool AllErrorsAreBenignExtensionOwnership(string stderr)
    {
        var errorLines = stderr
            .Split('\n')
            .Where(l => l.Contains("pg_restore: error:", StringComparison.Ordinal))
            .ToList();
        return errorLines.Count > 0
            && errorLines.All(l =>
                l.Contains("must be owner of extension", StringComparison.Ordinal));
    }

    /// <summary>
    /// Translate an Npgsql connection string into the <c>PG*</c> environment
    /// variables libpq tools read, and return the database name (pg_restore
    /// needs it as <c>--dbname</c>). Kept static + pure for unit testing.
    /// </summary>
    public static (Dictionary<string, string?> Env, string Database) BuildPgEnvironment(
        string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var env = new Dictionary<string, string?>
        {
            ["PGHOST"] = b.Host,
            ["PGPORT"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGUSER"] = b.Username,
            ["PGPASSWORD"] = b.Password,
            ["PGDATABASE"] = b.Database,
        };
        return (env, b.Database ?? string.Empty);
    }

    private static Process StartProcess(
        string fileName,
        IEnumerable<string> args,
        Dictionary<string, string?> env,
        bool redirectStdout,
        bool redirectStdin)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = redirectStdin,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        try
        {
            return Process.Start(psi)
                ?? throw new BackupException($"Could not start {fileName}.");
        }
        catch (Exception ex) when (ex is not BackupException)
        {
            throw new BackupException(
                $"Could not run {fileName} — is postgresql-client installed? ({ex.Message})", ex);
        }
    }
}

/// <summary>Thrown when pg_dump/pg_restore can't be run or fails.</summary>
public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
    public BackupException(string message, Exception inner) : base(message, inner) { }
}
