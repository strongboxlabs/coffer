using System.Globalization;
using System.Text.Json;

using Npgsql;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The statistics and the before/after verdict behind <c>AnnotationApiTimingTests</c>,
/// kept apart from the HTTP calls so <c>AnnotationApiGateTests</c> can pin them in the
/// ordinary suite.
/// </summary>
/// <remarks>
/// <para>A call is judged by whether its change is NOTICEABLE end to end. A call whose
/// before median is clearly under the ~100 ms mark passes when its after median stays
/// clearly under it, however much it grew; within noise of the mark it is borderline,
/// reported and not passed silently. A call whose before median is over the mark, or
/// within noise of it, passes when it grew by no more than 10% of its before time or
/// the before runs' noise, whichever is larger. A call that hit the database command
/// timeout before must answer within <see cref="TimedOutBeforeLimitMs"/> after, every
/// time: its slowest answer is judged, not its median, and the untimed first-pass and
/// warm-up answers count as well as the timed ones.</para>
/// <para>The noise has two parts, and the larger one counts. Within a run, the timed
/// rounds alternate between two halves of the same build, and the gap between the
/// halves' medians is what this machine and seed produce with nothing changed. Between
/// runs, two separate before runs (a fresh container, a fresh seed, a cold plan cache
/// each) give the drift an after run is exposed to: the gap between their medians.
/// Medians gate the growth rules; p95 is reported only, because the tail of a
/// twenty-sample run is one or two samples. The hard limit is the exception: it bounds
/// every answer the call gives in the run, cold or warm, so the slowest of them is held
/// to it.</para>
/// </remarks>
public static class AnnotationApiTiming
{
    /// <summary>The prefix every figure line carries (and stress-lane.sh lists).</summary>
    public const string Prefix = "annotation-api:";

    /// <summary>The "not noticeable" mark, end to end.</summary>
    public const double NoticeableMs = 100;

    /// <summary>The growth allowed to a call already over the mark.</summary>
    public const double AllowedGrowth = 0.10;

    /// <summary>
    /// How fast a call that ran into the database command timeout before must answer
    /// after, end to end. Its before figure is only a lower bound, so a growth
    /// allowance cannot be computed for it; this is the target it is held to instead.
    /// </summary>
    public const double TimedOutBeforeLimitMs = 15_000;

    /// <summary>
    /// The network round trip added when none is given: browser and server on the same
    /// home network.
    /// </summary>
    public const double DefaultRoundTripMs = 5;

    /// <summary>The cause token a timeout line carries once its cause is confirmed.</summary>
    public const string CommandTimeoutCause = "db-command-timeout";

    /// <summary>
    /// Per-call figures over one run, or merged over several. <paramref name="MaxMs"/> is
    /// the slowest answer of the run, the first pass and warm-ups included, which a call
    /// held to a hard limit is judged by; the other figures cover the timed rounds only,
    /// and <paramref name="Samples"/> counts those.
    /// </summary>
    public sealed record CallFigures(
        string Call, double MedianMs, double P95Ms, double MaxMs, double NoiseMs, int Samples);

    /// <summary>How an after figure compares with its before figure.</summary>
    public enum Verdict { Pass, Borderline, Fail }

    /// <summary>One call's verdict and the printed line that explains it.</summary>
    public sealed record Judgement(string Call, Verdict Verdict, string Line);

    /// <summary>
    /// A call that failed instead of answering: what the user gets is the error, so that
    /// is what a run records for it, with how long it took to fail.
    /// <paramref name="CommandTimeout"/> is true only when the failure was confirmed as
    /// the database command timeout.
    /// </summary>
    public sealed record CallTimeout(string Call, int Status, double Ms, bool CommandTimeout);

    /// <summary>
    /// The before side of the gate, merged over every before run given.
    /// <paramref name="Disagreements"/> are calls the runs do not agree on: answered in
    /// one and timed out in another, or missing from one.
    /// </summary>
    public sealed record BeforeRuns(
        int Runs,
        IReadOnlyDictionary<string, CallFigures> Figures,
        IReadOnlyDictionary<string, CallTimeout> Timeouts,
        IReadOnlyList<string> Disagreements);

    /// <summary>
    /// Whether a failed request ran into the database command timeout: a 500 whose
    /// exception chain holds Npgsql's timeout signature, an <see cref="NpgsqlException"/>
    /// raised by a <see cref="TimeoutException"/>. Any other failure, however slow, is a
    /// broken call, not a slow one.
    /// </summary>
    public static bool IsDatabaseCommandTimeout(int status, Exception? exception)
    {
        if (status != 500) return false;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException { InnerException: TimeoutException }) return true;
        }
        return false;
    }

    /// <summary>
    /// The round trip to add, from the override variable's raw value: the default when
    /// it is unset, otherwise a positive number of milliseconds.
    /// </summary>
    /// <exception cref="FormatException">The value is set but not a positive, finite number.</exception>
    public static double RoundTrip(string? raw)
    {
        if (raw is not { Length: > 0 }) return DefaultRoundTripMs;
        var ms = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(ms) || ms <= 0)
            throw new FormatException($"a round trip override must be a positive number of ms, not '{raw}'");
        return ms;
    }

    /// <summary>
    /// The order one round visits the calls in: a shuffle seeded by the round number,
    /// so each round gives every call different neighbours and two runs over the same
    /// calls visit them in the same orders.
    /// </summary>
    public static IReadOnlyList<T> RoundOrder<T>(IReadOnlyList<T> calls, int round)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var order = calls.ToArray();
        new Random(round).Shuffle(order);
        return order;
    }

    /// <summary>The printed line for a failed call; <see cref="ParseTimeouts"/> reads it back.</summary>
    public static string FormatTimeout(CallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        var cause = timeout.CommandTimeout ? $" cause={CommandTimeoutCause}" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} {timeout.Call,-34} timeout status={timeout.Status} after={timeout.Ms:F1}ms{cause}");
    }

    /// <summary>
    /// The failed calls in a saved run's output, keyed by call. A line without the
    /// confirmed cause reads back as unconfirmed.
    /// </summary>
    public static IReadOnlyDictionary<string, CallTimeout> ParseTimeouts(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new Dictionary<string, CallTimeout>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var at = line.IndexOf(Prefix, StringComparison.Ordinal);
            if (at < 0) continue;
            var tokens = line[(at + Prefix.Length)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 4 || tokens[1] != "timeout") continue;
            var values = tokens.Skip(2)
                .Select(t => t.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
            result[tokens[0]] = new CallTimeout(
                tokens[0],
                int.Parse(values["status"], CultureInfo.InvariantCulture),
                double.Parse(values["after"].TrimEnd('m', 's'), CultureInfo.InvariantCulture),
                CommandTimeout: values.GetValueOrDefault("cause") == CommandTimeoutCause);
        }
        return result;
    }

    /// <summary>
    /// The before side from the saved output of each before run. A call answered in
    /// every run gets merged figures: the median of the runs' medians, the largest p95,
    /// and the larger of the largest within-run noise and the spread of the runs'
    /// medians. A call that timed out in every run keeps its earliest failure, confirmed
    /// only if every run confirmed it.
    /// </summary>
    public static BeforeRuns ParseBefore(IReadOnlyList<IReadOnlyList<string>> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var figures = runs.Select(r => Parse(r)).ToList();
        var timeouts = runs.Select(r => ParseTimeouts(r)).ToList();

        var merged = new Dictionary<string, CallFigures>(StringComparer.Ordinal);
        var timedOut = new Dictionary<string, CallTimeout>(StringComparer.Ordinal);
        var disagreements = new List<string>();
        var calls = figures.SelectMany(f => f.Keys).Concat(timeouts.SelectMany(t => t.Keys))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (figures.All(f => f.ContainsKey(call)))
            {
                var each = figures.Select(f => f[call]).ToList();
                var medians = each.Select(f => f.MedianMs).ToList();
                merged[call] = new CallFigures(
                    call,
                    Median(medians),
                    each.Max(f => f.P95Ms),
                    each.Max(f => f.MaxMs),
                    Math.Max(each.Max(f => f.NoiseMs), medians.Max() - medians.Min()),
                    each.Sum(f => f.Samples));
            }
            else if (timeouts.All(t => t.ContainsKey(call)))
            {
                var each = timeouts.Select(t => t[call]).ToList();
                var earliest = each.MinBy(t => t.Ms)!;
                timedOut[call] = earliest with { CommandTimeout = each.All(t => t.CommandTimeout) };
            }
            else
            {
                disagreements.Add(call);
            }
        }
        return new BeforeRuns(runs.Count, merged, timedOut, disagreements);
    }

    public static double Median(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("no samples", nameof(samples));
        var sorted = samples.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Nearest-rank percentile: the smallest sample at or above the fraction.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double fraction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("no samples", nameof(samples));
        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>
    /// Figures for one call from its timed samples in the order they were taken; the even
    /// positions are one A/A half and the odd positions the other.
    /// <paramref name="untimedMs"/> are the call's other answers in the run (the first
    /// pass and the warm-ups): they count toward the slowest answer only, because the
    /// hard limit bounds every answer the user waits for, cold or warm.
    /// </summary>
    public static CallFigures Summarize(
        string call, IReadOnlyList<double> samplesInRunOrder, IReadOnlyList<double>? untimedMs = null)
    {
        ArgumentNullException.ThrowIfNull(samplesInRunOrder);
        if (samplesInRunOrder.Count < 2)
            throw new ArgumentException("an A/A figure needs at least two samples", nameof(samplesInRunOrder));
        var a = samplesInRunOrder.Where((_, i) => i % 2 == 0).ToArray();
        var b = samplesInRunOrder.Where((_, i) => i % 2 == 1).ToArray();
        return new CallFigures(
            call,
            Median(samplesInRunOrder),
            Percentile(samplesInRunOrder, 0.95),
            samplesInRunOrder.Concat(untimedMs ?? []).Max(),
            Math.Abs(Median(a) - Median(b)),
            samplesInRunOrder.Count);
    }

    /// <summary>The printed line for one call; <see cref="Parse"/> reads it back.</summary>
    public static string Format(CallFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} {figures.Call,-34} median={figures.MedianMs:F1}ms p95={figures.P95Ms:F1}ms " +
            $"max={figures.MaxMs:F1}ms noise={figures.NoiseMs:F1}ms n={figures.Samples}");
    }

    /// <summary>
    /// The figure lines in <paramref name="lines"/> (a saved run's output, indented or
    /// not), keyed by call. Lines without a median, such as verdicts and notes, are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, CallFigures> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new Dictionary<string, CallFigures>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var at = line.IndexOf(Prefix, StringComparison.Ordinal);
            if (at < 0) continue;
            var tokens = line[(at + Prefix.Length)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var values = tokens.Skip(1)
                .Select(t => t.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
            if (tokens.Length == 0 || !values.ContainsKey("median")) continue;

            var call = tokens[0];
            result[call] = new CallFigures(
                call, Ms(Value("median")), Ms(Value("p95")), Ms(Value("max")), Ms(Value("noise")),
                int.Parse(Value("n"), CultureInfo.InvariantCulture));

            string Value(string key) => values.TryGetValue(key, out var v) ? v
                : throw new FormatException(
                    $"the figure line for {call} has no {key}=; it was saved by an older run, so record it again");
        }
        return result;

        static double Ms(string value) =>
            double.Parse(value.EndsWith("ms", StringComparison.Ordinal) ? value[..^2] : value,
                CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Refuses before runs that are not separate runs: two paths naming the same file, or
    /// two files whose figure and timeout lines are identical. Either would give a
    /// between-run spread of zero and let the gate fall back to within-run noise alone.
    /// </summary>
    /// <exception cref="ArgumentException">Two of the runs are the same run.</exception>
    public static void EnsureSeparateBeforeRuns(IReadOnlyList<string> paths, IReadOnlyList<IReadOnlyList<string>> runs)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(runs);
        if (paths.Count != runs.Count)
            throw new ArgumentException($"{paths.Count} paths for {runs.Count} runs", nameof(runs));

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var resolved = paths.Select(Path.GetFullPath).ToList();
        var figureLines = runs.Select(FigureLines).ToList();
        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                if (pathComparer.Equals(resolved[i], resolved[j]))
                    throw new ArgumentException(
                        $"before runs '{paths[i]}' and '{paths[j]}' are the same file; the gate needs separate runs",
                        nameof(paths));
                if (figureLines[i] == figureLines[j])
                    throw new ArgumentException(
                        $"before runs '{paths[i]}' and '{paths[j]}' have identical figure lines, so one is a copy " +
                        "of the other; the gate needs separate runs",
                        nameof(runs));
            }
        }

        // A run's figures and timeouts as the gate reads them, in a fixed order, so that
        // other lines and line order do not make a copy look like a separate run.
        static string FigureLines(IReadOnlyList<string> run) => string.Join('\n',
            Parse(run).Values.OrderBy(f => f.Call, StringComparer.Ordinal).Select(Format)
                .Concat(ParseTimeouts(run).Values.OrderBy(t => t.Call, StringComparer.Ordinal).Select(FormatTimeout)));
    }

    /// <summary>
    /// The verdict for one call. Both medians are taken end to end by adding
    /// <paramref name="roundTripMs"/>, the network round trip an in-process host does not
    /// pay; the noise is the before side's.
    /// </summary>
    public static Judgement Judge(CallFigures before, CallFigures after, double roundTripMs)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var was = before.MedianMs + roundTripMs;
        var now = after.MedianMs + roundTripMs;
        var noise = before.NoiseMs;

        Verdict verdict;
        string rule;
        // Only a before median clearly under the mark gets the lenient rule; one within
        // noise of it is judged as over, so noise cannot pick the rule.
        if (was < NoticeableMs - noise)
        {
            verdict = now < NoticeableMs - noise ? Verdict.Pass
                : now <= NoticeableMs + noise ? Verdict.Borderline
                : Verdict.Fail;
            rule = string.Create(CultureInfo.InvariantCulture,
                $"mark={NoticeableMs:F0}ms+-{noise:F1}ms");
        }
        else
        {
            var allowed = was + Math.Max(AllowedGrowth * was, noise);
            verdict = now <= allowed ? Verdict.Pass : Verdict.Fail;
            rule = string.Create(CultureInfo.InvariantCulture, $"allowed={allowed:F1}ms");
        }

        return new Judgement(after.Call, verdict, string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} verdict {after.Call} before={was:F1}ms after={now:F1}ms {rule} " +
            $"rtt={roundTripMs:F1}ms -> {Word(verdict)}"));
    }

    /// <summary>
    /// Judge an after run against the before runs. Every call on either side gets a
    /// verdict, and these fail: a gate with fewer than two before runs (no between-run
    /// noise), a call the before runs never measured or disagree on, a before call the
    /// after run lacks, a call that times out after, and a call that timed out before
    /// unless its timeout was confirmed and it now answers within
    /// <see cref="TimedOutBeforeLimitMs"/>.
    /// </summary>
    public static IReadOnlyList<Judgement> Gate(
        BeforeRuns before,
        IReadOnlyList<CallFigures> after,
        IReadOnlyList<CallTimeout> afterTimeouts,
        double roundTripMs)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(afterTimeouts);

        var judgements = new List<Judgement>();
        if (before.Runs < 2)
            judgements.Add(new Judgement("(before runs)", Verdict.Fail,
                $"{Prefix} verdict the gate needs two before runs for its between-run noise, " +
                $"got {before.Runs} -> fail"));

        foreach (var a in after)
        {
            if (before.Figures.TryGetValue(a.Call, out var b))
                judgements.Add(Judge(b, a, roundTripMs));
            else if (before.Timeouts.TryGetValue(a.Call, out var t))
                judgements.Add(JudgeTimedOutBefore(t, a, roundTripMs));
            else if (before.Disagreements.Contains(a.Call, StringComparer.Ordinal))
                judgements.Add(new Judgement(a.Call, Verdict.Fail,
                    $"{Prefix} verdict {a.Call} the before runs disagree on it -> fail"));
            else
                judgements.Add(new Judgement(a.Call, Verdict.Fail,
                    $"{Prefix} verdict {a.Call} has no before figure -> fail"));
        }

        judgements.AddRange(afterTimeouts.Select(t => new Judgement(t.Call, Verdict.Fail,
            string.Create(CultureInfo.InvariantCulture,
                $"{Prefix} verdict {t.Call} after=timeout({t.Ms:F1}ms) -> fail"))));

        var inAfter = after.Select(a => a.Call).Concat(afterTimeouts.Select(t => t.Call))
            .ToHashSet(StringComparer.Ordinal);
        var beforeCalls = before.Figures.Keys.Concat(before.Timeouts.Keys).Concat(before.Disagreements)
            .Order(StringComparer.Ordinal);
        judgements.AddRange(beforeCalls.Where(c => !inAfter.Contains(c)).Select(c => new Judgement(c, Verdict.Fail,
            $"{Prefix} verdict {c} is in the before runs but not in the after run -> fail")));
        return judgements;
    }

    /// <summary>
    /// A call that timed out before: its before figure is only a lower bound, so it is
    /// held to <see cref="TimedOutBeforeLimitMs"/> end to end, and only when the before
    /// failure was confirmed as the database command timeout.
    /// </summary>
    private static Judgement JudgeTimedOutBefore(CallTimeout before, CallFigures after, double roundTripMs)
    {
        // The limit is on every answer, untimed ones included, so the slowest is held to it.
        var slowest = after.MaxMs + roundTripMs;
        var verdict = before.CommandTimeout && slowest <= TimedOutBeforeLimitMs ? Verdict.Pass : Verdict.Fail;
        var cause = before.CommandTimeout ? CommandTimeoutCause : "unconfirmed";
        return new Judgement(after.Call, verdict, string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} verdict {after.Call} before=timeout({before.Ms:F1}ms,{cause}) " +
            $"after-median={after.MedianMs + roundTripMs:F1}ms after-max={slowest:F1}ms " +
            $"limit={TimedOutBeforeLimitMs:F0}ms rtt={roundTripMs:F1}ms -> {Word(verdict)}"));
    }

    private static string Word(Verdict verdict) => verdict.ToString().ToLowerInvariant();

    /// <summary>
    /// The lines in a <c>list_transactions</c> answer. Streamable HTTP answers a request
    /// as one SSE event whose data is the JSON-RPC response; the tool's result is the
    /// JSON text of its first content block.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The request failed (a JSON-RPC error) or the tool did (a result flagged
    /// <c>isError</c>, as a tool's exception comes back): the call has no answer to
    /// count, and read as zero rows it would pass for an empty listing.
    /// </exception>
    public static int McpLineCount(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var json = body.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l[5..].Trim())
            .LastOrDefault() ?? body;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException("MCP error: " + error.GetRawText());
        var result = doc.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            throw new InvalidOperationException("MCP tool error: " + text);

        using var inner = JsonDocument.Parse(text);
        return inner.RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "lines", StringComparison.OrdinalIgnoreCase))
            .Value.GetArrayLength();
    }
}
