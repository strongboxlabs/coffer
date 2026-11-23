using System.Text.Json;
using System.Text.Json.Serialization;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Provisioning;

/// <summary>
/// Seeds the starter category tree into a new ledger (ADR-0071 D5) so a fresh
/// ledger is usable out of the box. Categories are accounts
/// (<c>account_type='category'</c> + <c>category_kind</c>, ADR-0017).
///
/// The embedded catalogue is GENERATED from the demo sample by
/// <c>data/samples/starter-categories.gen.mjs</c> (ADR-0091) — so a new ledger
/// and the Demo ledger get the SAME categories; a separate hand-written tree
/// drifts into an unrelated taxonomy. Applied on the empty-new-ledger paths; the
/// import paths bring their own categories, but for the Demo import those are
/// these same ones.
///
/// The catalogue is a tree of nodes, each with its own kind (income, expense or
/// adjustment), nested to any depth the generator allows (three levels). A
/// child's kind must be its parent's: a branch that mixes kinds is refused
/// before anything is written.
///
/// Singleton + stateless: the catalogue is parsed once from the embedded
/// resource. Callers pass a service-role <see cref="AppDbContext"/> (the ledger
/// + owner grant already exist; RLS is bypassed for the seed).
/// </summary>
public sealed class StarterCategoriesSeeder
{
    private const string ResourceName = "Coffer.Api.Provisioning.starter-categories.json";

    private static readonly Lazy<IReadOnlyList<StarterCategory>> Catalogue = new(Load);

    private readonly TimeProvider _clock;

    public StarterCategoriesSeeder(TimeProvider clock) => _clock = clock;

    /// <summary>Total categories, at every level, the seed will create.</summary>
    public int CategoryCount => Count(Catalogue.Value);

    private static int Count(IReadOnlyList<StarterCategory> nodes) =>
        nodes.Sum(c => 1 + Count(c.ChildNodes));

    /// <summary>
    /// Insert the starter categories into <paramref name="ledgerId"/>. Parent
    /// ids are minted client-side so the whole tree commits in one
    /// <c>SaveChanges</c>. Returns the number of categories created.
    /// </summary>
    /// <exception cref="InvalidOperationException">The catalogue is invalid (a
    /// child's kind differs from its parent's, see <see cref="Parse"/>);
    /// nothing is added.</exception>
    public async Task<int> SeedAsync(AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var catalogue = Catalogue.Value;
        var now = _clock.GetUtcNow().UtcDateTime;
        var created = 0;
        // A category has no currency of its own (ADR-0109 D9): its row carries the
        // ledger's reporting currency, so it never reads as foreign.
        var currency = await Db.Repositories.DisplayCurrency.ForLedgerAsync(db, ledgerId, cancellationToken)
            .ConfigureAwait(false);

        void Add(StarterCategory node, Guid? parentId)
        {
            var id = Guid.NewGuid();
            db.Accounts.Add(NewCategory(id, ledgerId, parentId, node.Name, node.Kind, currency, now));
            created++;
            foreach (var child in node.ChildNodes)
                Add(child, id);
        }

        foreach (var top in catalogue)
            Add(top, parentId: null);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static void CheckKinds(StarterCategory node, string path)
    {
        foreach (var child in node.ChildNodes)
        {
            var childPath = $"{path} > {child.Name}";
            if (child.Kind != node.Kind)
                throw new InvalidOperationException(
                    $"Starter category '{childPath}' is {child.Kind} under a {node.Kind} parent; a child must have its parent's kind.");
            CheckKinds(child, childPath);
        }
    }

    private static AccountRow NewCategory(
        Guid id, Guid ledgerId, Guid? parent, string name, string kind, string currency, DateTime now) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        ParentId = parent,
        Name = name,
        AccountType = "category",
        CategoryKind = kind,        // 'income' | 'expense' | 'adjustment' (ADR-0017 invariant, mig 224)
        CurrencyCode = currency,
        OpeningBalance = 0m,        // DB CHECK forces 0 on categories
        IsActive = true,
        CreatedAt = now,
    };

    private static IReadOnlyList<StarterCategory> Load()
    {
        using var stream = typeof(StarterCategoriesSeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        return Parse(stream);
    }

    /// <summary>
    /// Read a catalogue and refuse a branch whose child has a different kind
    /// from its parent: a category tree never mixes kinds within a branch.
    /// </summary>
    internal static IReadOnlyList<StarterCategory> Parse(Stream stream)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        var catalogue = JsonSerializer.Deserialize<StarterCatalogue>(stream, options)
            ?? throw new InvalidOperationException("Starter-categories resource deserialized to null.");
        foreach (var top in catalogue.Categories)
            CheckKinds(top, top.Name);
        return catalogue.Categories;
    }

    private sealed record StarterCatalogue(
        [property: JsonPropertyName("categories")] IReadOnlyList<StarterCategory> Categories);

    internal sealed record StarterCategory(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("children")] IReadOnlyList<StarterCategory>? Children = null)
    {
        [JsonIgnore]
        public IReadOnlyList<StarterCategory> ChildNodes => Children ?? [];
    }
}
