using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

using Coffer.Api.Tests.Integration.Infra;

using static Coffer.Api.Tests.Integration.Stress.AnnotationApiTiming;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Whole API calls on the stress ledger, timed at the client: the before and after
/// measurement for moving the memo and tags rules into the API.
/// </summary>
/// <remarks>
/// <para><b>What is timed.</b> Each sample is one HTTP request through the real
/// pipeline (routing, authentication, the repository, the database, JSON
/// serialisation) until the whole body has been read: the register page of a bank
/// account, a category and a brokerage (first page and the next keyset page, by date
/// and by amount, and with a tag filter, a text search and a category filter), each
/// register's scroll-track buckets and status counts (unfiltered and with a tag filter), the
/// report summary with and without a tag filter, the tag cells including
/// "(untagged)", the home screen's cash flow, the budget drill-down, the home screen's ledger-wide
/// entries (unfiltered, with the caller's today and outflows only), and MCP <c>list_transactions</c> with and
/// without an account and, without one, with a text search and a tag filter. REST calls carry a session cookie and MCP calls a
/// connected-apps bearer, the production authentication paths.</para>
/// <para><b>How it is hosted.</b> <see cref="ApiFactory"/> is a
/// <c>WebApplicationFactory</c>: the API runs in this process on ASP.NET Core's
/// TestServer, and the client's handler hands requests to it in memory. No socket,
/// no TLS, no proxy. The figures therefore miss the network round trip a browser
/// pays, which the ~100 ms mark includes; the gate adds it back:
/// <see cref="AnnotationApiTiming.DefaultRoundTripMs"/>, or <see cref="RoundTripVariable"/>
/// when set.</para>
/// <para><b>What counts as an answer.</b> Every call must answer with rows, and a
/// filtered call with a different first page from the same call unfiltered: an empty
/// page, or a filter the API ignores, is cheap and would report a figure that measures
/// nothing. A call that fails with a 500 is a timeout only when the exception behind
/// it carries the database command timeout's signature; any other failure fails the
/// test.</para>
/// <para><b>Noise.</b> After two warm-up rounds, twenty timed rounds visit every call
/// once each, in an order shuffled per round from a fixed seed, so no call always
/// follows the same neighbour and both runs visit the calls in the same orders. Even
/// and odd rounds form an A/A pair over the same build; the gap between their medians
/// is the call's within-run noise. The gate also takes the drift between two separate
/// before runs (<see cref="AnnotationApiTiming.ParseBefore"/>). The warm-up answers stay
/// out of the statistics but not out of the slowest answer, so a hard limit holds for
/// the cold first answer as well.</para>
/// <para><b>The gate.</b> With <see cref="BeforeFileVariable"/> naming the saved
/// output of at least two before runs, every call is judged
/// (<see cref="AnnotationApiTiming.Gate"/>) and a failing call fails the test.
/// Without it the test only measures, like the rest of this lane. A call that hits the
/// database command timeout is printed as a timeout line instead of figures and left
/// out of the shuffled rounds; when the before runs recorded such a call, the after run
/// times it apart too, after the shuffled rounds, so both runs shuffle the same calls.
/// Before and after runs belong on the same machine and seed with nothing else running
/// (no preflight, no CI), alternating before and after builds if more than one pair is
/// taken.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AnnotationApiTimingTests : DatabaseTest
{
    /// <summary>
    /// The saved output of the before runs, as paths separated by the platform's path
    /// separator; set to gate this run against them.
    /// </summary>
    public const string BeforeFileVariable = "COFFER_ANNOTATION_API_BEFORE";

    /// <summary>Overrides the round trip in ms added for the ~100 ms mark.</summary>
    public const string RoundTripVariable = "COFFER_ANNOTATION_API_RTT_MS";

    /// <summary>Marks the plan lines, which the gate's parser never reads.</summary>
    private const string ExplainPrefix = "explain:";

    private const int WarmupRounds = 2;
    private const int TimedRounds = 20;
    private const int PageSize = 100;   // the SPA's register page (useWindowedRegister)

    private readonly ITestOutputHelper _output;

    public AnnotationApiTimingTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    /// <summary>
    /// One timed call: how to build its request and how many rows its body holds. A
    /// filtered call also names the same call unfiltered and how to tell two of their
    /// pages apart.
    /// </summary>
    private sealed record Call(
        string Name, HttpClient Client, Func<HttpRequestMessage> Request, Func<string, int> Rows,
        string? UnfilteredUrl = null, Func<string, string>? PageIdentity = null);

    [Fact]
    public async Task Annotation_read_paths_print_their_end_to_end_cost()
    {
        // Both inputs are read before the seed, so a bad one fails in seconds, not minutes.
        var roundTrip = RoundTrip(Environment.GetEnvironmentVariable(RoundTripVariable));
        var before = await ReadBeforeAsync();

        var ledger = await SyntheticLedger.CreateAsync(Db);
        var scale = StressLedger.Scale.Default;
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, scale);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seed:    {seeded.Elapsed.TotalSeconds,7:F1}s  (target {scale.TotalTxns:N0} txns)"));
        await StressLedger.AssertInvariantsAsync(Db, ledger.LedgerId, scale);

        var failures = new FailureRecorder();
        // The view's reads, and the header reads a tag filter or a text search makes before them.
        var explain = new ExplainCapture
        {
            Selects = sql => sql.Contains("resolved_transactions", StringComparison.Ordinal)
                             || sql.Contains("FROM txn_leg_note_tags", StringComparison.Ordinal)
                             || sql.Contains("FROM txn_headers", StringComparison.Ordinal),
        };
        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithMcpEnabled()
            .WithServices(services => services.AddSingleton<IExceptionHandler>(failures))
            .WithServices(explain.Register);
        using var rest = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        rest.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        rest.Timeout = TimeSpan.FromMinutes(5);
        using var mcp = await StressMcpClient.CreateAsync(Db, factory, ledger, "stress-timing");

        var calls = await BuildCallsAsync(ledger.LedgerId, seeded, rest, mcp);

        // The first warm-up pass also sorts out the calls that cannot answer. One that
        // dies at the database command timeout is the user's experience of it, so it is
        // recorded as such and left out of the timed rounds (twenty more half-minute
        // waits would measure the timeout, not the call). Any other failure is a broken
        // harness or a broken API and fails the test here.
        var timeouts = new List<CallTimeout>();
        var shuffled = new List<Call>();
        var apart = new List<Call>();
        // The first-pass and warm-up answers are kept apart from the timed samples: they
        // do not enter the statistics, but the hard limit bounds them too.
        var untimed = calls.ToDictionary(c => c.Name, _ => new List<double>(WarmupRounds));
        foreach (var call in calls)
        {
            var sample = await SendAsync(call);
            if (!Answered(sample) && IsDatabaseCommandTimeout(sample.Status, failures.For(sample.Body)))
            {
                timeouts.Add(new CallTimeout(call.Name, sample.Status, sample.Ms, CommandTimeout: true));
                continue;
            }
            AssertAnswered(call, sample, failures);
            untimed[call.Name].Add(sample.Ms);
            Assert.True(call.Rows(sample.Body) > 0, $"{call.Name} returned no rows");
            await AssertFilterAppliesAsync(call, sample.Body, failures);
            // A call the before runs saw time out is timed apart, so the shuffled rounds
            // hold the same calls in both runs and no call's neighbours change between them.
            (before?.Timeouts.ContainsKey(call.Name) == true ? apart : shuffled).Add(call);
        }

        var samples = shuffled.Concat(apart).ToDictionary(c => c.Name, _ => new List<double>(TimedRounds));
        for (var round = 1; round < WarmupRounds + TimedRounds; round++)
        {
            foreach (var call in RoundOrder(shuffled, round))
                await TakeAsync(call, round);
        }
        foreach (var call in apart)
        {
            for (var round = 1; round < WarmupRounds + TimedRounds; round++)
                await TakeAsync(call, round);
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} host=in-process-testserver rounds={TimedRounds} warmup={WarmupRounds} " +
            $"page={PageSize} txns={scale.TotalTxns} rtt-added={roundTrip:F1}ms"));
        var figures = shuffled.Concat(apart)
            .Select(c => Summarize(c.Name, samples[c.Name], untimed[c.Name])).ToList();
        foreach (var f in figures)
            _output.WriteLine(Format(f));
        foreach (var t in timeouts)
            _output.WriteLine(FormatTimeout(t));

        // The judgement is written before anything else can fail the run, so the
        // verdict is always in the output; it is asserted after the diagnostic.
        var judgements = before is null ? null : Gate(before, figures, timeouts, roundTrip);
        foreach (var j in judgements ?? [])
            _output.WriteLine(j.Line);

        // A diagnostic only, after every timed sample: every call's statements as the
        // database plans and runs them, the calls timed apart included, to read when a
        // call is slow. Nothing in it fails the test: a call that does not answer, or a
        // plan that cannot be taken, is written out and the next call goes on.
        explain.Enabled = true;
        foreach (var call in shuffled.Concat(apart))
        {
            try
            {
                var sample = await SendAsync(call);
                if (!Answered(sample))
                    _output.WriteLine($"{ExplainPrefix} {call.Name} did not answer: {sample.Status} after {sample.Ms:F0}ms " +
                                      $"{failures.For(sample.Body)?.Message}");
            }
            catch (Exception e)
            {
                _output.WriteLine($"{ExplainPrefix} {call.Name} failed: {e.GetType().Name}: {e.Message}");
            }
            foreach (var (sql, plan) in explain.Drain())
            {
                _output.WriteLine($"{ExplainPrefix} {call.Name}");
                foreach (var line in sql.Split('\n').Concat(plan.Split('\n')))
                    _output.WriteLine($"{ExplainPrefix}   {line.TrimEnd()}");
            }
        }
        explain.Enabled = false;

        if (judgements is not null)
        {
            var failed = judgements.Where(j => j.Verdict == Verdict.Fail).Select(j => j.Call).ToList();
            Assert.True(failed.Count == 0, "noticeably slower than before: " + string.Join(", ", failed));
        }

        async Task TakeAsync(Call call, int round)
        {
            var sample = await SendAsync(call);
            AssertAnswered(call, sample, failures);
            (round >= WarmupRounds ? samples : untimed)[call.Name].Add(sample.Ms);
        }
    }

    /// <summary>The before runs <see cref="BeforeFileVariable"/> names, or null when it is unset.</summary>
    private static async Task<BeforeRuns?> ReadBeforeAsync()
    {
        if (Environment.GetEnvironmentVariable(BeforeFileVariable) is not { Length: > 0 } raw) return null;
        var paths = raw.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var runs = new List<IReadOnlyList<string>>();
        foreach (var path in paths)
            runs.Add(await File.ReadAllLinesAsync(path));
        // One run given twice, by path or as a copy, would pass as two with no drift between them.
        EnsureSeparateBeforeRuns(paths, runs);
        return ParseBefore(runs);
    }

    private readonly record struct Sample(int Status, double Ms, string Body);

    private static bool Answered(Sample sample) => sample.Status is >= 200 and < 300;

    private static async Task<Sample> SendAsync(Call call)
    {
        using var request = call.Request();
        var watch = Stopwatch.StartNew();
        using var response = await call.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        return new Sample((int)response.StatusCode, watch.Elapsed.TotalMilliseconds, body);
    }

    private static void AssertAnswered(Call call, Sample sample, FailureRecorder failures)
    {
        if (Answered(sample)) return;
        Assert.Fail($"{call.Name}: {sample.Status} after {sample.Ms:F0}ms " +
                    $"{sample.Body[..Math.Min(sample.Body.Length, 400)]} {failures.For(sample.Body)}");
    }

    /// <summary>
    /// A filtered call's first page must differ from the same call's unfiltered one:
    /// an after build that ignored the filter would answer faster and pass otherwise.
    /// </summary>
    private static async Task AssertFilterAppliesAsync(Call call, string filteredBody, FailureRecorder failures)
    {
        if (call.UnfilteredUrl is null || call.PageIdentity is null) return;
        using var response = await call.Client.GetAsync(call.UnfilteredUrl);
        var unfiltered = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"{call.Name} unfiltered: {(int)response.StatusCode} {failures.For(unfiltered)}");
        Assert.True(call.PageIdentity(filteredBody) != call.PageIdentity(unfiltered),
            $"{call.Name}: the filtered page is the unfiltered page, so the filter was not applied");
    }

    /// <summary>
    /// Sees every unhandled exception the API's exception handler is about to answer,
    /// keyed by the request's trace id, which the problem body carries; it handles
    /// none, so the response is the API's own.
    /// </summary>
    private sealed class FailureRecorder : IExceptionHandler
    {
        private readonly ConcurrentDictionary<string, Exception> _byTraceId = new(StringComparer.Ordinal);

        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            _byTraceId[httpContext.TraceIdentifier] = exception;
            return ValueTask.FromResult(false);
        }

        /// <summary>The exception behind a problem body, or null when there was none.</summary>
        public Exception? For(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("traceId", out var id)
                       && id.GetString() is { } traceId
                       && _byTraceId.TryGetValue(traceId, out var exception)
                    ? exception
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static async Task<List<Call>> BuildCallsAsync(
        Guid ledgerId, StressLedger.Seeded seeded, HttpClient rest, HttpClient mcp)
    {
        // Bank 2 holds one-splits, splits and transfers both ways; category 8 holds
        // one-splits, split lines and symmetric split lines (see the seed's plan).
        var bank = StressLedger.BankId(ledgerId, 2);
        var category = StressLedger.CategoryId(ledgerId, 8);
        var tag = StressLedger.TagName(7);
        const string search = "note-17";

        // Each register with the filter that means something there: a category on the
        // bank, the bank on the category (the filter matches the counterparty), the
        // dividends' income category on the brokerage.
        var registers = new (string Name, Guid Account, Guid Counterparty)[]
        {
            ("bank", bank, category),
            ("category", category, bank),
            ("brokerage", seeded.BrokerageId, StressLedger.IncomeCategoryId(ledgerId)),
        };

        var calls = new List<Call>();
        foreach (var (name, account, counterparty) in registers)
        {
            var page = $"/api/ledgers/{ledgerId}/transactions?account_id={account}&limit={PageSize}";
            foreach (var (sortName, sort) in new[] { ("date", ""), ("amount", "&sort=amount&dir=desc") })
            {
                var first = page + sort;
                // The keyset page is the one the first page's cursor opens, resolved once.
                var cursor = await FirstPageCursorAsync(rest, first);
                calls.Add(Rest($"register.{name}.first.{sortName}", rest, first, EntryCount));
                calls.Add(Rest($"register.{name}.next.{sortName}", rest,
                    $"{first}&direction=before&cursor={Uri.EscapeDataString(cursor)}", EntryCount));
            }
            calls.Add(Rest($"register.{name}.tag", rest, $"{page}&tag={Uri.EscapeDataString(tag)}", EntryCount)
                with { UnfilteredUrl = page, PageIdentity = EntryKeys });
            calls.Add(Rest($"register.{name}.search", rest, $"{page}&search={Uri.EscapeDataString(search)}", EntryCount)
                with { UnfilteredUrl = page, PageIdentity = EntryKeys });
            calls.Add(Rest($"register.{name}.category", rest, $"{page}&category_id={counterparty}", EntryCount)
                with { UnfilteredUrl = page, PageIdentity = EntryKeys });

            // The scroll-track buckets and the status counts the register loads beside
            // every page, and again on every filter change: both count entries over the
            // whole register, not one page of it.
            var buckets = $"/api/ledgers/{ledgerId}/transactions/index-buckets?account_id={account}";
            var counts = $"/api/ledgers/{ledgerId}/transactions/status-counts?account_id={account}";
            calls.Add(Rest($"register.{name}.buckets", rest, buckets, BucketCount));
            calls.Add(Rest($"register.{name}.buckets.tag", rest, $"{buckets}&tag={Uri.EscapeDataString(tag)}", BucketCount)
                with { UnfilteredUrl = buckets, PageIdentity = BodyText });
            calls.Add(Rest($"register.{name}.counts", rest, counts, AllCount));
            calls.Add(Rest($"register.{name}.counts.tag", rest, $"{counts}&tag={Uri.EscapeDataString(tag)}", AllCount)
                with { UnfilteredUrl = counts, PageIdentity = BodyText });
        }

        var reports = $"/api/ledgers/{ledgerId}/reports/summary?measure=Net&bucket=Month";
        calls.Add(Rest("report.summary", rest, reports, RowCount));
        calls.Add(Rest("report.summary.tag", rest, $"{reports}&tagId={StressLedger.TagId(ledgerId, 7)}", RowCount)
            with { UnfilteredUrl = reports, PageIdentity = RowsJson });
        calls.Add(Rest("report.tag-cells", rest, $"{reports}&groupBy=Tag", UntaggedRowCount));
        // The home screen's cash flow: income against expense, twelve whole months to a
        // fixed caller's today inside the seeded span.
        calls.Add(Rest("report.cash-flow", rest, CashFlowUrl(ledgerId), RowCount));
        calls.Add(Rest("budget.drill-down", rest,
            $"/api/ledgers/{ledgerId}/budget/transactions?month=2012-06&categoryId={category}", LineCount));
        // The home screen's ledger-wide entries: Recent transactions' page, with the
        // caller's today and with outflows only. To review's needs-review shape is not
        // here: this seed holds no row awaiting review, so it would answer no rows.
        var entries = $"/api/ledgers/{ledgerId}/transactions/entries";
        calls.Add(Rest("transactions.entries", rest, entries, EntryCount));
        calls.Add(Rest("transactions.entries.today", rest, $"{entries}?today=2026-06-15", EntryCount)
            with { UnfilteredUrl = entries, PageIdentity = LedgerEntryKeys });
        calls.Add(Rest("transactions.entries.outflow", rest, $"{entries}?direction=outflow", EntryCount)
            with { UnfilteredUrl = entries, PageIdentity = LedgerEntryKeys });

        calls.Add(McpList("mcp.list-transactions.account", mcp, new { ledgerId, accountId = bank, limit = 200 }));
        calls.Add(McpList("mcp.list-transactions.all", mcp, new { ledgerId, limit = 200 }));
        // Search and the tag filter read the rule over the ledger's whole money side.
        calls.Add(McpList("mcp.list-transactions.text", mcp, new { ledgerId, text = search, limit = 200 }));
        calls.Add(McpList("mcp.list-transactions.tag", mcp, new { ledgerId, tagId = StressLedger.TagId(ledgerId, 7), limit = 200 }));
        return calls;
    }

    /// <summary>The home screen's cash-flow request (<c>report.cash-flow</c>), shared with
    /// <see cref="StoredNotesSpikeStressTests"/>.</summary>
    internal static string CashFlowUrl(Guid ledgerId) =>
        $"/api/ledgers/{ledgerId}/reports/summary?measure=Net&groupBy=CategoryKind&bucket=Month"
        + "&dateMode=Sliding&windowCount=12&windowUnit=Month&windowAlign=true&today=2026-06-15";

    private static Call Rest(string name, HttpClient client, string url, Func<string, int> rows) =>
        new(name, client, () => new HttpRequestMessage(HttpMethod.Get, url), rows);

    private static async Task<string> FirstPageCursorAsync(HttpClient client, string url)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
        return doc.RootElement.GetProperty("cursorForOlder").GetString()
            ?? throw new InvalidOperationException($"{url}: the first page has no older page");
    }

    private static int EntryCount(string body) => ArrayLength(body, "entries");

    private static int RowCount(string body) => ArrayLength(body, "rows");

    private static int LineCount(string body) => ArrayLength(body, "lines");

    /// <summary>The months the scroll track lists.</summary>
    private static int BucketCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }

    /// <summary>The entries the status counts see in all.</summary>
    private static int AllCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("all").GetInt32();
    }

    /// <summary>A small answer as the API wrote it.</summary>
    private static string BodyText(string body) => body;

    /// <summary>A register page's entries in order: each one's transaction or split-group id.</summary>
    private static string EntryKeys(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return string.Join(',', doc.RootElement.GetProperty("entries").EnumerateArray().Select(e =>
            e.GetProperty("kind").GetString() == "group"
                ? e.GetProperty("groupId").GetString()
                : e.GetProperty("txn").GetProperty("id").GetString()));
    }

    /// <summary>A ledger-wide entries page in order: each entry's account and its transaction or split-group id.</summary>
    private static string LedgerEntryKeys(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return string.Join(',', doc.RootElement.GetProperty("entries").EnumerateArray().Select(e =>
        {
            var entry = e.GetProperty("entry");
            var id = entry.GetProperty("kind").GetString() == "group"
                ? entry.GetProperty("groupId").GetString()
                : entry.GetProperty("txn").GetProperty("id").GetString();
            return $"{e.GetProperty("accountId").GetString()}/{id}";
        }));
    }

    /// <summary>A report's rows as the API wrote them.</summary>
    private static string RowsJson(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("rows").GetRawText();
    }

    /// <summary>Rows, but only if "(untagged)" is among them: the bucket is the point.</summary>
    private static int UntaggedRowCount(string body) =>
        body.Contains("(untagged)", StringComparison.Ordinal) ? RowCount(body) : 0;

    private static int ArrayLength(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetArrayLength();
    }

    // ----- MCP over streamable HTTP ------------------------------------------

    private static Call McpList(string name, HttpClient client, object arguments) =>
        new(name, client, () => StressMcpClient.ToolCall("list_transactions", arguments), McpLineCount);
}
