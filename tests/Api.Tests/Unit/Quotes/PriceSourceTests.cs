using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Unit.Quotes;

/// <summary>
/// The source-priority ladder (ADR-0070 D2/D4 + ADR-0084 D1).
/// <see cref="PriceSource.Rank"/> is the single runtime ladder the API writers
/// (<c>QuoteOrchestrator</c>, the manual add-price, and the trade-derived price
/// path) share; these pin its shape so a drift from the ADR fails fast.
/// </summary>
public sealed class PriceSourceTests
{
    [Theory]
    [InlineData(PriceSource.Manual, 3)]
    [InlineData(PriceSource.Fetch, 3)]
    [InlineData(PriceSource.Trade, 2)]
    [InlineData(PriceSource.Simplefin, 1)]
    [InlineData(PriceSource.Import, 0)]
    [InlineData("something-unknown", 0)]
    public void Rank_matches_the_ladder(string source, int expected) =>
        Assert.Equal(expected, PriceSource.Rank(source));

    [Fact]
    public void Ladder_ordering_is_import_lt_simplefin_lt_trade_lt_fetch_eq_manual()
    {
        // ADR-0084 D1: manual == Yahoo/fetch  >  trade  >  simplefin  >  import
        Assert.Equal(PriceSource.Rank(PriceSource.Manual), PriceSource.Rank(PriceSource.Fetch));
        Assert.True(PriceSource.Rank(PriceSource.Fetch) > PriceSource.Rank(PriceSource.Trade));
        Assert.True(PriceSource.Rank(PriceSource.Trade) > PriceSource.Rank(PriceSource.Simplefin));
        Assert.True(PriceSource.Rank(PriceSource.Simplefin) > PriceSource.Rank(PriceSource.Import));
    }

    [Fact]
    public void Trade_beats_import_and_simplefin_but_loses_to_feed_and_manual()
    {
        // A trade is a real execution — it outranks the one-time import seed and
        // the SimpleFIN intraday balance — but a Yahoo EOD close or a manual
        // gap-fill overwrites it, so the scheduled feed reclaims the day.
        var trade = PriceSource.Rank(PriceSource.Trade);
        Assert.True(trade > PriceSource.Rank(PriceSource.Import));
        Assert.True(trade > PriceSource.Rank(PriceSource.Simplefin));
        Assert.True(trade < PriceSource.Rank(PriceSource.Fetch));
        Assert.True(trade < PriceSource.Rank(PriceSource.Manual));
    }

    [Fact]
    public void Importer_floor_rule_agrees_with_the_ladder()
    {
        // ADR-0070 D4: the importer's SQL floor-rule (ON CONFLICT ... WHERE
        // source = 'import') means an 'import' write only ever refreshes another
        // 'import' row — it can replace nothing that outranks it. Equivalent to:
        // 'import' is the unique minimum of the ladder.
        var importRank = PriceSource.Rank(PriceSource.Import);
        Assert.True(importRank < PriceSource.Rank(PriceSource.Simplefin));
        Assert.True(importRank < PriceSource.Rank(PriceSource.Trade));
        Assert.True(importRank < PriceSource.Rank(PriceSource.Fetch));
        Assert.True(importRank < PriceSource.Rank(PriceSource.Manual));
    }
}
