using System.Text.Json;

using Coffer.Api.Db;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// The shared vector file, run through the .NET side. The web tests read the same
/// file for the editor's pre-join (precedent: <c>CsvVocabularyParityTests</c>), so a
/// rule changed on one side only fails on the other.
/// </summary>
public sealed class AnnotationVectorTests
{
    private const string VectorFile = "src/Web/src/lib/annotations/annotation-vectors.json";

    private static readonly Lazy<JsonDocument> Vectors =
        new(() => JsonDocument.Parse(File.ReadAllText(Locate(VectorFile))));

    public static TheoryData<string> Names(string section) =>
        [.. Section(section).Select(v => v.GetProperty("name").GetString()!)];

    public static TheoryData<string> MemoJoinNames() => Names("memoJoin");
    public static TheoryData<string> TagUnionNames() => Names("tagUnion");
    public static TheoryData<string> BlankMemoNames() => Names("blankMemo");
    public static TheoryData<string> TagCapNames() => Names("tagCap");
    public static TheoryData<string> OwnershipNames() => Names("ownership");

    [Theory]
    [MemberData(nameof(MemoJoinNames))]
    public void Memo_join(string name)
    {
        var v = Vector("memoJoin", name);

        var joined = AnnotationText.JoinMemos(Str(v, "header"), Str(v, "line"));

        Assert.Equal(Str(v, "expected"), joined);
    }

    [Theory]
    [MemberData(nameof(TagUnionNames))]
    public void Tag_union(string name)
    {
        var v = Vector("tagUnion", name);

        var union = AnnotationText.UnionTags(List(v, "first"), List(v, "second"));

        Assert.Equal(List(v, "expected"), union);
    }

    [Theory]
    [MemberData(nameof(BlankMemoNames))]
    public void Blank_memo_normalisation(string name)
    {
        var v = Vector("blankMemo", name);

        Assert.Equal(Str(v, "expected"), AnnotationText.NormalizeMemo(Str(v, "input")));
        // One normaliser: a header's free-text fields store exactly what a memo does,
        // so a memo moving between the header and a leg never changes its bytes.
        Assert.Equal(Str(v, "expected"), HeaderText.Normalize(Str(v, "input")));
    }

    [Theory]
    [MemberData(nameof(TagCapNames))]
    public void Tag_limit(string name)
    {
        // Only GROWING a set past the limit is refused. Through the
        // planner, on the side's own leg of a one-split, the commonest set there is.
        var v = Vector("tagCap", name);
        var stored = v.GetProperty("stored").GetInt32();
        var saved = v.GetProperty("saved").GetInt32();
        var limit = v.GetProperty("limit").GetString() == "systemMerge" ? TagLimit.SystemMerge : TagLimit.Enforce;

        var shape = Scenarios.OneSplit(Scenarios.Bank, Scenarios.Bank, Scenarios.Groceries);
        var bank = new LegKey(0, Scenarios.Bank);
        var current = new Annotations<string>(null, new Dictionary<LegKey, Notes<string>>
        {
            [bank] = new(null, [.. Enumerable.Range(1, stored).Select(i => $"t{i}")]),
        });
        IReadOnlyList<string> sent = [.. Enumerable.Range(1, saved).Select(i => $"t{i}")];

        var plan = AnnotationPlanner.Plan(
            ShapeChange<string>.Unchanged(shape, current),
            new AnnotationIntent<string>(Scenarios.Bank, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set(sent)),
            limit);

        Assert.Equal(v.GetProperty("accepted").GetBoolean(), plan.IsValid);
        if (plan.IsValid)
            Assert.Equal(sent, plan.Result.For(bank).Tags);
        else
            Assert.Equal(AnnotationViolationCodes.TagsTooMany, Assert.Single(plan.Violations).Code);
    }

    [Fact]
    public void The_name_folding_vectors_cover_every_case_the_folding_rule_must_handle()
    {
        // The keys are the database's to compute (tag_name_key); C# never folds a tag
        // name. What this side can hold is that the vectors exist and are well-formed,
        // so the SQL tests that read them cannot quietly lose a case.
        var vectors = Section("nameFolding").ToList();
        var covers = vectors.Select(v => v.GetProperty("covers").GetString()).ToHashSet();

        Assert.Superset(
            new HashSet<string?> { "ascii-case", "dotted-capital-i", "sharp-s", "final-sigma", "tab", "nbsp", "ideographic-space" },
            covers);
        foreach (var v in vectors)
        {
            var key = v.GetProperty("key").GetString()!;
            Assert.NotEmpty(key);
            Assert.False(char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1]), $"Key '{key}' keeps outer whitespace.");
        }
    }

    [Theory]
    [MemberData(nameof(OwnershipNames))]
    public void Ownership(string name)
    {
        var v = Vector("ownership", name);
        var before = Shape(v.GetProperty("before"), originating: null);
        var after = Shape(v.GetProperty("after"), originating: null);
        var expected = v.GetProperty("expected");

        if (v.TryGetProperty("repointTarget", out var target))
        {
            var owner = AnnotationPlanner.Repoint(before, after, Account(target.GetString()!));
            Assert.Equal(Account(expected.GetProperty("originating").GetString()!), owner);
            return;
        }

        var survivors = v.GetProperty("survivors").GetString() == "byLegId"
            ? ShapeChange<string>.ByLegId(before, after)
            : ShapeChange<string>.ByAccountAndRole(before, after);
        var plan = AnnotationPlanner.Plan(
            new ShapeChange<string>(before, Annotations<string>.Empty, after, survivors),
            AnnotationIntent<string>.Untouched(Account(v.GetProperty("side").GetString()!)));

        if (expected.TryGetProperty("violation", out var violation))
        {
            Assert.Contains(violation.GetString(), plan.Violations.Select(x => x.Code));
        }
        else
        {
            Assert.True(plan.IsValid, string.Join("; ", plan.Violations.Select(x => x.Detail)));
            Assert.Equal(Account(expected.GetProperty("originating").GetString()!), plan.OriginatingAccountId);
        }
    }

    // ---------------------------------------------------------------------

    private static IEnumerable<JsonElement> Section(string section) =>
        Vectors.Value.RootElement.GetProperty(section).EnumerateArray();

    private static JsonElement Vector(string section, string name) =>
        Section(section).Single(v => v.GetProperty("name").GetString() == name);

    private static string? Str(JsonElement v, string property) =>
        v.GetProperty(property).ValueKind == JsonValueKind.Null ? null : v.GetProperty(property).GetString();

    private static List<string> List(JsonElement v, string property) =>
        [.. v.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private static Guid Account(string name) => Scenarios.Id(name);

    private static TxnShape Shape(JsonElement shape, Guid? originating)
    {
        var owner = shape.TryGetProperty("originating", out var o) ? Account(o.GetString()!) : originating;
        var action = shape.TryGetProperty("action", out var a) ? a.GetString() : null;
        var legs = shape.GetProperty("legs").EnumerateArray().Select(l => new ShapeLeg(
            new LegKey(l.GetProperty("posting").GetInt32(), Account(l.GetProperty("account").GetString()!)),
            Scenarios.Id("leg:" + l.GetProperty("id").GetString()),
            l.TryGetProperty("role", out var role) ? role.GetString() : null,
            l.TryGetProperty("investmentSide", out var side) && side.GetBoolean(),
            l.TryGetProperty("brokerage", out var brokerage) && brokerage.GetBoolean(),
            l.TryGetProperty("transferSource", out var source) && source.GetBoolean())).ToList();
        return new TxnShape(owner, action, legs);
    }

    private static string Locate(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate '{relative}' from {AppContext.BaseDirectory}");
    }
}
