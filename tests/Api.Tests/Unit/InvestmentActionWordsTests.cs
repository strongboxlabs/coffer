using System.Reflection;

using Coffer.Api.Db.Repositories;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The investment action words the register's Action sort orders by (ADR-0028) are the
/// SPA's table, embedded from the one JSON file, and they name every action in the
/// catalog: a new action fails here until it has a word.
/// </summary>
public sealed class InvestmentActionWordsTests
{
    private static IReadOnlySet<string> CatalogActions() =>
        typeof(LedgerActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_table_names_every_catalog_action_and_nothing_else()
    {
        var catalog = CatalogActions();
        // Anchor: the reflection finds the catalog, not an empty set that any table matches.
        Assert.Contains(LedgerActions.DividendReinvest, catalog);
        Assert.Equal(catalog.Order(StringComparer.Ordinal), InvestmentActionWords.Words.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("buy", "Buy")]
    [InlineData("buyx", "BuyXfr")]
    [InlineData("sell", "Sell")]
    [InlineData("sellx", "SellXfr")]
    [InlineData("dividend_cash", "Dividend")]
    [InlineData("dividend_reinvest", "Reinvest")]
    [InlineData("divx", "DividendXfr")]
    [InlineData("transfer", "Xfr")]
    [InlineData("misc", "Misc")]
    [InlineData("transfer_shares", "Transfer shares")]
    // The derived action a cash leg facing an asset account carries is its own word, as
    // the SPA's formatInvestmentAction reads it.
    [InlineData("Xfr", "Xfr")]
    public void Each_action_sorts_by_the_word_its_chip_shows(string action, string word)
    {
        Assert.Equal(word, InvestmentActionWords.WordOf.Compile()(action));
    }

    [Fact]
    public void No_action_has_no_word()
    {
        Assert.Null(InvestmentActionWords.WordOf.Compile()(null));
    }
}
