using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// A test that writes an input of the memo-and-tags rule past the writers settles the
/// stored presentation after it: the backfill, a recompute, or the stored-equals-computed
/// check.
/// </summary>
/// <remarks>
/// <para>Raw SQL and set-based updates reach no writer, so a raw insert of a header, a leg
/// or a tag assignment, an update of an input column or a delete of a tag assignment
/// leaves the legs it touches with no stored presentation, or a stale one: a state no
/// writer leaves behind. Every reader would then be tested against data the app cannot
/// make. The fixture's teardown check finds what such a test left in the shared test
/// database; this guard names the write that did it, and covers the databases a test
/// builds for itself, which the teardown never sees. A tracked save on a context without
/// the production interceptors is the one form it cannot see; the teardown check covers it.</para>
/// <para>Scope: every test project under <c>tests/</c>. Each write must be followed, in its own method, by a call
/// that settles the stored presentation. A seed whose writes are spread over helpers is
/// listed with the one entry point that settles them all, which must still do so.
/// Exempt, by file: tests whose writes run on a schema staged before migration 264, which
/// has no stored presentation, a test that makes the presentation drift on purpose, and
/// the two scanners that spell the patterns out.</para>
/// </remarks>
public sealed partial class StoredNotesTestSeedTests
{
    /// <summary>Files whose inserts need nothing after them, each with its reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["tests/Api.Tests/Integration/Transactions/HeaderOriginalsUpgradeTests.cs"] =
            "stages the schema before migration 230",
        ["tests/Api.Tests/Integration/Reminders/ReminderOccurrenceUniquenessUpgradeTests.cs"] =
            "stages the schema before migration 218",
        ["tests/Api.Tests/Integration/Reminders/ReminderEstimateUpgradeTests.cs"] =
            "stages the schema before migration 220",
        ["tests/Api.Tests/Integration/Transactions/OriginatingAccountBackfillTests.cs"] =
            "stages the schema before migration 263",
        ["tests/Api.Tests/Integration/Stress/OriginatingAccountBackfillCostTests.cs"] =
            "stages the schema before migration 263 to time its backfill",
        ["tests/Api.Tests/Integration/Transactions/Migration264UpgradeTests.cs"] =
            "stages the schema before migration 260 and seeds what the earlier release stored, for 264's data steps",
        ["tests/Api.Tests/Integration/Stress/Migration264CostTests.cs"] =
            "stages the schema before migration 264 and puts the earlier release's placement back, to time 264",
        ["tests/Importer.Moneydance.Tests/Db/Migration264VintageTests.cs"] =
            "rewinds a database staged before migration 260 to each importer vintage, for 264's data steps",
        ["tests/Importer.Moneydance.Tests/Db/Migration263InKindSaleTests.cs"] =
            "replays an earlier release's post-import step on a database staged before migration 260, for 263's in-kind rebuild",
        ["tests/Api.Tests/Integration/Infra/TxnHeaderSeed.cs"] =
            "inserts a header with no legs, which has no stored presentation",
        ["tests/Api.Tests/Integration/Transactions/StoredNotesConsistencyTests.cs"] =
            "makes the stored presentation drift on purpose, for the check that finds it; each test puts it right before it ends",
        ["tests/Api.Tests/Unit/StoredNotesWriteCatalogTests.cs"] =
            "spells write patterns out as sample text for its own matchers",
        ["tests/Api.Tests/Unit/AnnotationWriteCatalogTests.cs"] =
            "spells write patterns out as sample text for its own matchers",
        ["tests/Api.Tests/Unit/TestSeedOriginatingAccountTests.cs"] =
            "spells insert patterns out as sample text for its own matchers",
    };

    /// <summary>Files whose inserts sit in helpers, by the entry point that settles them all.</summary>
    private static readonly Dictionary<string, string> SettledBy = new(StringComparer.Ordinal)
    {
        ["tests/Api.Tests/Integration/Infra/StressLedger.cs"] = "SeedLedgerAsync",
        ["tests/Api.Tests/Integration/Transactions/StoredNotesBackfillTests.cs"] =
            "The_backfill_stores_what_the_rule_computes_and_converges_after_a_partial_run",
    };

    /// <summary>
    /// A raw write of an input: an insert into an input table, an update that sets an
    /// input column (as SQL or as a set-based <c>SetProperty</c>), or a delete of a tag
    /// assignment.
    /// </summary>
    [GeneratedRegex(
        @"\bINSERT\s+INTO\s+(?:public\.)?(?:txn_headers|txn_legs|txn_header_tags|txn_leg_tags)\b"
        + @"|\bUPDATE\s+(?:public\.)?txn_headers\s+SET\b(?:(?!\bWHERE\b)[^;""])*?\b(?:memo|action|originating_account_id)\s*="
        + @"|\bUPDATE\s+(?:public\.)?txn_legs\s+SET\b(?:(?!\bWHERE\b)[^;""])*?\b(?:account_id|posting_index|leg_memo|header_id)\s*="
        + @"|\bDELETE\s+FROM\s+(?:public\.)?(?:txn_header_tags|txn_leg_tags)\b"
        + @"|\bSetProperty\(\s*\w+\s*=>\s*\w+\.(?:Memo|Action|OriginatingAccountId|AccountId|PostingIndex|LegMemo|HeaderId)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex InputWrite();

    [GeneratedRegex(@"\b(?:BackfillNotesAsync|RecomputeAllNotesAsync|RecomputeNotesAsync|LegNotesBackfill\.RunAsync|LegNotes\.RecomputeAsync|LegNotes\.DifferencesAsync|AssertStoredAsync)\s*\(")]
    private static partial Regex Settles();

    [GeneratedRegex(@"(?:public|private|internal|protected)[^;{}=]*?\s(\w+)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex MemberHeader();

    [Fact]
    public void Every_raw_insert_of_an_input_is_followed_by_the_backfill_or_the_check()
    {
        var files = TestFiles();
        // Fail closed: a wrong root would scan nothing and pass, and every test project counts.
        Assert.True(files.Count > 300, $"Scanned only {files.Count} test files; the guard is not looking at tests/.");
        foreach (var project in new[] { "tests/Api.Tests/", "tests/Importer.Moneydance.Tests/" })
            Assert.True(files.Count(f => f.Relative.StartsWith(project, StringComparison.Ordinal)) > 20,
                $"the guard scans almost nothing under {project}");

        var violations = new List<string>();
        var inserting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, code) in files)
        {
            var inserts = InputWrite().Matches(code);
            if (inserts.Count > 0) inserting.Add(relative);
            if (inserts.Count == 0 || Exempt.ContainsKey(relative)) continue;

            if (SettledBy.TryGetValue(relative, out var entry))
            {
                var body = Body(code, entry);
                if (body is null)
                    violations.Add($"{relative}: listed as settled by {entry}, which it no longer has");
                else if (!Settles().IsMatch(body))
                    violations.Add($"{relative}:{entry} no longer settles the stored presentation its helpers' inserts leave");
                continue;
            }

            foreach (Match insert in inserts)
            {
                var (name, end) = Enclosing(code, insert.Index);
                if (Settles().IsMatch(code[insert.Index..end])) continue;
                var line = code.AsSpan(0, insert.Index).Count('\n') + 1;
                violations.Add($"{relative}:{line} ({name}): a raw write of an input with no backfill or stored-equals-computed check after it");
            }
        }
        Assert.True(violations.Count == 0, "Raw seeds that leave the stored presentation unsettled:\n" + string.Join("\n", violations));

        var stale = Exempt.Keys.Concat(SettledBy.Keys).Where(k => !inserting.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "These entries no longer insert an input; remove them:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void The_scan_tells_a_settled_insert_from_an_unsettled_one()
    {
        const string code = """
            public sealed class Sample
            {
                public async Task SettledAsync()
                {
                    await Exec("INSERT INTO txn_legs (id) VALUES (1)");
                    await ledger.RecomputeAllNotesAsync();
                }

                public async Task SettledBeforeAsync()
                {
                    await ledger.RecomputeAllNotesAsync();
                    await Exec("INSERT INTO public.txn_header_tags (header_id) VALUES (1)");
                }

                private async Task NextAsync() { }
            }
            """;
        var inserts = InputWrite().Matches(code).ToList();
        Assert.Equal(2, inserts.Count);
        var (first, firstEnd) = Enclosing(code, inserts[0].Index);
        Assert.Equal("SettledAsync", first);
        Assert.Matches(Settles(), code[inserts[0].Index..firstEnd]);
        var (second, secondEnd) = Enclosing(code, inserts[1].Index);
        Assert.Equal("SettledBeforeAsync", second);
        Assert.DoesNotMatch(Settles(), code[inserts[1].Index..secondEnd]);
        Assert.DoesNotMatch(InputWrite(), "INSERT INTO txn_legs_archive (id) VALUES (1)");

        // Updates of an input column and deletes of a tag assignment count; an update of
        // anything else does not, even when its WHERE names an input column.
        Assert.Matches(InputWrite(), "UPDATE txn_headers SET action = 'misc' WHERE id = 1");
        Assert.Matches(InputWrite(), "UPDATE txn_legs SET amount = 1, leg_memo = 'x' WHERE id = 1");
        Assert.Matches(InputWrite(), "DELETE FROM txn_leg_tags WHERE leg_id = 1");
        Assert.Matches(InputWrite(), "s.SetProperty(h => h.Action, \"buy\")");
        Assert.DoesNotMatch(InputWrite(), "UPDATE txn_legs SET header_total_postings = 2 WHERE header_id = 1");
        Assert.DoesNotMatch(InputWrite(), "UPDATE txn_headers SET is_hidden = TRUE WHERE memo = 'x'");
    }

    /// <summary>The member around <paramref name="index"/>: its name, and its text up to the next member.</summary>
    private static (string Name, int End) Enclosing(string code, int index)
    {
        var headers = MemberHeader().Matches(code).ToList();
        var at = headers.FindLastIndex(h => h.Index <= index);
        var end = at + 1 < headers.Count ? headers[at + 1].Index : code.Length;
        return (at < 0 ? "(none)" : headers[at].Groups[1].Value, end);
    }

    /// <summary>The text of member <paramref name="name"/>, or null if the file has none.</summary>
    private static string? Body(string code, string name)
    {
        var headers = MemberHeader().Matches(code).ToList();
        var at = headers.FindIndex(h => h.Groups[1].Value == name);
        if (at < 0) return null;
        var end = at + 1 < headers.Count ? headers[at + 1].Index : code.Length;
        return code[headers[at].Index..end];
    }

    private static List<(string Relative, string Code)> TestFiles()
    {
        var root = RepoRoot();
        var testsRoot = Path.Combine(root, "tests");
        return Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !f.Relative.Contains("/bin/", StringComparison.Ordinal)
                     && !f.Relative.Contains("/obj/", StringComparison.Ordinal)
                     // This file spells the patterns out.
                     && !f.Relative.EndsWith("/StoredNotesTestSeedTests.cs", StringComparison.Ordinal))
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
