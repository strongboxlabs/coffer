using System.Text.RegularExpressions;

namespace Coffer.Api.Ingest.SimpleFin;

/// <summary>
/// Heuristic classifier for SimpleFIN brokerage-account transaction
/// descriptions (ADR-0031 Phase 3b). SimpleFIN sends only free-text
/// descriptions for investment activity (no native action / ticker /
/// shares fields per the research summary), so the only signal is
/// pattern-matching the description string.
/// </summary>
/// <remarks>
/// <para>Conservative by design: <see cref="Classify"/> returns
/// <c>(null, null)</c> whenever the description doesn't match any
/// known pattern. The orchestrator's brokerage branch falls back
/// to a cash-flow row with <c>needs_review=true</c> in that case,
/// which is the safe outcome — a misclassified row would land in
/// the wrong action bucket silently.</para>
///
/// <para>Action vocabulary matches ADR-0027's catalog
/// (<c>buy</c> / <c>sell</c> / <c>dividend_cash</c> /
/// <c>dividend_reinvest</c> / <c>transfer</c>). Short / cover /
/// misc are NOT classified — out-of-scope per ADR-0027.
/// <c>buyx</c> / <c>sellx</c> / <c>divx</c> aren't inferred either;
/// the user picks the cross-account variant manually if needed.</para>
///
/// <para>Edge cases the regex deliberately doesn't handle today —
/// each is a follow-up if real data shows it matters:
/// <list type="bullet">
/// <item>Tickers with dots (BRK.B, SHOP.TO). Allowed: A–Z only,
///   1–5 chars.</item>
/// <item>Tickers longer than 5 chars (GOOGL is 5 — borderline OK,
///   most mutual fund tickers are 5).</item>
/// <item>Compound actions ("BUY TO COVER" → sell-equivalent;
///   unmatched today).</item>
/// </list></para>
/// </remarks>
public static class SimpleFinDescriptionClassifier
{
    /// <summary>
    /// Classify a SimpleFIN transaction description into an
    /// optional ADR-0027 action + optional ticker hint. Both
    /// values are independently nullable: an unrecognized action
    /// with a valid-looking ticker still surfaces the ticker
    /// (the user might want to attribute a free-text row to a
    /// security manually); a recognized action without a ticker
    /// surfaces the action alone (the user still has to pick the
    /// security).
    /// </summary>
    public static (string? Action, string? TickerHint) Classify(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return (null, null);

        var action = MatchAction(description);
        var ticker = MatchTicker(description);
        return (action, ticker);
    }

    private static string? MatchAction(string description)
    {
        // Order matters: more-specific patterns first so
        // "DIVIDEND REINVESTMENT" doesn't match the bare
        // "DIVIDEND" pattern.
        if (ReinvestRx.IsMatch(description)) return "dividend_reinvest";
        if (DividendRx.IsMatch(description)) return "dividend_cash";
        if (BuyRx.IsMatch(description)) return "buy";
        if (SellRx.IsMatch(description)) return "sell";
        if (TransferRx.IsMatch(description)) return "transfer";
        return null;
    }

    private static string? MatchTicker(string description)
    {
        var match = TickerRx.Match(description);
        return match.Success ? match.Groups[1].Value : null;
    }

    // -------- compiled regex patterns --------
    //
    // All anchored at the start of the description (after
    // optional whitespace) because SimpleFIN puts the verb first.
    // Case-insensitive defensively — most descriptions are
    // uppercase but providers can drift.

    private const RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>Matches "REINVESTMENT" / "REINVEST" / "DIVIDEND
    /// REINVEST(MENT)" at the start. Must precede
    /// <see cref="DividendRx"/> in the dispatch order so the
    /// DIVIDEND prefix doesn't claim a reinvestment first.</summary>
    private static readonly Regex ReinvestRx =
        new(@"^\s*(REINVEST(MENT)?|DIVIDEND\s+REINVEST(MENT)?)\b", Opts);

    /// <summary>Matches "DIVIDEND" / "DIV" / "DIVIDEND RECEIVED"
    /// at the start.</summary>
    private static readonly Regex DividendRx =
        new(@"^\s*(DIV(IDEND)?(\s+RECEIVED)?)\b", Opts);

    /// <summary>Matches "YOU BOUGHT" / "BOUGHT" / "BUY" at the
    /// start.</summary>
    private static readonly Regex BuyRx =
        new(@"^\s*(YOU\s+BOUGHT|BOUGHT|BUY)\b", Opts);

    /// <summary>Matches "YOU SOLD" / "SOLD" / "SELL" at the
    /// start.</summary>
    private static readonly Regex SellRx =
        new(@"^\s*(YOU\s+SOLD|SOLD|SELL)\b", Opts);

    /// <summary>Matches "TRANSFER" at the start. Direction
    /// (in vs out) is sign-discriminated per ADR-0029, not part
    /// of the classification.</summary>
    private static readonly Regex TransferRx =
        new(@"^\s*TRANSFER\b", Opts);

    /// <summary>Extracts a 1–5 character uppercase ticker from
    /// the first parenthesized group in the description. Matches
    /// e.g. <c>(ETFA)</c>, <c>(STKB)</c>. Won't match
    /// <c>(Cash)</c> (mixed case) or <c>(NASDAQ)</c> (6 chars).
    /// First match wins — SimpleFIN's pattern is "FUND NAME
    /// (TICKER) (Cash)" so the ticker comes first.</summary>
    private static readonly Regex TickerRx =
        new(@"\(([A-Z]{1,5})\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
