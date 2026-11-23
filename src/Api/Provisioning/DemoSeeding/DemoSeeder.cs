using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Ingest;
using Coffer.Api.Snapshots;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The Demo job's examples phase (ADR-0115 D3): once the Demo's import has committed, it
/// adds what an export cannot carry, step by step, through the API's own writers, and
/// records on the Demo marker (<see cref="DemoMarker"/>) which steps it added. Singleton.
/// </summary>
/// <remarks>
/// <para><b>Once per Demo.</b> It runs only on a ledger whose one Demo marker is still
/// <c>running</c> and unclaimed: the marker its own import opened. It claims that marker in
/// one guarded write before the first step, so a second run, at the same time or later, is
/// refused, logged, and leaves the marker as it is: it can neither add the examples twice
/// nor rewrite the record of the first.</para>
/// <para><b>Each step on its own.</b> Every step gets a fresh service-role context (the
/// import writes as that role too) and names the Demo's ledger in every call; the writers
/// take the ledger's write lock as they always do, and nothing wraps a step in a
/// transaction. A writer's refusal is a failure of that step. A step that fails keeps what
/// it wrote, the Demo stays usable, and the steps that need it are skipped and say so. Every
/// step needs the first, which finds the imported rows; the Home layout also needs the
/// saved reports, whose tiles it shows.</para>
/// <para><b>The marker is the record.</b> Each step's result is written to the marker as
/// soon as it is known, so a process that dies mid-run leaves an accurate record for the
/// startup sweep (<see cref="DemoMarkerSweep"/>). The marker is closed once, in a
/// <c>finally</c>, whatever happened: <c>completed</c> when every step was added,
/// <c>failed</c> when the first was not, <c>partial</c> otherwise, with each step not added
/// and why.</para>
/// <para><b>Its own time.</b> The run has its own cancellation, tied to the server stopping
/// and to <see cref="Budget"/>, never to a request. A cancelled run skips what is left. Every
/// date a step computes comes from the Demo's own last day; the clock is read only for the
/// times the marker and the writers stamp.</para>
/// <para><b>Never throws.</b> Every outcome lands on the marker, and nothing here can fail
/// the job, whose ledger is already kept.</para>
/// </remarks>
public sealed class DemoSeeder
{
    /// <summary>The longest one examples phase may run before what is left is skipped.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>Why a Demo whose import was not lossless gets no examples: they are written
    /// against the rows the manifest expects, and an import that skipped or failed a check
    /// may not have them.</summary>
    public const string NotStartedLossyImport = "not started: the import reported problems";

    private readonly ServiceDbContextFactory _dbFactory;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<DemoSeeder> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IReadOnlyList<IFileProvider> _fileProviders;
    private readonly SyncConnectionLock _syncLock;

    public DemoSeeder(
        ServiceDbContextFactory dbFactory,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime lifetime,
        IEnumerable<IFileProvider> fileProviders,
        SyncConnectionLock syncLock)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(fileProviders);
        _dbFactory = dbFactory;
        _clock = clock;
        _loggers = loggerFactory;
        _logger = loggerFactory.CreateLogger<DemoSeeder>();
        _lifetime = lifetime;
        _fileProviders = fileProviders.ToList();
        _syncLock = syncLock;
    }

    /// <summary>A test's seam: runs inside each step that is about to run, before its body,
    /// so a test can hold the run at a step or make one fail. The app sets none.</summary>
    internal Func<DemoSeedStep, CancellationToken, Task>? BeforeStepAsync { get; set; }

    /// <summary>The body of each step, by key.</summary>
    /// <exception cref="KeyNotFoundException">The key is not a step.</exception>
    internal static DemoSeedStepBody BodyOf(string key) => key switch
    {
        DemoSeedSteps.Resolve => ResolveAsync,
        "folders" => DemoLedgerSteps.FoldersAsync,
        "investments" => DemoLedgerSteps.InvestmentsAsync,
        "budgets" => DemoLedgerSteps.BudgetsAsync,
        "register" => DemoRegisterSteps.RegisterAsync,
        "card-statement" => DemoCardStatementSteps.CardStatementAsync,
        "reminders" => DemoReminderSteps.RemindersAsync,
        "reports" => DemoReportSteps.ReportsAsync,
        "home" => DemoReportSteps.HomeAsync,
        "snapshot" => SnapshotAsync,
        "check" => CheckAsync,
        _ => throw new KeyNotFoundException($"no Demo examples step has the key '{key}'"),
    };

    /// <summary>What a step needs added before it can run.</summary>
    private static IReadOnlyList<string> Needs(DemoSeedStep step) => step.Key switch
    {
        DemoSeedSteps.Resolve => [],
        "home" => [DemoSeedSteps.Resolve, "reports"],
        _ => [DemoSeedSteps.Resolve],
    };

    /// <summary>
    /// Add the examples to the Demo of <paramref name="context"/>, reporting before each
    /// step how many of the job's steps are done (the step's number less one), and close
    /// its marker. Never throws.
    /// </summary>
    /// <param name="context">The Demo, as its job's own import returned it.</param>
    /// <param name="reportCompleted">Told the job's completed-step count before each step.</param>
    /// <param name="cancellationToken">Cancels the run as the server stopping does. The app
    /// passes none.</param>
    public async Task<DemoSeedOutcome> SeedAsync(
        DemoSeedContext context, Action<int> reportCompleted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reportCompleted);

        var marker = await ClaimAsync(context.LedgerId).ConfigureAwait(false);
        if (marker is null) return DemoSeedOutcome.Refused;

        var run = new DemoSeedRun(context, _clock, _fileProviders, _syncLock) { Loggers = _loggers };
        var results = new List<DemoSeedStepResult>();
        using var deadline = new CancellationTokenSource(Budget, _clock);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.ApplicationStopping, deadline.Token, cancellationToken);
        try
        {
            foreach (var step in DemoSeedSteps.All)
            {
                Report(reportCompleted, step.Number - 1);
                var result = await RunStepAsync(run, step, results, cts.Token).ConfigureAwait(false);
                results.Add(result);
                await RecordProgressAsync(marker, results).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Nothing above should throw; if it does, what was not reached is recorded as
            // not added, and the marker is still closed below.
            _logger.LogError(ex, "Demo examples on ledger {LedgerId} stopped early.", context.LedgerId);
        }
        finally
        {
            foreach (var step in DemoSeedSteps.All.Where(s => results.All(r => r.Step != s)))
                results.Add(new DemoSeedStepResult(step, false, "failed: the examples stopped early"));
            await CloseAsync(marker, results).ConfigureAwait(false);
        }

        return new DemoSeedOutcome(StatusOf(results), results);
    }

    /// <summary>
    /// Close the Demo marker of <paramref name="ledgerId"/> as <c>failed</c> without
    /// adding anything, with <paramref name="reason"/>: the examples phase of a Demo whose
    /// import was not lossless. Refused like <see cref="SeedAsync"/> unless the marker is
    /// the one its import opened. Never throws.
    /// </summary>
    public async Task<DemoSeedOutcome> CloseNotStartedAsync(Guid ledgerId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var marker = await ClaimAsync(ledgerId).ConfigureAwait(false);
        if (marker is null) return DemoSeedOutcome.Refused;

        var results = DemoSeedSteps.All.Select(s => new DemoSeedStepResult(s, false, reason)).ToList();
        await CloseAsync(marker, results, errorMessage: reason).ConfigureAwait(false);
        return new DemoSeedOutcome(DemoMarker.Failed, results);
    }

    private async Task<DemoSeedStepResult> RunStepAsync(
        DemoSeedRun run, DemoSeedStep step, IReadOnlyList<DemoSeedStepResult> done, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new DemoSeedStepResult(step, false, "skipped: cancelled");

        foreach (var need in Needs(step))
        {
            var needed = done.Single(r => r.Step.Key == need);
            if (!needed.Added)
                return new DemoSeedStepResult(step, false, "skipped: needs " + NeedName(needed.Step));
        }

        try
        {
            if (BeforeStepAsync is { } hook) await hook(step, cancellationToken).ConfigureAwait(false);

            var body = BodyOf(step.Key);
            await using var db = _dbFactory.Create();
            await body(run, db, cancellationToken).ConfigureAwait(false);
            return new DemoSeedStepResult(step, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DemoSeedStepResult(step, false, "skipped: cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Demo examples on ledger {LedgerId}: step {Step} ({Key}) failed.",
                run.LedgerId, step.Number, step.Key);
            return new DemoSeedStepResult(step, false, "failed: " + ex.Message);
        }
    }

    /// <summary>Step 11: find every row the manifest names. Every later step needs it.</summary>
    private static async Task ResolveAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken) =>
        run.Ids = await DemoSeedResolver.ResolveAsync(db, run.LedgerId, run.Context.Manifest, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Step 20: one manual snapshot of the Demo, owned by its owner, taken after every
    /// other example so it holds them all. It is captured through the service role, as the
    /// snapshot endpoint captures once the caller's access is established, so it holds
    /// every member's rows. A Demo already at the snapshot limit is a failure: a manual
    /// snapshot never evicts another.
    /// </summary>
    private static async Task SnapshotAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var snapshots = new LedgerSnapshotsRepository(
            db, run.Loggers.CreateLogger<LedgerSnapshotsRepository>(), run.Clock);
        var result = await snapshots.CreateAsync(
                run.LedgerId, kind: "manual", createdByUserId: run.Context.OwnerUserId,
                description: DemoSeedContent.SnapshotDescription, cancellationToken)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case LedgerSnapshotsRepository.CreateOutcome.Created:
                return;
            case LedgerSnapshotsRepository.CreateOutcome.AtCap:
                throw new DemoSeedStepException(
                    "the Demo already has as many snapshots as a ledger keeps, and a manual one never replaces another");
            default:
                throw new DemoSeedStepException("the snapshot was not taken (" + result.Outcome + ")");
        }
    }

    /// <summary>
    /// Step 21: every derived projection of the seeded Demo must agree with what it is
    /// derived from, or the step fails and names those that do not. It only checks: a
    /// repair here would hide the defect that made a projection disagree.
    /// </summary>
    private static async Task CheckAsync(DemoSeedRun run, AppDbContext db, CancellationToken cancellationToken)
    {
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db, run.Clock), new HoldingsRecomputeService(db), run.Clock);
        var report = await consistency.CheckAsync(run.LedgerId, run.AsOf, cancellationToken).ConfigureAwait(false);
        if (report.Healthy) return;

        var unhealthy = report.Projections.Where(p => !p.Healthy).Select(p => p.Projection).ToList();
        throw new DemoSeedStepException("the consistency check found projections out of step: "
            + string.Join(", ", unhealthy));
    }

    /// <summary>The ledger's marker, claimed for this run, when it is the ledger's only
    /// marker and no run has claimed it; otherwise null, logged. The claim is one guarded
    /// write, so of two runs only one gets the marker, and it stamps when the examples
    /// started.</summary>
    private async Task<DemoMarkerRow?> ClaimAsync(Guid ledgerId)
    {
        try
        {
            await using var db = _dbFactory.Create();
            var operations = new LedgerOperationsRepository(db);
            var markers = await operations.DemoMarkersOfAsync(ledgerId, CancellationToken.None).ConfigureAwait(false);
            var claimedDetails = Details([]);
            if (markers is [var only]
                && await operations.ClaimDemoMarkerAsync(ledgerId, only.Id, claimedDetails, Now(), CancellationToken.None)
                    .ConfigureAwait(false))
                return only with { DetailsJson = claimedDetails };

            _logger.LogError(
                "Demo examples refused on ledger {LedgerId}: it has {Count} Demo marker(s) ({Statuses}), "
                + "and they run only on the one marker its import opened, still running and not yet claimed.",
                ledgerId, markers.Count, string.Join(", ", markers.Select(m => m.Status)));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo examples on ledger {LedgerId}: its marker could not be read.", ledgerId);
            return null;
        }
    }

    private async Task RecordProgressAsync(DemoMarkerRow marker, IReadOnlyList<DemoSeedStepResult> results)
    {
        try
        {
            await using var db = _dbFactory.Create();
            var recorded = await new LedgerOperationsRepository(db)
                .RecordDemoMarkerProgressAsync(marker.LedgerId, marker.Id, Details(results), CancellationToken.None)
                .ConfigureAwait(false);
            if (!recorded)
                _logger.LogWarning(
                    "Demo examples on ledger {LedgerId}: marker {MarkerId} is no longer running; progress not recorded.",
                    marker.LedgerId, marker.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo examples on ledger {LedgerId}: progress could not be recorded.", marker.LedgerId);
        }
    }

    private async Task CloseAsync(DemoMarkerRow marker, IReadOnlyList<DemoSeedStepResult> results, string? errorMessage = null)
    {
        var status = StatusOf(results);
        var error = errorMessage ?? ErrorOf(results);
        try
        {
            await using var db = _dbFactory.Create();
            var closed = await new LedgerOperationsRepository(db).CloseDemoMarkerAsync(
                    marker.LedgerId, marker.Id, status, Details(results), error, Now(), CancellationToken.None)
                .ConfigureAwait(false);
            if (closed)
                _logger.LogInformation("Demo examples on ledger {LedgerId}: {Status}{Error}.",
                    marker.LedgerId, status, error is null ? string.Empty : " (" + error + ")");
            else
                _logger.LogWarning(
                    "Demo examples on ledger {LedgerId}: marker {MarkerId} was no longer running, so it was not closed as {Status}.",
                    marker.LedgerId, marker.Id, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo examples on ledger {LedgerId}: the marker could not be closed as {Status}.",
                marker.LedgerId, status);
        }
    }

    private void Report(Action<int> reportCompleted, int completed)
    {
        try
        {
            reportCompleted(completed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Demo examples: progress could not be reported.");
        }
    }

    /// <summary>The time the marker is stamped with: the one read of the clock here, and
    /// never a date a step computes.</summary>
    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static string Details(IEnumerable<DemoSeedStepResult> results) =>
        DemoMarker.Details(results.Where(r => r.Added).Select(r => r.Step.Key).ToHashSet(StringComparer.Ordinal));

    private static string StatusOf(IReadOnlyList<DemoSeedStepResult> results)
    {
        if (results.All(r => r.Added)) return DemoMarker.Completed;
        return results.Any(r => r.Step.Key == DemoSeedSteps.Resolve && r.Added) ? DemoMarker.Partial : DemoMarker.Failed;
    }

    private static string? ErrorOf(IEnumerable<DemoSeedStepResult> results)
    {
        var missing = results.Where(r => !r.Added).Select(r => (r.Step.MissingName ?? r.Step.Label) + ": " + r.Reason).ToList();
        return missing.Count == 0 ? null : string.Join("; ", missing);
    }

    private static string NeedName(DemoSeedStep step) => step.MissingName ?? "the imported rows";
}

/// <summary>What one examples phase did. <see cref="Status"/> is the marker's closing
/// status, or null when the run was refused and the marker left as it was.</summary>
public sealed record DemoSeedOutcome(string? Status, IReadOnlyList<DemoSeedStepResult> Steps)
{
    /// <summary>A run refused before it started: nothing was written.</summary>
    public static DemoSeedOutcome Refused { get; } = new(null, []);
}

/// <summary>One step's result: added, or not and why.</summary>
public sealed record DemoSeedStepResult(DemoSeedStep Step, bool Added, string? Reason);
