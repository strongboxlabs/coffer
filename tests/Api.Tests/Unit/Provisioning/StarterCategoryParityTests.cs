using System.Text;
using System.Text.Json;
using Coffer.Api.Provisioning;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// ADR-0091: a new ledger and the Demo ledger must get the SAME categories.
/// </summary>
/// <remarks>
/// <para>They did not, for a long time. A new ledger was seeded from a
/// hand-written <c>starter-categories.json</c> (61 entries) while Demo inherited
/// whatever the committed demo export carried (108) — and only about 6
/// of ~22 top-level names overlapped, so the categories a user saw while
/// exploring Demo had almost nothing to do with the ones their own ledger got.
/// </para>
/// <para>The Demo's curated base is now the single source of truth: the C# Demo
/// generator writes it into every Demo export, and the starter catalogue is
/// generated from it by <c>data/samples/starter-categories.gen.mjs</c>. A
/// generator alone is not enough, though: nothing stops someone editing the base
/// and forgetting to regenerate, or hand-editing the JSON. This test is the thing
/// that notices. It compares the generator's OUTPUT (the tests' fixed-date short
/// span) with the starter FILE, because that is where the drift would appear; the
/// final assertion then confirms the build embedded the file we just checked.</para>
/// <para>An entry is its full segment path plus its kind, at every depth, and
/// the export side applies the <c>coffer_category_kind</c> override the importer
/// applies (ADR-0091, amendment of 2026-11-18). The existence assertions are
/// what make the override and the third level count: without them, losing the
/// override on both sides, or flattening both trees, would still compare
/// equal.</para>
/// </remarks>
public sealed class StarterCategoryParityTests
{
    /// <summary>Moneydance account types that denote a category.</summary>
    private static readonly Dictionary<string, string> Kinds =
        new() { ["i"] = "income", ["e"] = "expense" };

    private const string OverrideKey = "coffer_category_kind";
    private const string Separator = " > ";

    private sealed record Entry(string Kind, string Path)
    {
        /// <summary>The parent's full path, or null at the top level.</summary>
        public string? Parent
        {
            get
            {
                var i = Path.LastIndexOf(Separator, StringComparison.Ordinal);
                return i < 0 ? null : Path[..i];
            }
        }

        public string Name => Parent is null ? Path : Path[(Parent.Length + Separator.Length)..];
    }

    [Fact]
    public void Starter_catalogue_matches_the_demo_export_category_tree()
    {
        var exportEntries = ReadExport(DemoTestExports.ShortBytes);
        var starterEntries = ReadStarter(Locate("src/Api/Provisioning/starter-categories.json")).ToHashSet();

        // Report the actual difference — "counts differ" sends the reader hunting.
        var onlyInExport = exportEntries.Except(starterEntries).OrderBy(e => e.ToString()).ToList();
        var onlyInStarter = starterEntries.Except(exportEntries).OrderBy(e => e.ToString()).ToList();

        Assert.True(
            onlyInExport.Count == 0 && onlyInStarter.Count == 0,
            $"""
            Starter categories and the demo export have diverged.
            Run: node data/samples/starter-categories.gen.mjs

            Only in the demo export ({onlyInExport.Count}):
              {string.Join("\n  ", onlyInExport)}

            Only in starter-categories.json ({onlyInStarter.Count}):
              {string.Join("\n  ", onlyInStarter)}
            """);

        // Sanity: a real tree, not an empty or flat one.
        Assert.True(starterEntries.Count > 50, $"expected a substantial tree, got {starterEntries.Count}");
        Assert.Contains(starterEntries, e => e.Parent is not null);
        Assert.Contains(starterEntries, e => e.Kind == "income");
        Assert.Contains(starterEntries, e => e.Kind == "expense");

        // The third kind and the third level are present on both sides.
        Assert.Contains(starterEntries, e => e.Kind == "adjustment");
        Assert.Contains(new Entry("expense", "Vacation > Travel > Airfare"), starterEntries);

        // The EMBEDDED resource is what actually seeds a ledger; confirm the build
        // embedded the same file this test just compared.
        Assert.Equal(starterEntries.Count, new StarterCategoriesSeeder(TimeProvider.System).CategoryCount);
    }

    /// <summary>
    /// A name may legitimately appear under both kinds — "Investment" is an
    /// expense parent (Trading Commission) AND an income parent (Dividends, cap
    /// gains). That is correct modelling: income and expense are separate
    /// namespaces. Only a repeat within the same kind and parent is a fault.
    /// </summary>
    [Fact]
    public void No_duplicate_category_within_a_kind_and_parent()
    {
        var entries = ReadStarter(Locate("src/Api/Provisioning/starter-categories.json"));

        var dupes = entries
            .GroupBy(e => e)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Kind} {g.Key.Path}")
            .ToList();

        Assert.True(dupes.Count == 0, "duplicate categories: " + string.Join(", ", dupes));

        // And the cross-kind case is genuinely present, so this test would notice
        // if a future "dedupe" collapsed it by name alone.
        Assert.Contains(new Entry("income", "Investment"), entries);
        Assert.Contains(new Entry("expense", "Investment"), entries);
    }

    /// <summary>
    /// The Moneydance model books an ATM withdrawal as an expense and an opening
    /// balance as income. Neither survives translation to double-entry: a cash
    /// withdrawal is a transfer between accounts, and an opening balance booked as
    /// income overstates income in every report. ADR-0091 dropped both.
    /// </summary>
    [Fact]
    public void Import_artefacts_are_absent()
    {
        var entries = ReadStarter(Locate("src/Api/Provisioning/starter-categories.json"));
        var names = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("ATM Withdrawal", names);
        Assert.DoesNotContain("Initial Balance", names);

        // "Gas" alone was ambiguous against Automotive > Fuel; it means the utility.
        Assert.DoesNotContain(
            entries,
            e => e.Parent == "Bills" && e.Name.Equals("Gas", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Parent == "Bills" && e.Name == "Natural Gas");
    }

    /// <summary>
    /// The seeder refuses a catalogue whose branch mixes kinds, so a hand-edit
    /// cannot seed an income child under an expense parent.
    /// </summary>
    [Fact]
    public void A_catalogue_that_mixes_kinds_within_a_branch_is_refused()
    {
        const string json = """
            { "categories": [
              { "name": "Vacation", "kind": "expense", "children": [
                  { "name": "Travel", "kind": "expense", "children": [
                      { "name": "Refund", "kind": "income" } ] } ] } ] }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var ex = Assert.Throws<InvalidOperationException>(() => StarterCategoriesSeeder.Parse(stream));
        Assert.Contains("Vacation > Travel > Refund", ex.Message);
    }

    private static HashSet<Entry> ReadExport(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var accounts = doc.RootElement.GetProperty("all_items").EnumerateArray()
            .Where(x => x.TryGetProperty("obj_type", out var t) && t.GetString() == "acct")
            .ToDictionary(a => a.GetProperty("id").GetString()!);

        bool IsCategory(string? id) =>
            id is not null
            && accounts.TryGetValue(id, out var a)
            && Kinds.ContainsKey(a.GetProperty("type").GetString()!);

        // Up the parent chain while the parent is a category: a parent that is
        // not a category (MD's root) means this is the top level.
        string PathOf(JsonElement a)
        {
            var name = a.GetProperty("name").GetString()!;
            var parentId = a.TryGetProperty("parentid", out var p) ? p.GetString() : null;
            return IsCategory(parentId) ? PathOf(accounts[parentId!]) + Separator + name : name;
        }

        var result = new HashSet<Entry>();
        foreach (var a in accounts.Values)
        {
            if (!Kinds.TryGetValue(a.GetProperty("type").GetString()!, out var kind)) continue;   // real account
            if (a.TryGetProperty(OverrideKey, out var over)) kind = over.GetString()!;
            result.Add(new Entry(kind, PathOf(a)));
        }
        return result;
    }

    /// <summary>Every node at every depth, as a list so a repeat stays visible.</summary>
    private static List<Entry> ReadStarter(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<Entry>();

        void Walk(JsonElement nodes, string? parentPath)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var nodePath = parentPath is null
                    ? node.GetProperty("name").GetString()!
                    : parentPath + Separator + node.GetProperty("name").GetString();
                result.Add(new Entry(node.GetProperty("kind").GetString()!, nodePath));
                if (node.TryGetProperty("children", out var children)) Walk(children, nodePath);
            }
        }

        Walk(doc.RootElement.GetProperty("categories"), null);
        return result;
    }

    /// <summary>Walk up from the test binary to the repo root.</summary>
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
