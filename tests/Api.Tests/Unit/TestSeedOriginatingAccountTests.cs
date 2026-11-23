using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Every header a test seeds names its originating account (migration 263).
/// </summary>
/// <remarks>
/// <para>The column is nullable, because a header with no legs names no account, so a
/// raw <c>INSERT INTO txn_headers</c> that leaves it out still succeeds — and seeds a
/// header with legs and no owner, a state no writer produces. Every reader that takes
/// its perspective from the owner would then be tested against data the app cannot
/// make. So the seed must say what it means: the owner, or an explicit NULL for a
/// header that has no legs.</para>
///
/// <para>Headers built in C# cannot leave it out: the API's <c>TxnHeaderRow</c> is built
/// only by factories that take it (its constructors are private, which the compiler
/// enforces), and the importer's positional record takes it as a required argument.
/// This guard covers the one way around both, the SQL string, and checks that the
/// constructor stays closed.</para>
///
/// <para>Exempt, by file: tests that stage a database BEFORE 263, where the column does
/// not exist and 263 itself fills it.</para>
/// </remarks>
public sealed partial class TestSeedOriginatingAccountTests
{
    /// <summary>Test files whose header inserts run on a schema without the column.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["tests/Api.Tests/Integration/Transactions/HeaderOriginalsUpgradeTests.cs"] =
            "stages the schema before migration 230",
        ["tests/Api.Tests/Integration/Reminders/ReminderOccurrenceUniquenessUpgradeTests.cs"] =
            "stages the schema before migration 218",
        ["tests/Api.Tests/Integration/Reminders/ReminderEstimateUpgradeTests.cs"] =
            "stages the schema before migration 220",
        ["tests/Api.Tests/Integration/Transactions/OriginatingAccountBackfillTests.cs"] =
            "stages the schema before migration 263, which backfills the column under test",
        ["tests/Api.Tests/Integration/Stress/OriginatingAccountBackfillCostTests.cs"] =
            "stages the schema before migration 263 to time its backfill",
        ["tests/Api.Tests/Integration/Transactions/Migration264UpgradeTests.cs"] =
            "stages the schema before migration 260; 263 backfills the column on the way to 264",
        ["tests/Importer.Moneydance.Tests/Db/Migration264VintageTests.cs"] =
            "fires occurrences on a database staged before migration 260; 263 backfills the column",
    };

    [GeneratedRegex(@"INSERT\s+INTO\s+(?:public\.)?txn_headers\b(?<rest>[^;]{0,600})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeaderInsert();

    [GeneratedRegex(@"^\s*\((?<columns>[^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex ColumnList();

    [GeneratedRegex(@"\bnew\s+(?:Coffer\.Api\.Db\.Entities\.)?TxnHeaderRow\s*(?:\(\s*\))?\s*\{")]
    private static partial Regex ObjectInitializer();

    [Fact]
    public void Every_test_seed_of_a_header_names_its_originating_account()
    {
        var files = TestFiles();
        // Fail closed: a wrong root would scan nothing and pass.
        Assert.True(files.Count > 300, $"Scanned only {files.Count} test files; the guard is not looking at tests/.");

        var violations = new List<string>();
        var exemptWithInserts = new HashSet<string>();
        var exemptFullyCovered = new HashSet<string>(Exempt.Keys);
        foreach (var (relative, code) in files)
        {
            foreach (Match insert in HeaderInsert().Matches(code))
            {
                var columns = ColumnList().Match(insert.Groups["rest"].Value);
                var names = columns.Success && columns.Groups["columns"].Value
                    .Split(',').Any(c => c.Trim() == "originating_account_id");
                if (Exempt.ContainsKey(relative))
                {
                    exemptWithInserts.Add(relative);
                    if (!names) exemptFullyCovered.Remove(relative);
                    continue;
                }
                if (!names)
                {
                    var line = code.AsSpan(0, insert.Index).Count('\n') + 1;
                    violations.Add($"{relative}:{line}: INSERT INTO txn_headers without originating_account_id "
                        + "(name the owner, or NULL for a header with no legs)");
                }
            }

            foreach (Match m in ObjectInitializer().Matches(code))
            {
                var line = code.AsSpan(0, m.Index).Count('\n') + 1;
                violations.Add($"{relative}:{line}: a TxnHeaderRow built with an initializer; use a "
                    + "TxnHeaderRow factory, which takes the originating account");
            }
        }

        Assert.True(violations.Count == 0, "Test seeds without an owner:\n" + string.Join("\n", violations));

        var stale = Exempt.Keys
            .Where(k => !exemptWithInserts.Contains(k) || exemptFullyCovered.Contains(k))
            .ToList();
        Assert.True(stale.Count == 0,
            "These exemptions no longer apply (no header insert, or every one names the column); "
            + "remove them:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void The_scan_recognizes_an_insert_without_the_column()
    {
        const string bad = """
            INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at)
            VALUES (@Id, @L, 'manual', 'p', NOW(), NOW());
            """;
        const string good = """
            INSERT INTO public.txn_headers
                (id, ledger_id, origin, posted_at, transacted_at, originating_account_id)
            VALUES (@Id, @L, 'manual', NOW(), NOW(), @Owner);
            """;
        static bool Names(string sql)
        {
            var insert = HeaderInsert().Match(sql);
            Assert.True(insert.Success);
            var columns = ColumnList().Match(insert.Groups["rest"].Value);
            return columns.Success && columns.Groups["columns"].Value
                .Split(',').Any(c => c.Trim() == "originating_account_id");
        }
        Assert.False(Names(bad));
        Assert.True(Names(good));
        Assert.Matches(ObjectInitializer(), "db.Add(new TxnHeaderRow { Id = id });");
        Assert.Matches(ObjectInitializer(), "var h = new TxnHeaderRow() {");
    }

    private static List<(string Relative, string Code)> TestFiles()
    {
        var root = RepoRoot();
        var testsRoot = Path.Combine(root, "tests");
        return Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !f.Relative.Contains("/bin/", StringComparison.Ordinal)
                     && !f.Relative.Contains("/obj/", StringComparison.Ordinal)
                     // These files spell the patterns out: this one, and the write
                     // catalog's self-test vectors, which are source text, not seeds.
                     && !f.Relative.EndsWith("/TestSeedOriginatingAccountTests.cs", StringComparison.Ordinal)
                     && !f.Relative.EndsWith("/Unit/AnnotationWriteCatalogTests.cs", StringComparison.Ordinal))
            .Select(f => (f.Relative, CodeOnly(File.ReadAllText(f.Full))))
            .ToList();
    }

    /// <summary>The source with every line that starts with <c>//</c> blanked, keeping line numbers.</summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
