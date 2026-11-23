using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Db;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The stored memo-and-tags presentation at the stress lane's scale: what the upgrade's
/// backfill costs, that the stored rows are fresh (stored equals computed) and right
/// (every reader reads what the rule computes over the loaded rows), and what a save pays
/// for the recompute.
/// </summary>
/// <remarks>
/// <para>No read is timed here. The reads are measured by the gate
/// (<see cref="AnnotationApiTimingTests"/>), which runs on this branch unchanged: same
/// seed, its row and filter checks, A/A noise, on a quiet machine. The save figures are
/// indicative only.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesSpikeStressTests : DatabaseTest
{
    private const string Prefix = "notes-spike:";
    private const int Warmup = 2;
    private const int Rounds = 9;

    private readonly ITestOutputHelper _output;

    public StoredNotesSpikeStressTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private void Print(FormattableString line) => _output.WriteLine($"{Prefix} {line.ToString(CultureInfo.InvariantCulture)}");

    [Fact]
    public async Task Backfill_checks_and_save_print_their_costs_at_the_default_scale()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Default;
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        await PrintBackfillAsync("default", ledger.LedgerId, scale, seeded);

        var check = Stopwatch.StartNew();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.Database.SetCommandTimeout(600);
            var differences = await LegNotes.DifferencesAsync(db, ledger.LedgerId, CancellationToken.None);
            Assert.True(differences.Count == 0, string.Join("\n", differences.Take(20)));
        }
        Print($"check stored==computed over the ledger {check.Elapsed.TotalSeconds:F1}s");

        // Stored == computed only proves the rows are fresh. This compares what every
        // reader reads, through each perspective it picks in SQL, with the rule run over
        // the loaded rows and a perspective picked in C#, for every leg of the seed's own
        // shapes; and the facts the rule is computed from (the other leg, the posting
        // count) with the view's own.
        var equivalence = Stopwatch.StartNew();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.Database.SetCommandTimeout(3600);
            await StoredNotesEquivalence.AssertEquivalentAsync(db, ledger.LedgerId);
        }
        Print($"check stored==rule over every reader perspective {equivalence.Elapsed.TotalSeconds:F1}s");

        // And what the readers' filters select on it, through the repositories: every
        // tag in the reports and the ledger-wide listing, a sample in the scoped ones.
        var filters = Stopwatch.StartNew();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.Database.SetCommandTimeout(3600);
            await StoredNotesEquivalence.AssertFiltersEquivalentAsync(db, ledger.LedgerId, new(Scopes: 2, ScopedTags: 6));
        }
        Print($"check filters stored==rule {filters.Elapsed.TotalSeconds:F1}s");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var rest = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        rest.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        rest.Timeout = TimeSpan.FromMinutes(5);
        await PrintSaveCostAsync(ledger, rest);
    }

    /// <summary>
    /// The gate's read calls on the default seed, each with its plan and an indicative
    /// end-to-end median: what a reader of the stored columns pays, statement by
    /// statement. Not the gate (no before runs, no added round trip, nine rounds); the
    /// plans are the point. Run again after the table is compacted, to separate what the
    /// backfill's rewrite of every leg leaves behind from what the read costs.
    /// </summary>
    [Fact]
    public async Task Read_calls_print_their_plans_and_indicative_medians()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Default);

        var explain = new ExplainCapture { Selects = sql => sql.Contains("resolved_transactions", StringComparison.Ordinal) };
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithMcpEnabled().WithServices(explain.Register);
        using var rest = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        rest.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        rest.Timeout = TimeSpan.FromMinutes(5);
        using var mcp = await StressMcpClient.CreateAsync(Db, factory, ledger, "stress-spike-plans");
        var calls = await ReadCallsAsync(ledger.LedgerId, seeded, rest, mcp);

        await MeasureAsync("as-seeded", calls, explain);

        // The backfill updated every leg once, so the table holds a dead version of each
        // row until it is vacuumed, and its live rows are spread over twice the pages.
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.Database.SetCommandTimeout(600);
            await db.Database.ExecuteSqlRawAsync("VACUUM FULL txn_legs");
            await db.Database.ExecuteSqlRawAsync("ANALYZE txn_legs");
        }
        await MeasureAsync("compacted", calls, explain);
    }

    private sealed record ReadCall(string Name, HttpClient Client, Func<HttpRequestMessage> Request);

    private async Task MeasureAsync(string label, IReadOnlyList<ReadCall> calls, ExplainCapture explain)
    {
        var samples = calls.ToDictionary(c => c.Name, _ => new List<double>());
        for (var round = 0; round < Warmup + Rounds; round++)
        {
            foreach (var call in AnnotationApiTiming.RoundOrder(calls, round + 1))
            {
                using var request = call.Request();
                var watch = Stopwatch.StartNew();
                using var response = await call.Client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                watch.Stop();
                Assert.True(response.IsSuccessStatusCode, $"{call.Name}: {(int)response.StatusCode} {body[..Math.Min(400, body.Length)]}");
                Assert.True(body.Length > 2, $"{call.Name}: empty answer");
                if (call.Name.StartsWith("mcp.", StringComparison.Ordinal))
                    Assert.True(AnnotationApiTiming.McpLineCount(body) > 0, $"{call.Name}: no lines {body[..Math.Min(400, body.Length)]}");
                if (round >= Warmup) samples[call.Name].Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        foreach (var call in calls)
            Print($"read {label} {call.Name} median={AnnotationApiTiming.Median(samples[call.Name]):F1}ms (indicative, in-process, no added round trip)");

        explain.Enabled = true;
        foreach (var call in calls)
        {
            using (var request = call.Request())
            using (await call.Client.SendAsync(request)) { }
            foreach (var (sql, plan) in explain.Drain())
            {
                _output.WriteLine($"explain: {label}.{call.Name}");
                foreach (var line in sql.Split('\n').Concat(plan.Split('\n')))
                    _output.WriteLine($"explain:   {line.TrimEnd()}");
            }
        }
        explain.Enabled = false;
    }

    /// <summary>The gate's calls (<see cref="AnnotationApiTimingTests"/>), built the same way.</summary>
    private static async Task<List<ReadCall>> ReadCallsAsync(
        Guid ledgerId, StressLedger.Seeded seeded, HttpClient rest, HttpClient mcp)
    {
        const int pageSize = 100;
        var bank = StressLedger.BankId(ledgerId, 2);
        var category = StressLedger.CategoryId(ledgerId, 8);
        var tag = StressLedger.TagName(7);
        const string search = "note-17";
        var registers = new (string Name, Guid Account, Guid Counterparty)[]
        {
            ("bank", bank, category),
            ("category", category, bank),
            ("brokerage", seeded.BrokerageId, StressLedger.IncomeCategoryId(ledgerId)),
        };

        var calls = new List<ReadCall>();
        ReadCall Get(string name, string url) => new(name, rest, () => new HttpRequestMessage(HttpMethod.Get, url));
        foreach (var (name, account, counterparty) in registers)
        {
            var page = $"/api/ledgers/{ledgerId}/transactions?account_id={account}&limit={pageSize}";
            foreach (var (sortName, sort) in new[] { ("date", ""), ("amount", "&sort=amount&dir=desc") })
            {
                var first = page + sort;
                using var doc = System.Text.Json.JsonDocument.Parse(await rest.GetStringAsync(first));
                var cursor = doc.RootElement.GetProperty("cursorForOlder").GetString()!;
                calls.Add(Get($"register.{name}.first.{sortName}", first));
                calls.Add(Get($"register.{name}.next.{sortName}", $"{first}&direction=before&cursor={Uri.EscapeDataString(cursor)}"));
            }
            calls.Add(Get($"register.{name}.tag", $"{page}&tag={Uri.EscapeDataString(tag)}"));
            calls.Add(Get($"register.{name}.search", $"{page}&search={Uri.EscapeDataString(search)}"));
            calls.Add(Get($"register.{name}.category", $"{page}&category_id={counterparty}"));
            var buckets = $"/api/ledgers/{ledgerId}/transactions/index-buckets?account_id={account}";
            var counts = $"/api/ledgers/{ledgerId}/transactions/status-counts?account_id={account}";
            calls.Add(Get($"register.{name}.buckets", buckets));
            calls.Add(Get($"register.{name}.buckets.tag", $"{buckets}&tag={Uri.EscapeDataString(tag)}"));
            calls.Add(Get($"register.{name}.counts", counts));
            calls.Add(Get($"register.{name}.counts.tag", $"{counts}&tag={Uri.EscapeDataString(tag)}"));
        }
        var reports = $"/api/ledgers/{ledgerId}/reports/summary?measure=Net&bucket=Month";
        calls.Add(Get("report.summary", reports));
        calls.Add(Get("report.summary.tag", $"{reports}&tagId={StressLedger.TagId(ledgerId, 7)}"));
        calls.Add(Get("report.tag-cells", $"{reports}&groupBy=Tag"));
        calls.Add(Get("report.cash-flow", AnnotationApiTimingTests.CashFlowUrl(ledgerId)));
        calls.Add(Get("budget.drill-down", $"/api/ledgers/{ledgerId}/budget/transactions?month=2012-06&categoryId={category}"));

        ReadCall List(string name, object arguments) => new(name, mcp, () => StressMcpClient.ToolCall("list_transactions", arguments));
        calls.Add(List("mcp.list-transactions.account", new { ledgerId, accountId = bank, limit = 200 }));
        calls.Add(List("mcp.list-transactions.all", new { ledgerId, limit = 200 }));
        calls.Add(List("mcp.list-transactions.text", new { ledgerId, text = search, limit = 200 }));
        calls.Add(List("mcp.list-transactions.tag", new { ledgerId, tagId = StressLedger.TagId(ledgerId, 7), limit = 200 }));
        return calls;
    }

    [Fact]
    public async Task Backfill_prints_its_cost_at_four_times_the_default_bank_volume()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Default with { BankTxns = StressLedger.Scale.Default.BankTxns * 4 };
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        await PrintBackfillAsync("4x-bank", ledger.LedgerId, scale, seeded);
    }

    private async Task PrintBackfillAsync(string label, Guid ledgerId, StressLedger.Scale scale, StressLedger.Seeded seeded)
    {
        await using var db = Db.NewServiceFactory().Create();
        var legs = await db.TxnLegs.CountAsync(l => l.LedgerId == ledgerId);
        var notes = await StoredNotesEquivalence.PresentedLegsAsync(db, ledgerId);
        var tags = await db.TxnLegNoteTags.CountAsync(t => t.LedgerId == ledgerId);
        Assert.Equal(legs, notes);
        var rate = legs / Math.Max(seeded.NotesElapsed.TotalSeconds, 0.001);
        Print($"backfill {label} txns={scale.TotalTxns} legs={legs} tag-rows={tags} batch={Coffer.Api.Migrations.LegNotesBackfill.BatchSize} elapsed={seeded.NotesElapsed.TotalSeconds:F1}s ({rate:F0} legs/s) seed={seeded.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// A save's cost with the recompute in it: a memo edit on a one-split and a split, each
    /// timed end to end, and the recompute of the split's header alone.
    /// </summary>
    private async Task PrintSaveCostAsync(SyntheticLedger ledger, HttpClient rest)
    {
        Guid oneSplit, split;
        var bank = StressLedger.BankId(ledger.LedgerId, 2);
        await using (var db = Db.NewServiceFactory().Create())
        {
            oneSplit = await db.TxnLegs.AsNoTracking()
                .Where(l => l.AccountId == bank && l.HeaderTotalPostings == 1)
                .Select(l => l.HeaderId).FirstAsync();
            split = await db.TxnLegs.AsNoTracking()
                .Where(l => l.AccountId == bank && l.HeaderTotalPostings > 1)
                .Join(db.TxnHeaders, l => l.HeaderId, h => h.Id, (l, h) => h)
                .Where(h => h.OriginatingAccountId == bank && h.Action == null)
                .Select(h => h.Id).FirstAsync();
        }

        foreach (var (name, header) in new[] { ("one-split", oneSplit), ("split", split) })
        {
            var times = new List<double>();
            for (var i = 0; i < Warmup + Rounds; i++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await rest.PatchAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/transactions/{header}", new { notes = new { side = bank, memo = $"spike memo {i}" } });
                watch.Stop();
                Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                if (i >= Warmup) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            var recompute = new List<double>();
            await using (var db = Db.NewServiceFactory().Create())
            {
                for (var i = 0; i < Warmup + Rounds; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await LegNotes.RecomputeAsync(db, [header], CancellationToken.None);
                    watch.Stop();
                    if (i >= Warmup) recompute.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
            Print($"save {name} PATCH memo median={AnnotationApiTiming.Median(times):F1}ms; recompute alone median={AnnotationApiTiming.Median(recompute):F1}ms");
        }
    }

}
