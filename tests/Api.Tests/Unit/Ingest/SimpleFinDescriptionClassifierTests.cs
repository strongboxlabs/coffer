using Coffer.Api.Ingest.SimpleFin;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// Unit tests for the SimpleFIN description classifier (ADR-0031
/// Phase 3b). Stateless pure function — covers every regex branch
/// + the ticker extraction.
/// </summary>
public sealed class SimpleFinDescriptionClassifierTests
{
    // ----- action classification -----

    [Theory]
    [InlineData("YOU BOUGHT ACME INDEX FUNDS S&P 500 ETF (ETFA) (Cash) Cash", "buy")]
    [InlineData("BOUGHT APPLE INC COMMON STOCK", "buy")]
    [InlineData("BUY 100 SHARES OF MSFT", "buy")]
    [InlineData("  YOU BOUGHT 50 SHARES", "buy")]  // leading whitespace tolerated
    public void Classify_returns_buy_for_buy_descriptions(string description, string expected)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData("YOU SOLD 50 SHARES OF AAPL", "sell")]
    [InlineData("SOLD 100 SHARES OF IDXC", "sell")]
    [InlineData("SELL TO OPEN", "sell")]
    public void Classify_returns_sell_for_sell_descriptions(string description, string expected)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData("DIVIDEND RECEIVED FROM ETFA", "dividend_cash")]
    [InlineData("DIVIDEND APPLE INC", "dividend_cash")]
    [InlineData("DIV PAYMENT", "dividend_cash")]
    public void Classify_returns_dividend_cash_for_dividend_descriptions(string description, string expected)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData("REINVESTMENT ACME INDEX (ETFA)", "dividend_reinvest")]
    [InlineData("REINVEST DIVIDEND", "dividend_reinvest")]
    [InlineData("DIVIDEND REINVESTMENT FROM ETFA", "dividend_reinvest")]
    public void Classify_returns_dividend_reinvest_for_reinvestment_descriptions(string description, string expected)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal(expected, action);
    }

    [Fact]
    public void Reinvestment_takes_precedence_over_plain_dividend()
    {
        // The classifier dispatches reinvestment patterns BEFORE
        // the bare-dividend pattern so "DIVIDEND REINVESTMENT"
        // doesn't get tagged as a cash dividend.
        var (action, _) = SimpleFinDescriptionClassifier.Classify(
            "DIVIDEND REINVESTMENT FROM ACME INDEX FUNDS (ETFA)");
        Assert.Equal("dividend_reinvest", action);
    }

    [Theory]
    [InlineData("TRANSFER FROM CHECKING")]
    [InlineData("TRANSFER TO SAVINGS")]
    [InlineData("TRANSFER 1000.00")]
    public void Classify_returns_transfer_for_transfer_descriptions(string description)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal("transfer", action);
    }

    [Theory]
    [InlineData("STARBUCKS COFFEE PURCHASE")]
    [InlineData("ATM WITHDRAWAL")]
    [InlineData("BANK FEE")]
    [InlineData("PAYROLL DEPOSIT")]
    [InlineData("INTEREST PAYMENT")]
    public void Classify_abstains_on_unrecognized_descriptions(string description)
    {
        var (action, _) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Null(action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_returns_null_for_null_or_blank_description(string? description)
    {
        var (action, ticker) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Null(action);
        Assert.Null(ticker);
    }

    // ----- case-insensitivity -----

    [Fact]
    public void Classify_is_case_insensitive_on_action_keywords()
    {
        // Most real SimpleFIN descriptions are uppercase, but the
        // classifier defends against mixed-case providers.
        var (action, _) = SimpleFinDescriptionClassifier.Classify("you bought etfa");
        Assert.Equal("buy", action);
    }

    // ----- ticker extraction -----

    [Theory]
    [InlineData("YOU BOUGHT ACME INDEX (ETFA) (Cash) Cash", "ETFA")]
    [InlineData("YOU SOLD (AAPL) APPLE STOCK", "AAPL")]
    [InlineData("DIVIDEND FROM (T) AT&T COMMON", "T")]
    [InlineData("REINVESTMENT (GOOGL) ALPHABET INC", "GOOGL")]
    public void Classify_extracts_ticker_from_parens(string description, string expected)
    {
        var (_, ticker) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Equal(expected, ticker);
    }

    [Fact]
    public void Classify_picks_first_uppercase_paren_group_when_multiple_present()
    {
        // SimpleFIN's pattern is "FUND NAME (TICKER) (Cash) Cash".
        // First match wins; (Cash) is mixed case so wouldn't match
        // even if we walked all groups.
        var (_, ticker) = SimpleFinDescriptionClassifier.Classify(
            "YOU BOUGHT ACME (ETFA) (Cash) Cash");
        Assert.Equal("ETFA", ticker);
    }

    [Theory]
    [InlineData("YOU BOUGHT ACME INDEX FUND")]            // no parens at all
    [InlineData("YOU BOUGHT (Cash) Cash")]                    // mixed-case in parens
    [InlineData("YOU BOUGHT (NASDAQ) MARKET INDEX")]          // 6 chars in parens
    [InlineData("YOU BOUGHT (BRK.B) BERKSHIRE HATHAWAY")]     // ticker with dot — not supported in Phase 3b
    public void Classify_returns_null_ticker_when_no_match(string description)
    {
        var (_, ticker) = SimpleFinDescriptionClassifier.Classify(description);
        Assert.Null(ticker);
    }

    [Fact]
    public void Classify_independent_action_and_ticker_outputs()
    {
        // A description can match a ticker without matching any
        // action keyword — the classifier surfaces both
        // independently so the orchestrator / editor can use
        // either signal.
        var (action, ticker) = SimpleFinDescriptionClassifier.Classify(
            "STOCK SPLIT FOR (AAPL) APPLE INC");
        Assert.Null(action);          // no STOCK SPLIT pattern
        Assert.Equal("AAPL", ticker); // ticker still extracted
    }
}
