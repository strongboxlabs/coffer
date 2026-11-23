using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Import;
using Coffer.Api.Ingest.Csv;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// Adding a Demo through the hub's endpoint and reading what its job and its examples
/// phase left, for the tests of the examples phase. Every Demo here is the three-month
/// span for <see cref="Today"/>, so one takes seconds.
/// </summary>
internal static class DemoSeedingHarness
{
    /// <summary>The date every Demo here is added for: the generator's test date.</summary>
    public const string Today = "2026-11-18";

    /// <summary>A factory whose Demo is the three-month span for the date it is given, with
    /// the import optionally replaced, and optionally without the startup sweep. Its
    /// examples phase runs on <see cref="DemoSeederClock"/>, ahead of the real time.</summary>
    public static ApiFactory Factory(TestDatabase db, IMoneydanceImportService? import = null, bool withoutSweep = false) =>
        new ApiFactory(db).WithServices(s =>
        {
            DemoSeederClock.UseFor(s);
            s.RemoveAll<Func<DateOnly, DemoSpec>>();
            s.AddSingleton<Func<DateOnly, DemoSpec>>(asOf => new DemoSpec(DemoSpec.DefaultSeed, asOf, DemoSpan.LastMonths(3)));
            if (import is not null)
            {
                s.RemoveAll<IMoneydanceImportService>();
                s.AddSingleton(import);
            }
            if (withoutSweep)
            {
                foreach (var sweep in s.Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType == typeof(DemoMarkerSweep)).ToList())
                    s.Remove(sweep);
            }
        });

    /// <summary>Add a Demo and wait for its job to end; the job and its ledger.</summary>
    public static async Task<(ImportJob Job, Guid LedgerId)> AddDemoAsync(ApiFactory factory, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var (jobId, ledgerId) = await StartAsync(client);
        var finished = await WaitForEndAsync(registry, jobId);
        Assert.Equal(ImportJobState.Succeeded, finished.State);
        return (finished, ledgerId);
    }

    /// <summary>Add a Demo; its job id, and the ledger id once the job publishes it (or
    /// <see cref="Guid.Empty"/> when <paramref name="expectLedger"/> is false).</summary>
    public static async Task<(Guid JobId, Guid LedgerId)> StartAsync(HttpClient client, bool expectLedger = true)
    {
        var jobId = await PostAsync(client);
        return (jobId, expectLedger ? await LedgerIdAsync(client) : Guid.Empty);
    }

    /// <summary>Add a Demo; its job id.</summary>
    public static async Task<Guid> PostAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var started = await client.PostAsync($"/api/ledgers/demo?today={Today}", content: null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        return Guid.Parse(body.RootElement.GetProperty("jobId").GetString()!);
    }

    /// <summary>The caller's Demo job's ledger, once its import has committed, for at most 60 s.</summary>
    public static async Task<Guid> LedgerIdAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var job = JsonDocument.Parse(await client.GetStringAsync("/api/ledgers/demo/job"));
            if (job.RootElement.GetProperty("ledgerId").GetString() is { } ledgerId) return Guid.Parse(ledgerId);
            Assert.Equal("running", job.RootElement.GetProperty("state").GetString());
            await Task.Delay(100);
        }
        Assert.Fail("The Demo's import did not commit within 60 s.");
        return default;
    }

    /// <summary>The job once it leaves running, read from the registry, for at most 60 s.</summary>
    public static async Task<ImportJob> WaitForEndAsync(ImportJobRegistry registry, Guid jobId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var job = registry.Snapshot(jobId) ?? throw new InvalidOperationException("the job is gone");
            if (job.State != ImportJobState.Running) return job;
            await Task.Delay(100);
        }
        Assert.Fail("The Demo job did not finish within 60 s.");
        return null!;
    }

    /// <summary>The Demo marker of <paramref name="ledgerId"/>.</summary>
    public static async Task<LedgerOperationRow> MarkerAsync(TestDatabase db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var context = db.NewDbContext();
        return await context.LedgerOperations.AsNoTracking()
            .SingleAsync(o => o.LedgerId == ledgerId && o.ProviderKey == LedgerOperationsRepository.DemoMarkerProviderKey);
    }

    /// <summary>The ledger list's <c>demoExamplesMissing</c> for <paramref name="ledgerId"/>.</summary>
    public static async Task<IReadOnlyList<string>?> MissingAsync(HttpClient client, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/ledgers"));
        var row = Assert.Single(list.RootElement.EnumerateArray(),
            r => r.GetProperty("id").GetString() == ledgerId.ToString());
        var missing = row.GetProperty("demoExamplesMissing");
        return missing.ValueKind == JsonValueKind.Null
            ? null
            : missing.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>
    /// What the register step leaves: each rename beside the bank's originals, each re-sent
    /// copy hidden beside the visible row it repeats, and exactly the manifest's rows of
    /// checking's last closed statement reconciling, every other leg of it still uncleared
    /// and no checking leg outside it reconciling. The expected originals are read here from
    /// the generator's own pattern, not from the step.
    /// </summary>
    public static async Task AssertRegisterAsync(TestDatabase db, Guid ledgerId, DemoSeedIds ids, DemoManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(manifest);
        await using var context = db.NewDbContext();
        var checking = ids.Accounts[DemoAccountRoles.JointChecking];

        // (a) Each candidate carries its new payee, with the bank's own text and the row's
        // date and amount kept beside it as the originals, as the register's first edit of an
        // imported row keeps them.
        Assert.Equal(3, manifest.EditCandidates.Count);
        foreach (var candidate in manifest.EditCandidates)
        {
            var (bankText, _, _, cents) = Assert.Single(
                RegisterExamplesPattern.EditCandidates, c => c.NewPayee == candidate.NewPayee);
            var headerId = ids.Transactions[candidate.TxnId];
            var header = await context.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId);
            Assert.Equal(candidate.NewPayee, header.Payee);
            Assert.False(header.IsHidden);
            var original = await context.TxnHeaderOriginals.AsNoTracking().SingleAsync(o => o.HeaderId == headerId);
            Assert.Equal(bankText, original.Payee);
            Assert.Equal(header.PostedAt, original.PostedAt);
            Assert.Equal(-cents / 100m, original.Amount);
        }

        // (b) Each copy the bank re-sent is hidden, not deleted: the row, its Moneydance id and
        // its legs are all still there, out of review. The copy it repeats stays visible.
        Assert.Equal(2, manifest.DuplicateTxnIds.Count);
        foreach (var mdId in manifest.DuplicateTxnIds)
        {
            var copy = await context.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == ids.Transactions[mdId]);
            Assert.Equal(ledgerId, copy.LedgerId);
            Assert.Equal(mdId, copy.ExternalId);
            Assert.True(copy.IsHidden);
            Assert.False(copy.NeedsReview);
            var amount = await context.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == copy.Id && l.AccountId == checking).Select(l => l.Amount).SingleAsync();
            var twins = await (
                    from header in context.TxnHeaders.AsNoTracking()
                    where header.LedgerId == ledgerId && header.Id != copy.Id && header.ExternalId != null
                        && header.PostedAt.Date == copy.PostedAt.Date && header.Payee == copy.Payee
                    join leg in context.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                    where leg.AccountId == checking && leg.Amount == amount
                    select header)
                .ToListAsync();
            var twin = Assert.Single(twins);
            Assert.False(twin.IsHidden);
        }

        // (c) Inside checking's last closed statement, exactly the manifest's rows are
        // reconciling on checking; every other imported row of it, the hidden copies
        // included, is still uncleared there; and no checking leg outside it is reconciling.
        var statement = manifest.CheckingReconcile!;
        var legs = await (
                from header in context.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId && header.ExternalId != null
                join leg in context.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == checking
                join recon in context.TxnLegRecon.AsNoTracking() on leg.Id equals recon.LegId into recons
                from recon in recons.DefaultIfEmpty()
                select new { MdId = header.ExternalId!, header.PostedAt, Status = recon == null ? "uncleared" : recon.Status })
            .ToListAsync();
        var inStatement = legs
            .Where(l => DateOnly.FromDateTime(l.PostedAt) >= statement.PeriodStart
                && DateOnly.FromDateTime(l.PostedAt) <= statement.PeriodEnd)
            .ToList();
        // A row with several postings has a leg on checking for each: every one is marked.
        var marked = inStatement.Where(l => statement.ReconcilingTxnIds.Contains(l.MdId)).ToList();
        Assert.Equal(
            statement.ReconcilingTxnIds.Order(StringComparer.Ordinal),
            marked.Select(l => l.MdId).Distinct().Order(StringComparer.Ordinal));
        Assert.All(marked, l => Assert.True(l.Status == DemoRegisterSteps.Reconciling, $"{l.MdId} is {l.Status}"));
        // The rest: the other half, and the two copies the half was counted without.
        var rest = inStatement.Where(l => !statement.ReconcilingTxnIds.Contains(l.MdId)).ToList();
        Assert.True(
            rest.Select(l => l.MdId).Distinct().Count() >= statement.ReconcilingTxnIds.Count,
            $"only {rest.Count} legs of the statement are left");
        Assert.All(rest, l => Assert.True(l.Status == "uncleared", $"{l.MdId} is {l.Status}"));
        Assert.All(manifest.DuplicateTxnIds, d => Assert.Contains(rest, l => l.MdId == d));
        Assert.Equal(marked.Count, legs.Count(l => l.Status == DemoRegisterSteps.Reconciling));
    }

    /// <summary>The tables a snapshot captures that the steps before it write: each must
    /// hold rows in the Demo's snapshot.</summary>
    private static readonly string[] ExampleParts =
    [
        "account_folders", "account_folder_members", "security_components", "budget_targets",
        "txn_header_originals", "txn_leg_recon", "recurring_occurrence_exceptions", "saved_reports", "report_pins",
    ];

    /// <summary>
    /// What the snapshot step leaves: one manual snapshot, the owner's, listed by the
    /// snapshots panel as restorable, holding every row the Demo holds now in each table it
    /// captures (nothing the examples write comes after it), the examples' own rows among
    /// them, and the investment settings as the steps set them.
    /// </summary>
    /// <returns>The snapshot's id.</returns>
    public static async Task<Guid> AssertSnapshotAsync(
        TestDatabase db, HttpClient client, Guid ledgerId, Guid ownerId, DemoSeedIds ids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        // (a) As the snapshots panel lists it.
        var listed = await client.GetFromJsonAsync<List<SnapshotSummaryDto>>($"/api/ledgers/{ledgerId}/snapshots");
        var snapshot = Assert.Single(listed!);
        Assert.Equal("manual", snapshot.Kind);
        Assert.Equal(ownerId, snapshot.CreatedByUserId);
        Assert.Equal(DemoSeedContent.SnapshotDescription, snapshot.Description);
        Assert.True(snapshot.Restorable);
        Assert.True(snapshot.ContentSizeStored > 0);

        // (b) Every captured table holds, in the snapshot, every row the Demo holds of it now.
        // The capture is the whole of each table for the ledger, so a table that has grown
        // since is one an example wrote after the snapshot.
        await using var conn = db.OpenServiceConnection();
        var parts = (await conn.QueryAsync<string>(
                "SELECT DISTINCT part_name FROM ledger_snapshot_parts WHERE snapshot_id = @S ORDER BY 1",
                new { S = snapshot.Id }))
            .ToList();
        Assert.Superset(ExampleParts.ToHashSet(StringComparer.Ordinal), parts.ToHashSet(StringComparer.Ordinal));
        foreach (var part in parts)
        {
            var captured = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COALESCE(SUM(jsonb_array_length(content)), 0)
                  FROM ledger_snapshot_parts WHERE snapshot_id = @S AND part_name = @P
                """,
                new { S = snapshot.Id, P = part });
            var live = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{part}\" WHERE ledger_id = @L", new { L = ledgerId });
            Assert.True(captured == live, $"{part}: the snapshot holds {captured} rows, the Demo {live}");
            if (ExampleParts.Contains(part)) Assert.True(captured > 0, part + " is empty in the snapshot");
        }

        // (c) The investment settings as the investments step set them: the trade-commission
        // flag on the brokerage alone, and the target-date fund's look-through.
        Assert.Equal(
            [ids.Accounts[DemoAccountRoles.Brokerage]],
            await conn.QueryAsync<Guid>(
                """
                SELECT (e->>'id')::uuid
                  FROM ledger_snapshot_parts p, jsonb_array_elements(p.content) e
                 WHERE p.snapshot_id = @S AND p.part_name = 'accounts' AND (e->>'is_trade_commission')::boolean
                """,
                new { S = snapshot.Id }));
        var lookThrough = await conn.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM((e->>'weight')::numeric), 0)
              FROM ledger_snapshot_parts p, jsonb_array_elements(p.content) e
             WHERE p.snapshot_id = @S AND p.part_name = 'security_components' AND (e->>'security_id')::uuid = @Sec
            """,
            new { S = snapshot.Id, Sec = ids.Securities[DemoSecurities.TargetDate] });
        Assert.Equal(1m, lookThrough);

        // (d) The Demo's only snapshot.
        await using var context = db.NewDbContext();
        var row = await context.LedgerSnapshots.AsNoTracking().SingleAsync(s => s.LedgerId == ledgerId);
        Assert.Equal(snapshot.Id, row.Id);
        Assert.True(row.ContentSizeUncompressed > 0);
        return snapshot.Id;
    }

    /// <summary>
    /// What the card statement step leaves, read back through the app's own readers where
    /// it has one: one saved layout; one file import under its own Activity entry, every
    /// statement line one row of it on the card; the lines the owner had typed in merged
    /// into those typed rows, which survive settled on the statement's day; and every other
    /// line in the card's review queue. The expected lines are read here from the CSV text.
    /// </summary>
    /// <returns>The import's Activity entry.</returns>
    public static async Task<LedgerOperationRow> AssertCardStatementAsync(
        TestDatabase db, HttpClient client, Guid ledgerId, Guid ownerId, DemoSeedIds ids, DemoManifest manifest, string csv)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(csv);
        var statement = manifest.CardStatement!;
        Assert.Equal(2, statement.ExportedRows.Count);
        var card = ids.Accounts[DemoAccountRoles.RewardsCard];

        // The statement's lines: an ISO date first and the amount last, after its header.
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => (
                Date: DateOnly.ParseExact(l[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount: decimal.Parse(l[(l.LastIndexOf(',') + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.True(lines.Count > statement.ExportedRows.Count, $"the statement has {lines.Count} lines");

        // (a) One layout, as the layouts' endpoint lists it.
        var layouts = await client.GetFromJsonAsync<List<CsvMappingDto>>($"/api/ledgers/{ledgerId}/csv-mappings");
        var layout = Assert.Single(layouts!);
        Assert.Equal(DemoSeedContent.CardStatementLayoutName, layout.Name);
        Assert.Equal(DemoSeedContent.CardStatementLayoutYaml, layout.DefinitionYaml);
        Assert.Equal(CsvMappingValidator.CurrentVersion, layout.SchemaVersion);

        // (b) One file import of the owner's, with its own entry, every line one row on the
        // card, imported as a file import is. The entry is started by hand, not by an upload,
        // since nothing was uploaded, as the Demo's Moneydance import's is.
        await using var context = db.NewDbContext();
        var import = await context.LedgerOperations.AsNoTracking()
            .SingleAsync(o => o.LedgerId == ledgerId && o.ProviderKey == CsvGenericFileProvider.Key);
        Assert.Equal("ingest", import.Family);
        Assert.Equal("completed", import.Status);
        Assert.Equal("manual", import.TriggeredVia);
        Assert.Equal(ownerId, import.TriggeredByUserId);
        Assert.Null(import.FeedConnectionId);
        Assert.Null(import.ErrorMessage);
        var rows = await (
                from header in context.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId && header.LedgerOperationId == import.Id
                join leg in context.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == card
                select new { Header = header, leg.Amount })
            .ToListAsync();
        Assert.Equal(lines.Count, rows.Select(r => r.Header.Id).Distinct().Count());
        Assert.Equal(
            lines.Order(),
            rows.Select(r => (Date: DateOnly.FromDateTime(r.Header.PostedAt), r.Amount)).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal("file_import", r.Header.Origin);
            Assert.Equal(CsvGenericFileProvider.Key, r.Header.ProviderKey);
            Assert.False(r.Header.IsHidden);
        });

        // (c) Each line typed in before the statement: its copy is folded into the typed
        // row, out of review; the typed row survives, settled, visible, on that day.
        var losers = new HashSet<Guid>();
        foreach (var typed in statement.ExportedRows)
        {
            var copy = Assert.Single(rows, r => DateOnly.FromDateTime(r.Header.PostedAt) == typed.Date
                && r.Amount == typed.Amount / 100m).Header;
            var winnerId = ids.Transactions[typed.TxnId];
            Assert.Equal(winnerId, copy.IsMergedInto);
            Assert.False(copy.NeedsReview);
            losers.Add(copy.Id);

            var winner = await context.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winnerId);
            Assert.Equal(typed.TxnId, winner.ExternalId);
            Assert.True(winner.IsMergeWinner);
            Assert.Null(winner.IsMergedInto);
            Assert.False(winner.NeedsReview);
            Assert.False(winner.IsHidden);
            Assert.Equal(typed.Date, DateOnly.FromDateTime(winner.PostedAt));
        }
        Assert.Equal(statement.ExportedRows.Count, losers.Count);

        // Every other line waits in review, and the card's review count is those lines.
        var review = rows.Where(r => !losers.Contains(r.Header.Id)).ToList();
        Assert.Equal(lines.Count - statement.ExportedRows.Count, review.Count);
        Assert.All(review, r =>
        {
            Assert.True(r.Header.NeedsReview);
            Assert.Null(r.Header.IsMergedInto);
        });
        var accounts = await client.GetFromJsonAsync<List<AccountSummary>>($"/api/ledgers/{ledgerId}/accounts");
        var cardSummary = Assert.Single(accounts!, a => a.Id == card);
        Assert.Equal(review.Count, cardSummary.NeedsReviewCount);
        Assert.Equal(CsvGenericFileProvider.Key, cardSummary.ImportProviderKey);
        return import;
    }

    /// <summary>
    /// The Demo's Activity, newest first: the card statement's import, then the examples,
    /// which started before it and recorded it, then the Moneydance import they follow.
    /// Each is the owner's, started by hand rather than by an upload, and done.
    /// </summary>
    public static async Task AssertActivityAsync(HttpClient client, Guid ledgerId, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var list = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/ledger-operations"));
        var rows = list.RootElement.EnumerateArray().ToList();
        Assert.Equal(
            [CsvGenericFileProvider.Key, LedgerOperationsRepository.DemoMarkerProviderKey, "moneydance"],
            rows.Select(r => r.GetProperty("providerKey").GetString()!));
        Assert.Equal(["manual", "manual", "manual"], rows.Select(r => r.GetProperty("triggeredVia").GetString()!));
        Assert.All(rows, r =>
        {
            Assert.Equal("ingest", r.GetProperty("family").GetString());
            Assert.Equal(ownerId, r.GetProperty("triggeredByUserId").GetGuid());
            Assert.NotEqual(JsonValueKind.Null, r.GetProperty("completedAt").ValueKind);
        });
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        Assert.Equal("completed", rows[2].GetProperty("status").GetString());
    }

    /// <summary>A marker's details, by step key.</summary>
    public static IReadOnlyDictionary<string, int> Details(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var details = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateObject()) details[entry.Name] = entry.Value.GetInt32();
        return details;
    }
}
