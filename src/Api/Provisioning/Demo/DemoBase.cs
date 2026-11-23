using System.Text.Json;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>The two Moneydance category account types.</summary>
public enum MdCategoryType
{
    /// <summary>An income category, Moneydance type <c>i</c>.</summary>
    Income,

    /// <summary>An expense category, Moneydance type <c>e</c>.</summary>
    Expense,
}

/// <summary>
/// The hand-curated core of the Demo export, embedded from
/// <c>data/samples/moneydance-export-demo.base.json</c>: metadata, the root account,
/// every category, the fiat currencies, the security subtypes, the online-service
/// and misc rows, and <c>local_settings</c>. It carries no transaction, price,
/// split or real account; the generator writes all of those.
/// </summary>
/// <remarks>
/// <para>Parsed once and cached. The generator re-emits each base row verbatim,
/// from its raw text, so a Coffer-only key on a category row (the adjustment kind
/// override) passes through untouched.</para>
///
/// <para><b>Categories are addressed by (Moneydance type, segment array), never by
/// a display path.</b> Two base names contain the display separator
/// (<c>Books/Magazines</c>, <c>Water/Sewer</c>), and there are two
/// <c>Investment</c> roots, one income and one expense, so a path alone is
/// ambiguous. The index key is the type code and the segments joined with U+001F,
/// compared ordinally; loading refuses a name containing U+001F and two rows with
/// the same (type, path).</para>
/// </remarks>
public sealed class DemoBase
{
    /// <summary>The manifest resource name of the embedded base export.</summary>
    public const string ResourceName = "Coffer.Api.Provisioning.Demo.base.json";

    private const char KeySeparator = '\u001F';

    private static readonly Lazy<DemoBase> Embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, string> _categoryIdsByKey;
    private readonly HashSet<string> _categoryIds;
    private readonly Dictionary<string, (MdCategoryType Type, IReadOnlyList<string> Segments)> _pathsById;

    // The document is never disposed: every element below is a view into it, and the
    // embedded instance lives as long as the process.
    private DemoBase(JsonDocument document)
    {
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("the Demo base export's root is not a JSON object");
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("the Demo base export has no 'metadata' object");
        if (!root.TryGetProperty("all_items", out var allItems) || allItems.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the Demo base export has no 'all_items' array");

        Metadata = metadata;
        LocalSettings = root.TryGetProperty("local_settings", out var settings) && settings.ValueKind == JsonValueKind.Object
            ? settings
            : null;

        var items = new List<JsonElement>(allItems.GetArrayLength());
        var categoryRows = new Dictionary<string, (string Type, string Name, string? ParentId)>(StringComparer.Ordinal);
        string? rootId = null;
        string? rootCurrencyId = null;

        foreach (var item in allItems.EnumerateArray())
        {
            items.Add(item);
            var objType = Text(item, "obj_type");
            var id = Text(item, "id") ?? throw new InvalidDataException("a Demo base row has no 'id'");

            switch (objType)
            {
                case "acct":
                    var type = Text(item, "type")
                        ?? throw new InvalidDataException($"Demo base account {id} has no 'type'");
                    if (type == "r")
                    {
                        if (rootId is not null)
                            throw new InvalidDataException("the Demo base export has more than one root account");
                        rootId = id;
                        rootCurrencyId = Text(item, "currid");
                    }
                    else if (type is "i" or "e")
                    {
                        var name = Text(item, "name") ?? string.Empty;
                        if (name.Contains(KeySeparator, StringComparison.Ordinal))
                            throw new InvalidDataException($"Demo base category {id} has U+001F in its name");
                        categoryRows[id] = (type, name, Text(item, "parentid"));
                    }
                    else
                    {
                        // The generator owns every real account: a base one would be
                        // emitted twice or with no history behind its balance.
                        throw new InvalidDataException(
                            $"the Demo base export carries a type-{type} account ({id}); real accounts are generated");
                    }
                    break;
                case "txn" or "csnap" or "csplit" or "reminder":
                    throw new InvalidDataException(
                        $"the Demo base export carries a '{objType}' row ({id}); history is generated");
            }
        }

        RootAccountId = rootId ?? throw new InvalidDataException("the Demo base export has no root account");
        BaseCurrencyId = rootCurrencyId
            ?? throw new InvalidDataException("the Demo base export's root account has no 'currid'");
        Items = items;

        _categoryIdsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        _categoryIds = new HashSet<string>(StringComparer.Ordinal);
        _pathsById = new Dictionary<string, (MdCategoryType, IReadOnlyList<string>)>(StringComparer.Ordinal);
        foreach (var (id, row) in categoryRows)
        {
            var segments = SegmentsOf(id, categoryRows);
            _pathsById[id] = (row.Type == "i" ? MdCategoryType.Income : MdCategoryType.Expense, segments);
            var key = Key(row.Type, segments);
            if (!_categoryIdsByKey.TryAdd(key, id))
            {
                throw new InvalidDataException(
                    $"two Demo base categories share the path {Describe(key)}: {_categoryIdsByKey[key]} and {id}");
            }
            _categoryIds.Add(id);
        }
    }

    /// <summary>The embedded base export, parsed on first use.</summary>
    public static DemoBase Instance => Embedded.Value;

    /// <summary>The base export's metadata object. The generator writes its own,
    /// honest metadata; this one is kept as the shape's template.</summary>
    public JsonElement Metadata { get; }

    /// <summary>Every base row, in file order.</summary>
    public IReadOnlyList<JsonElement> Items { get; }

    /// <summary>The base export's <c>local_settings</c> object, when it has one.</summary>
    public JsonElement? LocalSettings { get; }

    /// <summary>The MD id of the root account every generated account hangs under.</summary>
    public string RootAccountId { get; }

    /// <summary>The MD id of the root account's currency, which every generated
    /// account is held in.</summary>
    public string BaseCurrencyId { get; }

    /// <summary>The MD ids of every base category.</summary>
    public IReadOnlySet<string> CategoryIds => _categoryIds;

    /// <summary>Parse a base export from UTF-8 JSON. The product uses
    /// <see cref="Instance"/>; this exists so tests can feed a malformed base.</summary>
    public static DemoBase Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var document = JsonDocument.Parse(utf8Json);
        try
        {
            return new DemoBase(document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The MD id of the category at (<paramref name="type"/>, <paramref name="segments"/>),
    /// such as <c>Category(Expense, "Vacation", "Travel", "Airfare")</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No base category has that type
    /// and path.</exception>
    public string Category(MdCategoryType type, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0) throw new ArgumentException("a category path has at least one segment", nameof(segments));

        var key = Key(Code(type), segments);
        return _categoryIdsByKey.TryGetValue(key, out var id)
            ? id
            : throw new InvalidOperationException($"category not in base: {Describe(key)}");
    }

    /// <summary>True when <paramref name="mdId"/> is a base category.</summary>
    public bool IsCategory(string mdId) => _categoryIds.Contains(mdId);

    /// <summary>The type and segment array of the base category <paramref name="mdId"/>.</summary>
    /// <exception cref="KeyNotFoundException">It is not a base category.</exception>
    public (MdCategoryType Type, IReadOnlyList<string> Segments) PathOf(string mdId) =>
        _pathsById.TryGetValue(mdId, out var path)
            ? path
            : throw new KeyNotFoundException($"{mdId} is not a base category");

    /// <summary>True when the category <paramref name="mdId"/> is
    /// (<paramref name="type"/>, <paramref name="segments"/>) or lies beneath it.</summary>
    public bool IsWithin(string mdId, MdCategoryType type, IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!_pathsById.TryGetValue(mdId, out var path) || path.Type != type || path.Segments.Count < segments.Count) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            if (!string.Equals(path.Segments[i], segments[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>The Moneydance type code of a category type.</summary>
    public static string Code(MdCategoryType type) => type switch
    {
        MdCategoryType.Income => "i",
        MdCategoryType.Expense => "e",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a category type"),
    };

    private static DemoBase LoadEmbedded()
    {
        using var stream = typeof(DemoBase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the embedded resource {ResourceName} is missing from the API assembly");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    private static string[] SegmentsOf(
        string id, Dictionary<string, (string Type, string Name, string? ParentId)> categories)
    {
        var segments = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        while (categories.TryGetValue(current, out var row))
        {
            if (!seen.Add(current))
                throw new InvalidDataException($"Demo base category {id} sits in a parent cycle");
            segments.Add(row.Name);
            if (row.ParentId is null) break;
            current = row.ParentId;
        }
        segments.Reverse();
        return [.. segments];
    }

    private static string Key(string typeCode, IEnumerable<string> segments) =>
        typeCode + KeySeparator + string.Join(KeySeparator, segments);

    private static string Describe(string key)
    {
        var parts = key.Split(KeySeparator);
        return parts[0] + " " + string.Join(" > ", parts.Skip(1));
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
