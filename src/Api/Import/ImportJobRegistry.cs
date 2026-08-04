namespace Coffer.Api.Import;

/// <summary>Lifecycle state of a background Moneydance import (ADR-0071 D2).</summary>
public enum ImportJobState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A single in-flight or completed import. Mutated only through
/// <see cref="ImportJobRegistry"/> (under its lock); callers read immutable
/// snapshots. Progress is coarse — <see cref="Completed"/> of
/// <see cref="Total"/> pipeline steps — which is all the wizard's bar needs.
/// </summary>
public sealed class ImportJob
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string LedgerName { get; init; }

    public ImportJobState State { get; set; } = ImportJobState.Running;
    public int Completed { get; set; }
    public int Total { get; set; }
    public string? Step { get; set; }
    public Guid? LedgerId { get; set; }
    public string? Error { get; set; }

    internal ImportJob Clone() => new()
    {
        Id = Id,
        UserId = UserId,
        LedgerName = LedgerName,
        State = State,
        Completed = Completed,
        Total = Total,
        Step = Step,
        LedgerId = LedgerId,
        Error = Error,
    };
}

/// <summary>
/// In-memory registry of import jobs (ADR-0071 D2). Singleton. Jobs are
/// transient by design: a container restart mid-import rolls the transaction
/// back (no partial ledger) and drops the job — the user simply retries. Bounds
/// concurrency to one running import per user.
/// </summary>
public sealed class ImportJobRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ImportJob> _jobs = new();

    /// <summary>
    /// Register a new job iff the user has no import currently running. Returns
    /// false when one is already in flight (the caller maps this to
    /// <c>import-already-running</c>). Purges the user's finished jobs first so
    /// the map doesn't grow without bound.
    /// </summary>
    public bool TryStart(ImportJob job)
    {
        lock (_gate)
        {
            if (_jobs.Values.Any(j => j.UserId == job.UserId && j.State == ImportJobState.Running))
                return false;

            foreach (var stale in _jobs.Values
                         .Where(j => j.UserId == job.UserId && j.State != ImportJobState.Running)
                         .Select(j => j.Id)
                         .ToList())
                _jobs.Remove(stale);

            _jobs[job.Id] = job;
            return true;
        }
    }

    /// <summary>Apply a mutation to a job under the lock (no-op if it's gone).</summary>
    public void Update(Guid id, Action<ImportJob> mutate)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var job))
                mutate(job);
        }
    }

    /// <summary>A consistent point-in-time copy, or null if unknown.</summary>
    public ImportJob? Snapshot(Guid id)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out var job) ? job.Clone() : null;
        }
    }
}
