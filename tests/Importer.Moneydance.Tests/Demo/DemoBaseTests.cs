using System.Text;
using System.Text.Json;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The embedded base export: its categories are addressed by (type, segments),
/// which is what tells the two <c>Investment</c> roots apart and lets a name hold
/// the display separator; and it carries no history and no real account, which the
/// generator owns.
/// </summary>
public sealed class DemoBaseTests
{
    private static DemoBase Base => DemoBase.Instance;

    [Fact]
    public void A_name_with_a_slash_is_one_segment()
    {
        var books = Base.Category(MdCategoryType.Expense, "Personal", "Books/Magazines");
        var water = Base.Category(MdCategoryType.Expense, "Bills", "Water/Sewer");

        Assert.True(Base.IsCategory(books));
        Assert.True(Base.IsCategory(water));
        Assert.NotEqual(books, water);
    }

    [Fact]
    public void The_two_Investment_roots_are_told_apart_by_type()
    {
        var expenseRoot = Base.Category(MdCategoryType.Expense, "Investment");
        var incomeRoot = Base.Category(MdCategoryType.Income, "Investment");
        Assert.NotEqual(expenseRoot, incomeRoot);

        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Investment", "Trading Commission")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Income, "Investment", "Dividends")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Income, "Investment", "Interest Received")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Income, "Investment", "Long-Term Cap. Gains")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Income, "Investment", "Short-Term Cap. Gains")));

        var wrongRoot = Assert.Throws<InvalidOperationException>(
            () => Base.Category(MdCategoryType.Income, "Investment", "Trading Commission"));
        Assert.Equal("category not in base: i Investment > Trading Commission", wrongRoot.Message);
    }

    [Fact]
    public void The_depth_3_branch_and_the_grown_rows_resolve()
    {
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Vacation", "Travel", "Airfare")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Vacation", "Travel", "Car Rental")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Bills", "Internet")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Childcare", "Baby Supplies")));
        Assert.True(Base.IsCategory(Base.Category(MdCategoryType.Expense, "Valuation Adjustment")));
    }

    [Fact]
    public void An_unknown_path_names_its_type_and_segments()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Base.Category(MdCategoryType.Expense, "Vacation", "Travel", "Cruise"));

        Assert.Equal("category not in base: e Vacation > Travel > Cruise", error.Message);
    }

    [Fact]
    public void The_base_carries_108_categories_one_root_and_no_history()
    {
        Assert.Equal(108, Base.CategoryIds.Count);

        var objTypes = Base.Items.Select(i => i.GetProperty("obj_type").GetString()).ToList();
        Assert.DoesNotContain("txn", objTypes);
        Assert.DoesNotContain("csnap", objTypes);
        Assert.DoesNotContain("csplit", objTypes);
        Assert.DoesNotContain("reminder", objTypes);

        var accountTypes = Base.Items
            .Where(i => i.GetProperty("obj_type").GetString() == "acct")
            .Select(i => i.GetProperty("type").GetString())
            .ToList();
        Assert.Single(accountTypes, t => t == "r");
        Assert.All(accountTypes, t => Assert.Contains(t, new[] { "r", "e", "i" }));

        // The dropped curated security is gone with its account.
        Assert.DoesNotContain(Base.Items, i =>
            i.GetProperty("obj_type").GetString() == "curr" && i.TryGetProperty("type", out var type) && type.GetString() == "s");
        Assert.Equal(47, objTypes.Count(t => t == "curr"));
    }

    [Fact]
    public void Local_settings_name_no_dropped_id()
    {
        var settings = Base.LocalSettings;
        Assert.NotNull(settings);

        var knownIds = Base.Items.Select(i => i.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var property in settings.Value.EnumerateObject())
        {
            var head = property.Name.Split('.')[0];
            if (Guid.TryParse(head, out _)) Assert.Contains(head, knownIds);
        }
    }

    [Fact]
    public void The_root_and_its_currency_are_found()
    {
        var root = Base.Items.Single(i =>
            i.GetProperty("obj_type").GetString() == "acct" && i.GetProperty("type").GetString() == "r");

        Assert.Equal(root.GetProperty("id").GetString(), Base.RootAccountId);
        Assert.Equal(root.GetProperty("currid").GetString(), Base.BaseCurrencyId);
        Assert.Contains(Base.Items, i =>
            i.GetProperty("obj_type").GetString() == "curr" && i.GetProperty("id").GetString() == Base.BaseCurrencyId);
    }

    [Fact]
    public void A_category_name_with_the_key_separator_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(
            Category("c1", "e", "Bad\u001FName", "root")));

        Assert.Contains("U+001F", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_categories_with_the_same_type_and_path_are_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(
            Category("c1", "e", "Home", "root"),
            Category("c2", "e", "Home", "root")));

        Assert.Contains("e Home", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_path_under_both_types_is_allowed()
    {
        var parsed = Parse(Category("c1", "e", "Investment", "root"), Category("c2", "i", "Investment", "root"));

        Assert.Equal("c1", parsed.Category(MdCategoryType.Expense, "Investment"));
        Assert.Equal("c2", parsed.Category(MdCategoryType.Income, "Investment"));
    }

    [Theory]
    [InlineData("""{"obj_type":"txn","id":"t1","acctid":"root","dt":"20260101"}""")]
    [InlineData("""{"obj_type":"acct","id":"b1","type":"b","name":"Checking","parentid":"root"}""")]
    public void A_base_with_history_or_a_real_account_is_refused(string row)
    {
        Assert.Throws<InvalidDataException>(() => Parse(row));
    }

    private static string Category(string id, string type, string name, string parent) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["obj_type"] = "acct",
            ["id"] = id,
            ["type"] = type,
            ["name"] = name,
            ["parentid"] = parent,
        });

    private static DemoBase Parse(params string[] rows)
    {
        var json = """{"metadata":{},"all_items":[{"obj_type":"acct","id":"root","type":"r","name":"Root","currid":"usd"},"""
            + string.Join(",", rows)
            + """],"local_settings":{}}""";
        return DemoBase.Parse(Encoding.UTF8.GetBytes(json));
    }
}
