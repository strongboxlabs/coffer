using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// The Demo's Home layout and the catalog reports it pins, as the SPA's
/// <c>src/Web/src/lib/demoHomeLayout.json</c> states them. The widget and catalog keys
/// are the SPA's, so the file lives beside its catalogs, whose drift test checks it; this
/// assembly embeds it, as it embeds the investment action words.
/// </summary>
/// <remarks>
/// <para>The layout is a version-2 Home document whose ids name no Coffer row. A Demo's
/// rows exist only after its import, so the file names them by reference, and
/// <see cref="Resolve"/> puts each id in its place:</para>
/// <list type="bullet">
///   <item><description><c>{"categoryRef": [segments]}</c>: the id of the expense
///   category at that path.</description></item>
///   <item><description><c>{"accountRef": role}</c>: the id of the account with that role
///   (<see cref="DemoAccountRoles"/>).</description></item>
///   <item><description><c>{"savedReportRef": n}</c>: the widget id of the tile showing
///   the n-th saved report the examples phase saved, counting from one.</description></item>
/// </list>
/// <para>A reference that finds nothing, or an object that names a reference this class
/// does not know, fails the step: a layout that pointed at nothing would show empty
/// widgets the Demo is meant to fill.</para>
/// </remarks>
internal sealed class DemoHomeLayout
{
    private const string ResourceName = "Coffer.Api.demo-home-layout.json";

    private const string CategoryRef = "categoryRef";
    private const string AccountRef = "accountRef";
    private const string SavedReportRef = "savedReportRef";

    private readonly JsonObject _layout;

    internal DemoHomeLayout(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _layout = document["layout"] as JsonObject
            ?? throw new InvalidOperationException("The Demo's Home layout file has no layout object.");
        CatalogPins = (document["catalogPins"] as JsonArray
                ?? throw new InvalidOperationException("The Demo's Home layout file lists no catalog pins."))
            .Select(p => p is JsonValue v && v.TryGetValue<string>(out var key)
                ? key
                : throw new InvalidOperationException("A catalog pin in the Demo's Home layout file is not a key."))
            .ToList();
    }

    /// <summary>The layout this build embeds.</summary>
    internal static DemoHomeLayout Embedded { get; } = Load();

    /// <summary>The catalog reports pinned beside the saved ones, by catalog key, in
    /// order.</summary>
    internal IReadOnlyList<string> CatalogPins { get; }

    /// <summary>
    /// The layout as the Home screen stores it, with each reference replaced by the id it
    /// names in this Demo.
    /// </summary>
    /// <param name="ids">The Demo's rows, as the first step found them.</param>
    /// <param name="savedReports">The saved reports' ids, in the order they were saved.</param>
    /// <exception cref="DemoSeedStepException">A reference finds nothing, or is not one
    /// of the three this layout may use.</exception>
    internal string Resolve(DemoSeedIds ids, IReadOnlyList<Guid> savedReports)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(savedReports);
        var problems = new List<string>();
        var resolved = ResolveNode(_layout, ids, savedReports, problems);
        if (problems.Count > 0)
            throw new DemoSeedStepException("the Home layout names what this Demo does not have: "
                + string.Join("; ", problems));
        return resolved!.ToJsonString();
    }

    private static JsonNode? ResolveNode(
        JsonNode? node, DemoSeedIds ids, IReadOnlyList<Guid> savedReports, List<string> problems)
    {
        switch (node)
        {
            case JsonObject reference when reference.Count == 1 && reference.Single() is var (refName, refValue)
                                           && refName.EndsWith("Ref", StringComparison.Ordinal):
                return JsonValue.Create(ResolveRef(refName, refValue, ids, savedReports, problems));
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (name, value) in obj) copy[name] = ResolveNode(value, ids, savedReports, problems);
                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array) items.Add(ResolveNode(item, ids, savedReports, problems));
                return items;
            default:
                return node?.DeepClone();
        }
    }

    private static string ResolveRef(
        string name, JsonNode? value, DemoSeedIds ids, IReadOnlyList<Guid> savedReports, List<string> problems)
    {
        switch (name)
        {
            case CategoryRef when value is JsonArray segments
                                  && segments.All(s => s is JsonValue v && v.TryGetValue<string>(out _)):
            {
                var path = new DemoCategoryRef(
                    MdCategoryType.Expense, segments.Select(s => s!.GetValue<string>()).ToList());
                if (ids.CategoriesByPath.TryGetValue(path.ToString(), out var category)) return category.ToString();
                problems.Add($"no category {path}");
                return string.Empty;
            }
            case AccountRef when value is JsonValue v && v.TryGetValue<string>(out var role):
                if (ids.Accounts.TryGetValue(role, out var account)) return account.ToString();
                problems.Add($"no account with the role {role}");
                return string.Empty;
            case SavedReportRef when value is JsonValue v && v.TryGetValue<int>(out var place):
                if (place >= 1 && place <= savedReports.Count)
                    return "report:saved:" + savedReports[place - 1].ToString().ToLowerInvariant();
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"no saved report {place} of the {savedReports.Count} saved"));
                return string.Empty;
            default:
                problems.Add($"{name} {value?.ToJsonString() ?? "null"} is not a reference this layout may use");
                return string.Empty;
        }
    }

    private static DemoHomeLayout Load()
    {
        using var stream = typeof(DemoHomeLayout).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        return new DemoHomeLayout(JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException("The Demo's Home layout file is not a JSON object."));
    }

    /// <summary>For a test: the layout of <paramref name="json"/>, a file of this shape.</summary>
    internal static DemoHomeLayout Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not a JSON object"));
}
