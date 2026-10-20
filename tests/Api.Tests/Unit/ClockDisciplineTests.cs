using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Everything in src/Api reads time through the injected <see cref="TimeProvider"/>:
/// the wall clock, elapsed time and timers alike.
/// </summary>
/// <remarks>
/// <para>Until 2026-10-13 the API read the wall clock directly in 147 places, and a
/// few of those reads were not even the process clock: <c>DateTime.UtcNow</c> inside
/// an EF lambda is translated to the database's <c>now()</c>, so the session idle
/// timeout compared a Postgres timestamp with a process one. No test could move
/// "now", so a due date, an expiry or a month boundary was tested against whatever
/// day the suite happened to run on, or not at all.</para>
///
/// <para>Asserted against the SOURCE, like <see cref="Db.WriteTransactionBoundaryTests"/>:
/// a stray clock read has no symptom until the day it matters, and the edit that adds
/// one is the moment to catch it. Lines that start with <c>//</c> are skipped so a
/// comment can still say what not to do; everything else is matched as text, so a
/// read hidden in a string or a trailing comment fails too, which is the safe side.</para>
///
/// <para>The one place allowed to name the system clock is its registration in
/// Program.cs. A timer or delay must be handed the clock, which the check reads as an
/// argument named <c>clock</c> or <c>_clock</c>, the convention every injection here
/// follows.</para>
/// </remarks>
public sealed class ClockDisciplineTests
{
    private const string Registration = "builder.Services.AddSingleton(TimeProvider.System);";

    private static readonly (string Rule, Regex Pattern)[] Banned =
    [
        ("read the injected TimeProvider, not the static wall clock",
            new Regex(@"\bDateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now|Today)\b", RegexOptions.Compiled)),
        ("import the injected TimeProvider, not the static wall clock",
            new Regex(@"using\s+static\s+System\.DateTime(?:Offset)?\s*;", RegexOptions.Compiled)),
        ("inject TimeProvider; TimeProvider.System is named only by its registration in Program.cs",
            new Regex(@"\bTimeProvider\s*\.\s*System\b", RegexOptions.Compiled)),
        ("time elapsed spans with clock.GetTimestamp()/GetElapsedTime(), not Stopwatch",
            new Regex(@"\bStopwatch\b", RegexOptions.Compiled)),
        ("time elapsed spans with clock.GetTimestamp(), not Environment.TickCount",
            new Regex(@"\bEnvironment\s*\.\s*TickCount(?:64)?\b", RegexOptions.Compiled)),
        ("create timers with clock.CreateTimer(...), not System.Threading.Timer or System.Timers.Timer",
            new Regex(@"\bnew\s+(?:System\.Threading\.|System\.Timers\.)?Timer\s*\(", RegexOptions.Compiled)),
        ("a CancellationTokenSource deadline needs the clock: new CancellationTokenSource(delay, clock)",
            new Regex(@"\.\s*CancelAfter\s*\(", RegexOptions.Compiled)),
    ];

    // Each of these must be handed the clock somewhere in its argument list.
    private static readonly Regex NeedsClock = new(
        @"\bTask\s*\.\s*Delay\s*\(|\bnew\s+PeriodicTimer\s*\(|\bnew\s+CancellationTokenSource\s*\((?!\s*\))",
        RegexOptions.Compiled);

    private static readonly Regex ClockArgument = new(@"\b_?clock\b", RegexOptions.Compiled);

    [Fact]
    public void Src_Api_reads_time_only_through_the_injected_clock()
    {
        var root = Path.Combine(RepoRoot(), "src", "Api");
        var files = SourceFiles(root);

        // Fail closed: a wrong root or an over-eager exclusion would scan nothing and pass.
        Assert.True(files.Count > 300,
            $"Scanned only {files.Count} files under {root}; the guard is not looking at the API.");
        Assert.Contains(files, f => f.EndsWith("Program.cs", StringComparison.Ordinal));

        var violations = new List<string>();
        var registrations = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            var code = CodeOnly(File.ReadAllText(file));
            var lines = code.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (relative == "src/Api/Program.cs" && line.Trim() == Registration)
                {
                    registrations++;
                    continue;
                }
                foreach (var (rule, pattern) in Banned)
                {
                    if (pattern.IsMatch(line))
                        violations.Add($"{relative}:{i + 1}: {rule}\n    {line.Trim()}");
                }
            }

            foreach (Match m in NeedsClock.Matches(code))
            {
                var call = Statement(code, m.Index);
                if (!ClockArgument.IsMatch(call))
                {
                    var lineNo = code.AsSpan(0, m.Index).Count('\n') + 1;
                    violations.Add(
                        $"{relative}:{lineNo}: pass the injected clock (Task.Delay(delay, clock, ct), "
                        + $"new PeriodicTimer(period, clock), new CancellationTokenSource(delay, clock))\n    {call.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Time read around the injected TimeProvider:\n" + string.Join("\n", violations));
        Assert.True(registrations == 1,
            $"Expected exactly one '{Registration}' in src/Api/Program.cs, found {registrations}. "
            + "The system clock is registered once and injected everywhere else.");
    }

    /// <summary>
    /// The source with every line that starts with <c>//</c> blanked, keeping line
    /// numbers. Comments may name the static clock to say why not to use it.
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    /// <summary>From <paramref name="start"/> to the end of the statement.</summary>
    private static string Statement(string code, int start)
    {
        var end = code.IndexOf(';', start);
        return end < 0 ? code[start..] : code[start..(end + 1)];
    }

    private static List<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.StartsWith("bin/", StringComparison.Ordinal)
                    && !rel.StartsWith("obj/", StringComparison.Ordinal);
            })
            .ToList();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
