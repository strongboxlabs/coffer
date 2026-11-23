using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db;
using Coffer.Api.Ingest;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// One run of the examples phase, as its steps see it: the Demo they work on, the ids the
/// first step found, what a step hands a later one, and the app's own collaborators that
/// the writers need.
/// </summary>
public sealed class DemoSeedRun
{
    internal DemoSeedRun(
        DemoSeedContext context,
        TimeProvider clock,
        IReadOnlyList<IFileProvider> fileProviders,
        SyncConnectionLock syncLock)
    {
        Context = context;
        Clock = clock;
        FileProviders = fileProviders;
        SyncLock = syncLock;
    }

    /// <summary>The Demo being seeded.</summary>
    public DemoSeedContext Context { get; }

    /// <summary>The Demo's ledger: every write names it.</summary>
    public Guid LedgerId => Context.LedgerId;

    /// <summary>The Demo's last day, the date every step computes from.</summary>
    public DateOnly AsOf => Context.AsOf;

    /// <summary>For the timestamps the writers stamp, never for a date.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The app's registered file importers, for the card statement.</summary>
    public IReadOnlyList<IFileProvider> FileProviders { get; }

    /// <summary>The app's one per-connection import lock, which the file import takes.</summary>
    public SyncConnectionLock SyncLock { get; }

    /// <summary>The app's loggers, for the writers a step builds that log.</summary>
    public ILoggerFactory Loggers { get; internal init; } = NullLoggerFactory.Instance;

    /// <summary>The ids the first step found. Every later step needs it, so it is set
    /// before any of them runs.</summary>
    public DemoSeedIds Ids
    {
        get => _ids ?? throw new InvalidOperationException("The imported rows have not been matched yet.");
        internal set => _ids = value;
    }

    private DemoSeedIds? _ids;

    /// <summary>The saved reports' ids by their place in the seeded list, for the Home
    /// layout's tiles.</summary>
    public IReadOnlyList<Guid> SavedReportIds { get; internal set; } = [];
}

/// <summary>A step body: what one examples step writes, on its own fresh service context.</summary>
/// <param name="run">The run the step belongs to.</param>
/// <param name="db">A service context created for this step alone.</param>
/// <param name="cancellationToken">The run's token.</param>
public delegate Task DemoSeedStepBody(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken);

/// <summary>A step could not add its example; the message says why, in words the Activity
/// detail can show.</summary>
public sealed class DemoSeedStepException : Exception
{
    public DemoSeedStepException(string message) : base(message)
    {
    }

    public DemoSeedStepException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DemoSeedStepException()
    {
    }
}
