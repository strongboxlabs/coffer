using System.Reflection;
using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Every write of a memo, a tag pairing or the originating account goes through the
/// write primitive's adapter, and every name-to-tag resolution through
/// <c>tag_name_key</c>: in the API through <c>AnnotationWriter</c>, in the Moneydance
/// importer through <c>ImportAnnotations</c> and its persistence methods.
/// </summary>
/// <remarks>
/// <para>Asserted against the SOURCE of <c>src/Api</c>, <c>src/Coffer.Domain.*</c> and
/// the importer's two projects, like <see cref="ClockDisciplineTests"/>: a writer that
/// places a value on its own has no symptom until a register shows the wrong thing, and
/// the edit that adds one is the moment to catch it. Lines that start with <c>//</c> are
/// skipped; everything else is matched as text, so a write hidden in a string fails too,
/// which is the safe side.</para>
/// <para>The importer's rows are positional records, so a memo or an owner is placed by
/// a constructor argument or a <c>with</c> expression rather than a mutator. Those are
/// read from its compiled code: the notes' init setters are called only by
/// <c>ImportAnnotations.Stamp</c>, and the rows are built only in listed places, each
/// naming its notes as null.</para>
///
/// <para>The allow-list names each exception by file and member, not by line, so an
/// edit elsewhere in the file does not stale it, and each entry says why. An entry
/// nothing matches any more fails the test, so the list cannot outlive what it
/// excuses.</para>
/// </remarks>
public sealed class AnnotationWriteCatalogTests
{
    private sealed record Rule(string Name, string Why, Func<string, string, IEnumerable<int>> Find);

    private sealed record Allowed(string File, string Member, string Rule, string Reason);

    private const string TagJunctionWrite = "tag junction write";
    private const string Mutator = "memo or owner mutator";
    private const string HeaderConstruction = "header construction";
    private const string SetPropertyOnNotes = "bulk update of a memo or the owner";
    private const string PropertyOfNotes = "change-tracker access to a memo or the owner";
    private const string SqlLiteral = "SQL naming the notes columns or tables";
    private const string NameFolding = "tag name folded in .NET";
    private const string SavedReportSpec = "saved report spec write";
    private const string RepointCall = "saved-report repoint call";
    private const string TagSeed = "tag names bound to a header or leg";

    private static readonly Rule[] Rules =
    [
        new(TagJunctionWrite, "tag pairings are written by AnnotationWriter.ApplyAsync",
            (_, code) => Lines(code, new Regex(@"\b(?:TxnHeaderTags|TxnLegTags)\s*\.\s*(?:Add|AddRange|Remove|RemoveRange|Update)\s*\(|\bnew\s+Txn(?:Header|Leg)TagRow\b"))
                .Concat(Statements(code, new Regex(@"\b(?:TxnHeaderTags|TxnLegTags)\b"), new Regex(@"\bExecute(?:Update|Delete)(?:Async)?\s*\(")))),
        new(Mutator, "only the adapter stores a plan's memos and owner",
            (_, code) => Lines(code, new Regex(@"\b(?:AssignMemo|AssignLegMemo|AssignOriginatingAccount)\s*\("))),
        new(HeaderConstruction, "a header is built only by a factory that names its owner",
            (_, code) => Lines(code, new Regex(
                @"\bnew\s+TxnHeaderRow\b|\bTxnHeaderRow\??\s+\w+\s*=\s*new\s*[({]|\bTxnHeaders\s*\.\s*Add(?:Range)?(?:Async)?\s*\(\s*new\s*[({]"))),
        new(SetPropertyOnNotes, "a bulk update would place a value the plan never saw",
            (_, code) => Lines(code, new Regex(@"SetProperty\s*\(\s*\w+\s*=>\s*\w+\s*\.\s*(?:Memo|LegMemo|OriginatingAccountId)\b"))),
        new(PropertyOfNotes, "a change-tracker write would place a value the plan never saw",
            (_, code) => Lines(code, new Regex(
                @"\.\s*Property\s*\(\s*(?:\w+\s*=>\s*\w+\s*\.\s*(?:Memo|LegMemo|OriginatingAccountId)\b|""(?:Memo|LegMemo|OriginatingAccountId)""|nameof\s*\([^)]*\b(?:Memo|LegMemo|OriginatingAccountId)\s*\))"))),
        // Matched outside string quotes too, so SQL in a multi-line raw string is found.
        new(SqlLiteral, "no raw SQL writes notes; EF mappings only",
            (_, code) => Lines(code, new Regex(
                @"\b(?:txn_header_tags|txn_leg_tags|leg_memo|originating_account_id)\b|\b(?:UPDATE|INSERT\s+INTO|MERGE\s+INTO)\s+(?:ONLY\s+)?(?:""?public""?\s*\.\s*)?""?txn_(?:headers|legs)\b",
                RegexOptions.IgnoreCase))),
        new(NameFolding, "two names are one tag when their tag_name_key is equal, computed in SQL",
            (file, code) => file.EndsWith("/TagDictionary.cs", StringComparison.Ordinal)
                            || file.EndsWith("/TagsRepository.cs", StringComparison.Ordinal)
                ? Lines(code, new Regex(@"\bTo(?:Lower|Upper)(?:Invariant)?\s*\(|\blower\s*\(", RegexOptions.IgnoreCase))
                : []),
        new(SavedReportSpec, "a saved report's spec is written by its repository, and its tag by the repoint",
            (file, code) => file.EndsWith("/SavedReportsRepository.cs", StringComparison.Ordinal)
                ? []
                : Lines(code, new Regex(@"\bnew\s+SavedReportRow\b|\w\s*\.\s*Spec\s*=(?![=>])|SetProperty\s*\(\s*\w+\s*=>\s*\w+\s*\.\s*Spec\b"))),
        new(RepointCall, "only the tag merge repoints saved reports",
            (_, code) => Lines(code, new Regex(@"\bSavedReportsRepointTag\s*\("))),
    ];

    private static readonly Allowed[] AllowList =
    [
        new("src/Api/Db/AnnotationWriter.cs", "DiffHeaderTagsAsync", TagJunctionWrite, "the adapter's header pairings"),
        new("src/Api/Db/AnnotationWriter.cs", "DiffLegTagsAsync", TagJunctionWrite, "the adapter's leg pairings"),
        new("src/Api/Db/AnnotationWriter.cs", "RepointPendingTag", TagJunctionWrite,
            "a save that lost a race for a new tag's name links the tag that won instead"),
        new("src/Api/Db/AnnotationWriter.cs", "ApplyManyAsync", Mutator, "the adapter stores the plan's memos and owner"),
        new("src/Api/Db/AnnotationWriter.cs", "ApplyRepoint", Mutator, "the adapter stores a repoint's owner"),
        new("src/Api/Db/AnnotationWriter.cs", "FollowRepointAsync", SetPropertyOnNotes,
            "the set-based repoint: ownership follows legs moved by account"),
        new("src/Api/Db/Entities/TxnHeaderRow.cs", "AssignOriginatingAccount", Mutator, "the mutator's definition"),
        new("src/Api/Db/Entities/TxnHeaderRow.cs", "AssignMemo", Mutator, "the mutator's definition"),
        new("src/Api/Db/Entities/TxnLegRow.cs", "AssignLegMemo", Mutator, "the mutator's definition"),
        new("src/Api/Db/Repositories/TagsRepository.cs", "MergeAsync", TagJunctionWrite, "the merge repoints a tag's pairings"),
        new("src/Api/Db/Repositories/TagsRepository.cs", "MergeAsync", RepointCall, "the merge repoints saved reports"),
        new("src/Api/Db/AppDbContext.cs", "OnModelCreating", SqlLiteral, "the EF column and table mappings"),
        new("src/Api/Db/AppDbContext.cs", "OnModelCreating", PropertyOfNotes, "the EF column mappings"),
        new("src/Api/Db/AppDbContext.cs", "SavedReportsRepointTag", RepointCall, "the function binding's definition"),
        // The stored presentation (migration 264): its columns are outputs, not notes.
        new("src/Api/Db/LegNotes.cs", "OnModelCreating", PropertyOfNotes,
            "the recompute's own store maps notes_memo, the stored presentation's memo, not a memo a writer places"),
        new("src/Api/Migrations/LegNotesBackfill.Store.cs", "OnModelCreating", PropertyOfNotes,
            "the backfill's frozen store maps notes_memo, the stored presentation's memo, not a memo a writer places"),
        new("src/Api/Migrations/LegNotesBackfill.cs", "OnModelCreating", PropertyOfNotes,
            "the backfill's frozen projection maps the inputs it reads; it writes only through its own store"),
        new("src/Api/Migrations/LegNotesBackfill.cs", "OnModelCreating", SqlLiteral,
            "the backfill's frozen projection maps the input columns and tables it reads"),
    ];

    /// <summary>The importer's rules: its SQL and name folding as the API's, and the tag names it binds.</summary>
    private static readonly Rule[] ImporterRules =
    [
        Rules.Single(r => r.Name == SqlLiteral) with { Why = "the importer's SQL places notes only in its persistence methods, as the adapter stamped them" },
        Rules.Single(r => r.Name == NameFolding),
        new(TagSeed, "the tag names a header or leg gets are the ones ImportAnnotations.Stamp planned",
            (_, code) => Lines(code, new Regex(@"\bnew\s+(?:ImportTagSet|LegTagSeed)\b"))),
    ];

    private static readonly Allowed[] ImporterAllowList =
    [
        new("src/Importer.Moneydance/Db/ImportAnnotations.cs", "Stamp", TagSeed, "binds the plan's leg tags to the legs it stamps"),
        new("src/Importer.Moneydance/Pipeline/TransactionImportStep.cs", "ExecuteAsync", TagSeed,
            "hands the stamped tag names of each transaction to the persistence"),
        new("src/Importer.Moneydance/Pipeline/InvestmentTransactionImportStep.cs", "ExecuteAsync", TagSeed,
            "hands the stamped tag names of each investment transaction to the persistence"),
        new("src/Importer.Moneydance/Pipeline/ReminderImportStep.cs", "ExecuteAsync", TagSeed,
            "hands the stamped tag names of each reminder template to the persistence"),
        new("src/Importer.Moneydance/Db/TransactionsRepository.cs", "BulkUpsertAsync", SqlLiteral,
            "the persistence: inserts the stamped headers and legs as the mappers and the adapter built them"),
        new("src/Importer.Moneydance/Db/TagsRepository.cs", "BulkSetTagsAsync", SqlLiteral,
            "the persistence: links the stamped header tags"),
        new("src/Importer.Moneydance/Db/TagsRepository.cs", "BulkSetLegTagsAsync", SqlLiteral,
            "the persistence: links the stamped leg tags"),
        new("src/Importer.Moneydance/Pipeline/OriginatingAccountInvariants.cs", "ViolationsSql", SqlLiteral,
            "reads the originating account to check the import's invariants; writes nothing"),
        new("src/Importer.Moneydance/Pipeline/PostingCountRecomputeStep.cs", "ExecuteAsync", SqlLiteral,
            "sets the legs' posting counts, which hold no note"),
    ];

    /// <summary>The importer's two projects, by path from the repository root.</summary>
    private static List<string> ImporterFiles(string root) =>
        new[] { "Importer.Moneydance", "Importer.Moneydance.Cli" }
            .SelectMany(d => SourceFiles(Path.Combine(root, "src", d)))
            .ToList();

    [Fact]
    public void Importer_notes_are_written_only_through_its_adapter_and_persistence()
    {
        var root = RepoRoot();
        var files = ImporterFiles(root);
        // Fail closed: a wrong root would scan nothing and pass.
        Assert.True(files.Count > 50, $"Scanned only {files.Count} files; the guard is not looking at the importer.");
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("src/Importer.Moneydance/Db/ImportAnnotations.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("src/Importer.Moneydance.Cli/Program.cs", StringComparison.Ordinal));

        var hits = files.SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f), ImporterRules)).ToList();

        var violations = hits
            .Where(h => !ImporterAllowList.Any(a => a.File == h.File && a.Member == h.Member && a.Rule == h.Rule))
            .Select(h => $"{h.File}:{h.Line} ({h.Member}): {h.Rule} — {ImporterRules.Single(r => r.Name == h.Rule).Why}\n    {h.Text}")
            .ToList();
        Assert.True(violations.Count == 0, "Notes written around the importer's adapter:\n" + string.Join("\n", violations));

        var stale = ImporterAllowList
            .Where(a => !hits.Any(h => a.File == h.File && a.Member == h.Member && a.Rule == h.Rule))
            .Select(a => $"{a.File} ({a.Member}): {a.Rule}")
            .ToList();
        Assert.True(stale.Count == 0, "Allow-list entries nothing matches any more:\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// The places that build the importer's header and leg rows, each with what it builds.
    /// Each names its notes as null; <c>ImportAnnotations.Stamp</c> writes the plan's.
    /// </summary>
    private static readonly Dictionary<string, string> ImporterRowBuilders = new(StringComparer.Ordinal)
    {
        ["TransactionMapper.Map"] = "a bank transaction's header and legs",
        ["ReminderMapper.Map"] = "a reminder template's header and legs",
        ["InvestmentTransactionMapper.BuildHeader"] = "an investment event's header",
        ["InvestmentTransactionMapper.ToLeg"] = "an investment event's legs",
    };

    /// <summary>The one place that sets an importer row's memo, leg memo or owner.</summary>
    private const string ImporterNotesWriter = "ImportAnnotations.Stamp";

    [Fact]
    public void Importer_rows_are_built_without_notes_and_only_its_adapter_sets_them()
    {
        var header = typeof(Coffer.Importer.Moneydance.Db.TxnHeaderRow);
        var leg = typeof(Coffer.Importer.Moneydance.Db.TxnLegRow);
        var setters = new HashSet<MethodInfo>(new[]
        {
            header.GetProperty(nameof(Coffer.Importer.Moneydance.Db.TxnHeaderRow.Memo))!.SetMethod!,
            header.GetProperty(nameof(Coffer.Importer.Moneydance.Db.TxnHeaderRow.OriginatingAccountId))!.SetMethod!,
            leg.GetProperty(nameof(Coffer.Importer.Moneydance.Db.TxnLegRow.LegMemo))!.SetMethod!,
        });

        // What each method a person wrote builds, and which of them set a note: a `with`
        // expression calls the property's init setter; a construction the row's own
        // constructor (the copy constructor a `with` clones through is not one).
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var builds = new Dictionary<string, (int Headers, int Legs)>(StringComparer.Ordinal);
        var setting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in header.Assembly.GetTypes())
        {
            // The rows themselves, and the compiler's top-level helpers, which no person wrote.
            if (type == header || type == leg || (type.Name.StartsWith('<') && type.DeclaringType is null)) continue;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                var place = AnnotationReaderCatalogTests.PlaceOf(method);
                foreach (var member in AnnotationReaderCatalogTests.ReferencedMembers(method))
                {
                    if (member is MethodInfo m && setters.Contains(m)) setting.Add(place);
                    if (member is not ConstructorInfo c || c.GetParameters() is [var only] && only.ParameterType == c.DeclaringType) continue;
                    var (h, l) = builds.GetValueOrDefault(place);
                    if (c.DeclaringType == header) builds[place] = (h + 1, l);
                    else if (c.DeclaringType == leg) builds[place] = (h, l + 1);
                }
            }
        }
        Assert.True(builds.Values.Sum(b => b.Headers) >= 3 && builds.Values.Sum(b => b.Legs) >= 3,
            "found almost no row constructions; the scan is not reading the importer");

        Assert.True(setting.SetEquals([ImporterNotesWriter]),
            $"the importer's memo, leg memo or owner is set by [{string.Join(", ", setting.Order(StringComparer.Ordinal))}]; only {ImporterNotesWriter} may");

        var violations = new List<string>();
        violations.AddRange(builds.Keys.Where(k => !ImporterRowBuilders.ContainsKey(k))
            .Select(k => $"{k} builds an importer row and is not listed; build it with its notes as null and list it"));
        violations.AddRange(ImporterRowBuilders.Keys.Where(k => !builds.ContainsKey(k))
            .Select(k => $"{k}: listed as building importer rows but builds none; remove the entry"));

        // Each construction names its notes, as null: one named null per row built.
        var root = RepoRoot();
        var files = ImporterFiles(root);
        foreach (var (place, (headers, legs)) in builds.Where(b => ImporterRowBuilders.ContainsKey(b.Key)))
        {
            var (typeName, methodName) = (place.Split('.')[0], place.Split('.')[1]);
            var file = files.Single(f => Path.GetFileName(f) == $"{typeName}.cs");
            var lines = CodeOnly(File.ReadAllText(file)).Split('\n');
            var members = MemberOfEachLine(lines);
            var body = string.Join('\n', lines.Where((_, i) => members[i] == methodName));
            int Count(string pattern) => Regex.Matches(body, pattern).Count;
            if (Count(@"(?<!\w)Memo\s*:\s*null\b") != headers || Count(@"\bOriginatingAccountId\s*:\s*null\b") != headers)
                violations.Add($"{place} builds {headers} header(s) but does not name Memo: null and OriginatingAccountId: null on each");
            if (Count(@"\bLegMemo\s*:\s*null\b") != legs)
                violations.Add($"{place} builds {legs} leg(s) but does not name LegMemo: null on each");
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Notes_are_written_only_through_the_write_primitive()
    {
        var root = RepoRoot();
        var files = SourceFiles(Path.Combine(root, "src", "Api"))
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "src"), "Coffer.Domain.*").SelectMany(SourceFiles))
            .ToList();

        // Fail closed: a wrong root or an over-eager exclusion would scan nothing and pass.
        Assert.True(files.Count > 300, $"Scanned only {files.Count} files; the guard is not looking at the API.");
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("src/Api/Db/AnnotationWriter.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("src/Coffer.Domain.Annotations/AnnotationPlanner.cs", StringComparison.Ordinal));

        var hits = files.SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f))).ToList();

        var violations = hits
            .Where(h => !AllowList.Any(a => a.File == h.File && a.Member == h.Member && a.Rule == h.Rule))
            .Select(h => $"{h.File}:{h.Line} ({h.Member}): {h.Rule} — {Rules.Single(r => r.Name == h.Rule).Why}\n    {h.Text}")
            .ToList();
        Assert.True(violations.Count == 0, "Notes written around the write primitive:\n" + string.Join("\n", violations));

        var stale = AllowList
            .Where(a => !hits.Any(h => a.File == h.File && a.Member == h.Member && a.Rule == h.Rule))
            .Select(a => $"{a.File} ({a.Member}): {a.Rule}")
            .ToList();
        Assert.True(stale.Count == 0, "Allow-list entries nothing matches any more:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void Every_header_factory_names_its_originating_account()
    {
        var path = Path.Combine(RepoRoot(), "src", "Api", "Db", "Entities", "TxnHeaderRow.Factories.cs");
        var code = CodeOnly(File.ReadAllText(path));
        var factories = new Regex(@"static\s+TxnHeaderRow\s+(\w+)\s*\(([^)]*)\)").Matches(code);

        Assert.True(factories.Count >= 4, $"Found {factories.Count} header factories; the scan is not reading the file.");
        Assert.All(factories, m => Assert.Matches(@"(?<!\?)\bGuid\s+originatingAccountId\b", m.Groups[2].Value));
    }

    /// <summary>
    /// With every constructor private, the compiler refuses a header built anywhere but
    /// in the class itself, a target-typed <c>new()</c> included, which no text rule can
    /// see in every form.
    /// </summary>
    [Fact]
    public void A_header_has_no_constructor_outside_its_class()
    {
        var declaring = SourceFiles(Path.Combine(RepoRoot(), "src", "Api"))
            .Where(f => DeclaresHeader.IsMatch(File.ReadAllText(f)))
            .ToList();
        Assert.True(declaring.Count >= 2, $"Found {declaring.Count} files declaring the header; the scan is not reading them.");
        var code = string.Join('\n', declaring.Select(f => CodeOnly(File.ReadAllText(f))));

        Assert.Empty(OpenConstructors(code));
        Assert.Matches(@"\bprivate\s+TxnHeaderRow\s*\(\s*\)", code);
    }

    /// <summary>
    /// The compiler holds a memo and the owner to their mutators only while their setters
    /// are private: an opened setter lets an assignment anywhere place a value, which no
    /// rule here matches.
    /// </summary>
    [Fact]
    public void The_memos_and_the_owner_have_no_setter_outside_their_entity()
    {
        var properties = new[]
        {
            typeof(Coffer.Api.Db.Entities.TxnHeaderRow).GetProperty(nameof(Coffer.Api.Db.Entities.TxnHeaderRow.Memo)),
            typeof(Coffer.Api.Db.Entities.TxnHeaderRow).GetProperty(nameof(Coffer.Api.Db.Entities.TxnHeaderRow.OriginatingAccountId)),
            typeof(Coffer.Api.Db.Entities.TxnLegRow).GetProperty(nameof(Coffer.Api.Db.Entities.TxnLegRow.LegMemo)),
        };
        var open = properties
            .Select(p => p!)
            .Where(p => p.SetMethod is not { IsPrivate: true })
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();
        Assert.True(open.Count == 0, $"settable outside their entity: {string.Join(", ", open)}; keep the setter private and write through the mutator");
    }

    [Theory]
    [InlineData("    internal TxnHeaderRow() { }", true)]
    [InlineData("    public TxnHeaderRow(Guid id) { }", true)]
    [InlineData("    protected internal TxnHeaderRow() { }", true)]
    [InlineData("internal sealed partial class TxnHeaderRow(Guid id)", true)]
    [InlineData("    private TxnHeaderRow() { }", false)]
    [InlineData("    internal static TxnHeaderRow NewTransaction(Guid id) => new();", false)]
    public void An_open_constructor_is_found(string line, bool open) =>
        Assert.Equal(open, OpenConstructors(line).Any());

    private static readonly Regex DeclaresHeader = new(@"\bclass\s+TxnHeaderRow\b");

    /// <summary>A constructor anything outside the class can call, or a primary constructor.</summary>
    private static IEnumerable<string> OpenConstructors(string code) =>
        new Regex(@"^\s*(?:(?:public|internal|protected|private|static|sealed|partial|unsafe)\s+)*(?:(?:public|internal|protected)\s+)+(?:(?:public|internal|protected|private)\s+)*TxnHeaderRow\s*\(|\bclass\s+TxnHeaderRow\s*\(", RegexOptions.Multiline)
            .Matches(code).Select(m => m.Value.Trim());

    [Theory]
    [InlineData(TagJunctionWrite, "        db.TxnHeaderTags.Add(row);")]
    [InlineData(TagJunctionWrite, "        _db.TxnLegTags.RemoveRange(rows);")]
    [InlineData(TagJunctionWrite, "        var x = new TxnLegTagRow { };")]
    [InlineData(TagJunctionWrite, "        await _db.TxnHeaderTags.Where(t => t.TagId == id)\n            .ExecuteDeleteAsync(ct);")]
    [InlineData(Mutator, "        header.AssignMemo(\"x\");")]
    [InlineData(Mutator, "        leg.AssignLegMemo(null);")]
    [InlineData(Mutator, "        header.AssignOriginatingAccount(id);")]
    [InlineData(HeaderConstruction, "        var h = new TxnHeaderRow { };")]
    [InlineData(HeaderConstruction, "        TxnHeaderRow h = new() { Id = id };")]
    [InlineData(HeaderConstruction, "        _db.TxnHeaders.Add(new() { Id = id });")]
    [InlineData(PropertyOfNotes, "        _db.Entry(h).Property(x => x.Memo).CurrentValue = \"x\";")]
    [InlineData(PropertyOfNotes, "        _db.Entry(l).Property(\"LegMemo\").CurrentValue = null;")]
    [InlineData(PropertyOfNotes, "        e.Property(nameof(TxnHeaderRow.OriginatingAccountId)).CurrentValue = id;")]
    [InlineData(SqlLiteral, "        var sql = \"\"\"\n            UPDATE public.txn_headers SET memo = NULL\n            \"\"\";")]
    [InlineData(SqlLiteral, "        var sql = $\"update ONLY txn_legs set amount = {a}\";")]
    [InlineData(SetPropertyOnNotes, "        q.ExecuteUpdateAsync(s => s.SetProperty(h => h.Memo, (string?)null));")]
    [InlineData(SetPropertyOnNotes, "        q.ExecuteUpdateAsync(s => s.SetProperty(l => l.OriginatingAccountId, id));")]
    [InlineData(SqlLiteral, "        var sql = \"UPDATE txn_legs SET leg_memo = NULL\";")]
    [InlineData(SqlLiteral, "        var sql = \"insert into txn_headers (id) values (1)\";")]
    [InlineData(SavedReportSpec, "        report.Spec = json;")]
    [InlineData(RepointCall, "        await _db.SavedReportsRepointTag(a, b, c).SingleAsync();")]
    public void Each_rule_finds_its_own_shape(string rule, string snippet)
    {
        var source = $"internal static class Sample\n{{\n    internal static void Member()\n    {{\n{snippet}\n    }}\n}}\n";

        var hits = Scan("src/Api/Sample.cs", source).ToList();

        Assert.Contains(hits, h => h.Rule == rule && h.Member == "Member");
        // A commented-out line is not a write.
        var commented = string.Join('\n', snippet.Split('\n').Select(l => "        // " + l.Trim()));
        Assert.DoesNotContain(Scan("src/Api/Sample.cs", source.Replace(snippet, commented)), h => h.Rule == rule);
    }

    [Theory]
    [InlineData(TagSeed, "        sets.Add(new ImportTagSet(id, IsHeader: true, [\"x\"]));")]
    [InlineData(TagSeed, "        var seed = new LegTagSeed(legId, names);")]
    [InlineData(SqlLiteral, "        const string sql = \"DELETE FROM txn_leg_tags WHERE leg_id = ANY(@ids)\";")]
    [InlineData(SqlLiteral, "        var sql = \"UPDATE txn_headers SET memo = @m\";")]
    public void Each_importer_rule_finds_its_own_shape(string rule, string snippet)
    {
        var source = $"internal static class Sample\n{{\n    internal static void Member()\n    {{\n{snippet}\n    }}\n}}\n";

        Assert.Contains(Scan("src/Importer.Moneydance/Sample.cs", source, ImporterRules), h => h.Rule == rule && h.Member == "Member");
        var commented = string.Join('\n', snippet.Split('\n').Select(l => "        // " + l.Trim()));
        Assert.DoesNotContain(Scan("src/Importer.Moneydance/Sample.cs", source.Replace(snippet, commented), ImporterRules), h => h.Rule == rule);
    }

    [Fact]
    public void A_constants_text_belongs_to_the_constant()
    {
        var lines = CodeOnly(""""
            internal static class Sample
            {
                public static void Before() { }

                public const string ViolationsSql = """
                    SELECT originating_account_id FROM txn_headers
                    """;
            }
            """").Split('\n');
        var members = MemberOfEachLine(lines);
        Assert.Equal("ViolationsSql", members[Array.FindIndex(lines, l => l.Contains("originating_account_id", StringComparison.Ordinal))]);
    }

    [Theory]
    [InlineData("src/Api/Db/TagDictionary.cs", "        var key = name.ToLowerInvariant();")]
    [InlineData("src/Api/Db/Repositories/TagsRepository.cs", "        .Where(t => t.Name.ToLower() == n)")]
    public void Name_folding_is_found_in_the_tag_files(string file, string snippet)
    {
        var source = $"internal static class Sample\n{{\n    internal static void Member()\n    {{\n{snippet}\n    }}\n}}\n";
        Assert.Contains(Scan(file, source), h => h.Rule == NameFolding);
        Assert.DoesNotContain(Scan("src/Api/Other.cs", source), h => h.Rule == NameFolding);
    }

    // ----- the scanner -----

    private sealed record Hit(string File, string Member, string Rule, int Line, string Text);

    private static IEnumerable<Hit> Scan(string relative, string source) => Scan(relative, source, Rules);

    private static IEnumerable<Hit> Scan(string relative, string source, Rule[] rules)
    {
        var code = CodeOnly(source);
        var lines = code.Split('\n');
        var members = MemberOfEachLine(lines);
        foreach (var rule in rules)
            foreach (var index in rule.Find(relative, code).Distinct())
                yield return new Hit(relative, members[index], rule.Name, index + 1, lines[index].Trim());
    }

    private static readonly Regex MemberDeclaration = new(
        @"^\s*(?:(?:public|private|internal|protected)\s+)[^=;(]*?\b(\w+)\s*(?:<[^()]*>)?\s*\(", RegexOptions.Compiled);

    /// <summary>A named constant, whose text (a SQL statement, say) belongs to it, not to the method above.</summary>
    private static readonly Regex ConstantDeclaration = new(
        @"^\s*(?:(?:public|private|internal|protected)\s+)+const\s+[\w?<>\[\]]+\s+(\w+)\s*=", RegexOptions.Compiled);

    /// <summary>The member each line belongs to: the nearest declaration at or above it.</summary>
    private static string[] MemberOfEachLine(string[] lines)
    {
        var result = new string[lines.Length];
        var current = "(type)";
        for (var i = 0; i < lines.Length; i++)
        {
            if (MemberDeclaration.Match(lines[i]) is { Success: true } m) current = m.Groups[1].Value;
            else if (ConstantDeclaration.Match(lines[i]) is { Success: true } c) current = c.Groups[1].Value;
            result[i] = current;
        }
        return result;
    }

    /// <summary>The zero-based line of each match.</summary>
    private static IEnumerable<int> Lines(string code, Regex pattern) =>
        pattern.Matches(code).Select(m => code.AsSpan(0, m.Index).Count('\n'));

    /// <summary>The line of each <paramref name="subject"/> whose statement (to the next
    /// <c>;</c>) also matches <paramref name="verb"/>.</summary>
    private static IEnumerable<int> Statements(string code, Regex subject, Regex verb)
    {
        foreach (Match m in subject.Matches(code))
        {
            var end = code.IndexOf(';', m.Index);
            var statement = end < 0 ? code[m.Index..] : code[m.Index..end];
            if (verb.IsMatch(statement)) yield return code.AsSpan(0, m.Index).Count('\n');
        }
    }

    /// <summary>The source with every line that starts with <c>//</c> blanked, keeping line numbers.</summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

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
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
