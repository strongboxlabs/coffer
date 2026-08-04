using System.Text.Json;
using Coffer.Api.Provisioning;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// ADR-0091: a new ledger and the Demo ledger must get the SAME categories.
/// </summary>
/// <remarks>
/// <para>They did not, for a long time. A new ledger was seeded from a
/// hand-written <c>starter-categories.json</c> (61 entries) while Demo inherited
/// whatever <c>moneydance-export-demo.json</c> carried (108) — and only about 6
/// of ~22 top-level names overlapped, so the categories a user saw while
/// exploring Demo had almost nothing to do with the ones their own ledger got.
/// </para>
/// <para>The export is now the single source of truth and the starter catalogue
/// is generated from it by <c>data/samples/starter-categories.gen.mjs</c>. A
/// generator alone is not enough, though: nothing stops someone editing the
/// export and forgetting to regenerate, or hand-editing the JSON. This test is
/// the thing that notices. It compares the two FILES, because that is where the
/// drift would appear; the final assertion then confirms the build embedded the
/// file we just checked.</para>
/// </remarks>
public sealed class StarterCategoryParityTests
{
    /// <summary>Moneydance account types that denote a category.</summary>
    private static readonly Dictionary<string, string> Kinds =
        new() { ["i"] = "income", ["e"] = "expense" };

    private sealed record Entry(string Kind, string? Parent, string Name);

    [Fact]
    public void Starter_catalogue_matches_the_demo_export_category_tree()
    {
        var exportEntries = ReadExport(Locate("data/samples/moneydance-export-demo.json"));
        var starterEntries = ReadStarter(Locate("src/Api/Provisioning/starter-categories.json"));

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

        // The EMBEDDED resource is what actually seeds a ledger; confirm the build
        // embedded the same file this test just compared.
        Assert.Equal(starterEntries.Count, new StarterCategoriesSeeder().CategoryCount);
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
            .GroupBy(e => (e.Kind, e.Parent, e.Name))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Kind} {g.Key.Parent ?? "(root)"} > {g.Key.Name}")
            .ToList();

        Assert.True(dupes.Count == 0, "duplicate categories: " + string.Join(", ", dupes));

        // And the cross-kind case is genuinely present, so this test would notice
        // if a future "dedupe" collapsed it by name alone.
        Assert.Contains(entries, e => e.Kind == "income" && e.Name == "Investment");
        Assert.Contains(entries, e => e.Kind == "expense" && e.Name == "Investment");
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

    private static HashSet<Entry> ReadExport(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var accounts = doc.RootElement.GetProperty("all_items").EnumerateArray()
            .Where(x => x.TryGetProperty("obj_type", out var t) && t.GetString() == "acct")
            .ToList();

        var nameById = accounts.ToDictionary(
            a => a.GetProperty("id").GetString()!,
            a => a.GetProperty("name").GetString()!);
        var kindById = accounts.ToDictionary(
            a => a.GetProperty("id").GetString()!,
            a => a.GetProperty("type").GetString()!);

        var result = new HashSet<Entry>();
        foreach (var a in accounts)
        {
            var type = a.GetProperty("type").GetString()!;
            if (!Kinds.TryGetValue(type, out var kind)) continue;   // real account, not a category

            var parentId = a.TryGetProperty("parentid", out var p) ? p.GetString() : null;
            // A parent that is not itself a category means this is a top level.
            var parentIsCategory = parentId is not null
                && kindById.TryGetValue(parentId, out var pt)
                && Kinds.ContainsKey(pt);

            result.Add(new Entry(
                kind,
                parentIsCategory ? nameById[parentId!] : null,
                a.GetProperty("name").GetString()!));
        }
        return result;
    }

    private static HashSet<Entry> ReadStarter(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new HashSet<Entry>();
        foreach (var top in doc.RootElement.GetProperty("categories").EnumerateArray())
        {
            var name = top.GetProperty("name").GetString()!;
            var kind = top.GetProperty("kind").GetString()!;
            result.Add(new Entry(kind, null, name));

            if (!top.TryGetProperty("children", out var children)) continue;
            foreach (var child in children.EnumerateArray())
                result.Add(new Entry(kind, name, child.GetString()!));
        }
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
