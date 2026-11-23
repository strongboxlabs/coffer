using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Seeds a large, internally consistent ledger for the on-demand scale lane
/// (follow-ups.md → Snapshot restore performance).
/// </summary>
/// <remarks>
/// <para>Deliberately NOT a committed fixture. The representative fixture
/// (<c>data/samples/reference-ledger.json</c>) exists to cover importer
/// SHAPES and is ~20 KB for a few dozen transactions; the same file at 50000
/// transactions would be tens of megabytes in every clone. This harness is about
/// SCALE, not import fidelity, so it seeds set-based straight into Postgres —
/// nothing large enters the repo, and a 50k-transaction ledger builds in seconds
/// instead of minutes through the import pipeline.</para>
/// <para>Row ids are derived from the row index rather than random, so a seeded
/// ledger is reproducible run to run — which matters when comparing timings.</para>
/// <para>Consistency is established by the product's own code, not by this
/// seeder guessing: it writes the transaction graph plus one open lot per
/// acquiring leg, then calls <c>recompute_holdings_cost_basis</c> once, which is
/// what derives holdings quantity/cost_basis, closes lots against sells, and
/// writes realized_gains. So the seeded ledger satisfies the same systemic
/// invariants <c>ReferenceLedgerInvariantsTests</c> asserts, by construction.</para>
/// <para>The graph's SHAPES are the ones the write paths build, because a reader's
/// cost depends on them: about 10% of bank rows are multi-splits (some with every
/// line to one category), 5% are transfers between two real accounts, and each
/// holding has a buy with a fee, a <c>buy_xfr</c> funded from a second brokerage, a
/// one-posting dividend and cash transfer, and an in-kind share transfer. Memos and
/// tags sit only where the write paths can put them, including tag sets over 20.
/// <see cref="AssertInvariantsAsync"/> checks all of it.</para>
/// </remarks>
public static class StressLedger
{
    /// <summary>Scale of a seeded stress ledger.</summary>
    /// <param name="BankTxns">
    /// Bank transactions (the bulk of the graph): one-splits to a category,
    /// transfers between banks, and multi-splits of two to five lines.
    /// </param>
    /// <param name="Holdings">Distinct securities held, each its own holding.</param>
    /// <param name="BuysPerHolding">Open lots created per holding.</param>
    /// <param name="SellsPerHolding">Disposals per holding — these drive the FIFO walk.</param>
    /// <param name="HeaderPayloadBytes">
    /// Approximate size of each bank header's <c>provider_raw_payload</c>. Imported
    /// headers carry the verbatim Moneydance JSON, far wider than the row's own
    /// columns — the default stands in for that width; 0 gives a thin row, for
    /// A/B comparison.
    /// </param>
    /// <param name="InvestingFromYears">
    /// Years after the bank stream's start that every investment event is dated from: 0
    /// starts them with it; more gives a ledger that banked for years before it invested.
    /// </param>
    public readonly record struct Scale(
        int BankTxns,
        int Holdings,
        int BuysPerHolding,
        int SellsPerHolding,
        int HeaderPayloadBytes = 2304,
        int InvestingFromYears = 0)
    {
        /// <summary>
        /// Investment events seeded per holding beside its buys and sells: a buy with
        /// a fee, a <c>buy_xfr</c> funded from the second brokerage, a one-posting
        /// dividend, a one-posting cash transfer and an in-kind share transfer.
        /// </summary>
        public const int ShapeEventsPerHolding = 5;

        /// <summary>Total transaction headers this scale produces.</summary>
        public int TotalTxns =>
            BankTxns + (Holdings * (BuysPerHolding + SellsPerHolding + ShapeEventsPerHolding));

        /// <summary>
        /// Lot rows this scale produces: one per acquiring leg, which is every buy,
        /// the buy with a fee, the <c>buy_xfr</c> and the in-kind transfer's
        /// destination leg.
        /// </summary>
        public int TotalLots => Holdings * (BuysPerHolding + 3);

        /// <summary>
        /// Holding rows this scale produces: every security is held in the brokerage
        /// that trades it and, through the in-kind transfers, in the second brokerage.
        /// </summary>
        public int TotalHoldings => Holdings * 2;

        /// <summary>~50k transactions across 200 holdings — the scale the on-demand lane targets.</summary>
        public static Scale Default => new(
            BankTxns: 45_600, Holdings: 200, BuysPerHolding: 20, SellsPerHolding: 2);

        /// <summary>
        /// Same order of investment transactions as <see cref="Default"/>, but
        /// concentrated: few holdings, hundreds of events each.
        /// </summary>
        /// <remarks>
        /// Breadth and depth are NOT interchangeable here. <see cref="Default"/>
        /// spreads 4400 investment transactions over 200 holdings — 22 events
        /// each — and measures the FIFO recompute at 0.3s, which looks like "the
        /// walk is cheap". But its inner loop re-queries the open-lot set once per event,
        /// so cost grows with events × open-lots WITHIN a holding, not with the
        /// number of holdings. 20 holdings × 500 events × up to 400 open lots is
        /// the shape that actually probes it — a long-held position with years of
        /// activity, which the 200-holding fixture does not have.
        /// </remarks>
        public static Scale Deep => new(
            BankTxns: 0, Holdings: 20, BuysPerHolding: 400, SellsPerHolding: 100);

        /// <summary>A small scale for proving the harness itself works quickly.</summary>
        public static Scale Smoke => new(
            BankTxns: 500, Holdings: 10, BuysPerHolding: 4, SellsPerHolding: 1);
    }

    /// <summary>What a seed produced: its duration and the accounts a caller drives.</summary>
    /// <param name="Elapsed">How long the seed took.</param>
    /// <param name="BrokerageId">The brokerage every investment event belongs to.</param>
    /// <param name="FundingBrokerageId">
    /// The second brokerage: it funds the <c>buy_xfr</c> events and receives the
    /// in-kind transfers, so it is a counterpart in both.
    /// </param>
    /// <param name="NotesElapsed">How long storing the memo-and-tags presentation of the
    /// seeded rows took, through the upgrade's own backfill.</param>
    public readonly record struct Seeded(
        TimeSpan Elapsed, Guid BrokerageId, Guid FundingBrokerageId, TimeSpan NotesElapsed = default);

    /// <summary>The year the seed's bank stream starts, on its first day: every seeded date
    /// counts from it.</summary>
    public const int BaseYear = 2010;

    /// <summary>The <paramref name="n"/>th seeded bank account (1-5).</summary>
    public static Guid BankId(Guid ledgerId, int n) => DerivedId("10", ledgerId, n);

    /// <summary>The <paramref name="n"/>th seeded expense category (1-20).</summary>
    public static Guid CategoryId(Guid ledgerId, int n) => DerivedId("11", ledgerId, n);

    /// <summary>The income category the dividends post to.</summary>
    public static Guid IncomeCategoryId(Guid ledgerId) => DerivedId("12", ledgerId, 1);

    /// <summary>The <paramref name="n"/>th seeded tag's name (1-64).</summary>
    public static string TagName(int n) => $"stress-tag-{n:D2}";

    /// <summary>The <paramref name="n"/>th seeded tag's id (1-64).</summary>
    public static Guid TagId(Guid ledgerId, int n) => DerivedId("13", ledgerId, n);

    /// <summary>
    /// The id the seed function derives for row <paramref name="index"/> under
    /// <paramref name="prefix"/>: the same <c>prefix || disc || ... || hex(index)</c>
    /// expression, so callers address seeded rows without a lookup.
    /// </summary>
    private static Guid DerivedId(string prefix, Guid ledgerId, int index) =>
        Guid.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{prefix}{ledgerId.ToString("N")[..6]}-0000-4000-8000-{index:x12}"));

    /// <summary>
    /// Seed <paramref name="ledger"/> to <paramref name="scale"/> and return how
    /// long the seed took. Call it once per ledger — ids collide otherwise.
    /// </summary>
    public static async Task<TimeSpan> SeedAsync(
        TestDatabase database,
        SyntheticLedger ledger,
        Scale scale,
        CancellationToken cancellationToken = default) =>
        (await SeedLedgerAsync(database, ledger, scale, cancellationToken).ConfigureAwait(false)).Elapsed;

    /// <summary>
    /// <see cref="SeedAsync"/>, also returning the two brokerages it created.
    /// </summary>
    public static async Task<Seeded> SeedLedgerAsync(
        TestDatabase database,
        SyntheticLedger ledger,
        Scale scale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        // The brokerages (and their sibling holdings accounts) come from the real
        // helper so the account shape matches what the product creates.
        var broker = await ledger
            .AddInvestmentAccountAsync("stress-brokerage", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var funding = await ledger
            .AddInvestmentAccountAsync("stress-brokerage-2", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await using var db = database.NewServiceFactory().Create();
        // The seed of a Default ledger runs well past Npgsql's 30s default.
        db.Database.SetCommandTimeout(600);
        await db.Database.ExecuteSqlRawAsync(SeedFunctionSql, cancellationToken).ConfigureAwait(false);

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // The seed writes, then recomputes: the ledger write lock (migration 275) is
            // taken first, as every writer that recomputes takes it.
            await db.SeedUnderLedgerLockAsync(ledger.LedgerId, () =>
                db.Database.ExecuteSqlInterpolatedAsync(
                    $@"SELECT stress_seed_ledger(
                           {ledger.LedgerId}, {broker.Id}, {broker.HoldingsAccountId!.Value},
                           {funding.Id}, {funding.HoldingsAccountId!.Value},
                           {scale.BankTxns}, {scale.Holdings}, {scale.BuysPerHolding}, {scale.SellsPerHolding},
                           {scale.HeaderPayloadBytes}, {scale.InvestingFromYears})",
                    cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Dropped once used. The seed runs in the ordinary suite too
            // (StressLedgerTests), and a function left behind is one more body for
            // any catalog scan over pg_proc the same test runs to find.
            await db.Database.ExecuteSqlRawAsync(DropSeedFunctionSql, CancellationToken.None)
                .ConfigureAwait(false);
        }
        var seedElapsed = startedAt.Elapsed;

        // The seed writes past the API, so the stored memo-and-tags presentation is
        // computed as the upgrade computes it for existing rows: the upgrade step's own
        // code, on the role migrations run as, over this ledger. Never written by hand.
        var notesWatch = System.Diagnostics.Stopwatch.StartNew();
        await Coffer.Api.Migrations.LegNotesBackfill.RunAsync(
                database.ServiceConnectionString, ledger.LedgerId, cancellationToken)
            .ConfigureAwait(false);
        var notesElapsed = notesWatch.Elapsed;

        // The investment writers store the price of every trade they write, so a ledger
        // they produced has one for each trade day. The seed writes past them, so it is
        // stored here as they store it. Without it a position is priced by the "last
        // trade" tier instead of its stored price, a state no writer produces.
        await ledger.RecomputeTradePricesAsync(cancellationToken).ConfigureAwait(false);

        // A bulk-seeded table has no statistics until autovacuum catches up, which
        // it will not do inside a test run. The planner then works from defaults —
        // measured here as 22 estimated rows against 50000 actual — and picks
        // plans no production database would pick.
        //
        // This is not a detail. Comparing migration 193's ctid keyset against 197's
        // primary-key keyset on the unanalyzed table showed the two within noise of
        // each other, because both fell back to a bitmap scan plus a 159 MB
        // external-merge sort. After ANALYZE the same comparison is 555 ms vs
        // 39 ms, and 197's index scan appears. Anything this lane concludes about
        // query PLANS is only as good as the statistics behind them. The tag tables
        // are in the list because every tag filter, tag report and "(untagged)"
        // bucket plans its joins against them.
        await db.Database.ExecuteSqlRawAsync(
            "ANALYZE accounts; ANALYZE securities; ANALYZE txn_headers; " +
            "ANALYZE txn_legs; ANALYZE holdings; ANALYZE lots; " +
            "ANALYZE tags; ANALYZE txn_header_tags; ANALYZE txn_leg_tags; " +
            "ANALYZE txn_header_account_balances; ANALYZE txn_leg_note_tags; " +
            "ANALYZE security_prices;",
            cancellationToken).ConfigureAwait(false);

        return new Seeded(seedElapsed, broker.Id, funding.Id, notesElapsed);
    }

    /// <summary>
    /// Assert the seeded ledger is one the product could have written: the posting
    /// structure the write paths build, the annotation placements they can reach, and
    /// every shape this seeder promises. Fails with the full list of violations.
    /// </summary>
    public static async Task AssertInvariantsAsync(
        TestDatabase database,
        Guid ledgerId,
        Scale scale,
        CancellationToken cancellationToken = default)
    {
        var violations = await FindInvariantViolationsAsync(database, ledgerId, scale, cancellationToken)
            .ConfigureAwait(false);
        Xunit.Assert.True(violations.Count == 0,
            "stress seed violates its invariants:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>The violations <see cref="AssertInvariantsAsync"/> reports, one line each.</summary>
    /// <remarks>
    /// <para>The seeder writes straight into Postgres, so nothing but this check stops
    /// it from writing a state no write path can reach, and a timing taken over such a
    /// state measures a ledger no user can have. Each placement rule below is one the
    /// repositories enforce on the baseline AND one the per-side memo and tags rules
    /// keep, so the same seed serves the before and the after measurement:</para>
    /// <list type="bullet">
    /// <item>a posting is two legs sharing one index (<c>AddPostingLegs</c>, the
    /// investment <c>BuildPostings</c>);</item>
    /// <item>a one-posting header carries no header memo or tags; its values sit on
    /// its legs, either side (a PATCH may name either account of a one-split as the
    /// source);</item>
    /// <item>an investment event's legs carry none (the investment writers write the
    /// header only), and every one of them has a posting role;</item>
    /// <item>on a split, the opposite side holds a value only on the first line: a
    /// one-split's opposite override survives splitting it, and no write reaches the
    /// opposite leg of a line added afterwards;</item>
    /// <item>no blank memo, and no two tags whose names differ only by case (the tag
    /// dictionary refuses a ledger holding both);</item>
    /// <item>every header's originating account keeps the invariants every writer
    /// keeps.</item>
    /// </list>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> FindInvariantViolationsAsync(
        TestDatabase database,
        Guid ledgerId,
        Scale scale,
        CancellationToken cancellationToken = default)
    {
        await using var db = database.NewServiceFactory().Create();
        db.Database.SetCommandTimeout(600);
        var violations = new List<string>();

        async Task<int> CountAsync(FormattableString sql) =>
            await db.Database.SqlQuery<int>(sql).SingleAsync(cancellationToken).ConfigureAwait(false);

        async Task NoneAsync(string rule, FormattableString sql)
        {
            var n = await CountAsync(sql).ConfigureAwait(false);
            if (n != 0) violations.Add($"{rule}: {n} offending rows");
        }

        async Task SomeAsync(string shape, FormattableString sql)
        {
            var n = await CountAsync(sql).ConfigureAwait(false);
            if (n == 0) violations.Add($"missing shape: {shape}");
        }

        // ----- Structure ----------------------------------------------------
        await NoneAsync("a posting is two legs on two accounts", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM txn_legs WHERE ledger_id = {ledgerId}
                 GROUP BY header_id, posting_index
                HAVING count(*) <> 2 OR count(DISTINCT account_id) <> 2) d
            """).ConfigureAwait(false);
        // An in-kind transfer's postings are each zero cash against the moved cost,
        // which balances across the pair, not within one posting.
        await NoneAsync("a posting balances", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
                 WHERE l.ledger_id = {ledgerId}
                   AND h.action IS DISTINCT FROM 'transfer_shares'
                 GROUP BY l.header_id, l.posting_index
                HAVING sum(l.amount) <> 0) d
            """).ConfigureAwait(false);
        await NoneAsync("a header balances", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM txn_legs WHERE ledger_id = {ledgerId}
                 GROUP BY header_id HAVING sum(amount) <> 0) d
            """).ConfigureAwait(false);
        await NoneAsync("posting indexes run from 0 without gaps", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM txn_legs WHERE ledger_id = {ledgerId}
                 GROUP BY header_id
                HAVING min(posting_index) <> 0
                    OR max(posting_index) + 1 <> count(DISTINCT posting_index)) d
            """).ConfigureAwait(false);
        await NoneAsync("header_total_postings is the header's posting count", $"""
            SELECT count(*)::int AS "Value"
              FROM txn_legs l
              JOIN (SELECT header_id, count(DISTINCT posting_index)::int AS n
                      FROM txn_legs WHERE ledger_id = {ledgerId} GROUP BY header_id) c
                ON c.header_id = l.header_id
             WHERE l.ledger_id = {ledgerId} AND l.header_total_postings <> c.n
            """).ConfigureAwait(false);
        await NoneAsync("account_postings_on_header is the account's posting count", $"""
            SELECT count(*)::int AS "Value"
              FROM txn_legs l
              JOIN (SELECT header_id, account_id, count(DISTINCT posting_index)::int AS n
                      FROM txn_legs WHERE ledger_id = {ledgerId}
                     GROUP BY header_id, account_id) c
                ON c.header_id = l.header_id AND c.account_id = l.account_id
             WHERE l.ledger_id = {ledgerId} AND l.account_postings_on_header <> c.n
            """).ConfigureAwait(false);

        var headers = await CountAsync(
            $"""SELECT count(*)::int AS "Value" FROM txn_headers WHERE ledger_id = {ledgerId}""")
            .ConfigureAwait(false);
        if (headers != scale.TotalTxns)
            violations.Add($"header count: {headers}, the scale declares {scale.TotalTxns}");
        var lots = await CountAsync(
            $"""SELECT count(*)::int AS "Value" FROM lots WHERE ledger_id = {ledgerId}""")
            .ConfigureAwait(false);
        if (lots != scale.TotalLots)
            violations.Add($"lot count: {lots}, the scale declares {scale.TotalLots}");
        var holdings = await CountAsync(
            $"""SELECT count(*)::int AS "Value" FROM holdings WHERE ledger_id = {ledgerId}""")
            .ConfigureAwait(false);
        if (holdings != scale.TotalHoldings)
            violations.Add($"holding count: {holdings}, the scale declares {scale.TotalHoldings}");
        await NoneAsync("open lots reconcile with the holding", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM holdings h
                  LEFT JOIN lots l ON l.holding_id = h.id AND l.is_closed = FALSE
                 WHERE h.ledger_id = {ledgerId}
                 GROUP BY h.id, h.quantity
                HAVING COALESCE(sum(l.quantity), 0) <> h.quantity) d
            """).ConfigureAwait(false);
        await NoneAsync("no holding is short", $"""
            SELECT count(*)::int AS "Value" FROM holdings
             WHERE ledger_id = {ledgerId} AND quantity < 0
            """).ConfigureAwait(false);

        // ----- Placements the write paths can reach --------------------------
        await NoneAsync("a one-posting header carries no header memo", $"""
            SELECT count(*)::int AS "Value" FROM txn_headers h
             WHERE h.ledger_id = {ledgerId} AND h.memo IS NOT NULL
               AND (SELECT count(DISTINCT l.posting_index) FROM txn_legs l
                     WHERE l.header_id = h.id) = 1
            """).ConfigureAwait(false);
        await NoneAsync("a one-posting header carries no header tags", $"""
            SELECT count(*)::int AS "Value" FROM txn_header_tags ht
             WHERE ht.ledger_id = {ledgerId}
               AND (SELECT count(DISTINCT l.posting_index) FROM txn_legs l
                     WHERE l.header_id = ht.header_id) = 1
            """).ConfigureAwait(false);
        await NoneAsync("an investment event's legs carry no memo or tags", $"""
            SELECT count(*)::int AS "Value"
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE l.ledger_id = {ledgerId} AND h.action IS NOT NULL
               AND (l.leg_memo IS NOT NULL
                    OR EXISTS (SELECT 1 FROM txn_leg_tags t WHERE t.leg_id = l.id))
            """).ConfigureAwait(false);
        await NoneAsync("an investment event's legs carry a posting role, a bank row's none", $"""
            SELECT count(*)::int AS "Value"
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE l.ledger_id = {ledgerId}
               AND (h.action IS NULL) = (l.posting_role IS NOT NULL)
            """).ConfigureAwait(false);
        // The split side: the real account on every line. Every seeded split has
        // one, and only its legs take values past the first line.
        await NoneAsync("a split has a real account on every line", $"""
            SELECT count(*)::int AS "Value" FROM txn_headers h
             WHERE h.ledger_id = {ledgerId} AND h.action IS NULL
               AND (SELECT count(DISTINCT posting_index) FROM txn_legs WHERE header_id = h.id) > 1
               AND NOT EXISTS (
                   SELECT 1 FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                    WHERE l.header_id = h.id AND a.account_type <> 'category'
                    GROUP BY l.account_id
                   HAVING count(DISTINCT l.posting_index) =
                          (SELECT count(DISTINCT posting_index) FROM txn_legs WHERE header_id = h.id))
            """).ConfigureAwait(false);
        await NoneAsync("a split's opposite side holds a value only on its first line", $"""
            SELECT count(*)::int AS "Value"
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE l.ledger_id = {ledgerId} AND h.action IS NULL
               AND l.posting_index > 0
               AND a.account_type = 'category'
               AND (l.leg_memo IS NOT NULL
                    OR EXISTS (SELECT 1 FROM txn_leg_tags t WHERE t.leg_id = l.id))
            """).ConfigureAwait(false);
        await NoneAsync("no blank memo", $"""
            SELECT (SELECT count(*) FROM txn_headers
                     WHERE ledger_id = {ledgerId} AND memo ~ '^\s*$')::int
                 + (SELECT count(*) FROM txn_legs
                     WHERE ledger_id = {ledgerId} AND leg_memo ~ '^\s*$')::int AS "Value"
            """).ConfigureAwait(false);
        await NoneAsync("no two tags share a name key", $"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT 1 FROM tags WHERE ledger_id = {ledgerId}
                 GROUP BY tag_name_key(name) HAVING count(*) > 1) d
            """).ConfigureAwait(false);

        // ----- The originating account ---------------------------------------
        // The shared check pins the owner wherever the shape names one. Where the shape
        // leaves a choice, any candidate is an owner a writer can produce: the side a
        // transaction is entered from owns it.
        foreach (var v in await OriginatingAccountCheck
                     .ViolationsAsync(database.ServiceConnectionString, ledgerId).ConfigureAwait(false))
            violations.Add($"originating account invariant {v}");
        // The import runs the same check over every ledger it writes. A plan that
        // evaluates a subquery per header grows with the square of the ledger; that
        // shape shows at any scale, so the smoke seed in the ordinary suite fails on it
        // too. A join planned as a nested loop over an unindexed set shows only at scale,
        // and the stress lane's OriginatingAccountCheckCostTests times that.
        foreach (var node in await OriginatingAccountCheck
                     .PerRowSubplansAsync(database.ServiceConnectionString, ledgerId).ConfigureAwait(false))
            violations.Add($"the originating account check runs a subquery per row: {node}");

        // ----- Shapes the seeder promises ------------------------------------
        if (scale.BankTxns > 0)
        {
            var bankRows = await CountAsync($"""
                SELECT count(*)::int AS "Value" FROM txn_headers
                 WHERE ledger_id = {ledgerId} AND action IS NULL
                """).ConfigureAwait(false);
            var splits = await CountAsync($"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action IS NULL
                   AND (SELECT count(DISTINCT posting_index) FROM txn_legs WHERE header_id = h.id) > 1
                """).ConfigureAwait(false);
            var share = bankRows == 0 ? 0d : (double)splits / bankRows;
            if (share is < 0.08 or > 0.12)
                violations.Add($"multi-split share: {share:P1} of bank rows, about 10% promised");

            await SomeAsync("a symmetric split (every line to one category)", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action IS NULL
                   AND (SELECT count(DISTINCT posting_index) FROM txn_legs WHERE header_id = h.id) > 1
                   AND (SELECT count(DISTINCT l.account_id) FROM txn_legs l
                          JOIN accounts a ON a.id = l.account_id
                         WHERE l.header_id = h.id AND a.account_type = 'category') = 1
                """).ConfigureAwait(false);
            await SomeAsync("a real-to-real transfer", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action IS NULL
                   AND NOT EXISTS (SELECT 1 FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                                    WHERE l.header_id = h.id AND a.account_type = 'category')
                """).ConfigureAwait(false);
            await SomeAsync("an opposite-side value on a one-split", $"""
                SELECT count(*)::int AS "Value"
                  FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                 WHERE l.ledger_id = {ledgerId} AND a.account_type = 'category'
                   AND l.header_total_postings = 1 AND l.leg_memo IS NOT NULL
                """).ConfigureAwait(false);
            await SomeAsync("an opposite-side value on a split's first line", $"""
                SELECT count(*)::int AS "Value"
                  FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                 WHERE l.ledger_id = {ledgerId} AND a.account_type = 'category'
                   AND l.header_total_postings > 1 AND l.posting_index = 0
                   AND l.leg_memo IS NOT NULL
                """).ConfigureAwait(false);
            await SomeAsync("a split with a transaction memo", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action IS NULL AND h.memo IS NOT NULL
                """).ConfigureAwait(false);
            await SomeAsync("a leg tag set over 20", $"""
                SELECT count(*)::int AS "Value" FROM (
                    SELECT 1 FROM txn_leg_tags WHERE ledger_id = {ledgerId}
                     GROUP BY leg_id HAVING count(*) > 20) d
                """).ConfigureAwait(false);
        }

        if (scale.Holdings > 0)
        {
            await SomeAsync("a buy with a fee", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action = 'buy'
                   AND EXISTS (SELECT 1 FROM txn_legs l
                                WHERE l.header_id = h.id AND l.posting_role = 'fee')
                """).ConfigureAwait(false);
            await SomeAsync("a buy_xfr funded from another brokerage", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers h
                 WHERE h.ledger_id = {ledgerId} AND h.action = 'buyx'
                   AND EXISTS (SELECT 1 FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                                WHERE l.header_id = h.id AND l.posting_role = 'transfer'
                                  AND a.holdings_account_id IS NOT NULL
                                  AND l.account_postings_on_header < l.header_total_postings)
                """).ConfigureAwait(false);
            await SomeAsync("a one-posting dividend", $"""
                SELECT count(*)::int AS "Value" FROM txn_legs
                 WHERE ledger_id = {ledgerId} AND header_total_postings = 1
                   AND header_id IN (SELECT id FROM txn_headers WHERE action = 'dividend_cash')
                """).ConfigureAwait(false);
            await SomeAsync("a one-posting cash transfer", $"""
                SELECT count(*)::int AS "Value" FROM txn_legs
                 WHERE ledger_id = {ledgerId} AND header_total_postings = 1
                   AND header_id IN (SELECT id FROM txn_headers WHERE action = 'transfer')
                """).ConfigureAwait(false);
            await SomeAsync("an in-kind share transfer", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers
                 WHERE ledger_id = {ledgerId} AND action = 'transfer_shares'
                """).ConfigureAwait(false);
            await SomeAsync("an event with a transaction memo", $"""
                SELECT count(*)::int AS "Value" FROM txn_headers
                 WHERE ledger_id = {ledgerId} AND action IS NOT NULL AND memo IS NOT NULL
                """).ConfigureAwait(false);
            await SomeAsync("a header tag set over 20", $"""
                SELECT count(*)::int AS "Value" FROM (
                    SELECT 1 FROM txn_header_tags WHERE ledger_id = {ledgerId}
                     GROUP BY header_id HAVING count(*) > 20) d
                """).ConfigureAwait(false);
        }

        // ----- The stored memo-and-tags presentation ---------------------------
        // Exactly what the rule computes from the seeded rows, for every leg.
        var notes = await Coffer.Api.Db.LegNotes.DifferencesAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        violations.AddRange(notes.Take(20).Select(d => $"stored notes: {d}"));
        if (notes.Count > 20) violations.Add($"stored notes: {notes.Count - 20} more legs differ");

        return violations;
    }

    /// <summary>
    /// A DB function rather than an inline DO block: a DO body cannot take bound
    /// parameters, and building this SQL by string concatenation to work around
    /// that is how injection-shaped bugs get into test infrastructure.
    /// </summary>
    private const string SeedFunctionSql = """
        CREATE OR REPLACE FUNCTION stress_seed_ledger(
            p_ledger_id            uuid,
            p_brokerage_id         uuid,
            p_holdings_account_id  uuid,
            p_brokerage2_id        uuid,
            p_holdings2_account_id uuid,
            p_bank_txns            integer,
            p_holdings             integer,
            p_buys_per_holding     integer,
            p_sells_per_holding    integer,
            p_header_payload_bytes integer DEFAULT 2304,
            p_investing_from_years integer DEFAULT 0
        ) RETURNS void
        LANGUAGE plpgsql
        AS $fn$
        DECLARE
            v_base    timestamptz := '2010-01-01T00:00:00Z';
            -- Where the investment events' dates start: the bank stream's start, or
            -- years after it for a ledger that banked before it invested.
            v_invest  timestamptz := '2010-01-01T00:00:00Z'::timestamptz
                                     + p_investing_from_years * interval '1 year';
            v_banks   uuid[];
            v_cats    uuid[];
            v_tags    uuid[];
            v_income  uuid;
            v_fee     uuid;
            -- Six hex digits of the ledger id, mixed into every derived id below.
            -- Without it the ids depend only on the row index, so seeding a SECOND
            -- ledger in the same database collides on accounts_pkey — as both stress
            -- tests do in one pass under the documented `--filter Integration.Stress`
            -- command. Ids stay derived (reproducible per ledger), just not global.
            v_disc    text := substr(replace(p_ledger_id::text, '-', ''), 1, 6);
        BEGIN
            -- ----- Accounts: a handful of banks, a spread of categories ---------
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                                  opening_balance, is_active, created_at)
            SELECT ('10' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'stress-bank-' || i, 'bank', 'USD',
                   1000 + i, TRUE, v_base
              FROM generate_series(1, 5) i;

            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, created_at)
            SELECT ('11' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'stress-cat-' || i, 'category', 'expense', 'USD',
                   0, TRUE, v_base
              FROM generate_series(1, 20) i;

            -- One income category for the dividends, one expense category for the fees.
            v_income := ('12' || v_disc || '-0000-4000-8000-' || lpad(to_hex(1), 12, '0'))::uuid;
            v_fee    := ('12' || v_disc || '-0000-4000-8000-' || lpad(to_hex(2), 12, '0'))::uuid;
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, created_at)
            VALUES (v_income, p_ledger_id, 'stress-income', 'category', 'income', 'USD', 0, TRUE, v_base),
                   (v_fee,    p_ledger_id, 'stress-fees',   'category', 'expense', 'USD', 0, TRUE, v_base);

            -- Ordered by id, so element n is the row whose id ends in hex(n), which is
            -- how StressLedger.BankId / CategoryId address them from C#.
            SELECT array_agg(id ORDER BY id) INTO v_banks
              FROM accounts WHERE ledger_id = p_ledger_id AND account_type = 'bank'
                AND name LIKE 'stress-bank-%';
            SELECT array_agg(id ORDER BY id) INTO v_cats
              FROM accounts WHERE ledger_id = p_ledger_id AND account_type = 'category'
                AND name LIKE 'stress-cat-%';

            -- ----- Tags: one vocabulary, no two names differing only by case ----
            INSERT INTO tags (id, ledger_id, name, created_at)
            SELECT ('13' || v_disc || '-0000-4000-8000-' || lpad(to_hex(t), 12, '0'))::uuid,
                   p_ledger_id, 'stress-tag-' || lpad(t::text, 2, '0'), v_base
              FROM generate_series(1, 64) t;
            SELECT array_agg(id ORDER BY id) INTO v_tags
              FROM tags WHERE ledger_id = p_ledger_id AND name LIKE 'stress-tag-%';

            -- ----- Bank activity: the posting plan --------------------------------
            -- One row per posting. A posting is TWO legs sharing one posting_index
            -- (TransactionsRepository.AddPostingLegs): the source account's leg and
            -- the counterparty's, opposite amounts. Row i is:
            --   * a split when i % 10 = 0: two to five lines, the bank on every
            --     line; every seventh one sends every line to ONE category (the
            --     symmetric shape, where both accounts are on every line);
            --   * a transfer between two banks when i % 20 = 5;
            --   * otherwise a one-split to a category.
            -- The source bank follows i / 10 rather than i, so splits and
            -- transfers spread over every bank instead of all landing on one.
            CREATE TEMP TABLE stress_bank_postings (
                i              integer,
                k              integer,
                is_split       boolean,
                is_symmetric   boolean,
                lines          integer,
                header_id      uuid,
                source_leg_id  uuid,
                counter_leg_id uuid,
                source_id      uuid,
                counter_id     uuid,
                amount         numeric
            ) ON COMMIT DROP;

            INSERT INTO stress_bank_postings
            SELECT t.i, k.k, t.is_split, t.is_symmetric, t.lines,
                   ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(t.i), 12, '0'))::uuid,
                   ('21' || v_disc || '-0000-4000-8000-' || lpad(to_hex(t.i * 16 + k.k * 2), 12, '0'))::uuid,
                   ('21' || v_disc || '-0000-4000-8000-' || lpad(to_hex(t.i * 16 + k.k * 2 + 1), 12, '0'))::uuid,
                   v_banks[1 + ((t.i / 10) % array_length(v_banks, 1))],
                   CASE WHEN t.is_transfer  THEN v_banks[1 + ((t.i / 10 + 1) % array_length(v_banks, 1))]
                        WHEN t.is_symmetric THEN v_cats[1 + ((t.i / 10) % array_length(v_cats, 1))]
                        -- 7 is coprime with 20, so a split's lines land on distinct categories.
                        ELSE v_cats[1 + ((t.i + k.k * 7) % array_length(v_cats, 1))] END,
                   -- Vary magnitudes; keep 2dp so ck_txn_legs_amount_scale_2 holds.
                   -round((1 + ((t.i + k.k * 131) % 977) + ((t.i + k.k) % 7) / 8.0)::numeric, 2)
              FROM (SELECT i,
                           i % 10 = 0                      AS is_split,
                           i % 10 = 0 AND (i / 10) % 7 = 0 AS is_symmetric,
                           i % 20 = 5                      AS is_transfer,
                           CASE WHEN i % 10 = 0 THEN 2 + (i / 10) % 4 ELSE 1 END AS lines
                      FROM generate_series(1, p_bank_txns) i) t,
                   generate_series(0, t.lines - 1) AS k(k);

            -- ----- Bank activity: headers -----------------------------------------
            -- provider_raw_payload carries the verbatim Moneydance `txn` JSON on
            -- every imported header (mig 109 / ADR-0035 §3), and it dominates row
            -- width: it carries every field MD wrote for the txn.
            -- Seeding six thin columns produces rows far narrower than
            -- imported ones, which is not a smaller version of production —
            -- it is a different shape, and it hides every cost that scales with
            -- payload width. Snapshot capture is exactly such a cost.
            --
            -- The md5 chain is deliberate: repeated literal padding compresses to
            -- almost nothing in TOAST and would restore the very shape this is
            -- trying to avoid.
            --
            -- The originating account is the posting plan's source bank, the side
            -- every row is entered from: the real side of a one-split to a category,
            -- the outflow side of a transfer, and the bank on every line of a split
            -- (also on a split whose every line goes to one category, where the
            -- category is on every line too and the real side is the split side).
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                                     payee, memo, provider_raw_payload, originating_account_id)
            SELECT ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_base + (i * interval '1 hour'),
                   -- transacted_at defaults to the posted date, which is what the
                   -- product does when nobody sets a tax date (migration 183 / #436).
                   v_base + (i * interval '1 hour'),
                   'stress-payee-' || (i % 500),
                   -- A transaction memo on half the splits and on nothing else: a
                   -- one-posting row keeps its values on its legs.
                   CASE WHEN i % 10 = 0 AND (i / 10) % 2 = 0
                        THEN 'stress split note-' || lpad((i % 97)::text, 2, '0') END,
                   CASE WHEN p_header_payload_bytes <= 0 THEN NULL ELSE
                     jsonb_build_object(
                       'obj_type', 'txn',
                       'id',       md5(i::text),
                       'acct',     md5((i * 7)::text),
                       'dt',       to_char(v_base + (i * interval '1 hour'), 'YYYYMMDD'),
                       'amt',      (i % 100000),
                       'desc',     'stress-payee-' || (i % 500),
                       'splits',   jsonb_build_array(
                                     jsonb_build_object('acct', md5((i * 3)::text),
                                                        'amt',  (i % 1000)),
                                     jsonb_build_object('acct', md5((i * 11)::text),
                                                        'amt', -(i % 1000))),
                       'raw',      (SELECT string_agg(md5((i * 1000 + g)::text), '')
                                      FROM generate_series(
                                             1, greatest(1, p_header_payload_bytes / 32)) g))
                   END,
                   o.source_id
              FROM generate_series(1, p_bank_txns) i
              JOIN (SELECT p.i AS row_i, p.source_id
                      FROM stress_bank_postings p WHERE p.k = 0) o ON o.row_i = i;

            -- ----- Bank activity: legs, memos and tags -----------------------------
            -- Where a value can sit is what the write paths allow (see
            -- FindInvariantViolationsAsync): a one-split's on either leg, since a
            -- PATCH may name either account as the source; a split's on the
            -- bank's line legs, and on the opposite leg of the FIRST line only, the
            -- override a one-split keeps when it is split. Posting counts are
            -- written here rather than left at their DEFAULT 1, which is right only
            -- for a one-posting header.
            INSERT INTO txn_legs (id, header_id, account_id, posting_index, amount, ledger_id,
                                  leg_memo, account_postings_on_header, header_total_postings)
            SELECT p.source_leg_id, p.header_id, p.source_id, p.k, p.amount, p_ledger_id,
                   CASE WHEN NOT p.is_split AND p.i % 3 = 0
                            THEN 'stress note-' || lpad((p.i % 97)::text, 2, '0')
                        WHEN p.is_split AND (p.i + p.k) % 2 = 0
                            THEN 'stress line note-' || lpad(((p.i + p.k) % 97)::text, 2, '0') END,
                   p.lines, p.lines
              FROM stress_bank_postings p
            UNION ALL
            SELECT p.counter_leg_id, p.header_id, p.counter_id, p.k, -p.amount, p_ledger_id,
                   CASE WHEN p.k = 0 AND ((NOT p.is_split AND p.i % 11 = 0)
                                          OR (p.is_split AND (p.i / 10) % 3 = 1))
                            THEN 'stress other note-' || lpad((p.i % 97)::text, 2, '0') END,
                   CASE WHEN p.is_symmetric THEN p.lines ELSE 1 END, p.lines
              FROM stress_bank_postings p;

            -- Tag j of a set seeded by x is tag (x * 7 + j) mod 64: distinct within
            -- the set, spread across the vocabulary. Sets over 20 are reachable (a
            -- posting's tags are uncapped on the baseline, and system merges may
            -- exceed the editor's limit after it), so a few legs carry one.
            INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
            SELECT s.leg_id, v_tags[1 + ((s.seed * 7 + j) % array_length(v_tags, 1))],
                   p_ledger_id, v_base
              FROM (SELECT p.source_leg_id AS leg_id, p.i + p.k AS seed,
                           CASE WHEN NOT p.is_split THEN
                                    CASE WHEN p.i % 1000 = 4 THEN 24
                                         WHEN p.i % 4 = 0 THEN 1 + (p.i / 4) % 3
                                         ELSE 0 END
                                WHEN p.i % 500 = 0 AND p.k = 1 THEN 22
                                WHEN (p.i + p.k) % 3 = 0 THEN 1
                                ELSE 0 END AS n
                      FROM stress_bank_postings p
                    UNION ALL
                    SELECT p.counter_leg_id, p.i + 1,
                           CASE WHEN NOT p.is_split AND p.i % 13 = 0 THEN 1
                                WHEN p.is_split AND (p.i / 10) % 3 = 1 THEN 1
                                ELSE 0 END
                      FROM stress_bank_postings p
                     WHERE p.k = 0) s,
                   generate_series(0, s.n - 1) j;

            -- Transaction tags on a third of the splits, and a set over 20 on a few
            -- (set_transaction_tags applies no limit on the baseline).
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id, created_at)
            SELECT ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s.i), 12, '0'))::uuid,
                   v_tags[1 + ((s.i * 5 + j) % array_length(v_tags, 1))],
                   p_ledger_id, v_base
              FROM (SELECT i, CASE WHEN (i / 10) % 50 = 7 THEN 22
                                   WHEN (i / 10) % 3 = 0 THEN 2
                                   ELSE 0 END AS n
                      FROM generate_series(10, p_bank_txns, 10) i) s,
                   generate_series(0, s.n - 1) j;

            -- ----- Securities + holding shells, in both brokerages ------------
            INSERT INTO securities (id, ledger_id, name, ticker, created_at)
            SELECT ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'Stress Security ' || i, 'STR' || i, v_base
              FROM generate_series(1, p_holdings) i;

            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            SELECT ('31' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_holdings_account_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 0, 0, v_base
              FROM generate_series(1, p_holdings) i
            UNION ALL
            -- The second brokerage receives every security once, in kind.
            SELECT ('32' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_holdings2_account_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 0, 0, v_base
              FROM generate_series(1, p_holdings) i;

            -- ----- Buys: cash out of the brokerage, shares into holdings -------
            -- Header id encodes (security, buy) so ids stay derived, not random.
            -- An investment event belongs to its brokerage, here and on every event
            -- below.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, payee, action,
                                     originating_account_id)
            -- Buys must ALL land before the sells below: the FIFO walk only offers
            -- a lot whose header is on or before the disposal date, so buys dated
            -- after a sell cannot fund it. Spacing these 90 days apart put most
            -- buys decades past the sells at BuysPerHolding=400 and left holdings
            -- oversold — caught by the lot-reconciliation assertion.
            SELECT ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_invest + ((s * 37 + b * 3) * interval '1 day'),
                   v_invest + ((s * 37 + b * 3) * interval '1 day'),
                   'buy-' || s, 'buy', p_brokerage_id
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b;

            -- One posting (InvestmentPostings.BuildSecPair): both legs at index 0,
            -- both with posting_role 'security'.
            INSERT INTO txn_legs (id, header_id, account_id, posting_index, amount, ledger_id,
                                  security_id, quantity, unit_price, posting_role)
            -- Security side: +cost, fractional quantity (12dp scale exercised).
            SELECT ('60' || v_disc || '-0000-4000-8000-' || lpad(to_hex((s * 1000 + b) * 2), 12, '0'))::uuid,
                   ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_holdings_account_id, 0,
                   round((10 * (b + 1) * (1 + (s % 13)))::numeric, 2),
                   p_ledger_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   round((1.5 + (b % 5) + (s % 3) / 3.0)::numeric, 12),
                   round((10 * (b + 1) * (1 + (s % 13)))::numeric
                         / round((1.5 + (b % 5) + (s % 3) / 3.0)::numeric, 12), 6),
                   'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b
            UNION ALL
            -- Cash side: -cost.
            SELECT ('60' || v_disc || '-0000-4000-8000-' || lpad(to_hex((s * 1000 + b) * 2 + 1), 12, '0'))::uuid,
                   ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_brokerage_id, 0,
                   -round((10 * (b + 1) * (1 + (s % 13)))::numeric, 2),
                   p_ledger_id, NULL, NULL, NULL, 'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b;

            -- ----- Sells: disposals, so the FIFO walk has lots to consume -------
            -- Dated after every buy so lot availability is never the constraint.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, payee, action,
                                     originating_account_id)
            SELECT ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_invest + interval '20 years' + ((s * 7 + k) * interval '1 day'),
                   v_invest + interval '20 years' + ((s * 7 + k) * interval '1 day'),
                   'sell-' || s, 'sell', p_brokerage_id
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k;

            INSERT INTO txn_legs (id, header_id, account_id, posting_index, amount, ledger_id,
                                  security_id, quantity, unit_price, posting_role)
            -- Security side: -proceeds, negative quantity (the disposal).
            SELECT ('61' || v_disc || '-0000-4000-8000-' || lpad(to_hex((s * 1000 + k) * 2), 12, '0'))::uuid,
                   ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_holdings_account_id, 0,
                   -round((40 * (1 + (s % 11)))::numeric, 2),
                   p_ledger_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   -round((2.0 + (k % 3))::numeric, 12),
                   round((40 * (1 + (s % 11)))::numeric
                         / round((2.0 + (k % 3))::numeric, 12), 6),
                   'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k
            UNION ALL
            -- Cash side: +proceeds.
            SELECT ('61' || v_disc || '-0000-4000-8000-' || lpad(to_hex((s * 1000 + k) * 2 + 1), 12, '0'))::uuid,
                   ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_brokerage_id, 0,
                   round((40 * (1 + (s % 11)))::numeric, 2),
                   p_ledger_id, NULL, NULL, NULL, 'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k;

            -- ----- The other investment shapes, one of each per holding ---------
            -- Dated just after the holding's first buy (day s*37 + 3), so the
            -- in-kind transfer at day +4 finds exactly that one lot open and FIFO
            -- moves it whole, as BuildTransferSharesPlanAsync would; the two
            -- acquisitions after it are still long before the sells.
            -- A multi-posting event carries the event's memo and tags on its
            -- header; a one-posting event (dividend, cash transfer) carries none,
            -- because its value would belong on its brokerage leg, which the
            -- investment writers do not write on the baseline.
            -- Every one belongs to the trading brokerage: it carries a leg in every
            -- posting of each (the second brokerage only funds the buy_xfr's transfer
            -- posting), and it is the source of the in-kind transfer, whose shares
            -- leave its holdings.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, payee, memo, action,
                                     originating_account_id)
            SELECT ('4' || e.kind || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_invest + ((s * 37 + e.day) * interval '1 day'),
                   v_invest + ((s * 37 + e.day) * interval '1 day'),
                   e.action || '-' || s,
                   CASE WHEN e.multi THEN 'stress event note-' || lpad((s % 97)::text, 2, '0') END,
                   e.action, p_brokerage_id
              FROM generate_series(1, p_holdings) s,
                   (VALUES ('6', 'transfer_shares', 4, TRUE),
                           ('2', 'buy',             5, TRUE),
                           ('3', 'buyx',            7, TRUE),
                           ('4', 'dividend_cash',   8, FALSE),
                           ('5', 'transfer',        9, FALSE)) AS e(kind, action, day, multi);

            -- The legs, pair by pair as InvestmentTransactionsRepository.BuildPostings
            -- and BuildTransferSharesPostings emit them.
            INSERT INTO txn_legs (id, header_id, account_id, posting_index, amount, ledger_id,
                                  security_id, quantity, unit_price, posting_role,
                                  account_postings_on_header, header_total_postings)
            WITH v AS (
                SELECT s,
                       ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid AS sec,
                       -- The first buy's lot (b = 1 above), which the transfer moves.
                       round((1.5 + (1 % 5) + (s % 3) / 3.0)::numeric, 12) AS q1,
                       round((10 * (1 + 1) * (1 + (s % 13)))::numeric, 2)  AS a1,
                       round((2.0 + (s % 4) / 4.0)::numeric, 12)          AS qf,
                       round((25 * (1 + (s % 9)))::numeric, 2)            AS pf,
                       round((4.95 + (s % 5))::numeric, 2)                AS fee,
                       round((1.0 + (s % 5) / 2.0)::numeric, 12)          AS qx,
                       round((30 * (1 + (s % 7)))::numeric, 2)            AS px,
                       round((3.1 + (s % 17))::numeric, 2)                AS dv,
                       round((100 + s)::numeric, 2)                       AS xf,
                       v_banks[1 + (s % array_length(v_banks, 1))]        AS bank
                  FROM generate_series(1, p_holdings) s
            ), c AS (
                -- The moved lot's unit cost at the lots column's scale, and the cost
                -- the transfer carries: round(quantity * unit_cost, 2), as the API.
                SELECT v.*, round(v.a1 / v.q1, 12) AS uc,
                       round(v.q1 * round(v.a1 / v.q1, 12), 2) AS cost
                  FROM v
            )
            SELECT ('6' || e.kind || v_disc || '-0000-4000-8000-'
                        || lpad(to_hex(c.s * 4 + e.k * 2 + e.side), 12, '0'))::uuid,
                   ('4' || e.kind || v_disc || '-0000-4000-8000-' || lpad(to_hex(c.s), 12, '0'))::uuid,
                   e.account_id, e.k, e.amount, p_ledger_id,
                   e.security_id, e.quantity, e.unit_price, e.role, e.aph, e.htp
              FROM c CROSS JOIN LATERAL (VALUES
                -- transfer_shares: zero cash against each brokerage's holdings.
                ('6', 0, 0, p_brokerage_id,         0::numeric, NULL::uuid, NULL::numeric, NULL::numeric, 'security', 1, 2),
                ('6', 0, 1, p_holdings_account_id,  -c.cost,    c.sec,      -c.q1,         c.uc,          'security', 1, 2),
                ('6', 1, 0, p_brokerage2_id,        0,          NULL,       NULL,          NULL,          'security', 1, 2),
                ('6', 1, 1, p_holdings2_account_id, c.cost,     c.sec,      c.q1,          c.uc,          'security', 1, 2),
                -- buy with a fee: the trade, then the fee against its category.
                ('2', 0, 0, p_brokerage_id,         -c.pf,      NULL,       NULL,          NULL,          'security', 2, 2),
                ('2', 0, 1, p_holdings_account_id,  c.pf,       c.sec,      c.qf,          round(c.pf / c.qf, 6), 'security', 1, 2),
                ('2', 1, 0, p_brokerage_id,         -c.fee,     c.sec,      NULL,          NULL,          'fee',      2, 2),
                ('2', 1, 1, v_fee,                  c.fee,      NULL,       NULL,          NULL,          'fee',      1, 2),
                -- buy_xfr: the trade, then the cash from the second brokerage.
                ('3', 0, 0, p_brokerage_id,         -c.px,      NULL,       NULL,          NULL,          'security', 2, 2),
                ('3', 0, 1, p_holdings_account_id,  c.px,       c.sec,      c.qx,          round(c.px / c.qx, 6), 'security', 1, 2),
                ('3', 1, 0, p_brokerage_id,         c.px,       NULL,       NULL,          NULL,          'transfer', 2, 2),
                ('3', 1, 1, p_brokerage2_id,        -c.px,      NULL,       NULL,          NULL,          'transfer', 1, 2),
                -- dividend_cash: one posting, the security stamped on the cash leg.
                ('4', 0, 0, p_brokerage_id,         c.dv,       c.sec,      NULL,          NULL,          'income',   1, 1),
                ('4', 0, 1, v_income,               -c.dv,      NULL,       NULL,          NULL,          'income',   1, 1),
                -- transfer: one posting, cash from a bank.
                ('5', 0, 0, p_brokerage_id,         c.xf,       NULL,       NULL,          NULL,          'transfer', 1, 1),
                ('5', 0, 1, c.bank,                 -c.xf,      NULL,       NULL,          NULL,          'transfer', 1, 1)
              ) AS e(kind, k, side, account_id, amount, security_id, quantity, unit_price, role, aph, htp);

            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id, created_at)
            SELECT ('4' || e.kind || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   v_tags[1 + ((s * 3 + j) % array_length(v_tags, 1))],
                   p_ledger_id, v_base
              FROM generate_series(1, p_holdings) s,
                   (VALUES ('6'), ('2'), ('3')) AS e(kind),
                   generate_series(0, CASE WHEN e.kind = '3' AND s % 50 = 3 THEN 21
                                           ELSE 1 + s % 2 END - 1) j;

            -- ----- Lots: one per acquiring leg ------------------------------------
            -- A buy's lot costs its amount plus its fee, as the create path writes
            -- it; recompute_holdings_cost_basis below brings every lot to the state
            -- the FIFO walk derives and closes them against sells.
            INSERT INTO lots (holding_id, leg_id, quantity, unit_cost, acquired_at, ledger_id)
            SELECT h.id, l.id, l.quantity,
                   round((l.amount + COALESCE((SELECT sum(f.amount) FROM txn_legs f
                                                WHERE f.header_id = l.header_id
                                                  AND f.posting_role = 'fee'
                                                  AND f.amount > 0), 0))
                         / l.quantity, 12),
                   hd.posted_at, p_ledger_id
              FROM txn_legs l
              JOIN txn_headers hd ON hd.id = l.header_id
              JOIN holdings h    ON h.security_id = l.security_id
                                AND h.account_id = l.account_id
             WHERE l.ledger_id = p_ledger_id
               AND hd.action IN ('buy', 'buyx')
               AND l.quantity > 0;

            -- An in-kind destination lot inherits the moved lot's cost and its
            -- acquisition date (ADR-0065 D2): the source's oldest lot, the one FIFO
            -- took.
            INSERT INTO lots (holding_id, leg_id, quantity, unit_cost, acquired_at, ledger_id)
            SELECT h.id, l.id, l.quantity, l.unit_price,
                   (SELECT min(src.acquired_at)
                      FROM lots src JOIN holdings sh ON sh.id = src.holding_id
                     WHERE sh.account_id = p_holdings_account_id
                       AND sh.security_id = l.security_id),
                   p_ledger_id
              FROM txn_legs l
              JOIN txn_headers hd ON hd.id = l.header_id
              JOIN holdings h    ON h.security_id = l.security_id
                                AND h.account_id = l.account_id
             WHERE l.ledger_id = p_ledger_id
               AND hd.action = 'transfer_shares'
               AND l.quantity > 0;

            -- ----- Let the product derive all investment state ------------------
            PERFORM recompute_holdings_cost_basis(p_ledger_id);
            -- Materialised balances, the one derived table snapshots do not carry.
            PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
        END;
        $fn$;
        """;

    private const string DropSeedFunctionSql = """
        DROP FUNCTION IF EXISTS stress_seed_ledger(
            uuid, uuid, uuid, uuid, uuid, integer, integer, integer, integer, integer, integer);
        """;
}
