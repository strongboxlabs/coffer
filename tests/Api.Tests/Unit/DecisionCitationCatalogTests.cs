using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// A decision cited in code names the record it lives in: "ADR-0108 D7", never a bare
/// decision number, and never a number from a list kept outside the repository, in code
/// or in the docs (<see cref="Every_numbered_decision_names_its_ADR"/>). A section cited in
/// code names its document too: "ADR-0030 §2", never a spec kept outside the repository
/// (<see cref="Every_section_citation_names_its_document"/>).
/// </summary>
/// <remarks>
/// <para>Every ADR numbers its own decisions from one, so a bare number names no record: the
/// reader cannot tell which ADR it means, and a number from a list kept outside the
/// repository cannot be looked up at all. A citation is accepted when the run of decision
/// numbers it sits in starts with an ADR reference (<c>ADR-0063 §D7/D8</c>,
/// <c>ADR-0108 D11 Step A, D15</c>, <c>ADR-0068/0081 D2</c>), across a wrapped comment line
/// too. Where no ADR records a rule, the comment states the rule instead of citing it.</para>
///
/// <para>Scanned: every source, test, migration and script file under <c>src</c>,
/// <c>tests</c> and <c>db</c>, comments and strings alike, since test labels cite
/// decisions too. Not scanned for a bare number: <c>docs/decisions</c>, where an ADR
/// refers to its own list. Not a citation: a .NET format specifier (<c>{n:D12}</c>,
/// <c>ToString("D2")</c>) and a QIF date line (<c>D01/05/2024</c>); a number followed by a slash is
/// checked as part of its run, at the run's last number.</para>
/// </remarks>
public sealed class DecisionCitationCatalogTests
{
    private static readonly Regex Citation = new(
        @"(?<![A-Za-z0-9_\-])D\d{1,3}(?![A-Za-z0-9_/])", RegexOptions.Compiled);

    // A format specifier: inside an interpolation hole after its colon, or ToString's argument.
    private static readonly Regex FormatSpecifierBefore = new(
        @"(?:\{[^{}\n]*:|ToString\("")$", RegexOptions.Compiled);

    // A wrapped comment line reads as one line: the break and the next line's comment marker.
    private static readonly Regex CommentWrap = new(
        @"\r?\n[ \t]*(?:///|//|--|\*|#)?[ \t]*", RegexOptions.Compiled);

    // The citations already in the run before this one, back to its first, with their
    // separators: in "ADR-0108 D11 Step A, Step M, D15", everything after the ADR reference.
    private static readonly Regex RunBefore = new(
        @"(?:(?:§?D\d{1,3}(?: [a-z]\b)?|Step [A-Z0-9]\b)(?:\s*(?:,|/|–|—|-|\band\b|\bor\b|&)\s*|\s+))*§?$",
        RegexOptions.Compiled);

    private static readonly Regex AdrReferenceBefore = new(@"ADR-\d{4}(?:/\d{4})*\s*$", RegexOptions.Compiled);

    private static readonly string[] Roots = ["src", "tests", "db"];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".mjs", ".sql", ".sh", ".ps1",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "wwwroot", "coverage", "test-results", "playwright-report",
    };

    /// <summary>Lines that may keep a bare decision number, by file and a fragment of the line
    /// (the text before the number), each with its reason.</summary>
    private static readonly Dictionary<(string File, string Fragment), string> Allowed = new()
    {
        [("db/migrations/127_loan_terms_and_account_opened_on.sql",
            "'once (ON CONFLICT DO NOTHING); Coffer owns it thereafter (")] =
            "a shipped COMMENT ON TABLE, whose text names ADR-0050 two lines up; rewording it would give fresh "
            + "installs a different catalog comment from upgraded ones",
        [("db/migrations/207_system_events_and_notification_subscribers.sql",
            "'info | warning | critical. Orthogonal to topic (")] =
            "a shipped COMMENT ON COLUMN (ADR-0096 D3); rewording it would give fresh installs a different "
            + "catalog comment from upgraded ones",
    };

    // The word "decision" or "resolution" (as in "build resolution") and a number, with an
    // optional letter, on one line or across a wrapped comment line: a list kept outside the
    // repository numbers its decisions and resolutions that way, and nothing here can look
    // one up. An ADR reference before it does not make it a citation, since most ADRs number
    // nothing and an outside number after an ADR names no decision anyone can find; the few
    // ADRs that keep a numbered list are allowed by line.
    private static readonly Regex NumberedDecision = new(
        @"(?<![A-Za-z0-9_\-])(?:[Dd]ecisions?|[Rr]esolutions?)(?:[ \t]+|[ \t]*\r?\n[ \t]*(?:///|//|--|\*|#|>)?[ \t]*)\d{1,3}[a-z]?(?![A-Za-z0-9_])",
        RegexOptions.Compiled);

    private static readonly string[] NumberedDecisionRoots = ["src", "tests", "db", "scripts", "docs", ".github"];

    private static readonly HashSet<string> NumberedDecisionExtensions = new(Extensions, StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".yml", ".yaml",
    };

    /// <summary>References to an ADR's own numbered list, the one kind of numbered decision
    /// a reader can look up, by file and a fragment of the line, each with its reason.</summary>
    private static readonly Dictionary<(string File, string Fragment), string> AllowedNumbered = new()
    {
        [("docs/decisions/0037-snapshots-and-backups.md", "differs from the live database (")] =
            "ADR-0037 cites its own numbered list of locked design decisions",
        [("docs/decisions/0082-per-account-reconciliation-status.md", "travels in the request **body** (")] =
            "ADR-0082 cites its own numbered list of decisions, set out below the line",
        [("docs/decisions/0106-folders-replace-tabs.md", "can no longer be restored (ADR-0037, ")] =
            "ADR-0037 keeps a numbered list of locked design decisions, and this names the ADR and its item",
    };

    /// <summary>
    /// A decision cited by the word and a number names the ADR it lives in, in code and in
    /// the docs alike.
    /// </summary>
    /// <remarks>
    /// The numbering of a list kept outside the repository cannot be looked up, and an
    /// ADR's own numbering means nothing outside it, whatever ADR reference stands before
    /// it. Accepted: only a reference to the numbered list of one of the few ADRs that keep
    /// one (<see cref="AllowedNumbered"/>). Scanned: the source, test, migration, script,
    /// Markdown and workflow files under <c>src</c>, <c>tests</c>, <c>db</c>,
    /// <c>scripts</c>, <c>docs</c> and <c>.github</c>, and the same kinds at the root.
    /// </remarks>
    [Fact]
    public void Every_numbered_decision_names_its_ADR()
    {
        var repo = RepoRoot();
        var files = NumberedDecisionRoots
            .SelectMany(r => SourceFiles(repo, Path.Combine(repo, r), NumberedDecisionExtensions))
            .Concat(Directory.EnumerateFiles(repo, "*", SearchOption.TopDirectoryOnly)
                .Where(f => NumberedDecisionExtensions.Contains(Path.GetExtension(f)))
                .Select(f => f.Replace('\\', '/')))
            .ToList();

        // Fail closed: a wrong root or extension would scan nothing and pass.
        Assert.True(files.Count > 1600, $"Scanned only {files.Count} files.");
        Assert.Contains(files, f => f.EndsWith("docs/decisions/0109-overview-is-an-attention-first-home.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("src/Web/src/routes/ledgers/BudgetPage.tsx", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("src/Web/README.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("scripts/audit-no-raw-sql.sh", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(".github/PULL_REQUEST_TEMPLATE.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(".github/workflows/ci.yml", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(repo, "README.md").Replace('\\', '/'), files);

        var violations = new List<string>();
        var allowedSeen = new HashSet<(string, string)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            foreach (Match m in NumberedDecision.Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var lineEnd = text.IndexOf('\n', m.Index);
                var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                var allowed = AllowedNumbered.Keys.FirstOrDefault(k =>
                    k.File == relative && line.Contains(k.Fragment, StringComparison.Ordinal));
                if (allowed != default)
                {
                    allowedSeen.Add(allowed);
                    continue;
                }
                var lineNo = text.AsSpan(0, m.Index).Count('\n') + 1;
                violations.Add($"{relative}:{lineNo}: {CommentWrap.Replace(m.Value, " ")}\n    {line}");
            }
        }

        Assert.True(violations.Count == 0,
            "A numbered decision names no record anyone here can open. Cite the ADR decision it maps to "
            + "(\"ADR-0109 D10\"), or state the rule where no ADR records it:\n" + string.Join("\n", violations));

        // Fail closed, and the matcher's positive control: every allowed line is still seen.
        var stale = AllowedNumbered.Keys.Where(k => !allowedSeen.Contains(k)).Select(k => $"{k.File}: {k.Fragment}").ToList();
        Assert.True(stale.Count == 0,
            "These allowed lines no longer carry a numbered decision, or the matcher stopped seeing them:\n"
            + string.Join("\n", stale));
    }

    /// <summary>The matcher on lines built here, so this file carries none. An ADR reference
    /// before the number does not excuse it, a wrapped comment line does not hide it, and a
    /// numbered resolution is caught as a numbered decision is.</summary>
    [Theory]
    [InlineData("the month's spend counts them (", "Decision", " 15a).", true)]
    [InlineData("the user can merge into it (", "decision", " 2).", true)]
    [InlineData("see ", "decision", "s 3 and 4", true)]
    [InlineData("the month stays whole (ADR-0099, ", "Decision", " 15a): it", true)]
    [InlineData("see ADR-0109's ", "Decision", " 15b here", true)]
    [InlineData("restored (ADR-0037, ", "decision", " 6).", true)]
    [InlineData("// the month stays whole, as ", "Decision", "\n    // 15a says", true)]
    [InlineData(" * the month stays whole, as ", "Decision", "\r\n * 15a says", true)]
    [InlineData("the month stays whole, as ", "decision", "\n15a says", true)]
    [InlineData("the ", "decision", "-maker", false)]
    [InlineData("a ", "decision", "al point", false)]
    [InlineData("// the last ", "decision", "\n\n// 15 lines down", false)]
    [InlineData("as build ", "resolution", " 7 settled", true)]
    [InlineData("per ", "Resolution", " 12b.", true)]
    [InlineData("see ", "resolution", "s 3 and 4", true)]
    [InlineData("(ADR-0114 D15, build ", "resolution", " 9)", true)]
    [InlineData("// the rule from build ", "resolution", "\n    // 7 says", true)]
    [InlineData("fails operator ", "resolution", "\n-- on the first chunk", false)]
    [InlineData("millisecond ", "resolution", " so it sorts", false)]
    public void The_numbered_decision_matcher_flags_every_number(
        string before, string word, string after, bool flagged)
    {
        Assert.Equal(flagged, NumberedDecision.IsMatch(before + word + after));
    }

    [Fact]
    public void Every_decision_citation_names_its_ADR()
    {
        var repo = RepoRoot();
        var files = Roots.SelectMany(r => SourceFiles(repo, Path.Combine(repo, r))).ToList();

        // Fail closed: a wrong root or an over-eager exclusion would scan nothing and pass.
        Assert.True(files.Count > 1500, $"Scanned only {files.Count} files under {string.Join(", ", Roots)}.");
        Assert.Contains(files, f => f.EndsWith("db/migrations/264_annotation_data.sql", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("src/Web/src/routes/ledgers/register/shell/RegisterRow.tsx", StringComparison.Ordinal));

        var violations = new List<string>();
        var allowedSeen = new HashSet<(string, string)>();
        var cited = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            foreach (Match m in Citation.Matches(text))
            {
                if (IsFormatSpecifier(text, m.Index)) continue;
                if (NamesItsAdr(text, m.Index))
                {
                    cited++;
                    continue;
                }

                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var lineEnd = text.IndexOf('\n', m.Index);
                var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                var allowed = Allowed.Keys.FirstOrDefault(k =>
                    k.File == relative && line.Contains(k.Fragment, StringComparison.Ordinal));
                if (allowed != default)
                {
                    allowedSeen.Add(allowed);
                    continue;
                }
                var lineNo = text.AsSpan(0, m.Index).Count('\n') + 1;
                violations.Add($"{relative}:{lineNo}: {m.Value}\n    {line}");
            }
        }

        // Fail closed: a matcher that stopped seeing citations would find no bare one either.
        Assert.True(cited > 300, $"Found only {cited} citations that name their ADR; the matcher is not seeing them.");
        Assert.True(violations.Count == 0,
            "A bare decision number names no record. Write the ADR it lives in (\"ADR-0108 D7\"), "
            + "or state the rule where no ADR records it:\n" + string.Join("\n", violations));

        var stale = Allowed.Keys.Where(k => !allowedSeen.Contains(k)).Select(k => $"{k.File}: {k.Fragment}").ToList();
        Assert.True(stale.Count == 0,
            "These allowed lines no longer carry a bare decision number; remove them from the list:\n"
            + string.Join("\n", stale));
    }

    // A gap between two words: spaces, or a wrapped comment or quote line with its marker.
    private const string Gap = @"(?:[ \t]+|[ \t]*\r?\n[ \t]*(?:///|//|--|\*|#|>)?[ \t]*)";

    // A section sign or the word "section(s)" before a section number.
    private const string SectionMark = @"(?:§[ \t]*|sections?" + Gap + @"§?[ \t]*)";

    // A section number, with its mark or without.
    private const string SectionNumber = SectionMark + @"?\d+(?:\.\d+)*";

    // A spec and a section number, in any case, three ways: the word (or a file name ending
    // in it, with or without ".md") and the number after it, on one line or across a wrapped
    // comment line; the number written into a name, its parts joined by underscores; and
    // the marked section first, then "of the" and up to three words ending in the word. A
    // spec named only by that word names no document a reader of the repository can open;
    // a section citation names its document instead (ADR-0030 §2,
    // engineering-standards §4.2.2). A letter before the word makes it part of a type name
    // (ReportSpec), not a citation.
    private static readonly Regex SpecSection = new(
        @"(?<![A-Za-z0-9])spec(?:\.md)?(?:'s)?(?:[ \t]*(?=§)|" + Gap + ")" + SectionNumber + @"(?![A-Za-z0-9_])"
        + @"|(?<![A-Za-z0-9])spec_\d+(?:_\d+)*(?![0-9])"
        + @"|(?<![A-Za-z0-9])" + SectionMark + @"\d+(?:\.\d+)*" + Gap + "of(?:" + Gap + "the)?"
        + Gap + @"(?:[A-Za-z]+(?:" + Gap + @"|-)){0,3}spec(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A section citation names the document it lives in: an ADR or a standards document
    /// with its section, never "spec" and a number.
    /// </summary>
    /// <remarks>
    /// A design spec kept outside the repository cannot be opened by a reader of the code,
    /// so its section numbers point nowhere. Where an ADR records the rule, cite the ADR;
    /// otherwise state the rule in the comment. Scanned: the same files as
    /// <see cref="Every_numbered_decision_names_its_ADR"/> (source, test, migration, script,
    /// Markdown and workflow files under <c>src</c>, <c>tests</c>, <c>db</c>,
    /// <c>scripts</c>, <c>docs</c> and <c>.github</c>, and the same kinds at the root),
    /// comments, strings and names alike.
    /// </remarks>
    [Fact]
    public void Every_section_citation_names_its_document()
    {
        var repo = RepoRoot();
        var files = NumberedDecisionRoots
            .SelectMany(r => SourceFiles(repo, Path.Combine(repo, r), NumberedDecisionExtensions))
            .Concat(Directory.EnumerateFiles(repo, "*", SearchOption.TopDirectoryOnly)
                .Where(f => NumberedDecisionExtensions.Contains(Path.GetExtension(f)))
                .Select(f => f.Replace('\\', '/')))
            .ToList();

        // Fail closed: a wrong root or an over-eager exclusion would scan nothing and pass.
        Assert.True(files.Count > 1600, $"Scanned only {files.Count} files.");
        Assert.Contains(files, f => f.EndsWith("src/Api/Provisioning/Demo/DemoProvenance.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("tests/Importer.Moneydance.Tests/Demo/DemoHouseholdTests.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("docs/decisions/0091-one-category-taxonomy.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("scripts/audit-no-raw-sql.sh", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(repo, "README.md").Replace('\\', '/'), files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            foreach (Match m in SpecSection.Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var lineEnd = text.IndexOf('\n', m.Index);
                var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                var lineNo = text.AsSpan(0, m.Index).Count('\n') + 1;
                violations.Add($"{relative}:{lineNo}: {CommentWrap.Replace(m.Value, " ")}\n    {line}");
            }
        }

        Assert.True(violations.Count == 0,
            "A spec section number names no document anyone here can open. Cite the ADR the rule lives in "
            + "(\"ADR-0030 §2\"), or state the rule where no ADR records it:\n" + string.Join("\n", violations));
    }

    /// <summary>The section matcher on lines built here, so this file carries none. A wrapped
    /// comment line, a name, the spec's file name, capitals, a plural or the number written
    /// first do not hide a citation; a section that names its document, a type whose name
    /// ends in Spec, and a word with no number are not citations.</summary>
    [Theory]
    [InlineData("per ", "spec", " 3.4", true)]
    [InlineData("public void Each_account_era_classifies_as_", "spec", "_3_6()", true)]
    [InlineData("Rule_of_", "spec", "_3_4_holds", true)]
    [InlineData("per demo-ledger-", "spec", " §3.4", true)]
    [InlineData("see demo-ledger-", "spec", ".md §3.4", true)]
    [InlineData("as the ", "spec", " sections 3.4 and 3.8 say", true)]
    [InlineData("THE ", "SPEC", " 3.4 RULE", true)]
    [InlineData("the rule in section 3.4 of the ", "spec", ".", true)]
    [InlineData("the rule in §3.4 of the design ", "spec", ".", true)]
    [InlineData("the household (", "spec", " 3.4, 3.8)", true)]
    [InlineData("as ", "Spec", " 3.11 says", true)]
    [InlineData("the ", "spec", " §7", true)]
    [InlineData("the ", "spec", "§3.6 rule", true)]
    [InlineData("the ", "spec", "'s section 7", true)]
    [InlineData("the shape (", "spec", " section 3.9)", true)]
    [InlineData("// the rule of ", "spec", "\n    // 3.4 says", true)]
    [InlineData(" * the rule of ", "spec", "\r\n * 3.4 says", true)]
    [InlineData("see ADR-0030 §2", "", "", false)]
    [InlineData("per engineering-standards §4.2.2", "", "", false)]
    [InlineData("a ReportSpec ", "spec", "", false)]
    [InlineData("a ", "ReportSpec", " 2", false)]
    [InlineData("the ", "spec", ", 3 days later", false)]
    [InlineData("var ", "spec", "2 = Build();", false)]
    [InlineData("section 3 of the Report", "Spec", " type", false)]
    [InlineData("step 3 of the ", "spec", " run", false)]
    [InlineData("// the last ", "spec", "\n\n// 3 lines down", false)]
    public void The_section_matcher_flags_every_spec_number(
        string before, string word, string after, bool flagged)
    {
        Assert.Equal(flagged, SpecSection.IsMatch(before + word + after));
    }

    /// <summary>A .NET format specifier, not a citation.</summary>
    private static bool IsFormatSpecifier(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        return FormatSpecifierBefore.IsMatch(text[lineStart..index]);
    }

    /// <summary>The citation's run starts with an ADR reference, read across a wrapped comment.</summary>
    private static bool NamesItsAdr(string text, int index)
    {
        var start = Math.Max(0, index - 200);
        var before = CommentWrap.Replace(text[start..index], " ");
        var run = RunBefore.Match(before);
        var head = run.Success ? before[..run.Index] : before;
        return AdrReferenceBefore.IsMatch(head);
    }

    private static IEnumerable<string> SourceFiles(string repo, string root, HashSet<string>? extensions = null) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => (extensions ?? Extensions).Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetRelativePath(repo, f).Replace('\\', '/').Split('/')
                .Any(SkippedDirectories.Contains))
            .Select(f => f.Replace('\\', '/'));

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
