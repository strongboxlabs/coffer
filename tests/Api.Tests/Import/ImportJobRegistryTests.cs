using Coffer.Api.Import;

namespace Coffer.Api.Tests.Import;

/// <summary>
/// Pure-logic tests for the import job registry (ADR-0071 D2): the
/// one-running-import-per-user guard and snapshot isolation. No DB.
/// </summary>
public sealed class ImportJobRegistryTests
{
    private static ImportJob NewJob(Guid userId, string name = "book") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        LedgerName = name,
        Total = 9,
    };

    [Fact]
    public void TryStart_allows_the_first_job_but_blocks_a_second_running_one_for_the_same_user()
    {
        var registry = new ImportJobRegistry();
        var user = Guid.NewGuid();

        Assert.True(registry.TryStart(NewJob(user)));
        Assert.False(registry.TryStart(NewJob(user)));
    }

    [Fact]
    public void TryStart_allows_concurrent_imports_for_different_users()
    {
        var registry = new ImportJobRegistry();

        Assert.True(registry.TryStart(NewJob(Guid.NewGuid())));
        Assert.True(registry.TryStart(NewJob(Guid.NewGuid())));
    }

    [Fact]
    public void TryStart_allows_a_new_import_once_the_previous_one_finished()
    {
        var registry = new ImportJobRegistry();
        var user = Guid.NewGuid();
        var first = NewJob(user);

        Assert.True(registry.TryStart(first));
        registry.Update(first.Id, j => j.State = ImportJobState.Succeeded);

        Assert.True(registry.TryStart(NewJob(user)));
        // The finished job is purged when the next one starts.
        Assert.Null(registry.Snapshot(first.Id));
    }

    [Fact]
    public void Snapshot_is_an_independent_copy()
    {
        var registry = new ImportJobRegistry();
        var job = NewJob(Guid.NewGuid());
        registry.TryStart(job);

        var before = registry.Snapshot(job.Id);
        registry.Update(job.Id, j => { j.State = ImportJobState.Failed; j.Error = "boom"; });
        var after = registry.Snapshot(job.Id);

        Assert.NotNull(before);
        Assert.Equal(ImportJobState.Running, before!.State);   // earlier snapshot unaffected
        Assert.Equal(ImportJobState.Failed, after!.State);
        Assert.Equal("boom", after.Error);
    }

    [Fact]
    public void Snapshot_returns_null_for_an_unknown_job()
    {
        var registry = new ImportJobRegistry();
        Assert.Null(registry.Snapshot(Guid.NewGuid()));
    }
}
