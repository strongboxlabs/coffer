using Xunit.Abstractions;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Base class of every integration test that touches the database: each test gets
/// its own database, cloned from the fixture's template before it runs and dropped
/// after it.
/// </summary>
/// <remarks>
/// <para>xunit makes one instance per test (per row of a theory), so <see cref="Db"/>
/// is never shared between two tests. It exists from <see cref="InitializeAsync"/> on:
/// a constructor cannot use it, and setup that needs the database goes in
/// <see cref="OnInitializedAsync"/>; teardown that needs it goes in
/// <see cref="OnDisposingAsync"/>, which runs before the database is checked and
/// dropped.</para>
/// <para>A test that needs a second database asks <see cref="TestDatabase.CreateSiblingAsync"/>
/// (a second clone) or <see cref="TestDatabase.EmptyDatabaseConnectionString"/> (no
/// schema); both are dropped with <see cref="Db"/>.</para>
/// </remarks>
public abstract class DatabaseTest : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private TestDatabase? _db;

    protected DatabaseTest(PostgresFixture fixture, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(output);
        _fixture = fixture;
        Output = output;
    }

    /// <summary>This test's output.</summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>This test's own database.</summary>
    protected TestDatabase Db => _db ?? throw new InvalidOperationException(
        "The test's database is created in InitializeAsync, after the constructor; "
        + "move setup that needs it into OnInitializedAsync.");

    /// <summary>Create the database, then run <see cref="OnInitializedAsync"/>. If that
    /// throws, the database is ended and released before the exception propagates:
    /// xunit 2 does not call <see cref="DisposeAsync"/> after a failed
    /// <c>InitializeAsync</c> (see <see cref="TestDatabaseCluster.SetUpOrTearDownAsync"/>).</summary>
    public async Task InitializeAsync()
    {
        _db = await _fixture.CreateDatabaseAsync(Output).ConfigureAwait(false);
        await TestDatabaseCluster.SetUpOrTearDownAsync(
            OnInitializedAsync,
            async () =>
            {
                _released = true;
                await _db.DisposeAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>The database was ended by a failed setup, so there is nothing left to
    /// check at disposal.</summary>
    private bool _released;

    /// <summary>Setup that needs <see cref="Db"/>; runs after it exists.</summary>
    protected virtual Task OnInitializedAsync() => Task.CompletedTask;

    /// <summary>Teardown that needs <see cref="Db"/>; runs before it is checked and
    /// dropped.</summary>
    protected virtual Task OnDisposingAsync() => Task.CompletedTask;

    /// <summary>
    /// The stored-equals-computed check (<see cref="StoredNotesDatabaseCheck"/>) over
    /// every ledger the test left in its database, before the database is dropped.
    /// </summary>
    /// <remarks>A test that seeded or changed an input of the rule past every writer and
    /// did not end with the backfill fails here, by name. A test that makes the stored
    /// values wrong on purpose puts them right before it ends.</remarks>
    private async Task AssertStoredNotesAreComputedAsync()
    {
        if (_db is null || _released) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var db = _db.NewServiceDbContext();
        await StoredNotesDatabaseCheck.AssertComputedAsync(db, "At the end of the test").ConfigureAwait(false);
        Output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"stored notes check {watch.Elapsed.TotalMilliseconds:F0} ms"));
    }

    public async Task DisposeAsync()
    {
        Exception? teardown = null;
        try
        {
            await OnDisposingAsync().ConfigureAwait(false);
            await AssertStoredNotesAreComputedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            teardown = ex;
        }

        try
        {
            if (_db is not null) await _db.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (teardown is not null)
        {
            throw new AggregateException(teardown, ex);
        }

        if (teardown is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(teardown).Throw();
    }
}
