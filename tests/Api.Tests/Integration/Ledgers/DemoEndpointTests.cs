using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Import;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Ledgers;

/// <summary>
/// The hub's Demo ledger: <c>POST /api/ledgers/demo</c> starts building a new Demo as a
/// background import, and <c>GET /api/ledgers/demo/job</c> finds that build again.
/// </summary>
/// <remarks>
/// <para>Each test builds the three-month span (<see cref="DemoTestExports.Tiny"/>'s span,
/// through the endpoint's spec seam) for the date it sends, so an import takes seconds
/// rather than the whole five years.</para>
/// <para>Where a test reads a running job, the import is held at its start
/// (<see cref="HeldImport"/>) so the state it reads cannot move on under it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DemoEndpointTests : DatabaseTest
{
    private const string Today = "2026-11-18";

    public DemoEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_Demo_starts_as_a_running_job_that_counts_every_step_and_imports_losslessly()
    {
        var held = new HeldImport();
        await using var factory = DemoFactory(held);
        using var client = factory.CreateClient();

        var started = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync()))
        {
            var job = body.RootElement;
            Assert.Equal("running", Text(job, "state"));
            Assert.Equal("Demo", Text(job, "ledgerName"));
            Assert.Equal("import", Text(job, "phase"));
            Assert.Equal(1, job.GetProperty("stepNumber").GetInt32());
            Assert.Equal(21, job.GetProperty("total").GetInt32());
            Assert.Equal("importing securities", Text(job, "stepLabel"));
            Assert.Equal(JsonValueKind.Null, job.GetProperty("ledgerId").ValueKind);
        }

        // Found again by a fresh request, as the hub finds it after navigating away.
        using (var again = factory.CreateClient())
        {
            var found = await again.GetAsync("/api/ledgers/demo/job");
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
            Assert.Equal("running", Text(body.RootElement, "state"));
            Assert.Equal("Demo", Text(body.RootElement, "ledgerName"));
        }

        held.Release();
        var finished = await PollToEndAsync(client);
        Assert.Equal("succeeded", Text(finished.RootElement, "state"));
        Assert.Equal("examples", Text(finished.RootElement, "phase"));
        Assert.Equal(21, finished.RootElement.GetProperty("stepNumber").GetInt32());
        Assert.Equal(21, finished.RootElement.GetProperty("completed").GetInt32());
        Assert.Equal(JsonValueKind.Null, finished.RootElement.GetProperty("stepLabel").ValueKind);
        Assert.Equal(JsonValueKind.Null, finished.RootElement.GetProperty("error").ValueKind);
        // Lossless as the job reports it: no failed check, no transaction left out.
        Assert.Empty(finished.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Empty(finished.RootElement.GetProperty("skippedTransactions").EnumerateArray());
        Assert.Equal(0, finished.RootElement.GetProperty("skippedTransactionsNotListed").GetInt32());
        var ledgerId = Guid.Parse(Text(finished.RootElement, "ledgerId")!);

        // Lossless as the import saw it: every check passed and no transaction step
        // skipped a row, counted or listed.
        var result = Assert.Single(held.Results);
        Assert.Equal(ledgerId, result.LedgerId);
        Assert.Empty(result.Validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}"));
        var transactionSteps = result.Steps
            .Where(s => s.StepName is "transactions" or "investment_transactions").ToList();
        Assert.Equal(2, transactionSteps.Count);
        Assert.All(transactionSteps, s =>
        {
            Assert.Equal(0, s.Skipped);
            Assert.Empty(s.Skips ?? []);
        });

        await using var db = Db.NewDbContext();
        var ledger = await db.Ledgers.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
        Assert.Equal("Demo", ledger.Name);
        Assert.True(await db.TxnHeaders.CountAsync(h => h.LedgerId == ledgerId) > 0, "the Demo imported no transaction");

        // Its import's Activity entry is started by hand rather than by an upload, beside
        // the marker its examples phase recorded itself on.
        var operations = await db.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledgerId).ToListAsync();
        var operation = Assert.Single(operations, o => o.ProviderKey == LedgerOperationsRepository.MoneydanceImportProviderKey);
        Assert.Equal("manual", operation.TriggeredVia);
        Assert.Equal("completed", operation.Status);
        Assert.Single(operations, o => o.ProviderKey == LedgerOperationsRepository.DemoMarkerProviderKey);

        // The list shows it as an ordinary ledger, with no example missing.
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/ledgers"));
        var row = Assert.Single(list.RootElement.EnumerateArray(), r => Text(r, "id") == ledgerId.ToString());
        Assert.True(row.TryGetProperty("demoExamplesMissing", out var missing));
        Assert.Equal(JsonValueKind.Null, missing.ValueKind);

        // The slot is free once the job reports its end.
        Assert.False(factory.Services.GetRequiredService<ImportMemoryGate>().IsHeld);
    }

    [Fact]
    public async Task With_no_Demo_job_the_lookup_is_empty()
    {
        await using var factory = DemoFactory(held: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ledgers/demo/job");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_uploaded_import_is_not_a_Demo_job()
    {
        await using var factory = DemoFactory(held: null);
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(DemoTestExports.TinyBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "export.json");
        content.Add(new StringContent("Uploaded"), "ledgerName");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = Text(startedBody.RootElement, "jobId");

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ledgers/demo/job")).StatusCode);

        // Let the upload finish before the host goes away, and check it is still recorded
        // as an upload.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? state = "running";
        while (state == "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var polled = JsonDocument.Parse(await client.GetStringAsync($"/api/imports/moneydance/{jobId}"));
            state = Text(polled.RootElement, "state");
        }
        Assert.Equal("succeeded", state);
        await using var db = Db.NewDbContext();
        var operation = await db.LedgerOperations.AsNoTracking().SingleAsync();
        Assert.Equal("file-upload", operation.TriggeredVia);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ledgers/demo/job")).StatusCode);
    }

    [Fact]
    public async Task A_second_Demo_while_one_builds_is_refused_as_an_import_already_running()
    {
        var held = new HeldImport();
        await using var factory = DemoFactory(held);
        using var client = factory.CreateClient();

        var first = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        using (var problem = await ProblemOf(second))
        {
            Assert.Equal("import-already-running", Text(problem.RootElement, "code"));
            // A busy refusal chose no name, so it names no build.
            Assert.False(problem.RootElement.TryGetProperty("ledgerName", out _));
        }

        held.Release();
        Assert.Equal("succeeded", Text((await PollToEndAsync(client)).RootElement, "state"));
        Assert.Single(held.Results);
    }

    [Fact]
    public async Task A_Demo_while_another_export_is_held_is_refused_before_it_is_generated()
    {
        var specs = new List<DateOnly>();
        await using var factory = DemoFactory(held: null, specs);
        using var client = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<ImportMemoryGate>();

        using (var slot = gate.TryEnter())
        {
            Assert.NotNull(slot);
            using var problem = await ProblemOf(await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null));
            Assert.Equal("import-already-running", Text(problem.RootElement, "code"));
            Assert.False(problem.RootElement.TryGetProperty("ledgerName", out _));
        }

        Assert.Empty(specs);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ledgers/demo/job")).StatusCode);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Both_routes_need_a_signed_in_caller()
    {
        await using var factory = DemoFactory(held: null).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ledgers/demo/job")).StatusCode);
    }

    [Fact]
    public async Task Each_Demo_takes_the_lowest_free_number_and_a_deleted_one_frees_its_number()
    {
        await using var factory = DemoFactory(held: null);
        using var client = factory.CreateClient();

        var first = await BuildDemoAsync(client);
        Assert.Equal("Demo", first.Name);
        var second = await BuildDemoAsync(client);
        Assert.Equal("Demo 2", second.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/ledgers/{second.LedgerId}")).StatusCode);

        var third = await BuildDemoAsync(client);
        Assert.Equal("Demo 2", third.Name);
        Assert.NotEqual(second.LedgerId, third.LedgerId);

        await using var db = Db.NewDbContext();
        Assert.Equal(
            ["Demo", "Demo 2"],
            await db.Ledgers.AsNoTracking().OrderBy(l => l.Name).Select(l => l.Name).ToListAsync());
    }

    [Fact]
    public async Task A_failed_build_reports_the_text_the_hub_shows_and_keeps_nothing()
    {
        var held = new HeldImport { Fail = true };
        await using var factory = DemoFactory(held);
        using var client = factory.CreateClient();

        var started = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        held.Release();

        using var finished = await PollToEndAsync(client);
        Assert.Equal("failed", Text(finished.RootElement, "state"));
        Assert.Equal(
            "Building Demo failed and nothing was kept. Try again, or check the server logs.",
            Text(finished.RootElement, "error"));
        Assert.Equal(21, finished.RootElement.GetProperty("stepNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, finished.RootElement.GetProperty("stepLabel").ValueKind);
        Assert.Equal(JsonValueKind.Null, finished.RootElement.GetProperty("ledgerId").ValueKind);

        // The import had written its ledger and every row before it failed, and the
        // rollback took them all.
        var created = Assert.Single(held.FailedLedgerIds);
        await using var db = Db.NewDbContext();
        Assert.False(await db.Ledgers.AnyAsync(l => l.Id == created));
        Assert.Equal(0, await db.Ledgers.CountAsync());
        Assert.Equal(0, await db.TxnHeaders.CountAsync());
        Assert.Equal(0, await db.LedgerOperations.CountAsync());
        Assert.False(factory.Services.GetRequiredService<ImportMemoryGate>().IsHeld);

        // Nothing was kept, so trying again takes the same name.
        held.Fail = false;
        var retried = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        using var retriedBody = JsonDocument.Parse(await retried.Content.ReadAsStringAsync());
        Assert.Equal("Demo", Text(retriedBody.RootElement, "ledgerName"));
        Assert.Equal("succeeded", Text((await PollToEndAsync(client)).RootElement, "state"));
    }

    [Fact]
    public async Task The_Demo_ends_on_the_date_sent_or_on_the_servers_date_without_one()
    {
        var specs = new List<DateOnly>();
        await using var factory = DemoFactory(held: null, specs);
        using var client = factory.CreateClient();
        var serverToday = DateOnly.FromDateTime(factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null)).StatusCode);
        Assert.Equal("succeeded", Text((await PollToEndAsync(client)).RootElement, "state"));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/ledgers/demo", content: null)).StatusCode);
        Assert.Equal("succeeded", Text((await PollToEndAsync(client)).RootElement, "state"));

        Assert.Equal([new DateOnly(2026, 11, 18), serverToday], specs);
    }

    [Fact]
    public async Task A_date_more_than_a_day_ahead_of_the_server_or_not_a_date_is_refused_before_generating()
    {
        var specs = new List<DateOnly>();
        await using var factory = DemoFactory(held: null, specs);
        using var client = factory.CreateClient();
        var serverToday = DateOnly.FromDateTime(factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var ahead = serverToday.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using (var problem = await ProblemOf(await client.PostAsync($"/api/ledgers/demo?today={ahead}", content: null)))
            Assert.Equal("demo-date-in-future", Text(problem.RootElement, "code"));

        // Not a date: refused by the query binding before the handler runs, as on every
        // route that takes today. (Production answers 400; the Development host these
        // tests run in throws the binding failure instead, so only the refusal is pinned.)
        Assert.False((await client.PostAsync("/api/ledgers/demo?today=not-a-date", content: null)).IsSuccessStatusCode);

        // Neither reached the generator, and neither holds the gate.
        Assert.Empty(specs);
        Assert.False(factory.Services.GetRequiredService<ImportMemoryGate>().IsHeld);

        // A day ahead is a local date some time zone already has: accepted.
        var tomorrow = serverToday.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/ledgers/demo?today={tomorrow}", content: null)).StatusCode);
        Assert.Equal("succeeded", Text((await PollToEndAsync(client)).RootElement, "state"));
        Assert.Equal([serverToday.AddDays(1)], specs);
    }

    [Fact]
    public async Task A_Demo_while_the_callers_own_import_runs_is_refused_before_it_takes_the_slot()
    {
        var specs = new List<DateOnly>();
        await using var factory = DemoFactory(held: null, specs);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var gate = factory.Services.GetRequiredService<ImportMemoryGate>();

        // An import of the caller's (dev auth signs requests in as the system user) that
        // holds no slot, as one does once its export is released.
        Assert.True(registry.TryStart(new ImportJob
        {
            Id = Guid.NewGuid(), UserId = UserRow.SystemUserId, LedgerName = "Uploaded", Total = 10,
        }));

        using var problem = await ProblemOf(await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null));
        Assert.Equal("import-already-running", Text(problem.RootElement, "code"));
        Assert.False(problem.RootElement.TryGetProperty("ledgerName", out _));

        // Refused before the slot was taken, so nothing was generated, and no one else's
        // upload was turned away in the meantime.
        Assert.Empty(specs);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task A_Demo_that_cannot_be_generated_answers_the_failed_build_text_with_its_name_and_holds_nothing()
    {
        var fail = true;
        await using var factory = DemoFactory(held: null, specThrows: () => Volatile.Read(ref fail));
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using (var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(ImportJobRunner.DemoFailedError("Demo"), Text(problem.RootElement, "detail"));
            // Named, so the hub can show which build failed.
            Assert.Equal("Demo", Text(problem.RootElement, "ledgerName"));
        }

        Assert.False(factory.Services.GetRequiredService<ImportMemoryGate>().IsHeld);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ledgers/demo/job")).StatusCode);

        // Nothing was kept, so trying again takes the same name.
        Volatile.Write(ref fail, false);
        var retried = await BuildDemoAsync(client);
        Assert.Equal("Demo", retried.Name);
    }

    [Fact]
    public async Task A_ledger_shared_with_the_caller_counts_for_the_Demos_number()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        await using (var db = Db.NewDbContext())
        {
            db.UserLedgerGrants.Add(new UserLedgerGrantRow { UserId = bob.UserId, LedgerId = alice.LedgerId, Role = "viewer" });
            await db.SaveChangesAsync();
        }

        await using var factory = DemoFactory(held: null).WithoutDevAuth();
        using var aliceClient = await SignedInAsync(factory, alice);
        using var bobClient = await SignedInAsync(factory, bob);

        // Alice names her ledger "Demo"; Bob sees it only as a viewer.
        Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{alice.LedgerId}")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { name = "Demo" }),
            })).StatusCode);

        // Bob owns no Demo, but sees Alice's, so his is the second.
        var built = await BuildDemoAsync(bobClient);
        Assert.Equal("Demo 2", built.Name);
    }

    // ---- helpers ----

    /// <summary>A factory whose Demo is the short span for the date it is given, with
    /// the import optionally <paramref name="held"/>, and each date the generator was
    /// asked for recorded in <paramref name="specs"/>. While <paramref name="specThrows"/>
    /// answers true the generator's spec throws, as a Demo that cannot be generated.</summary>
    private ApiFactory DemoFactory(HeldImport? held, List<DateOnly>? specs = null, Func<bool>? specThrows = null) =>
        new ApiFactory(Db).WithServices(s =>
        {
            s.RemoveAll<Func<DateOnly, DemoSpec>>();
            s.AddSingleton<Func<DateOnly, DemoSpec>>(asOf =>
            {
                if (specs is not null) lock (specs) specs.Add(asOf);
                if (specThrows?.Invoke() == true)
                    throw new InvalidOperationException("the Demo spec was told to fail");
                return new DemoSpec(DemoSpec.DefaultSeed, asOf, DemoSpan.LastMonths(3));
            });
            if (held is not null)
            {
                s.RemoveAll<IMoneydanceImportService>();
                s.AddSingleton<IMoneydanceImportService>(held);
            }
        });

    /// <summary>A client signed in as <paramref name="ledger"/>'s user, by a real session cookie.</summary>
    private static async Task<HttpClient> SignedInAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<(string Name, Guid LedgerId)> BuildDemoAsync(HttpClient client)
    {
        var started = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var finished = await PollToEndAsync(client);
        Assert.Equal("succeeded", Text(finished.RootElement, "state"));
        return (Text(finished.RootElement, "ledgerName")!, Guid.Parse(Text(finished.RootElement, "ledgerId")!));
    }

    /// <summary>Poll the Demo job until it leaves <c>running</c>, for at most 30 s.</summary>
    private static async Task<JsonDocument> PollToEndAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/api/ledgers/demo/job");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (Text(body.RootElement, "state") != "running") return body;
            body.Dispose();
            await Task.Delay(200);
        }
        Assert.Fail("The Demo job did not finish within 30 s.");
        return null!;
    }

    private static async Task<JsonDocument> ProblemOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The real import, held at its start until <see cref="Release"/>, recording each
    /// result. With <see cref="Fail"/> set it runs the real import, the caller's
    /// <c>beforeCommit</c> included, and throws at the last moment before the commit, with
    /// every row written; <see cref="FailedLedgerIds"/> records the ledger it had created.
    /// </summary>
    private sealed class HeldImport : IMoneydanceImportService
    {
        private readonly MoneydanceImportService _inner = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<MoneydanceImportResult> _results = [];

        private readonly List<Guid> _failedLedgerIds = [];

        public bool Fail { get; set; }

        public Task Reached => _reached.Task;

        public IReadOnlyList<Guid> FailedLedgerIds
        {
            get { lock (_failedLedgerIds) return _failedLedgerIds.ToList(); }
        }

        public IReadOnlyList<MoneydanceImportResult> Results
        {
            get { lock (_results) return _results.ToList(); }
        }

        public void Release() => _release.TrySetResult();

        public MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null) =>
            _inner.Preview(export, reportingCurrency);

        public async Task<MoneydanceImportResult> ImportAsync(
            NpgsqlConnection connection, MdExport export, string ledgerName, Guid ownerUserId,
            IProgress<ImportProgressUpdate>? progress, CancellationToken cancellationToken,
            Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
            string currencyCode = Coffer.Importer.Moneydance.Db.LedgersRepository.DefaultCurrencyCode)
        {
            _reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);

            var last = beforeCommit;
            if (Fail)
                last = async (transaction, ledgerId, ct) =>
                {
                    if (beforeCommit is not null) await beforeCommit(transaction, ledgerId, ct).ConfigureAwait(false);
                    lock (_failedLedgerIds) _failedLedgerIds.Add(ledgerId);
                    throw new InvalidOperationException("the held import was told to fail before its commit");
                };

            var result = await _inner.ImportAsync(connection, export, ledgerName, ownerUserId, progress,
                cancellationToken, last, currencyCode).ConfigureAwait(false);
            lock (_results) _results.Add(result);
            return result;
        }

        public Task<ReconcileResult> ReconcileAsync(
            NpgsqlConnection connection, MdExport export, Guid ownerUserId, Guid? compareLedgerId,
            IReadOnlyList<string> diffTickers, CancellationToken cancellationToken) =>
            _inner.ReconcileAsync(connection, export, ownerUserId, compareLedgerId, diffTickers, cancellationToken);
    }
}
