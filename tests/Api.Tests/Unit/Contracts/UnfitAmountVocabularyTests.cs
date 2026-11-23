using System.Reflection;
using System.Text.RegularExpressions;

using Coffer.Api.Contracts;

namespace Coffer.Api.Tests.Unit.Contracts;

/// <summary>
/// The "Amounts with more decimals than their currency" advisory's vocabularies (ADR-0114
/// D15), held between the API and the SPA. Its reasons name shapes only, as the approved
/// Category amounts wording defines the repairable shape by structure (one real line and
/// one category line): a reason for signs or zeros would send a repairable posting to the
/// advisory instead.
/// </summary>
public sealed class UnfitAmountVocabularyTests
{
    [Fact]
    public void The_reasons_are_the_two_shapes_the_repair_leaves_and_the_web_union_matches()
    {
        var api = Constants(typeof(UnfitAmountReasons));

        Assert.Equal(
            ["category-line-between-categories", "category-line-many-lines"],
            api.Order(StringComparer.Ordinal));
        Assert.Equal(api.Order(StringComparer.Ordinal), WebUnion("UnfitAmountReason").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_kinds_the_web_union_lists_are_the_api_kinds()
    {
        var api = Constants(typeof(UnfitAmountKinds));
        // Anchor: reflection read the class, not an empty set any union matches.
        Assert.Contains(UnfitAmountKinds.OpeningBalance, api);

        Assert.Equal(api.Order(StringComparer.Ordinal), WebUnion("UnfitAmountKind").Order(StringComparer.Ordinal));
    }

    private static List<string> Constants(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    private static List<string> WebUnion(string name)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Web", "src", "lib", "types", "ledger.ts"));
        var match = Regex.Match(source, $@"export type {Regex.Escape(name)}\s*=([^;]+);");
        Assert.True(match.Success, $"Could not find `export type {name} = ...;` in ledger.ts.");
        return [.. Regex.Matches(match.Groups[1].Value, @"'([^']*)'").Select(m => m.Groups[1].Value)];
    }

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
