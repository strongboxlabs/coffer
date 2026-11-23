using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Auth;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// The real entry point started with <c>--adopt-new-kek</c> (ADR-0092 D3), as a child
/// process on this test's own database: where in the boot the clearing runs, that it never
/// serves, that a staged restore waits, that the flag with a key in place refuses, and that
/// concurrent starts which may mint end on one key.
/// </summary>
/// <remarks>
/// A child process rather than <c>WebApplicationFactory</c>: the paths under test return
/// before the host runs, and the configuration they read is process environment, which a
/// child gets of its own instead of sharing with every collection in this process. It runs
/// the API built beside this test assembly's configuration.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdoptNewKekStartTests : DatabaseTest
{
    /// <summary>How long a child may take to exit or serve. A full migration from empty, the
    /// slowest case here, takes about 6 s on an idle developer machine; this bound is far past
    /// what a healthy run reaches on a machine also running CI shards, so hitting it means
    /// the child hung.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"coffer-adopt-start-{Guid.NewGuid():N}");
    private readonly List<Process> _started = [];

    public AdoptNewKekStartTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    protected override Task OnDisposingAsync()
    {
        // A child a failed assertion left running would hold this test's database open.
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    private string KeyPath => Path.Combine(_dir, "keys", "master.key");
    private string StagingDir => Path.Combine(_dir, "restore-staging");

    private sealed record Run(int ExitCode, string Output, bool Served);

    [Fact]
    public async Task An_adopt_start_clears_audits_saves_the_key_and_stores_new_oauth_keys_without_serving_or_touching_a_staged_restore()
    {
        await LostKeySeed.SeedAsync(Db);
        var staged = StageRestore();

        var run = await RunAsync(await StartAsync("--adopt-new-kek"));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.False(run.Served, run.Output);
        Assert.Contains("ABANDONED", run.Output, StringComparison.Ordinal);
        Assert.Contains("ledger keys replaced:            1", run.Output, StringComparison.Ordinal);
        Assert.Contains("OAuth server keys discarded:     yes", run.Output, StringComparison.Ordinal);
        var key = ReadKey();
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, key));
        await using (var db = Db.NewDbContext())
        {
            // The lost key's pair was deleted by the clearing, and the run stored a new one
            // under the new key before it released the mint lock (EverythingOpens covers it).
            Assert.Equal(1, await db.OidcServerKeys.CountAsync());
            Assert.Equal(1, await db.AdminAuditEvents.CountAsync(e => e.Action == AdminAuditActions.MasterKeyAdoptedNew));
            Assert.Equal(0, await db.FeedConnections.CountAsync(f => f.AccessUrlCiphertext != null));
        }
        // The staged restore and its source key are exactly as they were: neither the source
        // key adoption nor the restore ran.
        foreach (var (path, bytes) in staged)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_start_with_the_flag_and_a_key_in_place_refuses_and_changes_nothing()
    {
        // What a restart with the flag still set meets after a successful adoption.
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var existing = Convert.ToBase64String(new byte[32]);
        new MasterKeyStore(KeyPath).Write(existing, "test");
        var before = await LostKeySeed.SealedStateAsync(Db);

        var run = await RunAsync(await StartAsync("--adopt-new-kek"));

        Assert.True(run.ExitCode != 0, run.Output);
        Assert.False(run.Served, run.Output);
        Assert.Contains("Remove --adopt-new-kek", run.Output, StringComparison.Ordinal);
        Assert.Equal((existing, "test"), new MasterKeyStore(KeyPath).Read());
        Assert.Equal(before, await LostKeySeed.SealedStateAsync(Db));
    }

    [Fact]
    public async Task Two_adopt_starts_at_once_end_with_one_adoption_and_one_key_that_opens_everything()
    {
        await LostKeySeed.SeedAsync(Db);

        var first = StartAsync("--adopt-new-kek");
        var second = StartAsync("--adopt-new-kek");
        var runs = await Task.WhenAll(RunAsync(await first), RunAsync(await second));

        var output = string.Join("\n----\n", runs.Select(r => r.Output));
        Assert.Equal(1, runs.Count(r => r.ExitCode == 0 && r.Output.Contains("ABANDONED", StringComparison.Ordinal)));
        Assert.Equal(1, runs.Count(r => r.ExitCode != 0 && r.Output.Contains("Remove --adopt-new-kek", StringComparison.Ordinal)));
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, ReadKey()), output);
        await using var db = Db.NewDbContext();
        Assert.Equal(1, await db.AdminAuditEvents.CountAsync(e => e.Action == AdminAuditActions.MasterKeyAdoptedNew));
    }

    [Fact]
    public async Task An_adopt_start_over_nothing_sealed_saves_a_key_says_so_and_exits_without_serving()
    {
        // The repeat a committed-but-unsaved adoption of an install without ledgers needs:
        // the wipe has run, nothing sealed is left, and there is no key file.
        await LostKeySeed.SeedAsync(Db, withLedger: false);
        await new KekReconciliationService(
                Db.NewServiceFactory(),
                new LedgerKeyService(new MasterKey(RandomNumberGenerator.GetBytes(32), "unsaved")),
                NullLogger<KekReconciliationService>.Instance, TimeProvider.System)
            .ReconcileAsync();
        Assert.False(WrappedMaterialProbeTests.Probe(Db.ServiceConnectionString));

        var run = await RunAsync(await StartAsync("--adopt-new-kek"));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.False(run.Served, run.Output);
        Assert.Contains("nothing sealed to wipe", run.Output, StringComparison.Ordinal);
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, ReadKey()));
    }

    [Fact]
    public async Task A_first_start_with_the_flag_migrates_saves_a_key_and_exits_without_serving()
    {
        // A database no start has run on: no schema, nothing sealed.
        var service = Db.EmptyDatabaseConnectionString("adopt_first_start");
        var app = new NpgsqlConnectionStringBuilder(Db.AppConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(service).Database,
        }.ConnectionString;

        var run = await RunAsync(await StartAsync(["--adopt-new-kek"], service, app));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.False(run.Served, run.Output);
        Assert.Contains("nothing sealed to wipe", run.Output, StringComparison.Ordinal);
        Assert.NotNull(new MasterKeyStore(KeyPath).Read().Key);
        // Migrated, and its first sealed secret stored before the key was saved.
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(service).Options);
        Assert.Equal(1, await db.OidcServerKeys.CountAsync());
    }

    [Fact]
    public async Task A_first_start_that_cannot_store_its_oauth_keys_saves_no_key_and_exits_then_a_retry_completes()
    {
        // Nothing sealed, so this is a virgin mint. The service role, which owns the table,
        // is refused the insert: the start's first secret cannot be stored. Released over a
        // saved key with nothing sealed, the lock would let a start on another key volume
        // read the install as virgin and mint a second key.
        await using (var db = Db.NewDbContext())
            await db.OidcServerKeys.ExecuteDeleteAsync();
        await GrantOauthKeyInsertAsync(grant: false);

        var failed = await RunAsync(await StartAsync());

        Assert.True(failed.ExitCode != 0, failed.Output);
        Assert.False(failed.Served, failed.Output);
        Assert.Contains("could not store the OAuth server keys", failed.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(KeyPath), failed.Output);

        await GrantOauthKeyInsertAsync(grant: true);
        var retry = await RunAsync(await StartAsync());

        Assert.True(retry.Served, retry.Output);
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, ReadKey()));
        await using (var db = Db.NewDbContext())
            Assert.Equal(1, await db.OidcServerKeys.CountAsync());
    }

    /// <summary>Take the insert on <c>oidc_server_keys</c> from the service role, or give it
    /// back: privilege DDL on this test's own database, no trigger.</summary>
    private async Task GrantOauthKeyInsertAsync(bool grant)
    {
        await using var connection = new NpgsqlConnection(Db.ServiceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(grant
            ? "GRANT INSERT ON oidc_server_keys TO coffer_service"
            : "REVOKE INSERT ON oidc_server_keys FROM coffer_service", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_plain_start_that_finds_no_key_waits_for_the_mint_lock_then_uses_the_key_the_holder_saved()
    {
        // Nothing sealed, so a plain start that did not wait would read the install as virgin
        // and mint. The test holds the lock, playing an adopt run between its clearing and
        // its key write.
        await using (var db = Db.NewDbContext())
            await db.OidcServerKeys.ExecuteDeleteAsync();
        Assert.False(WrappedMaterialProbeTests.Probe(Db.ServiceConnectionString));

        Run run;
        using (var held = MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(30)))
        {
            var started = await StartAsync();
            await WaitForOutputAsync(started, "another start is minting or adopting one; waiting", KeyPath);
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(started.Process.HasExited, Text(started));
            Assert.False(File.Exists(KeyPath), "the waiting start wrote a key:" + Environment.NewLine + Text(started));

            // The holder's work: its key saved, its first secret sealed under it.
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            new MasterKeyStore(KeyPath).Write(Convert.ToBase64String(new byte[32]), "test");
            Assert.True(await new OpenIddictKeyMaterial(
                    Db.NewServiceFactory(), TimeProvider.System, NullLogger<OpenIddictKeyMaterial>.Instance)
                .LoadAsync(PostgresFixture.FixtureKeys(), Path.Combine(_dir, "no-legacy")));
            held.Dispose();
            run = await RunAsync(started);
        }

        // It used the holder's key, minting none of its own.
        Assert.True(run.Served, run.Output);
        Assert.Equal((Convert.ToBase64String(new byte[32]), "test"), new MasterKeyStore(KeyPath).Read());
        Assert.True(await LostKeySeed.EverythingOpensUnderAsync(Db, PostgresFixture.FixtureKeys()));
    }

    /// <summary>The last lines of a child's output, for a failure message.</summary>
    private static string Tail(string output) =>
        string.Join(Environment.NewLine, output.Split('\n').TakeLast(40));

    private static string Text((Process Process, StringBuilder Output) started)
    {
        lock (started.Output) return started.Output.ToString();
    }

    private static async Task WaitForOutputAsync(
        (Process Process, StringBuilder Output) started, string text, string keyPath)
    {
        var clock = Stopwatch.StartNew();
        while (!Text(started).Contains(text, StringComparison.Ordinal))
        {
            Assert.False(File.Exists(keyPath), $"Wrote a key instead of waiting:{Environment.NewLine}{Text(started)}");
            Assert.False(started.Process.HasExited, $"Exited before printing '{text}':{Environment.NewLine}{Text(started)}");
            Assert.True(clock.Elapsed < RunTimeout,
                $"TIMED OUT after {RunTimeout} waiting for '{text}'. The child's last output:{Environment.NewLine}{Tail(Text(started))}");
            await Task.Delay(100);
        }
    }

    // --- the child process ------------------------------------------------------------

    private LedgerKeyService ReadKey()
    {
        var (key, id) = new MasterKeyStore(KeyPath).Read();
        Assert.NotNull(key);
        return new LedgerKeyService(MasterKeyLoader.LoadFromValueOrThrow(key, id ?? "unknown", KeyPath));
    }

    /// <summary>A pending restore with a source key, staged the way the setup screen leaves
    /// one; the contents are never read when nothing applies them.</summary>
    private List<(string Path, byte[] Bytes)> StageRestore()
    {
        Directory.CreateDirectory(StagingDir);
        var files = new List<(string, byte[])>();
        foreach (var name in new[] { "archive.cofferbak", "passphrase", "attempt", "restore.pending", "source-master.key" })
        {
            var path = Path.Combine(StagingDir, name);
            var bytes = Encoding.UTF8.GetBytes(name == "source-master.key"
                ? Convert.ToBase64String(new byte[32]) : $"staged {name}");
            File.WriteAllBytes(path, bytes);
            files.Add((path, bytes));
        }
        return files;
    }

    private Task<(Process Process, StringBuilder Output)> StartAsync(params string[] args) =>
        StartAsync(args, Db.ServiceConnectionString, Db.AppConnectionString);

    private Task<(Process Process, StringBuilder Output)> StartAsync(
        string[] args, string serviceConnectionString, string appConnectionString)
    {
        var api = ApiAssembly();
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(api))))!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(api);
        foreach (var arg in args) info.ArgumentList.Add(arg);

        // Only what this run sets: the test process carries hosts' settings of its own.
        foreach (var name in info.Environment.Keys.ToList())
            if (name.StartsWith("COFFER_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(name);
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["COFFER_API__ConnectionString"] = appConnectionString,
            ["COFFER_API__ServiceConnectionString"] = serviceConnectionString,
            ["COFFER_API__MasterKey__Path"] = KeyPath,
            ["COFFER_API__Fido2__RpId"] = "localhost",
            ["COFFER_API__Fido2__Origins__0"] = "http://localhost",
            ["COFFER_API__Backup__Directory"] = Path.Combine(_dir, "backups"),
            ["COFFER_API__Backup__SweepTempAtBoot"] = "false",
            ["COFFER_API__Mcp__LegacyKeyDirectory"] = Path.Combine(_dir, "openiddict"),
            ["COFFER_RESTORE_STAGING_DIR"] = StagingDir,
        };
        foreach (var (name, value) in env) info.Environment[name] = value;

        var output = new StringBuilder();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        void Append(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        _started.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return Task.FromResult((process, output));
    }

    /// <summary>Wait for the run to exit, or stop it once it serves.</summary>
    private async Task<Run> RunAsync((Process Process, StringBuilder Output) started)
    {
        var (process, output) = started;
        using (process)
        {
            string Text() { lock (output) return output.ToString(); }
            var deadline = Stopwatch.StartNew();
            var served = false;
            while (!process.HasExited)
            {
                var text = Text();
                if (text.Contains("Now listening on", StringComparison.Ordinal)
                    || text.Contains("Application started", StringComparison.Ordinal))
                {
                    served = true;
                    process.Kill(entireProcessTree: true);
                    break;
                }
                if (deadline.Elapsed > RunTimeout)
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail($"TIMED OUT after {RunTimeout}: the API neither exited nor served. "
                        + $"Its last output:{Environment.NewLine}{Tail(Text())}");
                }
                await Task.Delay(100);
            }
            await process.WaitForExitAsync();
            var result = new Run(served ? -1 : process.ExitCode, Text(), served);
            Output.WriteLine($"--- exit {result.ExitCode}, served {result.Served}\n{result.Output}");
            return result;
        }
    }

    /// <summary>The API assembly built in this test assembly's configuration.</summary>
    private static string ApiAssembly()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var framework = here.Name;                       // net10.0
        var configuration = here.Parent!.Name;           // Debug | Release
        var dir = here;
        while (dir is not null && !(File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                                    && Directory.Exists(Path.Combine(dir.FullName, "src", "Api"))))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var api = Path.Combine(dir.FullName, "src", "Api", "bin", configuration, framework, "coffer-api.dll");
        Assert.True(File.Exists(api), $"Build the API first: {api} does not exist.");
        return api;
    }
}
