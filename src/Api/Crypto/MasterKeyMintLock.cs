using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Coffer.Api.Crypto;

/// <summary>
/// The one lock every start that may mint a master key takes (ADR-0092 D3): a virgin
/// install minting its first key, and a <c>--adopt-new-kek</c> run. Held from before the
/// key file is re-read through the moment the minted key is written.
/// </summary>
/// <remarks>
/// <para><b>The issue.</b> Two starts that both find no key file can both mint. Two adopt
/// runs each clear the database under their own key and each write a key file, so the file
/// can end up holding the key of the run whose ledger keys were overwritten. A start that
/// mints as virgin (nothing sealed is left, as after an adopt run on an install with no
/// ledger) can write its key between an adopt run's commit and its key write. Either way the
/// saved key does not open what the database holds.</para>
/// <para><b>The handling.</b> A Postgres session-level advisory lock, because the database
/// is the one thing every competitor shares: two containers need not share a filesystem
/// that honours file locks. Taken on its own unpooled connection, so closing it (or the
/// process dying) ends the session and releases the lock. That connection sets Npgsql's
/// <c>Keepalive</c> (a keepalive query every 30 s while idle) and <c>Tcp Keepalive</c>
/// (the operating system's TCP keepalive), so a dead peer is noticed. Every holder re-reads
/// the key file once it has the lock, so a start that waited uses the key the holder wrote.
/// The holder stores its first sealed secret (the OAuth keys) before it saves its key and
/// releases the lock, so a start that cannot see that key file finds sealed material and
/// refuses. The holder checks <see cref="IsStillHeld"/> before it clears, adopts a staged
/// key or saves a key: a lock session that dropped while the start ran its migrations means
/// another start may be minting.</para>
/// <para><b>On the maintenance database.</b> The session that holds the lock is connected
/// to <c>postgres</c>, not to the install's database: a restore swaps that database in with
/// <c>ALTER DATABASE ... RENAME</c>, which refuses while any session is connected to it, and
/// a start holding this lock can be the start that applies a staged restore. Advisory locks
/// are per database, so every competitor locks there; the key is derived from the
/// install's database name, so two installs sharing one server do not wait on each other.
/// The service role can connect to <c>postgres</c> (the migration runner does).</para>
/// <para><b>Why a context of its own.</b> This runs before migrations, so only built-in
/// functions exist, and it has to go through EF (no raw SQL in src/Api). EF needs a query
/// root that exists before migrations; <c>pg_catalog.pg_database</c> filtered to the current
/// database is exactly one row, so the built-in <c>pg_try_advisory_lock</c> is evaluated
/// once. Keeping that mapping out of <c>AppDbContext</c> keeps a catalog view out of the
/// application model.</para>
/// </remarks>
public sealed class MasterKeyMintLock : IDisposable
{
    /// <summary>The high 32 bits of every lock key: "Coff" as ASCII, recognisable in
    /// <c>pg_locks.classid</c>.</summary>
    private const long KeyPrefix = 0x436F6666L << 32;

    /// <summary>Where the lock is taken: the maintenance database every Postgres server has.</summary>
    internal const string MaintenanceDatabase = "postgres";

    /// <summary>The lock key for <paramref name="database"/>: the prefix, then the FNV-1a
    /// hash of its name, so it is stable across processes and versions.</summary>
    internal static long LockKeyFor(string database)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(database))
            hash = unchecked((hash ^ b) * 16777619u);
        return KeyPrefix | hash;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly MintLockDbContext _db;
    private bool _disposed;

    private MasterKeyMintLock(MintLockDbContext db) => _db = db;

    /// <summary>The key this lock holds (see <see cref="LockKeyFor"/>).</summary>
    internal long Key => _db.Key;

    /// <summary>
    /// Wait for the lock, at least <paramref name="timeout"/>. The wait is counted in
    /// polls, not read from a clock: this runs before the host's injected clock exists
    /// (ClockDisciplineTests), and a count of fixed sleeps can only overshoot the bound.
    /// </summary>
    /// <param name="onWaiting">Called once, when the lock is not free at first, so the
    /// caller can say what it is waiting for.</param>
    /// <exception cref="TimeoutException">Another holder kept it past the timeout.</exception>
    /// <exception cref="NpgsqlException">The database could not be reached.</exception>
    public static MasterKeyMintLock Acquire(
        string serviceConnectionString, TimeSpan timeout, Action? onWaiting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceConnectionString);

        var target = new NpgsqlConnectionStringBuilder(serviceConnectionString);
        var database = target.Database
            ?? throw new ArgumentException("The connection string names no database.", nameof(serviceConnectionString));
        var connectionString = new NpgsqlConnectionStringBuilder(serviceConnectionString)
        {
            Database = MaintenanceDatabase,
            Pooling = false,
            // Idle through the holder's migrations: a keepalive query every 30 s and TCP
            // keepalive, so a dead peer is noticed.
            KeepAlive = 30,
            TcpKeepAlive = true,
        }.ConnectionString;
        var db = new MintLockDbContext(connectionString, LockKeyFor(database));
        try
        {
            db.Database.OpenConnection();
            var pollsLeft = (long)Math.Ceiling(timeout / PollInterval);
            var announced = false;
            while (!db.TryLock())
            {
                if (pollsLeft-- <= 0)
                    throw new TimeoutException(
                        $"Another start held the master-key mint lock for more than {timeout.TotalSeconds:F0} s.");
                if (!announced)
                {
                    onWaiting?.Invoke();
                    announced = true;
                }
                Thread.Sleep(PollInterval);
            }
            return new MasterKeyMintLock(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when this lock's session is alive and still holds the lock, asked on that same
    /// session. False on any failure to ask: a caller about to write a key treats "cannot
    /// tell" as "lost".
    /// </summary>
    public bool IsStillHeld()
    {
        if (_disposed) return false;
        try
        {
            return _db.HoldsAdvisoryLock();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Release the lock and end its session.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _db.Unlock();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The session is about to end, which releases the lock anyway.
        }
        _db.Dispose();
    }

    /// <summary>Just enough model to call two built-in functions through LINQ.</summary>
    private sealed class MintLockDbContext : DbContext
    {
        private readonly string _connectionString;

        public MintLockDbContext(string connectionString, long key)
        {
            _connectionString = connectionString;
            Key = key;
        }

        public long Key { get; }

        private DbSet<CatalogDatabase> Databases => Set<CatalogDatabase>();
        private DbSet<CatalogLock> Locks => Set<CatalogLock>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql(_connectionString);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CatalogDatabase>(b =>
            {
                b.HasNoKey();
                b.ToView("pg_database", "pg_catalog");
                b.Property(d => d.Name).HasColumnName("datname");
            });
            modelBuilder.Entity<CatalogLock>(b =>
            {
                b.HasNoKey();
                b.ToView("pg_locks", "pg_catalog");
                b.Property(l => l.LockType).HasColumnName("locktype");
                b.Property(l => l.Pid).HasColumnName("pid");
                b.Property(l => l.Granted).HasColumnName("granted");
            });
        }

        public bool TryLock() =>
            Databases.Where(d => d.Name == CurrentDatabase()).Select(_ => TryAdvisoryLock(Key)).First();

        // This session takes no other advisory lock, so a granted one on its own pid is this.
        public bool HoldsAdvisoryLock() =>
            Locks.Any(l => l.LockType == "advisory" && l.Pid == BackendPid() && l.Granted);

        public void Unlock() =>
            _ = Databases.Where(d => d.Name == CurrentDatabase()).Select(_ => AdvisoryUnlock(Key)).First();

        [DbFunction("pg_try_advisory_lock", IsBuiltIn = true)]
        private static bool TryAdvisoryLock(long key) => throw new NotSupportedException(key.ToString());

        [DbFunction("pg_advisory_unlock", IsBuiltIn = true)]
        private static bool AdvisoryUnlock(long key) => throw new NotSupportedException(key.ToString());

        [DbFunction("pg_backend_pid", IsBuiltIn = true)]
        private static int BackendPid() => throw new NotSupportedException();

        [DbFunction("current_database", IsBuiltIn = true)]
        private static string CurrentDatabase() => throw new NotSupportedException();
    }

    private sealed class CatalogDatabase
    {
        public string Name { get; set; } = "";
    }

    private sealed class CatalogLock
    {
        public string LockType { get; set; } = "";
        public int? Pid { get; set; }
        public bool Granted { get; set; }
    }
}
