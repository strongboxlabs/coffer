using Dapper;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>holdings_market_value_as_of_set</c> must give the same answer however the
/// request is BATCHED: asking for many instants at once and asking for each alone
/// have to agree, row for row.
/// </summary>
/// <remarks>
/// These began as equivalence tests against the mig-172 per-instant feeder, which
/// migration 203 DROPPED — keeping it purely as a test reference would have been
/// two implementations of one rule held together by a test, which is the thing the
/// collapse existed to remove. What survives is the property that matters and that
/// one implementation can still be held to.
/// <para>
/// It is not a weaker property than it sounds. The function's whole trick is
/// algebraic: splits are folded into the legs so a running quantity becomes a prefix
/// sum in the LAST requested instant's basis, then divided back onto each instant.
/// The divisor only cancels if it is a factor of every term, and the merged
/// pseudo-event stream only lands on the right row if the ordering is right — both
/// of which depend on WHICH instants were asked for together. A one-instant call
/// exercises neither. Every scenario below therefore asks for the whole set and
/// compares against the same function asked one instant at a time, with the split
/// arithmetic additionally pinned to explicit values so it cannot drift as a set.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HoldingsValueAsOfSetBatchingTests
{
    private readonly PostgresFixture _fixture;

    public HoldingsValueAsOfSetBatchingTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d, int h = 0) =>
        new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    private sealed record ValueRow(
        Guid AccountId, Guid SecurityId, decimal Quantity, decimal MarketValue, string PricedFrom);

    /// <summary>The same function, asked for ONE instant — the unbatched case.</summary>
    private static async Task<List<ValueRow>> SingleAsync(
        PostgresFixture fixture, Guid ledgerId, DateTime asOf)
    {
        await using var conn = fixture.OpenServiceConnection();
        var rows = await conn.QueryAsync<ValueRow>(
            """
            SELECT account_id AS "AccountId", security_id AS "SecurityId",
                   quantity AS "Quantity", market_value AS "MarketValue",
                   priced_from AS "PricedFrom"
            FROM holdings_market_value_as_of_set(@ledgerId, ARRAY[@asOf]::timestamptz[], NULL)
            """,
            new { ledgerId, asOf });
        return rows.OrderBy(r => r.AccountId).ThenBy(r => r.SecurityId).ToList();
    }

    /// <summary>The batched form, filtered back to one instant for comparison.</summary>
    private static async Task<List<ValueRow>> BatchedAsync(
        PostgresFixture fixture, Guid ledgerId, DateTime[] asOfs, DateTime pick)
    {
        await using var conn = fixture.OpenServiceConnection();
        var rows = await conn.QueryAsync<ValueRow>(
            """
            SELECT account_id AS "AccountId", security_id AS "SecurityId",
                   quantity AS "Quantity", market_value AS "MarketValue",
                   priced_from AS "PricedFrom"
            FROM holdings_market_value_as_of_set(@ledgerId, @asOfs, NULL)
            WHERE as_of = @pick
            """,
            new { ledgerId, asOfs, pick });
        return rows.OrderBy(r => r.AccountId).ThenBy(r => r.SecurityId).ToList();
    }

    /// <summary>
    /// Ask for ALL the instants at once and compare each against the same function
    /// asked for that instant alone. The batched quantity is carried in the LAST
    /// instant's split basis and divided back, so only the all-at-once call
    /// exercises that division — a one-instant call has nothing to divide by.
    /// </summary>
    private static async Task AssertAgreeAtAllAsync(
        PostgresFixture fixture, Guid ledgerId, params DateTime[] instants)
    {
        foreach (var t in instants)
        {
            var expected = await SingleAsync(fixture, ledgerId, t);
            var actual = await BatchedAsync(fixture, ledgerId, instants, t);

            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].AccountId, actual[i].AccountId);
                Assert.Equal(expected[i].SecurityId, actual[i].SecurityId);
                Assert.Equal(expected[i].Quantity, actual[i].Quantity);
                Assert.Equal(expected[i].MarketValue, actual[i].MarketValue);
                Assert.Equal(expected[i].PricedFrom, actual[i].PricedFrom);
            }
        }
    }

    [Fact]
    public async Task Batching_is_invariant_with_no_splits()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 50m, 12m, Utc(2024, 4, 10));
        await ledger.AddSecurityPriceAsync(sec, 15m, Utc(2024, 6, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 1, 1), Utc(2024, 1, 10), Utc(2024, 2, 1),
            Utc(2024, 4, 10), Utc(2024, 7, 1), Utc(2024, 12, 31));
    }

    /// <summary>
    /// The case the rearrangement exists for. Quantity is carried in the LAST
    /// instant's basis, so every earlier instant divides by the splits between it
    /// and the end — and any instant sampled before a split must not inherit it.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_across_splits()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Splitter", "SPL");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecuritySplitAsync(sec, 2m, Utc(2024, 3, 1));      // 100 -> 200
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 40m, 50m, Utc(2024, 4, 10));
        await ledger.AddSecuritySplitAsync(sec, 3m, Utc(2024, 6, 1));      // 240 -> 720
        await ledger.AddSecurityPriceAsync(sec, 20m, Utc(2024, 7, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 1, 1), Utc(2024, 2, 1),
            Utc(2024, 3, 1),                      // exactly ON a split
            Utc(2024, 4, 10),                     // exactly ON a leg
            Utc(2024, 5, 1), Utc(2024, 6, 1), Utc(2024, 8, 1), Utc(2025, 1, 1));
    }

    /// <summary>
    /// A split landing on the SAME instant as a trade. Mig 172's canonical order
    /// puts a split before the legs sharing its instant, so it scales what was
    /// already held and NOT the trade arriving alongside it — which is why the
    /// folded factor uses a strictly-after test. Loosening that one comparison to
    /// >= doubles the position, and every other case in this file still passes,
    /// because no other fixture puts a split and a leg on the same timestamp.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_when_a_split_shares_an_instant_with_a_trade()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("SameInstant", "SMI");

        var shared = Utc(2024, 3, 1);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 20m, 100m, Utc(2024, 1, 10));
        // Both at `shared`: the split scales the 20 already held; the 100 does not
        // get scaled. Correct total is 40 + 100 = 140, not 40 + 200.
        await ledger.AddSecuritySplitAsync(sec, 2m, shared);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 50m, shared);
        await ledger.AddSecurityPriceAsync(sec, 55m, Utc(2024, 6, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 2, 1), shared, Utc(2024, 4, 1), Utc(2024, 12, 1));
    }

    /// <summary>
    /// Fractional and reverse ratios, which is where an inexact rearrangement
    /// shows up: 1.5 and 0.5 make the folded factors non-integers, so the divide
    /// back has to cancel to the cent rather than merely to a plausible number.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_with_fractional_and_reverse_splits()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Fractional", "FRC");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 30m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecuritySplitAsync(sec, 1.5m, Utc(2024, 2, 1));    // 30 -> 45
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 7m, 60m, Utc(2024, 3, 5));
        await ledger.AddSecuritySplitAsync(sec, 0.5m, Utc(2024, 5, 1));    // reverse split
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 3m, 130m, Utc(2024, 6, 5));
        await ledger.AddSecurityPriceAsync(sec, 140m, Utc(2024, 7, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 1, 15), Utc(2024, 2, 1), Utc(2024, 3, 5),
            Utc(2024, 4, 1), Utc(2024, 5, 1), Utc(2024, 6, 5), Utc(2024, 9, 1));
    }

    /// <summary>
    /// A position closed before the last instant. The batched form discovers
    /// positions up to the LAST instant and lets a zero total drop out, where the
    /// single-instant form never discovers it — the two must agree on the
    /// disappearance, in both directions across the closing date.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_for_a_position_closed_mid_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var kept = await ledger.AddSecurityAsync("Kept", "KPT");
        var sold = await ledger.AddSecurityAsync("Sold", "SLD");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, kept, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sold, 20m, 50m, Utc(2024, 1, 10));
        // Closed out entirely.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sold, -20m, 55m, Utc(2024, 5, 10));
        await ledger.AddSecurityPriceAsync(kept, 120m, Utc(2024, 6, 1));
        await ledger.AddSecurityPriceAsync(sold, 60m, Utc(2024, 6, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 2, 1), Utc(2024, 5, 10), Utc(2024, 5, 11), Utc(2024, 12, 31));
    }

    /// <summary>
    /// A price observed BEFORE a split, with no feed close after it, so both
    /// functions must back-adjust the per-share price onto the instant's basis.
    /// Getting this wrong double-counts the split and the market value comes out
    /// 2x — large, but only visible when the observation and the instant sit on
    /// opposite sides of a split.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_when_the_price_predates_a_split()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Stale", "STL");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, 120m, Utc(2024, 2, 1));   // last close
        await ledger.AddSecuritySplitAsync(sec, 4m, Utc(2024, 3, 1));     // nothing priced after

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 1, 15), Utc(2024, 2, 15), Utc(2024, 3, 15), Utc(2024, 12, 1));
    }

    /// <summary>
    /// No feed close at all, so both fall to tier 2 — the latest trade price on
    /// that (account, security) — and must report priced_from='trade'. The batched
    /// form resolves tier 2 through a short-circuited COALESCE rather than mig
    /// 172's IF/ELSE, so the tier choice itself is worth pinning.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_when_only_trade_prices_exist()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Unpriced", "UNP");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 5m, 130m, Utc(2024, 4, 10));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 2, 1), Utc(2024, 4, 10), Utc(2024, 8, 1));
    }

    /// <summary>
    /// Several brokerages holding the same security. The batched form partitions
    /// its running sum by (account, security); a partition that leaked across
    /// accounts would inflate one and empty the other, and both would still look
    /// like plausible numbers.
    /// </summary>
    [Fact]
    public async Task Batching_is_invariant_across_accounts_sharing_a_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var one = await ledger.AddInvestmentAccountAsync("Brokerage One");
        var two = await ledger.AddInvestmentAccountAsync("Brokerage Two");
        var sec = await ledger.AddSecurityAsync("Shared", "SHR");

        await ledger.AddInvestmentBuyAsync(
            one.Id, one.HoldingsAccountId!.Value, sec, 10m, 100m, Utc(2024, 1, 10));
        await ledger.AddInvestmentBuyAsync(
            two.Id, two.HoldingsAccountId!.Value, sec, 25m, 100m, Utc(2024, 2, 10));
        await ledger.AddSecuritySplitAsync(sec, 2m, Utc(2024, 3, 1));
        await ledger.AddInvestmentBuyAsync(
            one.Id, one.HoldingsAccountId!.Value, sec, 5m, 60m, Utc(2024, 4, 10));
        await ledger.AddSecurityPriceAsync(sec, 55m, Utc(2024, 6, 1));

        await AssertAgreeAtAllAsync(
            _fixture, ledger.LedgerId,
            Utc(2024, 1, 15), Utc(2024, 2, 10), Utc(2024, 3, 1),
            Utc(2024, 4, 10), Utc(2024, 12, 1));
    }
}
