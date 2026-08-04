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
/// and the Demo ledger get the SAME categories. It used to be a separate
/// hand-written tree, which is how the two drifted into unrelated taxonomies
/// (61 entries vs 108, only ~6 of ~22 top-level names in common). Applied on the
/// empty-new-ledger paths; the import paths still bring their own categories,
/// but for the Demo import those are now these same ones.
///
/// Singleton + stateless: the catalogue is parsed once from the embedded
/// resource. Callers pass a service-role <see cref="AppDbContext"/> (the ledger
/// + owner grant already exist; RLS is bypassed for the seed).
/// </summary>
public sealed class StarterCategoriesSeeder
{
    private const string ResourceName = "Coffer.Api.Provisioning.starter-categories.json";

    private static readonly Lazy<IReadOnlyList<StarterCategory>> Catalogue = new(Load);

    /// <summary>Total categories (parents + children) the seed will create.</summary>
    public int CategoryCount => Catalogue.Value.Sum(c => 1 + c.Children.Count);

    /// <summary>
    /// Insert the starter categories into <paramref name="ledgerId"/>. Parent
    /// ids are minted client-side so parents + children commit in one
    /// <c>SaveChanges</c>. Returns the number of categories created.
    /// </summary>
    public async Task<int> SeedAsync(AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var created = 0;

        foreach (var top in Catalogue.Value)
        {
            var parentId = Guid.NewGuid();
            db.Accounts.Add(NewCategory(parentId, ledgerId, parent: null, top.Name, top.Kind, now));
            created++;

            foreach (var childName in top.Children)
            {
                db.Accounts.Add(NewCategory(Guid.NewGuid(), ledgerId, parentId, childName, top.Kind, now));
                created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static AccountRow NewCategory(
        Guid id, Guid ledgerId, Guid? parent, string name, string kind, DateTime now) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        ParentId = parent,
        Name = name,
        AccountType = "category",
        CategoryKind = kind,        // 'income' | 'expense' (ADR-0017 invariant)
        CurrencyCode = "USD",
        OpeningBalance = 0m,        // DB CHECK forces 0 on categories
        IsActive = true,
        CreatedAt = now,
    };

    private static IReadOnlyList<StarterCategory> Load()
    {
        using var stream = typeof(StarterCategoriesSeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        var catalogue = JsonSerializer.Deserialize<StarterCatalogue>(stream, options)
            ?? throw new InvalidOperationException("Starter-categories resource deserialized to null.");
        return catalogue.Categories;
    }

    private sealed record StarterCatalogue(
        [property: JsonPropertyName("categories")] IReadOnlyList<StarterCategory> Categories);

    private sealed record StarterCategory(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("children")] IReadOnlyList<string>? ChildrenRaw = null)
    {
        [JsonIgnore]
        public IReadOnlyList<string> Children => ChildrenRaw ?? [];
    }
}
