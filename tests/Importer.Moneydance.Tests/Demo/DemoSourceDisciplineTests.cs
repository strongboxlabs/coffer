using System.Text.RegularExpressions;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The Demo generator reads no clock, draws from no runtime generator and does no
/// binary-fraction or hash-seeded arithmetic, so the same <see cref="DemoSpec"/> gives the
/// same bytes on every machine.
/// </summary>
/// <remarks>
/// <para>Asserted against the SOURCE of <c>src/Api/Provisioning/Demo/</c>, as
/// <c>ClockDisciplineTests</c> asserts the API's clock reads: the edit that adds one
/// is the moment to catch it, and a golden hash would only say that the bytes
/// moved. Lines that start with <c>//</c> (doc comments too) are skipped, so a
/// comment can say what not to do; everything else is matched as text, so a banned
/// word in a string or a trailing comment fails too, which is the safe side.</para>
/// <para>The seeding code, which writes timestamps and so needs the clock, lives
/// outside this folder, held to a rule of its own: it takes no date from the clock.</para>
/// </remarks>
public sealed class DemoSourceDisciplineTests
{
    private static readonly (string Rule, Regex Pattern)[] Banned =
    [
        ("AsOf is the only now: no TimeProvider", new Regex(@"\bTimeProvider\b", RegexOptions.Compiled)),
        ("AsOf is the only now: no wall clock",
            new Regex(@"\bDate(?:Time(?:Offset)?|Only)\s*\.\s*(?:UtcNow|Now|Today)\b", RegexOptions.Compiled)),
        ("AsOf is the only now: no elapsed-time clock",
            new Regex(@"\bStopwatch\b|\bEnvironment\s*\.\s*TickCount", RegexOptions.Compiled)),
        ("draw from DemoRng streams, not System.Random", new Regex(@"\bRandom\b", RegexOptions.Compiled)),
        ("draw from DemoRng streams, not the cryptographic generator",
            new Regex(@"\bRandomNumberGenerator\b", RegexOptions.Compiled)),
        ("ids come from DemoIds, not a runtime Guid",
            new Regex(@"\bGuid\s*\.\s*(?:NewGuid|CreateVersion7)\b", RegexOptions.Compiled)),
        ("decimal and long only: no double", new Regex(@"\bdouble\b|\bDouble\b", RegexOptions.Compiled)),
        ("decimal and long only: no float", new Regex(@"\bfloat\b|\bSystem\s*\.\s*Single\b", RegexOptions.Compiled)),
        ("no Math.*: its functions are double, and decimal powers are repeated multiplication",
            new Regex(@"\bMath(?:F)?\s*\.", RegexOptions.Compiled)),
        ("no GetHashCode: it is seeded per process", new Regex(@"\bGetHashCode\b", RegexOptions.Compiled)),
    ];

    [Fact]
    public void The_generator_reads_no_clock_and_uses_no_runtime_randomness_or_binary_fractions()
    {
        var root = Path.Combine(RepoRoot(), "src", "Api", "Provisioning", "Demo");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToList();

        // Fail closed: a wrong root would scan nothing and pass.
        Assert.True(files.Count >= 30, $"scanned only {files.Count} files under {root}");
        Assert.Contains(files, f => f.EndsWith("DemoExportGenerator.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Replace('\\', '/').Contains("/Household/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Replace('\\', '/').Contains("/Securities/", StringComparison.Ordinal));

        var violations = files.SelectMany(f => Violations(
            Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText(f))).ToList();
        Assert.True(violations.Count == 0, "The Demo generator must be a pure function of its spec:\n"
            + string.Join("\n", violations));
    }

    /// <summary>Each rule bites on the thing it bans, and a comment is not code.</summary>
    [Theory]
    [InlineData("var now = DateTime.UtcNow;")]
    [InlineData("var today = DateOnly.FromDateTime(DateTime.Now);")]
    [InlineData("var t = DateTimeOffset.UtcNow;")]
    [InlineData("private readonly TimeProvider _clock;")]
    [InlineData("var r = new Random(1);")]
    [InlineData("var r = Random.Shared.Next();")]
    [InlineData("var x = RandomNumberGenerator.GetInt32(10);")]
    [InlineData("var g = Guid.NewGuid();")]
    [InlineData("var g = Guid.CreateVersion7();")]
    [InlineData("double rate = 0.05;")]
    [InlineData("var x = (float)y;")]
    [InlineData("var p = Math.Pow(1.01, 12);")]
    [InlineData("var m = Math.Round(x);")]
    [InlineData("var h = key.GetHashCode();")]
    [InlineData("var s = Stopwatch.StartNew();")]
    public void Each_banned_read_is_caught(string line)
    {
        Assert.NotEmpty(Violations("probe.cs", "namespace X;\n" + line + "\n"));
        Assert.Empty(Violations("probe.cs", "namespace X;\n// " + line + "\n/// " + line + "\n"));
    }

    /// <summary>What the seeding code may not do with the clock it holds: take a date from
    /// it. It stamps times with the clock, and computes every date from the Demo's AsOf.</summary>
    private static readonly (string Rule, Regex Pattern)[] SeedingBanned =
    [
        ("dates come from AsOf: no date from the injected clock",
            new Regex(@"\bGetUtcNow\s*\(\s*\)\s*\.\s*(?:(?:Utc)?DateTime\s*\.\s*)?Date\b", RegexOptions.Compiled)),
        ("dates come from AsOf: no local clock", new Regex(@"\bGetLocalNow\b", RegexOptions.Compiled)),
        ("dates come from AsOf: no date from a timestamp",
            new Regex(@"\bDateOnly\s*\.\s*FromDateTime\b", RegexOptions.Compiled)),
        ("dates come from AsOf: no wall clock",
            new Regex(@"\bDate(?:Time(?:Offset)?|Only)\s*\.\s*(?:UtcNow|Now|Today)\b", RegexOptions.Compiled)),
        ("dates come from AsOf: not the register's today",
            new Regex(@"\bScheduledBoundary\s*\.\s*(?:Today|ScheduledFrom)\b", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// The only lines of the seeding code that read the clock, by file: the ones that stamp
    /// the marker's times. A date can be built from any reading of the clock (its year,
    /// month and day), so a reading anywhere else is refused whatever is done with it; the
    /// steps hand the clock to the writers, which stamp their own times.
    /// </summary>
    private static readonly (string File, string Line)[] ClockStamps =
    [
        ("DemoSeeder.cs", "private DateTime Now() => _clock.GetUtcNow().UtcDateTime;"),
        ("DemoMarkerSweep.cs", "_clock.GetUtcNow().UtcDateTime,"),
    ];

    private static readonly Regex ClockRead = new(
        @"\bGetUtcNow\b|\bGetTimestamp\b|\bTimeProvider\s*\.\s*System\b", RegexOptions.Compiled);

    /// <summary>
    /// The seeding code (<c>src/Api/Provisioning/DemoSeeding</c>, the seeder's steps
    /// among it) takes no date from the clock: every date it computes comes from the
    /// Demo's AsOf, so a Demo added on a given day is seeded the same whenever it runs.
    /// </summary>
    [Fact]
    public void The_seeding_takes_every_date_from_AsOf_never_from_the_clock()
    {
        var root = Path.Combine(RepoRoot(), "src", "Api", "Provisioning", "DemoSeeding");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToList();

        // Fail closed: a wrong root would scan nothing and pass, and a stamp that moved
        // would leave its allowance standing for some other line.
        Assert.Contains(files, f => f.EndsWith("DemoSeeder.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("DemoMarkerSweep.cs", StringComparison.Ordinal));
        foreach (var (file, line) in ClockStamps)
            Assert.True(
                files.Where(f => Path.GetFileName(f) == file)
                    .Sum(f => File.ReadAllLines(f).Count(l => l.Trim() == line)) == 1,
                $"{file} no longer stamps with exactly one line '{line}'");

        var violations = files.SelectMany(f => SeedingViolations(
            Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText(f))).ToList();
        Assert.True(violations.Count == 0, "The Demo seeding computes its dates from AsOf:\n"
            + string.Join("\n", violations));
    }

    /// <summary>Each seeding rule bites on the read it bans, a comment is not code, and the
    /// clock is read only on a stamping line, in its own file.</summary>
    [Theory]
    [InlineData("var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);")]
    [InlineData("var today = run.Clock.GetUtcNow().Date;")]
    [InlineData("var today = _clock.GetUtcNow().UtcDateTime.Date;")]
    [InlineData("var today = _clock.GetLocalNow();")]
    [InlineData("var today = DateTime.Today;")]
    [InlineData("var today = ScheduledBoundary.Today(null, _clock);")]
    [InlineData("var now = run.Clock.GetUtcNow(); var start = new DateOnly(now.Year, now.Month, now.Day);")]
    [InlineData("var at = _clock.GetUtcNow().UtcDateTime;")]
    [InlineData("var now = TimeProvider.System;")]
    [InlineData("private DateTime Now() => _clock.GetUtcNow().UtcDateTime;")]
    public void Each_seeding_date_read_is_caught(string line)
    {
        Assert.NotEmpty(SeedingViolations("src/Api/Provisioning/DemoSeeding/probe.cs", "namespace X;\n" + line + "\n"));
        Assert.Empty(SeedingViolations("src/Api/Provisioning/DemoSeeding/probe.cs", "namespace X;\n// " + line + "\n"));
    }

    /// <summary>A stamping line is allowed in its own file only.</summary>
    [Fact]
    public void A_stamp_is_allowed_only_where_it_is_listed()
    {
        var (file, line) = ClockStamps[0];
        Assert.Empty(SeedingViolations("src/Api/Provisioning/DemoSeeding/" + file, "namespace X;\n    " + line + "\n"));
        Assert.NotEmpty(SeedingViolations("src/Api/Provisioning/DemoSeeding/DemoLedgerSteps.cs", "namespace X;\n    " + line + "\n"));
    }

    private static IEnumerable<string> SeedingViolations(string relative, string source)
    {
        foreach (var violation in Violations(relative, source, SeedingBanned)) yield return violation;

        var file = Path.GetFileName(relative);
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal) || !ClockRead.IsMatch(line)) continue;
            if (ClockStamps.Contains((file, line))) continue;
            yield return $"{relative}:{i + 1}: dates come from AsOf: the clock is read only where the marker is stamped\n    {line}";
        }
    }

    private static IEnumerable<string> Violations(string relative, string source) =>
        Violations(relative, source, Banned);

    private static IEnumerable<string> Violations(string relative, string source, (string Rule, Regex Pattern)[] banned)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var (rule, pattern) in banned)
            {
                if (pattern.IsMatch(line)) yield return $"{relative}:{i + 1}: {rule}\n    {line.Trim()}";
            }
        }
    }

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
        throw new DirectoryNotFoundException($"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
