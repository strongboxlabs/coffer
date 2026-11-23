using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Import;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// The Demo job's examples phase and its record: the marker opened inside the Demo's
/// import, the run that adds the examples once and closes the marker whatever happens,
/// the startup sweep that closes a marker a restart left running, and the readers of that
/// marker (the ledger list, the undo endpoint).
/// </summary>
/// <remarks>
/// <para>Each test builds the three-month span through the hub's endpoint, for the date it
/// sends, so a Demo takes seconds. Every state is reached through the real writers: the
/// import opens the marker, the seeder records and closes it, the sweep closes an orphan.
/// A test holds or fails a step through the seeder's own seam, which runs inside the step
/// as its body would.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DemoSeederTests : DatabaseTest
{
    public DemoSeederTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>Every step's name as the hub lists a missing one, in step order.</summary>
    private static IReadOnlyList<string> EveryNamedStep =>
        DemoSeedSteps.All.Where(s => s.MissingName is not null).Select(s => s.MissingName!).ToList();

    [Fact]
    public async Task A_Demo_records_each_step_on_its_marker_and_the_list_names_the_steps_not_added()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var startedJob = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<(int Number, int Completed)>();
        seeder.BeforeStepAsync = async (step, _) =>
        {
            var job = registry.Snapshot(await startedJob.Task.ConfigureAwait(false))!;
            lock (progress) progress.Add((step.Number, job.Completed));
        };

        var jobId = await PostAsync(client);
        startedJob.SetResult(jobId);
        var ledgerId = await LedgerIdAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);
        Assert.Equal(ImportJobState.Succeeded, finished.State);
        Assert.Equal(21, finished.Completed);

        // Each step that ran saw the job count every step before it as done.
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal(p.Number - 1, p.Completed));
        Assert.Equal(DemoSeedSteps.All[0].Number, progress[0].Number);

        await using var db = Db.NewDbContext();
        var operations = await db.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledgerId).OrderBy(o => o.ProviderKey).ToListAsync();
        // The Moneydance import, the marker, and the card statement's own file import.
        Assert.Equal(["csv-generic", "demo", "moneydance"], operations.Select(o => o.ProviderKey));
        var marker = Assert.Single(operations, o => o.ProviderKey == "demo");
        Assert.Equal("ingest", marker.Family);
        Assert.Equal("manual", marker.TriggeredVia);
        Assert.Equal(finished.UserId, marker.TriggeredByUserId);
        Assert.NotNull(marker.CompletedAt);

        // Every step is recorded as added, so the marker is completed with no error.
        var details = Details(marker.DetailsJson);
        Assert.Equal(DemoSeedSteps.All.Select(s => s.Key).Order(StringComparer.Ordinal), details.Keys.Order(StringComparer.Ordinal));
        Assert.All(DemoSeedSteps.All, s => Assert.True(details[s.Key] == 1, s.Key + ": " + marker.ErrorMessage));
        Assert.Equal(DemoMarker.Completed, marker.Status);
        Assert.Null(marker.ErrorMessage);

        // The ledger list reads the real marker: nothing is missing.
        Assert.Null(await MissingAsync(client, ledgerId));
    }

    [Fact]
    public async Task The_marker_is_written_inside_the_import_and_rolls_back_with_it()
    {
        var import = new RollingBackImport();
        await using var factory = SeederFactory(import: import);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();

        var (jobId, _) = await StartAsync(client, expectLedger: false);
        var finished = await WaitForEndAsync(registry, jobId);
        Assert.Equal(ImportJobState.Failed, finished.State);

        // Inside the transaction, at its last moment, the Demo's marker was there, running
        // with nothing recorded; after the rollback, it is gone with the ledger.
        Assert.Equal([(DemoMarker.Running, "{}")], import.MarkersSeen);
        await using var db = Db.NewDbContext();
        Assert.Equal(0, await db.Ledgers.CountAsync());
        Assert.Equal(0, await db.LedgerOperations.CountAsync());
    }

    [Fact]
    public async Task A_Demo_whose_import_reported_problems_gets_no_examples_and_says_why()
    {
        await using var factory = SeederFactory(import: new LossyImport());
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var ran = 0;
        seeder.BeforeStepAsync = (_, _) =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        };

        var (jobId, ledgerId) = await StartAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);

        // The ledger is kept, so the job succeeded; no step ran.
        Assert.Equal(ImportJobState.Succeeded, finished.State);
        Assert.NotEmpty(finished.Warnings);
        Assert.Equal(0, ran);

        var marker = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Failed, marker.Status);
        Assert.Equal(DemoSeeder.NotStartedLossyImport, marker.ErrorMessage);
        Assert.All(Details(marker.DetailsJson).Values, v => Assert.Equal(0, v));
        Assert.Equal(EveryNamedStep, await MissingAsync(client, ledgerId));
    }

    [Fact]
    public async Task The_examples_run_once_and_a_second_run_leaves_the_record_of_the_first()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();

        var (jobId, ledgerId) = await StartAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);
        var before = await MarkerAsync(ledgerId);
        Assert.NotEqual(DemoMarker.Running, before.Status);

        var ran = 0;
        seeder.BeforeStepAsync = (_, _) =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        };
        var context = new DemoSeedContext(
            ledgerId, finished.UserId, DemoTestExports.AsOf, DemoTestExports.Tiny.Value.Manifest,
            DemoTestExports.Tiny.Value.CardStatementCsv);
        var again = await seeder.SeedAsync(context, _ => { });
        var notStarted = await seeder.CloseNotStartedAsync(ledgerId, DemoSeeder.NotStartedLossyImport);

        Assert.Null(again.Status);
        Assert.Empty(again.Steps);
        Assert.Null(notStarted.Status);
        Assert.Equal(0, ran);
        var after = await MarkerAsync(ledgerId);
        Assert.Equal(
            (before.Id, before.Status, before.DetailsJson, before.ErrorMessage, before.CompletedAt),
            (after.Id, after.Status, after.DetailsJson, after.ErrorMessage, after.CompletedAt));
        await using var db = Db.NewDbContext();
        Assert.Equal(1, await db.LedgerOperations.CountAsync(o => o.LedgerId == ledgerId && o.ProviderKey == "demo"));
    }

    [Fact]
    public async Task A_second_run_while_the_first_is_mid_way_is_refused_and_the_import_s_memory_is_already_free()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var gate = factory.Services.GetRequiredService<ImportMemoryGate>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new List<string>();
        var held = 0;
        seeder.BeforeStepAsync = async (step, _) =>
        {
            lock (ran) ran.Add(step.Key);
            // Only the job's own run is held, once, inside its second step.
            if (step.Key != "folders" || Interlocked.Exchange(ref held, 1) == 1) return;
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var (jobId, ledgerId) = await StartAsync(client);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        DemoSeedOutcome again;
        DemoSeedOutcome notStarted;
        LedgerOperationRow before;
        try
        {
            // The import's document and its memory slot were released before the
            // examples began: the next import need not wait for them.
            Assert.False(gate.IsHeld, "the examples phase runs with the import's memory slot still held");
            using (var slot = gate.TryEnter()) Assert.NotNull(slot);

            before = await MarkerAsync(ledgerId);
            Assert.Equal(DemoMarker.Running, before.Status);
            Assert.Equal(1, Details(before.DetailsJson)[DemoSeedSteps.Resolve]);

            var context = new DemoSeedContext(
                ledgerId, registry.Snapshot(jobId)!.UserId, DemoTestExports.AsOf, DemoTestExports.Tiny.Value.Manifest,
                DemoTestExports.Tiny.Value.CardStatementCsv);
            again = await seeder.SeedAsync(context, _ => { });
            notStarted = await seeder.CloseNotStartedAsync(ledgerId, DemoSeeder.NotStartedLossyImport);

            // Neither touched the marker the first run holds.
            var during = await MarkerAsync(ledgerId);
            Assert.Equal(
                (before.Status, before.DetailsJson, before.ErrorMessage, before.CompletedAt, before.StartedAt),
                (during.Status, during.DetailsJson, during.ErrorMessage, during.CompletedAt, during.StartedAt));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Null(again.Status);
        Assert.Empty(again.Steps);
        Assert.Null(notStarted.Status);

        // The first run then finishes as it would have, and no step ran twice.
        Assert.Equal(ImportJobState.Succeeded, (await WaitForEndAsync(registry, jobId)).State);
        var after = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Completed, after.Status);
        Assert.Equal(1, Details(after.DetailsJson)["check"]);
        lock (ran)
            Assert.All(ran.GroupBy(k => k, StringComparer.Ordinal), g => Assert.True(g.Count() == 1, g.Key + " ran " + g.Count() + " times"));
    }

    [Fact]
    public async Task The_run_s_own_deadline_skips_what_is_left_and_still_closes_the_marker()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var clock = factory.Services.GetRequiredService<DemoSeederClock>();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        const string DueAt = "investments";
        seeder.BeforeStepAsync = async (step, ct) =>
        {
            if (step.Key != DueAt) return;
            // The run's deadline falls due inside this step, which then waits on the run's
            // token as a slow writer would; without the deadline it would wait it out and
            // be added.
            clock.FireTimers();
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        };

        var (jobId, ledgerId) = await StartAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);

        // The job succeeded, and the server was never stopping: the deadline ended the run.
        Assert.Equal(ImportJobState.Succeeded, finished.State);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        var marker = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Partial, marker.Status);
        Assert.NotNull(marker.CompletedAt);
        var details = Details(marker.DetailsJson);
        Assert.Equal(1, details[DemoSeedSteps.Resolve]);
        Assert.Equal(1, details["folders"]);
        var dueAt = DemoSeedSteps.All.Single(s => s.Key == DueAt).Number;
        foreach (var step in DemoSeedSteps.All.Where(s => s.Number >= dueAt))
        {
            Assert.Equal(0, details[step.Key]);
            Assert.Contains(step.MissingName + ": skipped: cancelled", marker.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_Demo_listed_between_its_import_s_commit_and_its_examples_names_nothing_missing()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var runner = factory.Services.GetRequiredService<ImportJobRunner>();
        var reached = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.AfterImportCommitAsync = async id =>
        {
            reached.TrySetResult(id);
            await release.Task.ConfigureAwait(false);
        };

        var jobId = await PostAsync(client);
        try
        {
            Assert.Equal(jobId, await reached.Task.WaitAsync(TimeSpan.FromSeconds(60)));

            // The ledger is committed, and its marker records nothing yet. The job already
            // holds the ledger, so the hub keeps the build on its own row, and the ledger
            // list names no example as missing.
            var job = registry.Snapshot(jobId)!;
            Assert.Equal(ImportJobState.Running, job.State);
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/ledgers"));
            var row = Assert.Single(list.RootElement.EnumerateArray(),
                r => r.GetProperty("name").GetString() == job.LedgerName);
            Assert.Equal(row.GetProperty("id").GetGuid(), job.LedgerId);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("demoExamplesMissing").ValueKind);
            Assert.Equal(DemoMarker.Running, (await MarkerAsync(job.LedgerId!.Value)).Status);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(ImportJobState.Succeeded, (await WaitForEndAsync(registry, jobId)).State);
    }

    [Fact]
    public async Task When_the_first_step_fails_every_later_step_is_skipped_and_named()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var ran = new List<string>();
        seeder.BeforeStepAsync = (step, _) =>
        {
            lock (ran) ran.Add(step.Key);
            return step.Key == DemoSeedSteps.Resolve
                ? throw new InvalidOperationException("the first step was told to fail")
                : Task.CompletedTask;
        };

        var (jobId, ledgerId) = await StartAsync(client);
        Assert.Equal(ImportJobState.Succeeded, (await WaitForEndAsync(registry, jobId)).State);

        Assert.Equal([DemoSeedSteps.Resolve], ran);
        var marker = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Failed, marker.Status);
        Assert.All(Details(marker.DetailsJson).Values, v => Assert.Equal(0, v));
        Assert.Contains("failed: the first step was told to fail", marker.ErrorMessage, StringComparison.Ordinal);
        foreach (var name in EveryNamedStep)
            Assert.Contains(name + ": skipped: needs the imported rows", marker.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(EveryNamedStep, await MissingAsync(client, ledgerId));
    }

    [Fact]
    public async Task A_server_stopping_mid_run_skips_what_is_left_and_still_closes_the_marker()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        const string StopAt = "investments";
        seeder.BeforeStepAsync = (step, ct) =>
        {
            if (step.Key == StopAt)
            {
                lifetime.StopApplication();
                ct.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        // Stopping the server ends the test host too, so the job is read from the
        // registry rather than over HTTP once it is started.
        var jobId = await PostAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);
        Assert.Equal(ImportJobState.Succeeded, finished.State);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        var ledgerId = finished.LedgerId!.Value;

        var marker = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Partial, marker.Status);
        var details = Details(marker.DetailsJson);
        Assert.Equal(1, details[DemoSeedSteps.Resolve]);
        var stopAt = DemoSeedSteps.All.Single(s => s.Key == StopAt).Number;
        foreach (var step in DemoSeedSteps.All.Where(s => s.Number >= stopAt))
        {
            Assert.Equal(0, details[step.Key]);
            Assert.Contains(step.MissingName + ": skipped: cancelled", marker.ErrorMessage, StringComparison.Ordinal);
        }
        Assert.NotNull(marker.CompletedAt);
    }

    [Fact]
    public async Task A_marker_a_restart_left_running_is_listed_as_missing_and_the_sweep_closes_it()
    {
        // The first server holds its run inside the second step, after the first is recorded.
        await using var first = SeederFactory();
        using var client = first.CreateClient();
        var registry = first.Services.GetRequiredService<ImportJobRegistry>();
        var seeder = first.Services.GetRequiredService<DemoSeeder>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seeder.BeforeStepAsync = async (step, _) =>
        {
            if (step.Key != "folders") return;
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var (jobId, ledgerId) = await StartAsync(client);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        try
        {
            // While its job is live, the first server lists the Demo as still building.
            Assert.Null(await MissingAsync(client, ledgerId));
            var running = await MarkerAsync(ledgerId);
            Assert.Equal(DemoMarker.Running, running.Status);
            Assert.Equal(1, Details(running.DetailsJson)[DemoSeedSteps.Resolve]);

            // A server with no such job, before any sweep, lists every step not recorded.
            await using (var unswept = SeederFactory(withoutSweep: true))
            {
                using var other = unswept.CreateClient();
                Assert.Equal(EveryNamedStep, await MissingAsync(other, ledgerId));
                Assert.Equal(DemoMarker.Running, (await MarkerAsync(ledgerId)).Status);
            }

            // A server that starts with the marker running closes it as interrupted.
            await using (var restarted = SeederFactory())
            {
                _ = restarted.Services;
                var swept = await MarkerAsync(ledgerId);
                Assert.Equal(DemoMarker.Partial, swept.Status);
                Assert.Equal(
                    "Interrupted: the server restarted before these examples were added: "
                    + string.Join(", ", EveryNamedStep) + ".",
                    swept.ErrorMessage);
                Assert.Equal(running.DetailsJson, swept.DetailsJson);
                Assert.NotNull(swept.CompletedAt);
                using var other = restarted.CreateClient();
                Assert.Equal(EveryNamedStep, await MissingAsync(other, ledgerId));
            }
        }
        finally
        {
            release.TrySetResult();
        }

        // The held run then finds its marker closed, and neither records on it nor closes it.
        Assert.Equal(ImportJobState.Succeeded, (await WaitForEndAsync(registry, jobId)).State);
        var after = await MarkerAsync(ledgerId);
        Assert.Equal(DemoMarker.Partial, after.Status);
        Assert.StartsWith("Interrupted: ", after.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, Details(after.DetailsJson)["check"]);
    }

    [Fact]
    public async Task The_imported_rows_resolve_one_each_and_a_name_with_no_row_fails_the_match()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var (jobId, ledgerId) = await StartAsync(client);
        await WaitForEndAsync(registry, jobId);
        var manifest = DemoTestExports.Tiny.Value.Manifest;

        await using var db = Db.NewDbContext();
        var ids = await DemoSeedResolver.ResolveAsync(db, ledgerId, manifest, CancellationToken.None);
        Assert.Equal(manifest.AccountIds.Count, ids.Accounts.Count);
        Assert.Equal(manifest.Categories.Count, ids.Categories.Count);
        Assert.All(manifest.BudgetTargets, t => Assert.NotEqual(Guid.Empty, ids.Category(t.Category)));
        Assert.All(manifest.DuplicateTxnIds.Concat(manifest.CheckingReconcile!.ReconcilingTxnIds),
            id => Assert.True(ids.Transactions.ContainsKey(id), id));
        Assert.Contains(DemoSecurities.StableValue, ids.Securities.Keys);
        Assert.Contains(DemoSecurities.TargetDate, ids.Securities.Keys);
        Assert.NotEqual(Guid.Empty, ids.InternetReminder);
        // Each id is the ledger's own row: the resolved accounts are this ledger's.
        var accountIds = ids.AccountsByMdId.Values.Concat(ids.CategoriesByPath.Values).ToList();
        Assert.Equal(accountIds.Count, await db.Accounts.CountAsync(a => a.LedgerId == ledgerId && accountIds.Contains(a.Id)));

        var unknownAccount = manifest with
        {
            AccountIds = new Dictionary<string, string>(manifest.AccountIds) { ["probe-role"] = "probe-account-id" },
            DuplicateTxnIds = [.. manifest.DuplicateTxnIds, "probe-txn-id"],
        };
        var failure = await Assert.ThrowsAsync<DemoSeedStepException>(() =>
            DemoSeedResolver.ResolveAsync(db, ledgerId, unknownAccount, CancellationToken.None));
        Assert.Contains("no account with the Moneydance id probe-account-id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no transaction with the Moneydance id probe-txn-id", failure.Message, StringComparison.Ordinal);

        // Another ledger's rows are not this one's: the same manifest finds nothing there.
        var other = await db.Ledgers.AsNoTracking().Where(l => l.Id != ledgerId).Select(l => (Guid?)l.Id).FirstOrDefaultAsync();
        var elsewhere = await Assert.ThrowsAsync<DemoSeedStepException>(() =>
            DemoSeedResolver.ResolveAsync(db, other ?? Guid.NewGuid(), manifest, CancellationToken.None));
        Assert.Contains("no account with the Moneydance id", elsewhere.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_undo_endpoint_refuses_a_Demo_marker()
    {
        await using var factory = SeederFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var (jobId, ledgerId) = await StartAsync(client);
        await WaitForEndAsync(registry, jobId);
        var marker = await MarkerAsync(ledgerId);
        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.CountAsync(h => h.LedgerId == ledgerId);

        foreach (var dryRun in new[] { "true", "false" })
        {
            var response = await client.PostAsync(
                $"/api/ledgers/{ledgerId}/ledger-operations/{marker.Id}/undo-import?dryRun={dryRun}", content: null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("operation-not-an-import", problem.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(headers, await db.TxnHeaders.CountAsync(h => h.LedgerId == ledgerId));
        Assert.Equal(marker.Status, (await MarkerAsync(ledgerId)).Status);
    }

    /// <summary>A factory whose Demo is the short span for the date it is given, with the
    /// import optionally replaced, and optionally without the startup sweep.</summary>
    private ApiFactory SeederFactory(IMoneydanceImportService? import = null, bool withoutSweep = false) =>
        DemoSeedingHarness.Factory(Db, import, withoutSweep);

    private static Task<(Guid JobId, Guid LedgerId)> StartAsync(HttpClient client, bool expectLedger = true) =>
        DemoSeedingHarness.StartAsync(client, expectLedger);

    private static Task<Guid> PostAsync(HttpClient client) => DemoSeedingHarness.PostAsync(client);

    private static Task<Guid> LedgerIdAsync(HttpClient client) => DemoSeedingHarness.LedgerIdAsync(client);

    private static Task<ImportJob> WaitForEndAsync(ImportJobRegistry registry, Guid jobId) =>
        DemoSeedingHarness.WaitForEndAsync(registry, jobId);

    private Task<LedgerOperationRow> MarkerAsync(Guid ledgerId) => DemoSeedingHarness.MarkerAsync(Db, ledgerId);

    private static Task<IReadOnlyList<string>?> MissingAsync(HttpClient client, Guid ledgerId) =>
        DemoSeedingHarness.MissingAsync(client, ledgerId);

    private static IReadOnlyDictionary<string, int> Details(string json) => DemoSeedingHarness.Details(json);

    /// <summary>The real import, which at its last moment before the commit reads the Demo
    /// markers inside its own transaction and then fails, so the transaction rolls back.</summary>
    private sealed class RollingBackImport : IMoneydanceImportService
    {
        private readonly MoneydanceImportService _inner = new();
        private readonly List<(string Status, string Details)> _seen = [];

        public IReadOnlyList<(string Status, string Details)> MarkersSeen
        {
            get { lock (_seen) return _seen.ToList(); }
        }

        public MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null) =>
            _inner.Preview(export, reportingCurrency);

        public Task<MoneydanceImportResult> ImportAsync(
            NpgsqlConnection connection, MdExport export, string ledgerName, Guid ownerUserId,
            IProgress<ImportProgressUpdate>? progress, CancellationToken cancellationToken,
            Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
            string currencyCode = Coffer.Importer.Moneydance.Db.LedgersRepository.DefaultCurrencyCode) =>
            _inner.ImportAsync(connection, export, ledgerName, ownerUserId, progress, cancellationToken,
                async (transaction, ledgerId, ct) =>
                {
                    if (beforeCommit is not null) await beforeCommit(transaction, ledgerId, ct).ConfigureAwait(false);
                    await using var db = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(transaction.Connection!).Options);
                    await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                    var markers = await db.LedgerOperations.AsNoTracking()
                        .Where(o => o.LedgerId == ledgerId && o.ProviderKey == LedgerOperationsRepository.DemoMarkerProviderKey)
                        .Select(o => new { o.Status, o.DetailsJson })
                        .ToListAsync(ct).ConfigureAwait(false);
                    lock (_seen) _seen.AddRange(markers.Select(m => (m.Status, m.DetailsJson)));
                    throw new InvalidOperationException("the import was told to fail before its commit");
                },
                currencyCode);

        public Task<ReconcileResult> ReconcileAsync(
            NpgsqlConnection connection, MdExport export, Guid ownerUserId, Guid? compareLedgerId,
            IReadOnlyList<string> diffTickers, CancellationToken cancellationToken) =>
            _inner.ReconcileAsync(connection, export, ownerUserId, compareLedgerId, diffTickers, cancellationToken);
    }

    /// <summary>The real import, whose result also reports one failed check: an import that
    /// committed with a problem, as the runner sees one.</summary>
    private sealed class LossyImport : IMoneydanceImportService
    {
        private readonly MoneydanceImportService _inner = new();

        public MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null) =>
            _inner.Preview(export, reportingCurrency);

        public async Task<MoneydanceImportResult> ImportAsync(
            NpgsqlConnection connection, MdExport export, string ledgerName, Guid ownerUserId,
            IProgress<ImportProgressUpdate>? progress, CancellationToken cancellationToken,
            Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
            string currencyCode = Coffer.Importer.Moneydance.Db.LedgersRepository.DefaultCurrencyCode)
        {
            var result = await _inner.ImportAsync(connection, export, ledgerName, ownerUserId, progress,
                cancellationToken, beforeCommit, currencyCode).ConfigureAwait(false);
            return result with
            {
                Validation = new ImportValidator.ValidationReport(
                [
                    .. result.Validation.Checks,
                    ImportValidator.CheckResult.Fail("probe-check", "a check the test failed", "A check failed."),
                ]),
            };
        }

        public Task<ReconcileResult> ReconcileAsync(
            NpgsqlConnection connection, MdExport export, Guid ownerUserId, Guid? compareLedgerId,
            IReadOnlyList<string> diffTickers, CancellationToken cancellationToken) =>
            _inner.ReconcileAsync(connection, export, ownerUserId, compareLedgerId, diffTickers, cancellationToken);
    }
}
