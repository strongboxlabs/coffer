namespace Coffer.Api.Import;

/// <summary>Lifecycle state of a background Moneydance import (ADR-0071 D2).</summary>
public enum ImportJobState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>What an import job imports.</summary>
public enum ImportJobKind
{
    /// <summary>A Moneydance export the user uploaded.</summary>
    MoneydanceFile,

    /// <summary>The generated Demo, added from the hub (ADR-0115 D3).</summary>
    Demo,
}

/// <summary>Which part of its run a job is in. Only a Demo job has a second part.</summary>
public enum ImportJobPhase
{
    /// <summary>The import, up to and including its single commit.</summary>
    Import,

    /// <summary>A Demo's examples, added to the committed ledger (ADR-0115 D3).</summary>
    Examples,
}

/// <summary>
/// A single in-flight or completed import. Mutated only through
/// <see cref="ImportJobRegistry"/> (under its lock); callers read immutable
/// snapshots. Progress is coarse — <see cref="Completed"/> of
/// <see cref="Total"/> steps — which is all the wizard's bar needs.
/// </summary>
public sealed class ImportJob
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string LedgerName { get; init; }

    /// <summary>What the job imports. A Demo job also runs its examples phase.</summary>
    public ImportJobKind Kind { get; init; } = ImportJobKind.MoneydanceFile;

    /// <summary>The steps a job runs beyond the import's own, counted in
    /// <see cref="Total"/>: the examples phase's for a Demo, none otherwise.</summary>
    public int ExtraSteps { get; init; }

    public ImportJobPhase Phase { get; set; } = ImportJobPhase.Import;

    /// <summary>The new ledger's reporting currency (ADR-0114 D7), in stored form, chosen
    /// in the wizard; USD unless named.</summary>
    public string CurrencyCode { get; init; } = Db.Repositories.DisplayCurrency.Fallback;

    public ImportJobState State { get; set; } = ImportJobState.Running;
    public int Completed { get; set; }
    public int Total { get; set; }
    public string? Step { get; set; }
    public Guid? LedgerId { get; set; }
    public string? Error { get; set; }

    // What a finished import reports beyond its ledger (ImportJobOutcome); empty
    // until the job succeeds. The lists are never mutated once set, so a snapshot
    // shares them.
    public IReadOnlyList<ImportWarning> Warnings { get; set; } = [];
    public IReadOnlyList<ImportSkippedTransaction> SkippedTransactions { get; set; } = [];
    public int SkippedTransactionsNotListed { get; set; }

    internal ImportJob Clone() => new()
    {
        Id = Id,
        UserId = UserId,
        LedgerName = LedgerName,
        Kind = Kind,
        ExtraSteps = ExtraSteps,
        Phase = Phase,
        CurrencyCode = CurrencyCode,
        State = State,
        Completed = Completed,
        Total = Total,
        Step = Step,
        LedgerId = LedgerId,
        Error = Error,
        Warnings = Warnings,
        SkippedTransactions = SkippedTransactions,
        SkippedTransactionsNotListed = SkippedTransactionsNotListed,
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

    /// <summary>
    /// Whether the user has an import running now. A hint for a caller that would do
    /// costly work before <see cref="TryStart"/>; <see cref="TryStart"/> stays the
    /// authority, since a job can start between the two.
    /// </summary>
    public bool HasRunning(Guid userId)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => j.UserId == userId && j.State == ImportJobState.Running);
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

    /// <summary>
    /// A copy of the user's Demo job, running or finished, or null. The hub reads it to
    /// find a build again after navigating away (ADR-0115 D9).
    /// </summary>
    /// <remarks>A user holds at most one job: <see cref="TryStart"/> purges their finished
    /// jobs before it registers the next. So a later import of any kind ends the Demo
    /// job's lookup, and the hub reads the gap as a build it can no longer follow.</remarks>
    public ImportJob? LatestDemo(Guid userId)
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => j.UserId == userId && j.Kind == ImportJobKind.Demo)
                .Select(j => j.Clone())
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// The ledgers whose Demo job is still running, of every user. A running job has a
    /// ledger only once its import has committed, in the examples phase.
    /// </summary>
    public IReadOnlySet<Guid> RunningDemoLedgerIds()
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => j.Kind == ImportJobKind.Demo && j.State == ImportJobState.Running && j.LedgerId is not null)
                .Select(j => j.LedgerId!.Value)
                .ToHashSet();
        }
    }
}
