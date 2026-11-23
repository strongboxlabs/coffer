using System.Text.Json;

using Npgsql;

using Coffer.Api.Tests.Integration.Stress;

using static Coffer.Api.Tests.Integration.Stress.AnnotationApiTiming;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The arithmetic behind the memo-and-tags performance gate: what a timing run reports
/// per call, and how an after run is judged against the before runs.
/// </summary>
/// <remarks>
/// The timing test itself runs on demand in the stress lane, against a 50k-row seed,
/// so a slip in this arithmetic would surface only as a verdict nobody can check by
/// hand. It is pinned here, in the ordinary suite, instead.
/// </remarks>
public sealed class AnnotationApiGateTests
{
    private static CallFigures Figures(double median, double noise, string call = "c", double? max = null) =>
        new(call, median, max ?? median, max ?? median, noise, 20);

    private static CallTimeout Timeout(string call, bool confirmed = true) =>
        new(call, 500, 30061.2, confirmed);

    /// <summary>A saved run's output holding these figure and timeout lines.</summary>
    private static string[] Run(IEnumerable<CallFigures> figures, params CallTimeout[] timeouts) =>
        [.. figures.Select(f => "  " + Format(f)), .. timeouts.Select(t => "  " + FormatTimeout(t))];

    /// <summary>Two identical before runs: between-run noise zero, so the figures gate as given.</summary>
    private static BeforeRuns Twice(IEnumerable<CallFigures> figures, params CallTimeout[] timeouts)
    {
        var run = Run(figures.ToList(), timeouts);
        return ParseBefore([run, run]);
    }

    [Fact]
    public void Median_takes_the_middle_value_or_the_mean_of_the_middle_two()
    {
        Assert.Equal(3d, Median([5, 1, 3]));
        Assert.Equal(2.5d, Median([4, 1, 3, 2]));
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        var samples = Enumerable.Range(1, 20).Select(i => (double)i).Reverse().ToArray();

        // Nearest rank: the 19th of 20 samples, not an interpolated value.
        Assert.Equal(19d, Percentile(samples, 0.95));
        Assert.Equal(20d, Percentile(samples, 1.0));
        Assert.Equal(1d, Percentile(samples, 0.01));
    }

    [Fact]
    public void Within_run_noise_is_the_gap_between_the_medians_of_the_alternating_halves()
    {
        // Run order A B A B ...: the even samples are one half, the odd the other.
        double[] samples = [10, 20, 11, 21, 12, 22, 13, 23];

        var f = Summarize("register.bank.first.date", samples);

        Assert.Equal("register.bank.first.date", f.Call);
        Assert.Equal(16.5d, f.MedianMs);                 // median of all eight
        Assert.Equal(23d, f.P95Ms);
        Assert.Equal(23d, f.MaxMs);
        Assert.Equal(10d, f.NoiseMs);                   // |11.5 - 21.5|
        Assert.Equal(8, f.Samples);
    }

    [Fact]
    public void A_printed_line_parses_back_to_the_same_figures_and_other_lines_are_skipped()
    {
        var f = new CallFigures("report.tag-cells", 123.4, 150.5, 171.2, 6.7, 20);
        string[] log =
        [
            "  seed:      41.0s",
            "  " + Format(f),
            $"  {Prefix} host in-process test server",
            $"  {Prefix} verdict report.tag-cells before=1.0ms after=2.0ms -> pass",
        ];

        Assert.StartsWith(Prefix + " report.tag-cells", Format(f), StringComparison.Ordinal);
        var parsed = Parse(log);

        Assert.Equal(f, Assert.Single(parsed).Value);
    }

    [Fact]
    public void A_figure_line_without_its_slowest_sample_is_refused_rather_than_read_as_zero()
    {
        string[] log = [$"  {Prefix} report.tag-cells median=123.4ms p95=150.5ms noise=6.7ms n=20"];

        var refused = Assert.Throws<FormatException>(() => Parse(log));

        Assert.Contains("max=", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Before clearly under the mark: anything clearly under ~100 ms passes, however much it grew.
    [InlineData(40, 2, 90, 0, Verdict.Pass)]
    [InlineData(5, 2, 97.9, 0, Verdict.Pass)]
    // After within noise of the mark: reported, never passed silently.
    [InlineData(40, 2, 98.5, 0, Verdict.Borderline)]
    [InlineData(40, 2, 101.5, 0, Verdict.Borderline)]
    [InlineData(40, 2, 103, 0, Verdict.Fail)]
    [InlineData(97, 2, 100, 0, Verdict.Borderline)]
    // The mark is end to end: the round trip the in-process host does not pay counts.
    [InlineData(40, 2, 90, 15, Verdict.Fail)]
    // Before over the mark: within max(10% of before, noise) passes.
    [InlineData(200, 5, 219, 0, Verdict.Pass)]
    [InlineData(200, 5, 221, 0, Verdict.Fail)]
    [InlineData(200, 30, 225, 0, Verdict.Pass)]
    [InlineData(200, 30, 231, 0, Verdict.Fail)]
    [InlineData(200, 5, 150, 0, Verdict.Pass)]
    // Before within noise of the mark, on either side: judged as over it, so which rule
    // applies does not depend on which side of the mark the noise put the median.
    [InlineData(99, 2, 99, 0, Verdict.Pass)]
    [InlineData(99, 2, 108.8, 0, Verdict.Pass)]
    [InlineData(99, 2, 109, 0, Verdict.Fail)]
    [InlineData(98, 2, 105, 0, Verdict.Pass)]
    [InlineData(101, 2, 111, 0, Verdict.Pass)]
    public void Judge_applies_the_noticeable_change_rule(
        double beforeMedian, double beforeNoise, double afterMedian, double roundTrip, Verdict expected)
    {
        var judgement = Judge(Figures(beforeMedian, beforeNoise), Figures(afterMedian, 0.5), roundTrip);

        Assert.Equal(expected, judgement.Verdict);
        Assert.StartsWith(Prefix + " verdict c ", judgement.Line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, DefaultRoundTripMs)]
    [InlineData("", DefaultRoundTripMs)]
    [InlineData("12.5", 12.5)]
    [InlineData("1", 1)]
    public void The_round_trip_defaults_to_the_home_network_allowance_and_can_be_overridden(
        string? raw, double expected)
    {
        Assert.Equal(5d, DefaultRoundTripMs);
        Assert.Equal(expected, RoundTrip(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_round_trip_override_that_is_not_a_positive_number_is_refused(string raw)
    {
        Assert.ThrowsAny<FormatException>(() => RoundTrip(raw));
    }

    [Fact]
    public void Before_runs_merge_to_the_median_of_their_medians_and_the_larger_noise()
    {
        // Between-run drift dominates: medians 100 and 130, within-run noise 4 and 6.
        // Within-run noise dominates: medians 50 and 52, within-run noise 9 and 3.
        // The slowest answer merges to the slowest of either run.
        string[] first = Run([new("drift", 100, 120, 125, 4, 20), new("steady", 50, 60, 80, 9, 20)]);
        string[] second = Run([new("drift", 130, 140, 150, 6, 20), new("steady", 52, 55, 70, 3, 20)]);

        var before = ParseBefore([first, second]);

        Assert.Equal(2, before.Runs);
        Assert.Empty(before.Disagreements);
        Assert.Equal(new CallFigures("drift", 115, 140, 150, 30, 40), before.Figures["drift"]);
        Assert.Equal(new CallFigures("steady", 51, 60, 80, 9, 40), before.Figures["steady"]);
    }

    [Fact]
    public void Before_runs_that_disagree_on_a_call_name_it_and_keep_it_out_of_both_sides()
    {
        string[] first = Run([Figures(40, 1, "mixed"), Figures(40, 1, "only-first"), Figures(40, 1, "a")]);
        string[] second = Run([Figures(40, 1, "a")], Timeout("mixed"));

        var before = ParseBefore([first, second]);

        Assert.Equal(new[] { "mixed", "only-first" }, before.Disagreements.Order(StringComparer.Ordinal));
        Assert.Equal("a", Assert.Single(before.Figures).Key);
        Assert.Empty(before.Timeouts);
    }

    [Fact]
    public void A_timeout_merged_over_runs_keeps_the_earliest_failure_and_is_confirmed_only_if_every_run_confirmed_it()
    {
        string[] first = Run([], new CallTimeout("t", 500, 31000, true), new CallTimeout("u", 500, 30500, true));
        string[] second = Run([], new CallTimeout("t", 500, 30100, true), new CallTimeout("u", 500, 30200, false));

        var before = ParseBefore([first, second]);

        Assert.Equal(new CallTimeout("t", 500, 30100, true), before.Timeouts["t"]);
        Assert.Equal(new CallTimeout("u", 500, 30200, false), before.Timeouts["u"]);
    }

    [Fact]
    public void The_gate_needs_two_before_runs()
    {
        var one = ParseBefore([Run([Figures(40, 2, "a")])]);

        var judgements = Gate(one, [Figures(41, 1, "a")], [], roundTripMs: 0);

        Assert.Contains(judgements, j => j.Verdict == Verdict.Fail && j.Call != "a");
        Assert.Equal(Verdict.Pass, judgements.Single(j => j.Call == "a").Verdict);
    }

    [Fact]
    public void The_gate_judges_every_after_call_and_fails_one_the_before_runs_never_measured()
    {
        var before = Twice([Figures(40, 2, "a"), Figures(200, 5, "b")]);
        CallFigures[] after = [Figures(50, 1, "a"), Figures(260, 1, "b"), Figures(10, 1, "new")];

        var judgements = Gate(before, after, [], roundTripMs: 0);

        Assert.Equal(new[] { "a", "b", "new" }, judgements.Select(j => j.Call));
        Assert.Equal(
            new[] { Verdict.Pass, Verdict.Fail, Verdict.Fail }, judgements.Select(j => j.Verdict));
    }

    [Fact]
    public void The_gate_fails_a_before_call_the_after_run_does_not_contain()
    {
        var before = Twice([Figures(40, 2, "a"), Figures(40, 2, "dropped")], Timeout("dropped-timeout"));

        var judgements = Gate(before, [Figures(41, 1, "a")], [], roundTripMs: 0);

        Assert.Equal(Verdict.Pass, judgements.Single(j => j.Call == "a").Verdict);
        Assert.Equal(Verdict.Fail, judgements.Single(j => j.Call == "dropped").Verdict);
        Assert.Equal(Verdict.Fail, judgements.Single(j => j.Call == "dropped-timeout").Verdict);
    }

    [Fact]
    public void The_gate_fails_a_call_the_before_runs_disagree_on()
    {
        var before = ParseBefore([Run([Figures(40, 1, "mixed")]), Run([], Timeout("mixed"))]);

        var judgements = Gate(before, [Figures(41, 1, "mixed")], [], roundTripMs: 0);

        Assert.Equal(Verdict.Fail, Assert.Single(judgements).Verdict);
    }

    [Fact]
    public void A_timeout_line_parses_back_with_its_cause_and_is_not_read_as_figures()
    {
        var timeout = Timeout("report.tag-cells");
        string[] log = ["  " + FormatTimeout(timeout), "  " + Format(Figures(10, 1, "a"))];

        Assert.StartsWith(Prefix + " report.tag-cells", FormatTimeout(timeout), StringComparison.Ordinal);
        Assert.Contains("cause=" + CommandTimeoutCause, FormatTimeout(timeout), StringComparison.Ordinal);
        Assert.Equal(timeout, Assert.Single(ParseTimeouts(log)).Value);
        Assert.Equal("a", Assert.Single(Parse(log)).Key);
    }

    [Fact]
    public void A_timeout_line_without_a_cause_reads_back_unconfirmed()
    {
        string[] log = [$"  {Prefix} report.tag-cells    timeout status=500 after=31170.6ms"];

        Assert.False(Assert.Single(ParseTimeouts(log)).Value.CommandTimeout);
    }

    [Theory]
    [InlineData(14_000, 14_000, 0, Verdict.Pass)]
    [InlineData(15_000, 15_000, 0, Verdict.Pass)]
    [InlineData(16_000, 16_000, 0, Verdict.Fail)]
    // The limit holds for every answer, so the slowest answer is judged, not the median.
    [InlineData(10_000, 15_000, 0, Verdict.Pass)]
    [InlineData(10_000, 16_000, 0, Verdict.Fail)]
    [InlineData(10_000, 29_000, 0, Verdict.Fail)]
    // The limit is end to end too.
    [InlineData(10_000, 14_996, 5, Verdict.Fail)]
    public void A_call_that_timed_out_before_must_answer_within_the_limit_after(
        double afterMedian, double afterMax, double roundTrip, Verdict expected)
    {
        var before = Twice([], Timeout("slow"));

        var judgements = Gate(before, [Figures(afterMedian, 1, "slow", max: afterMax)], [], roundTrip);

        Assert.Equal(expected, Assert.Single(judgements).Verdict);
    }

    [Fact]
    public void The_slowest_answer_includes_the_untimed_answers_and_the_statistics_do_not()
    {
        // Few timed samples, halves at 1000 and 3000, so the untimed answers would move
        // the median, the p95 and either half's median if they were let in.
        double[] timed = [1_000, 3_000, 1_000, 3_000];

        var f = Summarize("report.tag-cells", timed, untimedMs: [25_000, 24_000, 23_000]);

        Assert.Equal(25_000d, f.MaxMs);
        Assert.Equal(2_000d, f.MedianMs);
        Assert.Equal(3_000d, f.P95Ms);
        Assert.Equal(2_000d, f.NoiseMs);
        Assert.Equal(4, f.Samples);
    }

    [Theory]
    // A slow first-pass answer, then a slow warm-up answer, with fast timed rounds.
    [InlineData(25_000, 2_100, 2_050, 0, Verdict.Fail)]
    [InlineData(3_000, 16_000, 2_050, 0, Verdict.Fail)]
    [InlineData(3_000, 2_100, 15_500, 0, Verdict.Fail)]
    // The limit on an untimed answer is end to end too.
    [InlineData(14_996, 2_100, 2_050, 5, Verdict.Fail)]
    [InlineData(14_995, 2_100, 2_050, 5, Verdict.Pass)]
    public void A_call_that_timed_out_before_is_held_to_the_limit_on_its_untimed_answers_too(
        double firstPass, double warmup1, double warmup2, double roundTrip, Verdict expected)
    {
        var before = Twice([], Timeout("slow"));
        var after = Summarize("slow", [.. Enumerable.Repeat(2_000d, 20)], untimedMs: [firstPass, warmup1, warmup2]);

        var judgements = Gate(before, [after], [], roundTrip);

        Assert.Equal(expected, Assert.Single(judgements).Verdict);
    }

    [Fact]
    public void Separate_before_runs_are_accepted()
    {
        string[] first = Run([Figures(40, 1, "a")], Timeout("t"));
        string[] second = Run([Figures(41, 1, "a")], Timeout("t"));

        EnsureSeparateBeforeRuns(["first.txt", "second.txt"], [first, second]);
    }

    [Theory]
    [InlineData("run.txt", "run.txt")]
    [InlineData("run.txt", "./run.txt")]
    [InlineData("logs/run.txt", "logs/../logs/run.txt")]
    public void One_file_named_twice_is_not_two_before_runs(string firstPath, string secondPath)
    {
        // Contents that differ, so only the paths can catch it.
        string[] first = Run([Figures(40, 1, "a")]);
        string[] second = Run([Figures(41, 1, "a")]);

        var refused = Assert.Throws<ArgumentException>(
            () => EnsureSeparateBeforeRuns([firstPath, secondPath], [first, second]));

        Assert.Contains("same file", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_files_with_identical_figure_lines_are_not_two_before_runs()
    {
        // A copy of one run: the surrounding lines differ, the figures and timeouts do not.
        string[] run = Run([Figures(40, 1, "a"), Figures(200, 5, "b")], Timeout("t"));
        string[] copy = ["  seed:      41.0s", .. run.Reverse(), $"  {Prefix} verdict a before=1.0ms after=2.0ms -> pass"];

        var refused = Assert.Throws<ArgumentException>(
            () => EnsureSeparateBeforeRuns(["first.txt", "copy.txt"], [run, copy]));

        Assert.Contains("identical", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copied_run_is_refused_among_more_than_two_before_runs()
    {
        string[] first = Run([Figures(40, 1, "a")]);
        string[] second = Run([Figures(41, 1, "a")]);

        Assert.Throws<ArgumentException>(
            () => EnsureSeparateBeforeRuns(["1.txt", "2.txt", "3.txt"], [first, second, first]));
    }

    [Fact]
    public void A_before_timeout_whose_cause_was_never_confirmed_fails_however_fast_the_call_is_after()
    {
        var before = Twice([], Timeout("slow", confirmed: false));

        var judgements = Gate(before, [Figures(10, 1, "slow")], [], roundTripMs: 0);

        Assert.Equal(Verdict.Fail, Assert.Single(judgements).Verdict);
    }

    [Fact]
    public void A_call_that_times_out_after_fails_whatever_it_did_before()
    {
        var before = Twice([Figures(40, 2, "a")], Timeout("still-slow"));

        var judgements = Gate(before, [], [Timeout("a"), Timeout("still-slow")], roundTripMs: 0);

        Assert.Equal(new[] { "a", "still-slow" }, judgements.Select(j => j.Call));
        Assert.All(judgements, j => Assert.Equal(Verdict.Fail, j.Verdict));
    }

    public static TheoryData<int, Exception?, bool> Failures => new()
    {
        // What the API logs when a query outruns its command timeout: the execution
        // strategy wraps Npgsql's read timeout.
        {
            500,
            new InvalidOperationException("transient failure",
                new NpgsqlException("Exception while reading from stream", new TimeoutException())),
            true
        },
        { 500, new NpgsqlException("Exception while reading from stream", new TimeoutException()), true },
        // The same exception answered as something other than a 500 is not this failure.
        { 503, new NpgsqlException("Exception while reading from stream", new TimeoutException()), false },
        // Any other 500, however slow, is a broken call.
        { 500, new InvalidOperationException("boom"), false },
        { 500, new NpgsqlException("Exception while reading from stream", new IOException()), false },
        { 500, new TimeoutException("not from the database"), false },
        { 500, null, false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Only_a_500_carrying_the_npgsql_timeout_signature_is_a_command_timeout(
        int status, Exception? exception, bool expected)
    {
        Assert.Equal(expected, IsDatabaseCommandTimeout(status, exception));
    }

    [Fact]
    public void Round_orders_are_seeded_shuffles_that_change_every_call_s_neighbours()
    {
        var calls = Enumerable.Range(0, 26).ToArray();
        var orders = Enumerable.Range(1, 21).Select(r => RoundOrder(calls, r)).ToList();

        // Each round visits every call once, and the same round always gives the same order.
        Assert.All(orders, o => Assert.Equal(calls, o.Order()));
        Assert.Equal(orders[3], RoundOrder(calls, 4));

        // No call always follows the same neighbour.
        foreach (var call in calls)
        {
            var predecessors = new HashSet<int>();
            foreach (var order in orders)
            {
                var at = order.ToList().IndexOf(call);
                if (at > 0) predecessors.Add(order[at - 1]);
            }
            Assert.True(predecessors.Count > 1, $"call {call} always follows the same neighbour");
        }
    }

    /// <summary>An MCP answer as streamable HTTP sends it: one SSE event carrying the JSON-RPC response.</summary>
    private static string SseAnswer(string result) =>
        $"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{result}}}\n\n";

    [Fact]
    public void An_mcp_answer_counts_its_lines()
    {
        var lines = JsonSerializer.Serialize(JsonSerializer.Serialize(new { lines = new[] { 1, 2, 3 } }));

        Assert.Equal(3, McpLineCount(SseAnswer($"{{\"content\":[{{\"type\":\"text\",\"text\":{lines}}}]}}")));
    }

    [Fact]
    public void An_mcp_tool_error_fails_the_call_instead_of_counting_as_no_rows()
    {
        // A tool that throws (a database command timeout among them) answers 200 with
        // isError. Read as zero rows, it would pass for an empty listing, and a before
        // run and an after run that both failed would compare equal.
        var body = SseAnswer(
            "{\"content\":[{\"type\":\"text\",\"text\":\"An error occurred invoking 'list_transactions'.\"}],\"isError\":true}");

        var error = Assert.Throws<InvalidOperationException>(() => McpLineCount(body));
        Assert.Contains("An error occurred invoking 'list_transactions'.", error.Message, StringComparison.Ordinal);
    }
}
