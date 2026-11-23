using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// One test's own database: a clone of the fixture's settled template, and every way
/// a test reaches it.
/// </summary>
/// <remarks>
/// <para>Created by <see cref="DatabaseTest"/> before the test runs and disposed after
/// it, so nothing a test writes is visible to any other test. Every connection string
/// here points at THIS database (or at one named under it); there is no member that
/// reaches a database shared between tests.</para>
/// <para>Three roles, as production has them: <c>coffer_service</c> (BYPASSRLS, owns
/// the schema), <c>coffer_app</c> (RLS applies; what the API's request path runs as),
/// and the container superuser, for a test that must change what a role may do, as an
/// operator would.</para>
/// <para>Disposal ends the test (<see cref="TestDatabaseCluster.EndTestAsync"/>): it
/// checks the test's sessions, clears every pool, hands this database, its siblings and
/// its empty databases to the background drop, and checks what the test must not leave
/// behind.</para>
/// </remarks>
public sealed partial class TestDatabase : IAsyncDisposable
{
    private readonly TestDatabaseCluster _cluster;
    private readonly TestDatabaseCredentials _credentials;
    private readonly string _owningTest;
    private readonly HashSet<string> _empties = new(StringComparer.Ordinal);
    private readonly List<TestDatabase> _siblings = new();
    private readonly bool _isSibling;
    private bool _disposed;

    internal TestDatabase(
        TestDatabaseCluster cluster, TestDatabaseCredentials credentials, string name, string owningTest,
        bool isSibling = false)
    {
        _cluster = cluster;
        _credentials = credentials;
        _owningTest = owningTest;
        _isSibling = isSibling;
        Name = name;
        ServiceConnectionString = credentials.Service(name);
        AppConnectionString = credentials.App(name);
        SuperuserConnectionString = credentials.Superuser(name);
    }

    /// <summary>This database's name (<c>t_&lt;seq&gt;_&lt;rand&gt;</c>).</summary>
    public string Name { get; }

    /// <summary><c>coffer_service</c> (BYPASSRLS) on this database.</summary>
    public string ServiceConnectionString { get; }

    /// <summary><c>coffer_app</c> (no BYPASSRLS) on this database. Raw SQL here sees
    /// nothing until <c>app.user_id</c> is set — the production RLS path.</summary>
    public string AppConnectionString { get; }

    /// <summary>The container superuser on this database. Its sessions are checked at
    /// the test's end like any other role's.</summary>
    public string SuperuserConnectionString { get; }

    /// <summary>
    /// A bare <see cref="AppDbContext"/> as the service role: a raw SEEDING handle.
    /// </summary>
    /// <remarks>
    /// <b>Not the production shape.</b> No recompute interceptors, so a save through it
    /// does not recompute derived state. <see cref="NewServiceFactory"/> is what
    /// production uses, and anything asserting that a write recomputed must go through
    /// that, or it proves nothing.
    /// </remarks>
    public AppDbContext NewServiceDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ServiceConnectionString).Options);

    /// <summary>Same as <see cref="NewServiceDbContext"/>.</summary>
    public AppDbContext NewDbContext() => NewServiceDbContext();

    /// <summary>A <see cref="ServiceDbContextFactory"/> on this database, wired the way
    /// production wires it (<see cref="PostgresFixture.ServiceFactoryFor"/>).</summary>
    public ServiceDbContextFactory NewServiceFactory() =>
        PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = AppConnectionString,
            ServiceConnectionString = ServiceConnectionString,
        }));

    /// <summary><see cref="NewServiceFactory"/> with <paramref name="extra"/> interceptors on
    /// every context after production's own: how a test injects a fault into a path that
    /// takes the factory.</summary>
    public ServiceDbContextFactory NewServiceFactoryWith(params IInterceptor[] extra) =>
        PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = AppConnectionString,
            ServiceConnectionString = ServiceConnectionString,
        }), extra);

    /// <summary>An <see cref="AppDbContext"/> as <c>coffer_app</c> with
    /// <c>app.user_id</c> pinned to <paramref name="userId"/> on open, to drive RLS
    /// directly without the HTTP pipeline.</summary>
    public AppDbContext NewAppDbContextAsUser(Guid userId) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(AppConnectionString)
            .AddInterceptors(new FixedUserConnectionInterceptor(userId))
            .Options);

    /// <summary>
    /// <see cref="NewAppDbContextAsUser"/> wired as a request's context is wired: the
    /// member's user on the app-role connection, plus the save interceptors production
    /// registers, the stored-notes recompute last (as <c>Program.cs</c> registers it).
    /// </summary>
    /// <param name="userId">The member the connection acts as.</param>
    /// <param name="extra">Test interceptors (a command counter, a planted failure), added
    /// before the save interceptors.</param>
    public AppDbContext NewRequestDbContextAsUser(
        Guid userId, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(AppConnectionString)
            .AddInterceptors(new FixedUserConnectionInterceptor(userId))
            .AddInterceptors(extra)
            .AddInterceptors(
                new LegDerivedRecomputeInterceptor(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LegDerivedRecomputeInterceptor>.Instance),
                new HoldingsRecomputeInterceptor(),
                new TradePriceFromLegInterceptor(),
                new LegNotesRecomputeInterceptor())
            .Options);

    /// <summary>An open raw connection as the service role.</summary>
    public NpgsqlConnection OpenServiceConnection()
    {
        var connection = new NpgsqlConnection(ServiceConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// The key service every test host runs under: 32 zero bytes, non-secret by
    /// intent, so material wrapped by a repository in a test opens in a host.
    /// </summary>
    public LedgerKeyService NewLedgerKeyService() => PostgresFixture.FixtureKeys();

    /// <summary>
    /// A service-role connection string for an EMPTY database named under this one
    /// (<c>&lt;Name&gt;_&lt;suffix&gt;</c>): Postgres reachable, no Coffer schema — the
    /// state a fresh install boots into.
    /// </summary>
    /// <remarks>
    /// Created on the first call for a suffix and reused by later calls in the same
    /// test; dropped with this database. Made the way a fresh install's database is
    /// (<see cref="TestDatabaseCluster.CreateEmptyAsync"/>): from <c>template0</c>, owned
    /// by <c>coffer_service</c>, with db/init's schema grants and extensions; without the
    /// grants the service role cannot migrate it.
    /// </remarks>
    public string EmptyDatabaseConnectionString(string suffix = "empty_probe")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Suffix().IsMatch(suffix))
            throw new ArgumentException(
                $"'{suffix}' must be 1-40 lowercase letters, digits or underscores.", nameof(suffix));
        var name = $"{Name}_{suffix}";
        if (_empties.Add(name))
            _cluster.CreateEmptyAsync(name, _owningTest).GetAwaiter().GetResult();
        return _credentials.Service(name);
    }

    /// <summary>
    /// A second clone of the template, named under this one, for a test that needs two
    /// databases. Dropped with this database; the caller does not dispose it.
    /// </summary>
    public async Task<TestDatabase> CreateSiblingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var name = $"{Name}_s{_siblings.Count + 1}";
        await _cluster.CloneTemplateAsync(name, _owningTest).ConfigureAwait(false);
        var sibling = new TestDatabase(_cluster, _credentials, name, _owningTest, isSibling: true);
        _siblings.Add(sibling);
        return sibling;
    }

    /// <summary>The session checks this database's disposal runs before it clears the
    /// pools, callable on their own so a test of the checks can plant what they must
    /// catch.</summary>
    internal Task CheckSessionsAsync(TimeSpan? settle = null) => _cluster.CheckSessionsAsync(Name, settle);

    /// <summary>End the test: check, clear every pool, queue the drop (whatever the
    /// checks found), check again (<see cref="TestDatabaseCluster.EndTestAsync"/>).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed || _isSibling) return;
        _disposed = true;
        await _cluster.EndTestAsync(Name).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();
}

/// <summary>The three roles' credentials on the fixture's container, as connection
/// strings for any database on it.</summary>
internal sealed class TestDatabaseCredentials
{
    private readonly string _service;
    private readonly string _app;
    private readonly string _superuser;

    public TestDatabaseCredentials(string service, string app, string superuser)
    {
        _service = service;
        _app = app;
        _superuser = superuser;
    }

    public string Service(string database) => TestDatabaseCluster.For(_service, database);
    public string App(string database) => TestDatabaseCluster.For(_app, database);
    public string Superuser(string database) => TestDatabaseCluster.For(_superuser, database);
}
