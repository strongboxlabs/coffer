using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Migrations;

namespace Coffer.Api.Backup;

/// <summary>
/// Whole-DB backup + restore engine (ADR-0060). Shells out to
/// <c>pg_dump</c> / <c>pg_restore</c> (postgresql-client in the image) and
/// pipes the archive through <see cref="BackupCrypto"/>, so the encrypted
/// artifact is a passphrase-sealed custom-format dump. Connects as the service
/// role (BYPASSRLS) — a backup must see every row.
///
/// Backup = <c>pg_dump -Fc</c> → encrypt → output stream.
/// Restore = decrypt the whole archive (every chunk authenticated) → build it as a
/// NEW database → migrate, reset access and install identity, analyse → swap it in with two
/// renames in one transaction, keeping the replaced database. Any failure before
/// the swap drops the new database; the live one is never touched. Destructive on
/// success; the callers gate it behind an explicit confirmation.
/// </summary>
public sealed class BackupService
{
    private readonly string _connectionString;
    private readonly string _compress;
    private readonly ILogger<BackupService> _logger;
    private readonly MasterKey _masterKey;
    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly TimeProvider _clock;

    public BackupService(
        IOptions<ApiOptions> options,
        ILogger<BackupService> logger,
        MasterKey masterKey,
        ServiceDbContextFactory serviceFactory,
        TimeProvider clock)
    {
        _connectionString = options.Value.ServiceConnectionString;
        _compress = options.Value.Backup.Compress?.Trim() ?? string.Empty;
        _logger = logger;
        _masterKey = masterKey;
        _serviceFactory = serviceFactory;
        _clock = clock;
    }

    /// <summary>Tables whose DATA a backup leaves out: the in-place snapshots,
    /// which are local restore points and not disaster recovery. The restore list
    /// skips the same tables, so an older archive that still carries chunks
    /// restores too.</summary>
    internal static readonly string[] SnapshotDataExclusions =
    [
        "--exclude-table-data=public.ledger_snapshots",
        "--exclude-table-data=public.ledger_snapshot_parts",
    ];

    public async Task CreateAsync(string passphrase, Stream output, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(output);

        using var tools = OpenPgTools();
        var env = tools.Env;
        // --no-owner so objects restore as the restoring role; KEEP privileges
        // (no --no-privileges) so the GRANTs to coffer_app/coffer_service +
        // REVOKEs (e.g. users.is_admin) come back — without them a restored DB
        // is inaccessible to the request-time role.
        // --compress: zstd by default (ADR-0062, ~10% smaller than zlib); the
        // custom format is self-describing so pg_restore picks the codec up.
        // --exclude-table-data=ledger_snapshots: the ADR-0037 in-place snapshots are
        // LOCAL restore points — up to 5 full, already-compressed copies of the
        // ledger, each as large as the ledger itself. They have no value in an off-host DR backup (ADR-0060) and they
        // dominate the dump (already-compressed blobs, so the zstd pass can't shrink
        // them — they pass through ~1:1, ballooning every backup as the daily auto-snap
        // accumulates). Exclude their DATA only — the table schema stays in the dump, so
        // a restored DB still has an (empty) ledger_snapshots; new snapshots regenerate.
        //
        // ledger_snapshot_parts too. Since migration 193 the snapshot payload lives in
        // that child table, and excluding only the parent left every backup holding
        // chunks whose parent rows it did not hold: the restore's
        // ledger_snapshot_parts_snapshot_id_fkey then failed, and every backup taken
        // while a snapshot existed was unrestorable. It also put the bulk the
        // exclusion was meant to drop straight back into the backup.
        string[] dumpArgs = string.IsNullOrEmpty(_compress)
            ? ["--format=custom", "--no-owner", .. SnapshotDataExclusions]
            : ["--format=custom", "--no-owner", .. SnapshotDataExclusions, $"--compress={_compress}"];
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

    /// <summary>
    /// Read a written artifact back the way a restore would, up to the point of
    /// touching a database: decrypt it in full (every chunk tag is authenticated),
    /// read its table of contents, and require TABLE DATA for every table in the
    /// live catalog except the declared exclusions. A backup that passes this can
    /// be decrypted and holds each table's rows; one that does not is the one that
    /// would have reported success for two months while unrestorable.
    /// </summary>
    /// <exception cref="BackupException">The artifact is not a restorable backup.</exception>
    public async Task VerifyAsync(string passphrase, Stream artifact, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(artifact);

        using var tools = OpenPgTools();
        var (env, database) = (tools.Env, tools.Database);
        SweepWorkDirectories(VerifyWorkDirPrefix, _clock.GetUtcNow().UtcDateTime.AddHours(-1));
        var workDir = CreatePrivateDirectory(
            Path.Combine(Path.GetTempPath(), VerifyWorkDirPrefix + Guid.NewGuid().ToString("N")));
        try
        {
            // Owner-only, and gone in the finally: it is the plaintext dump.
            var dumpPath = Path.Combine(workDir, "archive.dump");
            await using (var plain = new FileStream(dumpPath, PrivateCreate()))
            {
                try
                {
                    await BackupCrypto.DecryptAsync(artifact, passphrase, plain, ct).ConfigureAwait(false);
                }
                catch (BackupDecryptException ex)
                {
                    throw new BackupException($"the written backup could not be decrypted: {ex.Message}", ex);
                }
            }

            var toc = await RunForOutputAsync("pg_restore", ["--list", dumpPath], env, ct).ConfigureAwait(false);
            var live = (await RunPsqlAsync(env, database, LiveTablesSql, ct).ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var missing = TablesMissingData(toc, live);
            if (missing.Count > 0)
                throw new BackupException(
                    "the written backup holds no data entry for "
                    + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? ", …" : "") + ".");
            _logger.LogInformation("Backup verified: decrypted in full, data present for {Tables} live tables.", live.Length);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* swept by a later verification */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Every ordinary table of this database a dump should carry data for:
    /// not a system schema, and not owned by an extension (those come back with
    /// CREATE EXTENSION).</summary>
    private const string LiveTablesSql =
        """
        SELECT n.nspname || '.' || c.relname
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE c.relkind = 'r'
           AND n.nspname NOT IN ('pg_catalog', 'information_schema')
           AND n.nspname NOT LIKE 'pg\_toast%'
           AND NOT EXISTS (SELECT 1 FROM pg_depend d
                            WHERE d.classid = 'pg_class'::regclass
                              AND d.objid = c.oid AND d.deptype = 'e');
        """;

    /// <summary>
    /// The live tables (<c>schema.name</c>) the archive's table of contents carries
    /// no TABLE DATA entry for, other than <see cref="SnapshotDataExclusions"/>.
    /// Pure, so the check is tested without a database.
    /// </summary>
    internal static IReadOnlyList<string> TablesMissingData(string toc, IEnumerable<string> liveTables)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(liveTables);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in toc.Replace("\r\n", "\n").Split('\n'))
        {
            var m = TableDataEntry.Match(line);
            if (m.Success) present.Add($"{m.Groups[1].Value}.{m.Groups[2].Value}");
        }
        var excluded = SnapshotDataExclusions
            .Select(a => a["--exclude-table-data=".Length..])
            .ToHashSet(StringComparer.Ordinal);
        return liveTables
            .Where(t => !present.Contains(t) && !excluded.Contains(t))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // "4657; 0 267107 TABLE DATA public ledger_snapshot_parts coffer_service"
    private static readonly System.Text.RegularExpressions.Regex TableDataEntry = new(
        @"^\d+; \d+ \d+ TABLE DATA (\S+) (\S+) \S+\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// What a restore needs, checked BEFORE anything is staged so the operator
    /// learns it in the request rather than from a log after a restart: the role
    /// may create databases and owns the live one (ALTER DATABASE ... RENAME
    /// needs both), and the live database is one a swap can rebuild.
    /// </summary>
    public sealed record RestoreReadiness(
        string Role, string Database, bool CanCreateDatabases, bool OwnsDatabase,
        string LocaleProvider, string? RestoreMarker)
    {
        /// <summary>The longest suffix a restore appends — "_pre_restore_" plus a
        /// yyyyMMdd'T'HHmmss stamp — leaves this much of Postgres's 63 bytes.</summary>
        public const int MaxDatabaseNameLength = 63 - 28;

        public bool IsReady => Problems().Count == 0;

        public IReadOnlyList<string> Problems()
        {
            var problems = new List<string>();
            if (!OwnsDatabase || !CanCreateDatabases)
            {
                var missing = new List<string>();
                if (!OwnsDatabase) missing.Add($"to own the '{Database}' database");
                if (!CanCreateDatabases) missing.Add("to be allowed to create databases");
                problems.Add(
                    $"Restoring needs the {Role} role {string.Join(" and ", missing)}. On the server, run "
                    + "scripts/provision-restore-privileges.sh once (install.sh does it on upgrade) — or, as "
                    + $"the database superuser: ALTER ROLE {Role} CREATEDB; ALTER DATABASE \"{Database}\" "
                    + $"OWNER TO {Role};");
            }
            if (!SafeName.IsMatch(Database) || Database.Length > MaxDatabaseNameLength)
                problems.Add(
                    $"The database name '{Database}' must be a plain identifier of at most "
                    + $"{MaxDatabaseNameLength} characters for a restore to name its copies.");
            if (Database == MaintenanceDatabase)
                problems.Add($"The live database cannot be '{MaintenanceDatabase}': a restore renames from it.");
            if (LocaleProvider != "c")
                problems.Add(
                    $"The '{Database}' database uses locale provider '{LocaleProvider}'; a restore rebuilds "
                    + "libc databases only.");
            return problems;
        }

        public string Explain() =>
            IsReady ? "Ready." : string.Join(" ", Problems()) + " Nothing was changed.";

        /// <summary>The attempt and the replaced database a restore stamped on the
        /// live database, when one did (see <see cref="MarkerFor"/>).</summary>
        public (Guid AttemptId, string PreviousDatabase)? Marker =>
            ParseMarker(RestoreMarker);
    }

    /// <summary>The outcome of an applied restore: the database it replaced is kept
    /// under this name until the next restore, as the way back.</summary>
    public sealed record RestoreResult(string PreviousDatabase);

    public async Task<RestoreReadiness> CheckRestoreReadinessAsync(CancellationToken ct = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.RestoreReadiness().SingleAsync(ct).ConfigureAwait(false);
        return new RestoreReadiness(
            row.RoleName, row.DatabaseName, row.CanCreateDatabases, row.OwnsDatabase,
            row.LocaleProvider, row.RestoreMarker);
    }

    /// <summary>
    /// The stamp a restore puts on the database it built, before swapping it in:
    /// the attempt it belongs to and the name the replaced database is given. A
    /// boot that finds its own attempt's stamp on the live database knows the
    /// swap happened, whatever else did not.
    /// </summary>
    internal static string MarkerFor(Guid attemptId, string previousDatabase) =>
        $"coffer-restore:{attemptId:D}:{previousDatabase}";

    internal static (Guid AttemptId, string PreviousDatabase)? ParseMarker(string? marker)
    {
        if (marker is null) return null;
        var parts = marker.Split(':');
        return parts.Length == 3 && parts[0] == "coffer-restore" && Guid.TryParse(parts[1], out var id)
            ? (id, parts[2])
            : null;
    }

    /// <summary>
    /// Restore a backup by building it as a NEW database and swapping it in
    /// (ADR-0061, amended 2026-10-09). The live database is not touched until the
    /// new one is complete, from a version this build knows, migrated to this
    /// build's schema, signed out and reset to this install's identity, analysed and stamped
    /// with <paramref name="attemptId"/>; the swap itself is two renames in one
    /// transaction. On any failure before the swap the new database is dropped and
    /// the live one is exactly as it was. Pruning older replaced databases is NOT
    /// done here — <see cref="PruneAfterRestoreAsync"/> runs once the outcome is
    /// recorded, so a crash in between cannot cost the way back.
    /// </summary>
    /// <remarks>
    /// Replaces an in-place wipe-and-reload. That emptied the live schema by
    /// dropping every object the service role owns, from a hand-kept list of object
    /// kinds: migration 200 added an aggregate and 202 two composite types the list
    /// did not know, and from v0.62.0 every restore over an existing install failed
    /// in production — logged, while the UI said "Restore complete". Building a new
    /// database needs no list, and needs nothing dropped until there is something
    /// better to swap in.
    /// </remarks>
    /// <exception cref="RestoreOutcomeUnknownException">The swap's own result could
    /// not be established — reported as such, never guessed.</exception>
    public async Task<RestoreResult> RestoreAsync(
        Stream input, string passphrase, Guid attemptId, string migrationsDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(migrationsDirectory);

        using var tools = OpenPgTools();
        var (env, database) = (tools.Env, tools.Database);
        var readiness = await CheckRestoreReadinessAsync(ct).ConfigureAwait(false);
        if (!readiness.IsReady)
            throw new BackupException(readiness.Explain());

        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var incoming = SafeDatabaseName($"{database}_restore_{stamp}");
        var previous = SafeDatabaseName($"{database}_pre_restore_{stamp}");
        var marker = MarkerFor(attemptId, previous);

        // A restore that died earlier (container killed mid-build) leaves its
        // half-built database and its decrypted archive behind; neither was ever
        // swapped in or needed again.
        await DropOwnDatabasesAsync(env, database, $"{database}_restore_", keep: null, ct).ConfigureAwait(false);
        SweepWorkDirectories();

        var workDir = CreatePrivateDirectory(Path.Combine(Path.GetTempPath(), WorkDirPrefix + Guid.NewGuid().ToString("N")));
        try
        {
            // Decrypt the WHOLE archive first: every chunk tag is checked before the
            // database is asked to do anything. Owner-only: it is the plaintext.
            var dumpPath = Path.Combine(workDir, "archive.dump");
            await using (var plain = new FileStream(dumpPath, PrivateCreate()))
            {
                await BackupCrypto.DecryptAsync(input, passphrase, plain, ct).ConfigureAwait(false);
            }

            var toc = await RunForOutputAsync("pg_restore", ["--list", dumpPath], env, ct).ConfigureAwait(false);
            var listPath = Path.Combine(workDir, "restore.list");
            await File.WriteAllTextAsync(listPath, BuildRestoreList(toc), ct).ConfigureAwait(false);

            await CreateDatabaseLikeAsync(env, database, incoming, ct).ConfigureAwait(false);
            try
            {
                // --exit-on-error: the first failure ends it, and the database it
                // was building is dropped below. The extensions come from the dump:
                // pgcrypto and pg_trgm are trusted, so the owner may create them.
                await RunForOutputAsync("pg_restore",
                    ["--no-owner", "--exit-on-error", $"--use-list={listPath}", $"--dbname={incoming}", dumpPath],
                    env, ct).ConfigureAwait(false);

                // A backup from a NEWER build has migrations this one does not know.
                // DbUp ignores journal rows it has no script for, so it would come
                // up half-working and be reported restored.
                var applied = (await RunPsqlAsync(env, incoming,
                        "SELECT scriptname FROM \"__schema_migrations\";", ct).ConfigureAwait(false))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var unknown = UnknownMigrations(applied,
                    Directory.EnumerateFiles(migrationsDirectory, "*.sql").Select(Path.GetFileName)!);
                if (unknown.Count > 0)
                    throw new BackupException(
                        "This backup was taken by a newer version of Coffer than this one — it carries "
                        + $"migrations this build does not have ({string.Join(", ", unknown.Take(3))}"
                        + (unknown.Count > 3 ? ", …" : "") + "). Upgrade, then restore.");

                // An older backup is brought up to this build's schema BEFORE it can
                // become live, so a migration that cannot apply to it fails the
                // restore instead of the next boot.
                try
                {
                    MigrationRunner.Run(ConnectionStringFor(incoming), migrationsDirectory, _logger);
                }
                catch (Exception ex) when (ex is not BackupException and not OperationCanceledException)
                {
                    throw new BackupException($"the backup could not be migrated to this version: {ex.Message}", ex);
                }

                // Every way in granted before the backup, and the Drive identity (FU-076,
                // ADR-0074), reset in one transaction before the marker below says this
                // database is ready.
                await using (var incomingDb = _serviceFactory.CreateFor(ConnectionStringFor(incoming)))
                {
                    await using var tx = await incomingDb.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                    var reset = await RestoreReset.ApplyAsync(incomingDb, _clock.GetUtcNow().UtcDateTime, ct)
                        .ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Restore signs everyone out: revoked {Sessions} session(s), {McpTokens} MCP token(s), "
                        + "{Authorizations} OAuth authorization(s) and {OauthTokens} OAuth token(s); removed "
                        + "{Invites} pending invite(s); Drive disconnected.",
                        reset.Sessions, reset.McpTokens, reset.OauthAuthorizations, reset.OauthTokens, reset.Invites);
                }

                // Statistics for a database that has none yet: its first queries
                // would otherwise be planned blind.
                await RunPsqlAsync(env, incoming,
                    "ANALYZE;\n"
                    + $"COMMENT ON DATABASE {QuoteIdent(incoming)} IS {QuoteLiteral(marker)};", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BackupException or OperationCanceledException)
            {
                await AbandonAsync(env, database, incoming).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await AbandonAsync(env, database, incoming).ConfigureAwait(false);
                throw new BackupException(ex.Message, ex);
            }

            // Nothing of ours may hold either database open: a rename refuses while
            // any other session is connected, which is the point of it.
            NpgsqlConnection.ClearAllPools();
            try
            {
                await RunPsqlAsync(env, MaintenanceDatabase,
                    $"""
                    BEGIN;
                    ALTER DATABASE {QuoteIdent(database)} RENAME TO {QuoteIdent(previous)};
                    ALTER DATABASE {QuoteIdent(incoming)} RENAME TO {QuoteIdent(database)};
                    COMMIT;
                    """, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception swapError)
            {
                // psql's exit code is not the swap's result: the COMMIT can land and
                // the connection drop before psql hears it. The catalog decides.
                string? liveMarker;
                try
                {
                    liveMarker = (await RunPsqlAsync(env, MaintenanceDatabase,
                        "SELECT shobj_description(oid, 'pg_database') FROM pg_database "
                        + $"WHERE datname = {QuoteLiteral(database)};", CancellationToken.None)
                        .ConfigureAwait(false)).Trim();
                }
                catch (Exception checkError)
                {
                    throw new RestoreOutcomeUnknownException(
                        $"The swap reported an error ({swapError.Message}) and its result could not be "
                        + $"checked ({checkError.Message}). Look for databases named {incoming} and "
                        + $"{previous} before assuming either way.", swapError);
                }

                if (liveMarker == marker)
                {
                    _logger.LogWarning(swapError,
                        "The swap reported an error, but the live database carries this restore's stamp: it happened.");
                }
                else
                {
                    await AbandonAsync(env, database, incoming).ConfigureAwait(false);
                    if (swapError is BackupException)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(swapError).Throw();
                    throw new BackupException(swapError.Message, swapError);
                }
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* swept by the next restore */ }
            catch (UnauthorizedAccessException) { }
        }

        _logger.LogInformation("Restore complete; the replaced database is kept as {Previous}.", previous);
        return new RestoreResult(previous);
    }

    /// <summary>
    /// After an applied restore whose outcome is recorded: drop older replaced
    /// databases, keeping <paramref name="keep"/> — each is a full copy. Separate
    /// from <see cref="RestoreAsync"/> so that a crash before the outcome is
    /// recorded never costs the way back.
    /// </summary>
    public async Task PruneAfterRestoreAsync(string keep, CancellationToken ct = default)
    {
        using var tools = OpenPgTools();
        await DropOwnDatabasesAsync(tools.Env, tools.Database, $"{tools.Database}_pre_restore_", keep, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Migrations a backup has applied that this build does not ship.</summary>
    internal static IReadOnlyList<string> UnknownMigrations(
        IEnumerable<string> appliedInBackup, IEnumerable<string> shippedByThisBuild)
    {
        var known = shippedByThisBuild.ToHashSet(StringComparer.Ordinal);
        return appliedInBackup.Where(s => !known.Contains(s)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What of a restore error is safe to show anyone holding the attempt id: the
    /// tool's messages, without the lines that can quote row data — pg_restore's
    /// CONTEXT ("COPY ... line N: &lt;row&gt;") and a constraint's
    /// "Key (...)=(...)". The server log gets this too, not the whole text: a log is
    /// kept and shipped longer than anyone needs a failed restore's rows (FU-083).
    /// </summary>
    public static string SummarizeForOutcome(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var kept = message.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("CONTEXT:", StringComparison.Ordinal))
            .Where(l => !l.Contains("Key (", StringComparison.Ordinal))
            // DETAIL quotes the row ("Failing row contains (...)"), and the outcome is
            // kept indefinitely and readable by anyone holding the attempt id (FU-075).
            // Both spellings: psql's "DETAIL:" and pg_restore's "pg_restore: detail:".
            .Where(l => !DetailLine.IsMatch(l))
            .Where(l => !l.Contains("Failing row contains", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.TrimEnd());
        var text = string.Join(' ', kept.Where(l => l.Length > 0));
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    private const string WorkDirPrefix = "coffer-restore-";

    private static readonly System.Text.RegularExpressions.Regex DetailLine = new(
        @"^\s*(pg_restore:\s*|psql:\s*)?detail:", System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// At a server boot, before anything else runs, remove what a killed restore or
    /// verification left in temp: the decrypted dump (plaintext of the whole database) and
    /// the multipart buffers of an upload that never finished (FU-075). Nothing can be
    /// mid-run at this point, so all of them go; before, they waited for the next restore.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public static int SweepLeftoversAtBoot() =>
        SweepLeftoversAtBoot(
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } t ? t : Path.GetTempPath());

    /// <summary>The sweep, over directories the caller names (so it is testable without
    /// touching the machine's temp).</summary>
    internal static int SweepLeftoversAtBoot(string temp, string aspNetTemp)
    {
        var removed = 0;
        foreach (var prefix in new[] { WorkDirPrefix, VerifyWorkDirPrefix, PassDirPrefix })
        {
            foreach (var dir in Directory.EnumerateDirectories(temp, prefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        // ASP.NET Core buffers a large multipart file to ASPNETCORE_TEMP (else the system
        // temp) as ASPNETCORE_*.tmp and removes it when the request ends; a process killed
        // mid-upload leaves it. Here that is an encrypted archive, but it is still a copy
        // of every row.
        if (Directory.Exists(aspNetTemp))
        {
            foreach (var file in Directory.EnumerateFiles(aspNetTemp, "ASPNETCORE_*.tmp"))
            {
                try { File.Delete(file); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return removed;
    }

    // Separate from the restore's: a restore sweeps every "coffer-restore-*" directory,
    // so a verification sharing the prefix would lose its decrypted archive to one.
    private const string VerifyWorkDirPrefix = "coffer-verify-";

    /// <summary>Remove leftover plaintext work directories of a killed run. A restore
    /// sweeps all of its own (nothing else uses its prefix); a verification only
    /// those created before <paramref name="createdBeforeUtc"/>, since another may be mid-run.</summary>
    private static void SweepWorkDirectories(string prefix = WorkDirPrefix, DateTime? createdBeforeUtc = null)
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), prefix + "*"))
        {
            if (createdBeforeUtc is { } cutoff && Directory.GetCreationTimeUtc(dir) >= cutoff) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static FileStreamOptions PrivateCreate()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1 << 16,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>Drop the half-built incoming database. The live one was never
    /// touched; this only removes the attempt. Our own pooled connections to it go
    /// first; see <see cref="DropDatabaseSql"/> for why nothing else is forced.</summary>
    private async Task AbandonAsync(Dictionary<string, string?> env, string live, string incoming)
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            await DropOwnDatabasesAsync(env, live, incoming, keep: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (BackupException ex)
        {
            _logger.LogWarning(ex, "Could not drop the abandoned restore database {Name}; the next restore removes it.", incoming);
        }
    }

    /// <summary>
    /// The archive's table of contents with the snapshot tables' DATA commented
    /// out (pg_restore <c>--use-list</c> skips lines starting with ';'). A backup
    /// excludes it (<see cref="SnapshotDataExclusions"/>); archives taken before
    /// the chunk table was excluded carry chunks without their parent rows, and
    /// replaying them fails the foreign key between the two.
    /// </summary>
    internal static string BuildRestoreList(string toc)
    {
        ArgumentNullException.ThrowIfNull(toc);
        var lines = toc.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (SnapshotDataEntry.IsMatch(lines[i]))
                lines[i] = ";" + lines[i];
        }
        return string.Join('\n', lines);
    }

    // "4657; 0 267107 TABLE DATA public ledger_snapshot_parts coffer_service"
    private static readonly System.Text.RegularExpressions.Regex SnapshotDataEntry = new(
        @"^\d+; \d+ \d+ TABLE DATA public (ledger_snapshots|ledger_snapshot_parts) ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Where database-level DDL is issued from: neither the live nor the
    /// incoming database may be the session's own while it is renamed.</summary>
    private const string MaintenanceDatabase = "postgres";

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_connectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// CREATE DATABASE from template0 with the live database's encoding and
    /// collation, so text sorts and compares in the restored database exactly as
    /// it did. An ICU-provider database is refused rather than silently rebuilt
    /// under libc — Coffer's compose file never creates one.
    /// </summary>
    private async Task CreateDatabaseLikeAsync(
        Dictionary<string, string?> env, string like, string name, CancellationToken ct)
    {
        var row = (await RunPsqlAsync(env, like,
            "SELECT pg_encoding_to_char(encoding), datcollate, datctype, datlocprovider "
            + "FROM pg_database WHERE datname = current_database();", ct).ConfigureAwait(false))
            .Trim().Split('|');
        if (row.Length != 4)
            throw new BackupException($"Could not read the database's locale (got '{string.Join('|', row)}').");
        if (row[3] != "c")
            throw new BackupException(
                $"The '{like}' database uses locale provider '{row[3]}'; restore only rebuilds libc databases.");

        await RunPsqlAsync(env, MaintenanceDatabase,
            $"CREATE DATABASE {QuoteIdent(name)} TEMPLATE template0 ENCODING {QuoteLiteral(row[0])} "
            + $"LC_COLLATE {QuoteLiteral(row[1])} LC_CTYPE {QuoteLiteral(row[2])};", ct).ConfigureAwait(false);
    }

    /// <summary>Drop this role's databases named <paramref name="prefixOrName"/>
    /// (a prefix when it ends in '_'), except <paramref name="keep"/>. Only ever
    /// databases the role owns, and never the live one.</summary>
    private async Task DropOwnDatabasesAsync(
        Dictionary<string, string?> env, string live, string prefixOrName, string? keep, CancellationToken ct)
    {
        var pattern = prefixOrName.EndsWith('_')
            ? prefixOrName.Replace("_", "\\_", StringComparison.Ordinal) + "%"
            : prefixOrName.Replace("_", "\\_", StringComparison.Ordinal);
        var names = (await RunPsqlAsync(env, MaintenanceDatabase,
            $"""
            SELECT datname FROM pg_database
             WHERE datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user)
               AND datname LIKE {QuoteLiteral(pattern)}
               AND datname <> {QuoteLiteral(live)};
            """, ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n != keep);
        foreach (var name in names)
        {
            // Not inside a transaction: DROP DATABASE refuses one.
            await RunPsqlAsync(env, MaintenanceDatabase, DropDatabaseSql(name), ct).ConfigureAwait(false);
            _logger.LogInformation("Dropped database {Name}.", name);
        }
    }

    /// <summary>
    /// How a restore drops a database of its own. Deliberately NOT
    /// <c>WITH (FORCE)</c>: FORCE terminates every other session in the database
    /// first, and a non-superuser may not terminate a superuser's — which is what an
    /// autovacuum worker counts as. A freshly loaded database draws autovacuum within
    /// a minute, so FORCE failed exactly when a worker happened to be in it
    /// ("permission denied to terminate process"; 6 of 6 in a probe with a worker
    /// present) and left the database behind. A plain DROP has Postgres cancel the
    /// autovacuum worker itself and wait for it (6 of 6). Our own connections are
    /// closed before any drop, so there is nothing for FORCE to do — and a session
    /// that is genuinely someone else's should make the drop refuse, not be killed.
    /// </summary>
    internal static string DropDatabaseSql(string name) =>
        $"DROP DATABASE IF EXISTS {QuoteIdent(SafeDatabaseName(name))};";

    private static readonly System.Text.RegularExpressions.Regex SafeName =
        new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Database names are built from the configured one plus a fixed
    /// suffix and spliced into DDL, so they are held to a plain-identifier shape
    /// rather than trusted.</summary>
    private static string SafeDatabaseName(string name) =>
        SafeName.IsMatch(name)
            ? name
            : throw new BackupException(
                $"Database name '{name}' is not a plain identifier of at most 63 characters; refusing to restore.");

    private static string QuoteIdent(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Run SQL through psql against <paramref name="database"/>, stopping
    /// at the first error; returns unaligned, tuples-only output.</summary>
    private static async Task<string> RunPsqlAsync(
        Dictionary<string, string?> env, string database, string sql, CancellationToken ct)
    {
        using var proc = StartProcess(
            "psql",
            [$"--dbname={database}", "--no-psqlrc", "--no-align", "--tuples-only", "--quiet",
             "-v", "ON_ERROR_STOP=1", "-f", "-"],
            env,
            redirectStdout: true,
            redirectStdin: true);
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardInput.WriteAsync(sql.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new BackupException(
                $"psql failed on '{database}' (exit {proc.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
        return await stdout.ConfigureAwait(false);
    }

    /// <summary>Run a tool to completion and return its stdout; a non-zero exit
    /// throws with its stderr.</summary>
    private static async Task<string> RunForOutputAsync(
        string fileName, IEnumerable<string> args, Dictionary<string, string?> env, CancellationToken ct)
    {
        using var proc = StartProcess(fileName, args, env, redirectStdout: true, redirectStdin: false);
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new BackupException(
                $"{fileName} failed (exit {proc.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
        return await stdout.ConfigureAwait(false);
    }

    /// <summary>
    /// Translate an Npgsql connection string into the <c>PG*</c> environment
    /// variables libpq tools read, and return the database name (pg_restore
    /// needs it as <c>--dbname</c>). Kept static + pure for unit testing.
    /// </summary>
    /// <remarks>
    /// The TLS settings travel with it (FU-083). They did not: against an external Postgres
    /// configured <c>SSL Mode=VerifyFull</c>, the app verified the server while every backup
    /// and restore connected with libpq's default, <c>prefer</c>, which falls back to
    /// plaintext and checks no certificate. The password does not travel here; see
    /// <see cref="OpenPgTools"/>.
    /// </remarks>
    public static (Dictionary<string, string?> Env, string Database) BuildPgEnvironment(
        string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        // libpq has a connection parameter for a client key's passphrase but no
        // environment variable, so the tools could not open the key the app opens.
        if (!string.IsNullOrEmpty(b.SslPassword))
            throw new BackupException(
                "The connection string sets SSL Password, which the backup tools cannot be given. "
                + "Use an unencrypted client key, readable only by the server's user.");

        var env = new Dictionary<string, string?>
        {
            ["PGHOST"] = b.Host,
            ["PGPORT"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGUSER"] = b.Username,
            ["PGDATABASE"] = b.Database,
            ["PGSSLMODE"] = b.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Prefer => "prefer",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                var other => throw new BackupException($"SSL Mode {other} has no libpq equivalent."),
            },
            ["PGSSLROOTCERT"] = NullIfEmpty(b.RootCertificate),
            ["PGSSLCERT"] = NullIfEmpty(b.SslCertificate),
            ["PGSSLKEY"] = NullIfEmpty(b.SslKey),
        };
        return (env, b.Database ?? string.Empty);

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// One line of a libpq password file that matches any connection: the tools are
    /// handed a file of their own, so there is nothing else for it to match.
    /// </summary>
    internal static string PassFileLine(string password)
    {
        // The format is line-based; a line break cannot be escaped in it.
        if (password.Contains('\n') || password.Contains('\r'))
            throw new BackupException("The database password contains a line break, which the backup tools cannot be given.");
        return "*:*:*:*:" + password.Replace("\\", "\\\\", StringComparison.Ordinal)
                                    .Replace(":", "\\:", StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools' environment with the password in an owner-only file, removed when this
    /// is disposed (FU-083). In <c>PGPASSWORD</c> it sat in each tool's environment for
    /// the whole run, which libpq's own documentation advises against: on some systems
    /// another user can list it.
    /// </summary>
    private PgTools OpenPgTools()
    {
        var (env, database) = BuildPgEnvironment(_connectionString);
        var password = new NpgsqlConnectionStringBuilder(_connectionString).Password;
        if (string.IsNullOrEmpty(password)) return new PgTools(env, database, directory: null);

        var line = PassFileLine(password);
        var directory = CreatePrivateDirectory(
            Path.Combine(Path.GetTempPath(), PassDirPrefix + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(directory, "pgpass");
            using (var file = new FileStream(path, PrivateCreate()))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(line + "\n");
            }
            env["PGPASSFILE"] = path;
            return new PgTools(env, database, directory);
        }
        catch
        {
            PgTools.Remove(directory);
            throw;
        }
    }

    // Swept at boot with the work directories: a process killed mid-run leaves its file.
    private const string PassDirPrefix = "coffer-pgpass-";

    private sealed class PgTools(Dictionary<string, string?> env, string database, string? directory) : IDisposable
    {
        public Dictionary<string, string?> Env { get; } = env;
        public string Database { get; } = database;

        public void Dispose()
        {
            if (directory is not null) Remove(directory);
        }

        internal static void Remove(string directory)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* swept at the next boot */ }
            catch (UnauthorizedAccessException) { }
        }
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

/// <summary>
/// A restore's swap reported an error and its result could not be read back
/// from the catalog. Reported as "unknown" — never as failed (the live database
/// may have been replaced) nor as applied (it may not).
/// </summary>
public sealed class RestoreOutcomeUnknownException(string message, Exception inner) : Exception(message, inner);

/// <summary>Thrown when pg_dump/pg_restore can't be run or fails.</summary>
public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
    public BackupException(string message, Exception inner) : base(message, inner) { }
}
