using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The exchange-rate pairs a ledger needs (ADR-0114 D7, D10, D11), and how far back each
/// is needed. The ONE derivation: the market-series fetch reads it to know what to fetch,
/// the rates view lists it, the hand-entered rate refuses a pair outside it, and the
/// conversion reads use it to know which pairs a figure can draw on.
/// </summary>
/// <remarks>
/// <para>A pair is directed: <c>From</c> converts into <c>To</c>, the direction a rate is
/// fetched and stored in (ADR-0114 D4), so no needed pair is another's inverse unless the
/// ledger needs both directions. Three sources, each only where the two currencies
/// differ:</para>
/// <list type="bullet">
/// <item><see cref="NeededFxPairReasons.Position"/>: a security's price currency into the
/// currency of the account that holds it (ADR-0114 D7: a position converts from its price's
/// currency into its account's). Every currency any stored price of the security names,
/// not only its latest, since history converts at each day's price. A position is a
/// holdings row or any trade leg naming the security, so a position sold out still has
/// its history converted. The account is the brokerage whose holdings sibling holds it
/// (its currency is the brokerage's, ADR-0019 Rule 4; migration 274 reads the brokerage's
/// for the same reason), or the account itself when no brokerage names it.</item>
/// <item><see cref="NeededFxPairReasons.NetWorth"/>: a net-worth account's currency into
/// the reporting currency (ADR-0114 D7: a combined figure converts each account). Net worth's
/// accounts are <see cref="DisplayCurrency.NetWorthAccounts"/>'.</item>
/// <item><see cref="NeededFxPairReasons.Benchmark"/>: a benchmark's currency into every
/// currency a returns series can be drawn in, each brokerage's and the reporting
/// currency (ADR-0114 D3: a benchmark converts into the series' currency).</item>
/// </list>
/// <para><b>How far back</b> (<see cref="NeededFxPair.NeededFrom"/>, ADR-0114 D11): the earliest day
/// a figure needs the pair, the earliest across its sources. A position pair: the earliest
/// price of the security in the price currency. A net-worth pair: the ledger's earliest
/// day, the first live posting (never a reminder's template, hidden or merged away) or start
/// date of any of its non-category accounts, in any
/// currency, inactive ones included (ADR-0114 D11, ADR-0114 D13: a derived rate can need a leg on a
/// day before any account in its currency existed). A
/// benchmark pair: the first day any returns series can be invested from
/// (<see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>), where a benchmark
/// line is rebased. Null when no source names a day (a ledger
/// with no history yet): only the latest rate is needed then. It is the first day a
/// figure is ON; a reader converts a day at the last rate on or before it (ADR-0114 D4), so a
/// fetcher reaches back past it to the last published day.</para>
/// <para><b>What needs it</b> (<see cref="NeededFxPair.Sources"/>): each account,
/// security and benchmark that puts the pair on the list, named, so the rates view can
/// say what a missing day blanks. A position names the security and the account that
/// holds it; a net-worth pair each net-worth account in the From currency; a benchmark
/// pair each benchmark in the From currency. The names ride the statements that are read
/// anyway, so naming costs no extra statement.</para>
/// <para>Reads every account, holding and trade leg of the ledger in grouped queries,
/// not row by row; the result is ordered by From, then To, ordinally.</para>
/// </remarks>
public static class NeededFxPairs
{
    /// <summary>The pairs <paramref name="ledgerId"/> needs, ordered by From then To.</summary>
    public static async Task<IReadOnlyList<NeededFxPair>> ForLedgerAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return (await DeriveAsync(db, ledgerId, withStarts: false, cancellationToken).ConfigureAwait(false)).Pairs;
    }

    /// <summary>What <see cref="DeriveAsync"/> derived: the pairs, and with the starts asked
    /// for, the ledger's earliest day and, on a ledger with a benchmark, the first invested
    /// day (each null too when not asked for and not needed by a pair).</summary>
    private sealed record Derivation(IReadOnlyList<NeededFxPair> Pairs, DateOnly? Earliest, DateOnly? Invested);

    /// <summary>
    /// The one derivation behind <see cref="ForLedgerAsync"/> and <see cref="StartsAsync"/>:
    /// each statement runs once, so the starts a write compares cost no statement the pairs
    /// did not already need beyond the ledger's earliest day.
    /// </summary>
    /// <remarks>Its statements share one connection, opened here unless the caller holds it
    /// open already: each open of a pooled connection costs a round trip to set the user
    /// and one to reset it, as many again as the statements themselves.</remarks>
    private static async Task<Derivation> DeriveAsync(
        AppDbContext db, Guid ledgerId, bool withStarts, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DeriveOnConnectionAsync(db, ledgerId, withStarts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Derivation> DeriveOnConnectionAsync(
        AppDbContext db, Guid ledgerId, bool withStarts, CancellationToken cancellationToken)
    {
        var reporting = await db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => l.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reporting is null) return new Derivation([], null, null);

        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType != "category")
            .Select(a => new
            {
                a.Id, a.Name, a.CurrencyCode, a.AccountType, a.HoldingsAccountId, a.OpenedOn, a.OpeningBalance,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = accounts.ToDictionary(a => a.Id);
        // Holdings sibling → its brokerage.
        var brokerageOf = accounts
            .Where(a => a.HoldingsAccountId is not null)
            .GroupBy(a => a.HoldingsAccountId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).First());
        var netWorth = accounts.Where(a => !brokerageOf.ContainsKey(a.Id)).ToList();

        var pairs = new Dictionary<(string From, string To), (NeededFxPairReasons Reasons, DateOnly? From)>();
        var sources = new Dictionary<(string From, string To), HashSet<NeededFxPairSource>>();
        void Need(string from, string to, NeededFxPairReasons reason, DateOnly? neededFrom, NeededFxPairSource source)
        {
            if (string.Equals(from, to, StringComparison.Ordinal)) return;
            if (pairs.TryGetValue((from, to), out var had))
                pairs[(from, to)] = (had.Reasons | reason, Earlier(had.From, neededFrom));
            else
                pairs[(from, to)] = (reason, neededFrom);
            if (!sources.TryGetValue((from, to), out var named)) sources[(from, to)] = named = [];
            named.Add(source);
        }

        // ---- Positions: price currency → the holding account's currency. ----
        // The security's name and ticker ride the same statement, for the pair's sources.
        var priceCurrencies = await (
                from g in db.SecurityPrices.AsNoTracking()
                    .Where(p => p.LedgerId == ledgerId)
                    .GroupBy(p => new { p.SecurityId, p.CurrencyCode })
                    .Select(g => new { g.Key.SecurityId, g.Key.CurrencyCode, First = g.Min(p => p.PriceDate) })
                join s in db.Securities.AsNoTracking() on g.SecurityId equals s.Id
                select new { g.SecurityId, g.CurrencyCode, g.First, s.Name, s.Ticker })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (priceCurrencies.Count > 0)
        {
            var held = await db.Holdings.AsNoTracking()
                .Where(h => h.LedgerId == ledgerId)
                .Select(h => new { h.AccountId, h.SecurityId })
                .Union(db.TxnLegs.AsNoTracking()
                    .Where(l => l.LedgerId == ledgerId && l.SecurityId != null)
                    .Select(l => new { l.AccountId, SecurityId = l.SecurityId!.Value }))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var pricesBySecurity = priceCurrencies.ToLookup(p => p.SecurityId);
            foreach (var position in held)
            {
                var holder = brokerageOf.TryGetValue(position.AccountId, out var brokerage)
                    ? brokerage
                    : byId.GetValueOrDefault(position.AccountId);
                if (holder is null) continue;
                foreach (var price in pricesBySecurity[position.SecurityId])
                    Need(price.CurrencyCode, holder.CurrencyCode, NeededFxPairReasons.Position, price.First,
                        new NeededFxPairSource(NeededFxPairReasons.Position, price.SecurityId, price.Name,
                            price.Ticker, holder.Id, holder.Name));
            }
        }

        // ---- Net worth: account currency → reporting currency. ----
        var foreignNetWorth = netWorth
            .Select(a => a.CurrencyCode)
            .Where(c => !string.Equals(c, reporting, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        // Every net-worth pair starts on the ledger's earliest day (ADR-0114 D11, ADR-0114 D13),
        // not on the first day of its own currency's accounts: a rate between two account
        // currencies is derived through both legs into the reporting currency, so either leg
        // can be needed on a day before any account in its currency existed. The starts read
        // it whether or not a pair needs it.
        var earliest = withStarts || foreignNetWorth.Count > 0
            ? await EarliestOfAsync(db, ledgerId, accounts.Select(a => a.Id).ToList(),
                    accounts.Select(a => a.OpenedOn), cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (foreignNetWorth.Count > 0)
        {
            var from = earliest;
            foreach (var currency in foreignNetWorth.Order(StringComparer.Ordinal))
            {
                foreach (var a in netWorth.Where(a => string.Equals(a.CurrencyCode, currency, StringComparison.Ordinal)))
                    Need(currency, reporting, NeededFxPairReasons.NetWorth, from,
                        new NeededFxPairSource(NeededFxPairReasons.NetWorth, a.Id, a.Name, null, null, null));
            }
        }

        // ---- Benchmarks: benchmark currency → each currency a series is drawn in. ----
        var benchmarks = await db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .Select(b => new { b.Id, b.Symbol, b.Name, b.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        DateOnly? firstInvested = null;
        if (benchmarks.Count > 0)
        {
            // The accounts are read already: the investment ones are every account
            // FirstInvestedDayAsync reads, holdings siblings included.
            firstInvested = await InvestmentReportingRepository.FirstInvestedDayOfAsync(db, ledgerId,
                    [.. accounts
                        .Where(a => a.AccountType == "investment")
                        .Select(a => new InvestmentAccountStart(a.Id, a.OpenedOn, a.OpeningBalance))],
                    cancellationToken)
                .ConfigureAwait(false);
            var seriesCurrencies = netWorth
                .Where(a => a.AccountType == "investment")
                .Select(a => a.CurrencyCode)
                .Append(reporting)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var benchmark in benchmarks)
                foreach (var series in seriesCurrencies)
                    Need(benchmark.CurrencyCode, series, NeededFxPairReasons.Benchmark, firstInvested,
                        new NeededFxPairSource(NeededFxPairReasons.Benchmark, benchmark.Id, benchmark.Name,
                            benchmark.Symbol, null, null));
        }

        var ordered = pairs
            .OrderBy(p => p.Key.From, StringComparer.Ordinal)
            .ThenBy(p => p.Key.To, StringComparer.Ordinal)
            .Select(p => new NeededFxPair(p.Key.From, p.Key.To, p.Value.Reasons, p.Value.From,
                [.. sources[p.Key]
                    .OrderBy(s => SourceOrder(s.Reason))
                    .ThenBy(s => s.Name, StringComparer.Ordinal)
                    .ThenBy(s => s.AccountName, StringComparer.Ordinal)
                    .ThenBy(s => s.Id)]))
            .ToList();
        return new Derivation(ordered, earliest, firstInvested);
    }

    /// <summary>
    /// The ledger's earliest day (ADR-0114 D11): the earlier of the first live posting on any
    /// of its non-category accounts and their first opened date, inactive accounts and
    /// holdings siblings included; null when it has neither. Every net-worth pair is needed
    /// from it, and a write that can move it earlier reads it before writing to know whether
    /// to start the backfill, so the pairs and the triggers read this one rule.
    /// </summary>
    internal static async Task<DateOnly?> LedgerEarliestDayAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType != "category")
            .Select(a => new { a.Id, a.OpenedOn })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await EarliestOfAsync(db, ledgerId, accounts.Select(a => a.Id).ToList(),
                accounts.Select(a => a.OpenedOn), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Whether a write dated <paramref name="day"/> moves the ledger's earliest day,
    /// <paramref name="earliest"/> as read before the write, earlier (ADR-0114 D11): a ledger
    /// with no earliest day yet has it set by any dated write.</summary>
    internal static bool MovesEarlier(DateOnly day, DateOnly? earliest) =>
        earliest is not { } first || day < first;

    /// <summary>
    /// What a write can widen (ADR-0114 D11): the ledger's earliest day
    /// (<see cref="LedgerEarliestDayAsync"/>), where every net-worth pair starts; when the
    /// ledger has a benchmark, the first day a returns series can be invested from
    /// (<see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>), where every
    /// benchmark series and benchmark pair starts; and every needed pair with its start
    /// (<see cref="ForLedgerAsync"/>), which a price, a trade or a position move can add to or
    /// move earlier without moving either day. A writer reads them before its write and again
    /// after it, so the trigger is the one rule whatever the write was.
    /// </summary>
    /// <remarks>The pairs' own derivation, which reads both days anyway (the earliest for a
    /// net-worth pair, the first invested day for a benchmark pair), so each statement runs
    /// once: seven at most, every one of them bounded by an index or by the ledger's
    /// accounts, prices and security legs, never a scan of all its legs.
    /// <c>MarketSeriesStartCostTests</c> prints what it costs a write.</remarks>
    internal static async Task<NeededStarts> StartsAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var derived = await DeriveAsync(db, ledgerId, withStarts: true, cancellationToken).ConfigureAwait(false);
        return new NeededStarts(derived.Earliest, derived.Invested,
            derived.Pairs.ToDictionary(p => (p.FromCurrency, p.ToCurrency), p => p.NeededFrom));
    }

    /// <summary>The earlier of the first live posting on <paramref name="accountIds"/> and
    /// the first of <paramref name="openedOn"/>.</summary>
    private static async Task<DateOnly?> EarliestOfAsync(
        AppDbContext db, Guid ledgerId, List<Guid> accountIds, IEnumerable<DateOnly?> openedOn,
        CancellationToken cancellationToken)
    {
        var from = await FirstLivePostingAsync(db, ledgerId, accountIds, cancellationToken).ConfigureAwait(false);
        foreach (var opened in openedOn) from = Earlier(from, opened);
        return from;
    }

    /// <summary>
    /// The first day a live transaction is posted on any of <paramref name="accountIds"/>,
    /// the ledger's non-category accounts, inactive ones and holdings siblings included, for
    /// its earliest day; null when none has one. Live as
    /// <see cref="LedgerHistoryStart.FirstTransactionDayAsync"/> counts it: not hidden, not
    /// merged away, and not a reminder's template, whose posting date is the series' start
    /// date, not a day any figure is on.
    /// </summary>
    /// <remarks>One statement: the visible headers walked in posting order
    /// (<c>idx_txn_headers_ledger_posted_seq</c>), each one's legs probed, until the first
    /// live one with a leg on one of the accounts. Accounts that hold most of the ledger's
    /// legs, as its non-category accounts do, stop the walk within a few headers. For
    /// accounts that hold few, the walk is as long as the history before their first
    /// posting, so the first invested day reads
    /// <see cref="FirstLivePostingOfFewAsync"/> instead.</remarks>
    internal static async Task<DateOnly?> FirstLivePostingAsync(
        AppDbContext db, Guid ledgerId, List<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accountIds);
        if (accountIds.Count == 0) return null;
        var first = await LiveHeaders(db, ledgerId)
            .Where(h => db.TxnLegs.Any(l => l.HeaderId == h.Id && accountIds.Contains(l.AccountId)))
            .OrderBy(h => h.PostedAt)
            .Select(h => (DateTime?)h.PostedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return first is { } posted ? DateOnly.FromDateTime(posted) : null;
    }

    /// <summary>
    /// <see cref="FirstLivePostingAsync"/>'s day, live as it counts it, read from the legs of
    /// <paramref name="accountIds"/> rather than from the ledger's headers: for accounts
    /// that hold few of the ledger's legs, as its investment accounts do for its first
    /// invested day (<see cref="InvestmentReportingRepository.FirstInvestedDayAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>One statement: the distinct headers of the accounts' legs, read by account
    /// (<c>idx_txn_legs_account_id</c>), each one's posting day looked up by key when it is
    /// live, and the least of those. It costs the accounts' own legs and one key lookup per
    /// header they post on, however much history lies before their first posting.</para>
    /// <para>The lookup is a scalar subquery on purpose. Written as a join or a semi-join,
    /// the planner reads the headers for a hash instead, every one of the ledger's, since it
    /// prices that below a lookup per header; ordered and limited to the first, it walks the
    /// ledger's headers in posting order (<see cref="FirstLivePostingAsync"/>), as long as
    /// the history before the accounts' first posting, most of the ledger on one that
    /// banked for years before it invested. Postgres does not turn a scalar subquery in the
    /// select list into a join, so neither plan is open to it.
    /// <c>MarketSeriesStartCostTests</c> guards the rows each plan reads.</para>
    /// </remarks>
    internal static async Task<DateOnly?> FirstLivePostingOfFewAsync(
        AppDbContext db, Guid ledgerId, List<Guid> accountIds, CancellationToken cancellationToken)
    {
        var first = await FirstLivePostingInstantOfFewAsync(db, ledgerId, accountIds, cancellationToken)
            .ConfigureAwait(false);
        return first is { } posted ? DateOnly.FromDateTime(posted) : null;
    }

    /// <summary><see cref="FirstLivePostingOfFewAsync"/> as the instant the first live
    /// posting is at, rather than its day; the same one statement.</summary>
    internal static async Task<DateTime?> FirstLivePostingInstantOfFewAsync(
        AppDbContext db, Guid ledgerId, List<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accountIds);
        if (accountIds.Count == 0) return null;
        var live = LiveHeaders(db, ledgerId);
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && accountIds.Contains(l.AccountId))
            .Select(l => l.HeaderId)
            .Distinct()
            .Select(headerId => live
                .Where(h => h.Id == headerId)
                .Select(h => (DateTime?)h.PostedAt)
                .FirstOrDefault())
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The ledger's live headers: not hidden, not merged away, not a reminder's
    /// template.</summary>
    private static IQueryable<Entities.TxnHeaderRow> LiveHeaders(AppDbContext db, Guid ledgerId) =>
        db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId
                        && !h.IsHidden
                        && h.IsMergedInto == null
                        && !h.IsRecurringTemplate);

    /// <summary>Accounts first, then the securities they hold, then benchmarks.</summary>
    private static int SourceOrder(NeededFxPairReasons reason) => reason switch
    {
        NeededFxPairReasons.NetWorth => 0,
        NeededFxPairReasons.Position => 1,
        _ => 2,
    };

    private static DateOnly? Earlier(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : (a.Value < b.Value ? a : b);
}

/// <summary>
/// What a write can widen (<see cref="NeededFxPairs.StartsAsync"/>): the ledger's earliest
/// day, the first invested day when the ledger has a benchmark (null when it has none, or
/// nothing is invested yet), and every needed pair, From then To, with its start (null: only
/// the latest rate is needed).
/// </summary>
internal sealed record NeededStarts(
    DateOnly? LedgerEarliest,
    DateOnly? BenchmarkStart,
    IReadOnlyDictionary<(string From, string To), DateOnly?> Pairs)
{
    /// <summary>Whether <paramref name="after"/>, read after a write, needs anything this,
    /// read before it, did not (ADR-0114 D11): either day earlier, a pair it did not need, or
    /// a pair from an earlier day. A start that was null and now has a day moved earlier; one
    /// that is null after the write did not.</summary>
    public bool MovedEarlierBy(NeededStarts after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return Moved(after.LedgerEarliest, LedgerEarliest)
            || Moved(after.BenchmarkStart, BenchmarkStart)
            || after.Pairs.Any(p => !Pairs.TryGetValue(p.Key, out var had) || Moved(p.Value, had));
    }

    private static bool Moved(DateOnly? after, DateOnly? before) =>
        after is { } day && NeededFxPairs.MovesEarlier(day, before);
}

/// <summary>Why a ledger needs a pair (<see cref="NeededFxPairs"/>).</summary>
[Flags]
public enum NeededFxPairReasons
{
    None = 0,
    /// <summary>A position priced in From is held in an account in To.</summary>
    Position = 1,
    /// <summary>A net-worth account in From combines into the reporting currency To.</summary>
    NetWorth = 2,
    /// <summary>A benchmark in From is drawn against a returns series in To.</summary>
    Benchmark = 4,
}

/// <summary>One needed pair: From converts into To, needed from <paramref name="NeededFrom"/>
/// (null: only the latest rate is needed), for <paramref name="Sources"/>: accounts first,
/// then securities, then benchmarks, each by name.</summary>
public sealed record NeededFxPair(
    string FromCurrency, string ToCurrency, NeededFxPairReasons Reasons, DateOnly? NeededFrom,
    IReadOnlyList<NeededFxPairSource> Sources);

/// <summary>
/// One thing that needs a pair (<see cref="NeededFxPairs"/>), by <paramref name="Reason"/>,
/// a single flag: a net-worth account (<paramref name="Id"/> the account's); a position
/// (<paramref name="Id"/> the security's, <paramref name="Symbol"/> its ticker, and
/// <paramref name="AccountId"/> and <paramref name="AccountName"/> the account that holds
/// it); or a benchmark (<paramref name="Id"/> the benchmark's, <paramref name="Symbol"/> its
/// symbol).
/// </summary>
public sealed record NeededFxPairSource(
    NeededFxPairReasons Reason, Guid Id, string Name, string? Symbol, Guid? AccountId, string? AccountName);
