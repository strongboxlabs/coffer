using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The currencies a page of register rows is in, and a category register's running
/// balance converted into the ledger's reporting currency (ADR-0114 D7, D9).
/// </summary>
/// <remarks>
/// <para><b>Each row stays native.</b> A row's amount is in the currency of the money it
/// moved: on a real account, that account's; on a category, which has no currency of its
/// own, the real account on the other side of its posting, or the category's own code
/// when the posting is between two categories. That is the rule the category totals and
/// the summary report read, so a row and the figures summed from it name one currency.</para>
/// <para><b>An account's balance stays native</b>: a per-account figure (ADR-0114 D7). A
/// category's balance combines accounts, so it converts: each transaction at its own date,
/// the running sum of the converted amounts in the walk's order (ADR-0034's
/// <c>(posted_at, seq)</c> over the headers the walk counts), rounded once at the stored
/// money scale.</para>
/// <para><b>How it is computed.</b> The stored running balance already sums every leg as
/// it stands, so the converted balance at a row is that stored figure plus, for each leg
/// before it in another currency, the leg's amount times its rate less one. A leg in the
/// reporting currency adds nothing to the correction, so only the category's foreign legs
/// are read: one statement, grouped per header and currency, up to the page's latest row,
/// read with the stored balances of the page's rows so both come from one snapshot. It
/// reaches them from the ledger's real accounts in another currency, through the posting
/// key, and reads a category's own legs only when its own code is another currency (a
/// posting between two categories), so a busy category with nothing foreign costs
/// nothing to scan. Each
/// row's figure is a function of the history before it alone, never of the page, the
/// filter or the sort, so paging cannot move it.</para>
/// <para><b>Fails closed</b> (ADR-0114 D9): a foreign leg whose rate is missing makes its
/// row's balance, and every balance after it on that category, null, naming every missing
/// pair and day met so far. A partial running sum is never shown.</para>
/// <para><b>Cost.</b> A page with no category row reads one statement: the currencies of
/// the accounts it names. A category page reads the same statement, which also answers
/// whether any account of the ledger is in a currency other than the reporting one; when
/// none is, every balance is the stored one and nothing more is read, no rate included.
/// Otherwise one more statement reads the foreign legs, and the rate lookup at most three
/// (<see cref="ExchangeRateLookup"/>), however long the history.</para>
/// </remarks>
internal sealed class RegisterMoney
{
    private readonly IReadOnlyDictionary<Guid, string> _currencyOf;
    private readonly IReadOnlySet<Guid> _categories;
    private readonly string? _reporting;
    private readonly IReadOnlyDictionary<(Guid AccountId, Guid HeaderId), Converted>? _converted;

    private RegisterMoney(
        IReadOnlyDictionary<Guid, string> currencyOf,
        IReadOnlySet<Guid> categories,
        string? reporting,
        IReadOnlyDictionary<(Guid AccountId, Guid HeaderId), Converted>? converted)
    {
        _currencyOf = currencyOf;
        _categories = categories;
        _reporting = reporting;
        _converted = converted;
    }

    /// <summary>A row a page shows, as the view read it.</summary>
    internal readonly record struct Row(
        Guid AccountId,
        bool OnCategory,
        Guid? CounterpartyAccountId,
        string? CounterpartyAccountType,
        Guid HeaderId,
        DateTime PostedAt,
        long Seq);

    internal static Row Of(ResolvedTransactionView r) => new(
        r.AccountId, r.AccountType == "category", r.CounterpartyAccountId, r.CounterpartyAccountType,
        r.HeaderId, r.PostedAt, r.HeaderSeq);

    /// <summary>
    /// The currencies and converted balances of <paramref name="rows"/>, all on accounts of
    /// one ledger.
    /// </summary>
    public static async Task<RegisterMoney> LoadAsync(
        AppDbContext db, TimeProvider clock, IReadOnlyCollection<Row> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        HashSet<Guid> categories = [.. rows.Where(r => r.OnCategory).Select(r => r.AccountId)];
        if (rows.Count == 0)
            return new RegisterMoney(new Dictionary<Guid, string>(), categories, null, null);

        Guid[] ids = [.. rows.Select(r => r.AccountId)
            .Concat(rows.Where(r => r.OnCategory && r.CounterpartyAccountId is not null)
                .Select(r => r.CounterpartyAccountId!.Value))
            .Distinct()];

        if (categories.Count == 0)
        {
            var own = await db.Accounts.AsNoTracking()
                .Where(a => ids.Contains(a.Id))
                .Select(a => new { a.Id, a.CurrencyCode })
                .ToDictionaryAsync(a => a.Id, a => a.CurrencyCode, cancellationToken)
                .ConfigureAwait(false);
            return new RegisterMoney(own, categories, null, null);
        }

        // One statement: the accounts' currencies, the ledger's reporting currency and
        // whether any account of the ledger is in another. The two ledger figures are
        // keyed by a parameter, not by the row, so each is computed once for the statement.
        var anchor = categories.First();
        var ledgerOfAnchor = db.Accounts.Where(a => a.Id == anchor).Select(a => a.LedgerId);
        var reportingOfAnchor = db.Ledgers
            .Where(l => ledgerOfAnchor.Contains(l.Id))
            .Select(l => l.CurrencyCode);
        var read = await db.Accounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.CurrencyCode,
                a.LedgerId,
                Reporting = reportingOfAnchor.FirstOrDefault(),
                AnyForeign = db.Accounts.Any(o => ledgerOfAnchor.Contains(o.LedgerId)
                                                  && o.CurrencyCode != reportingOfAnchor.FirstOrDefault()),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currencyOf = read.ToDictionary(a => a.Id, a => a.CurrencyCode);
        var first = read.First(a => a.Id == anchor);
        var reporting = first.Reporting ?? DisplayCurrency.Fallback;

        // Every account in the reporting currency: every category leg is in it too, so each
        // stored balance is already the converted one, and no leg or rate is read.
        if (!first.AnyForeign) return new RegisterMoney(currencyOf, categories, reporting, null);

        var converted = await ConvertAsync(db, clock, first.LedgerId, reporting,
            [.. rows.Where(r => r.OnCategory)],
            [.. categories.Where(c => currencyOf.TryGetValue(c, out var own) && own != reporting)],
            cancellationToken).ConfigureAwait(false);
        return new RegisterMoney(currencyOf, categories, reporting, converted);
    }

    /// <summary>
    /// Each category row's balance in <paramref name="reporting"/>: its stored running
    /// balance, corrected for every foreign leg on or before it.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<(Guid AccountId, Guid HeaderId), Converted>> ConvertAsync(
        AppDbContext db, TimeProvider clock, Guid ledgerId, string reporting,
        IReadOnlyCollection<Row> rows, IReadOnlyCollection<Guid> foreignCategories,
        CancellationToken cancellationToken)
    {
        Guid[] accounts = [.. rows.Select(r => r.AccountId).Distinct()];
        Guid[] headers = [.. rows.Select(r => r.HeaderId).Distinct()];
        Guid[] ownForeign = [.. foreignCategories];
        var through = rows.Max(r => r.PostedAt);

        // The headers the walk counts (migration 277's balance_walk_headers): not a
        // template, not hidden, not merged into another.
        var walked = db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && !h.IsRecurringTemplate && !h.IsHidden && h.IsMergedInto == null
                        && h.PostedAt <= through);

        // A category leg's money is in the currency of the real account on the other side
        // of its posting, as the category totals read it, else the category's own. So a
        // leg is foreign in two ways, read apart so neither scans the category's legs for
        // nothing. Paid from a real account in another currency: reached from that
        // account's legs, which on most ledgers are few, through the posting key.
        var paidForeign =
            from p in db.TxnLegs.AsNoTracking()
            join pa in db.Accounts.AsNoTracking() on p.AccountId equals pa.Id
            join l in db.TxnLegs.AsNoTracking()
                on new { p.HeaderId, p.PostingIndex } equals new { l.HeaderId, l.PostingIndex }
            join h in walked on l.HeaderId equals h.Id
            where pa.LedgerId == ledgerId && pa.AccountType != "category" && pa.CurrencyCode != reporting
                  && l.Id != p.Id && accounts.Contains(l.AccountId)
            select new { l.AccountId, Currency = pa.CurrencyCode, h.PostedAt, h.Seq, l.Amount };
        // Between two categories, in a category's own code when that is not the reporting
        // currency: only those categories' legs are read, and only when there are any.
        var foreignLegs = ownForeign.Length == 0
            ? paidForeign
            : paidForeign.Concat(
                from l in db.TxnLegs.AsNoTracking()
                join a in db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                join h in walked on l.HeaderId equals h.Id
                where ownForeign.Contains(l.AccountId)
                      && !db.TxnLegs.Any(o => o.HeaderId == l.HeaderId && o.PostingIndex == l.PostingIndex
                                              && o.Id != l.Id
                                              && db.Accounts.Any(oa => oa.Id == o.AccountId
                                                                       && oa.AccountType != "category"))
                select new { l.AccountId, Currency = a.CurrencyCode, h.PostedAt, h.Seq, l.Amount });
        var foreign =
            from f in foreignLegs
            group f.Amount by new { f.AccountId, f.Currency, f.PostedAt, f.Seq } into g
            select new HistoryRow
            {
                AccountId = g.Key.AccountId,
                HeaderId = null,
                Currency = g.Key.Currency,
                PostedAt = g.Key.PostedAt,
                Seq = g.Key.Seq,
                Amount = g.Sum(),
            };
        // The rows' stored balances in the same statement, so the correction and the
        // figure it corrects are one snapshot.
        var stored =
            from b in db.TxnHeaderAccountBalances.AsNoTracking()
            where accounts.Contains(b.AccountId) && headers.Contains(b.HeaderId)
            select new HistoryRow
            {
                AccountId = b.AccountId,
                HeaderId = b.HeaderId,
                Currency = null,
                PostedAt = DateTime.MinValue,
                Seq = 0L,
                Amount = b.BalanceAfter,
            };
        var history = await foreign.Concat(stored).ToListAsync(cancellationToken).ConfigureAwait(false);

        var lookup = new ExchangeRateLookup(db, clock);
        var needs = lookup.NewNeeds();
        foreach (var f in history.Where(h => h.HeaderId is null))
            needs.Add(f.Currency!, reporting, DateOnly.FromDateTime(f.PostedAt));
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var walks = history.Where(h => h.HeaderId is null)
            .GroupBy(h => h.AccountId)
            .ToDictionary(g => g.Key, g => Walk.Of(g, rates, reporting));
        var storedAt = history.Where(h => h.HeaderId is not null)
            .ToDictionary(h => (h.AccountId, h.HeaderId!.Value), h => h.Amount);

        var result = new Dictionary<(Guid, Guid), Converted>();
        foreach (var r in rows)
        {
            var key = (r.AccountId, r.HeaderId);
            if (result.ContainsKey(key)) continue;
            var (delta, why) = walks.TryGetValue(r.AccountId, out var walk)
                ? walk.At(r.PostedAt, r.Seq)
                : (0m, RateReasons.None);
            result[key] = why.Count > 0
                ? new Converted(null, why)
                : storedAt.TryGetValue(key, out var s)
                    ? Converted.Of(ConvertedTotal.Round(s + delta))
                    : new Converted(null, RateReasons.None);
        }
        return result;
    }

    /// <summary>A foreign leg sum per (account, header, currency), or a stored running
    /// balance (<see cref="HeaderId"/> set).</summary>
    private sealed class HistoryRow
    {
        public Guid AccountId { get; init; }
        public Guid? HeaderId { get; init; }
        public string? Currency { get; init; }
        public DateTime PostedAt { get; init; }
        public long Seq { get; init; }
        public decimal Amount { get; init; }
    }

    /// <summary>
    /// One category's foreign legs in the walk's order, each with the running correction
    /// through it and how many of the missing rates met so far it has.
    /// </summary>
    private sealed class Walk
    {
        private readonly (DateTime PostedAt, long Seq)[] _keys;
        private readonly decimal[] _delta;
        private readonly int[] _missing;
        private readonly List<RateUnavailable> _why;

        private Walk((DateTime, long)[] keys, decimal[] delta, int[] missing, List<RateUnavailable> why)
        {
            _keys = keys;
            _delta = delta;
            _missing = missing;
            _why = why;
        }

        public static Walk Of(IEnumerable<HistoryRow> legs, ExchangeRateTable rates, string reporting)
        {
            var ordered = legs.OrderBy(l => l.PostedAt).ThenBy(l => l.Seq).ToArray();
            var keys = new (DateTime, long)[ordered.Length];
            var delta = new decimal[ordered.Length];
            var missing = new int[ordered.Length];
            var why = new List<RateUnavailable>();
            var sum = 0m;
            for (var i = 0; i < ordered.Length; i++)
            {
                var leg = ordered[i];
                var rate = rates.RateFor(leg.Currency!, reporting, DateOnly.FromDateTime(leg.PostedAt));
                if (rate.Unavailable is { } m)
                {
                    if (!why.Contains(m)) why.Add(m);
                }
                else
                {
                    // The stored balance holds the leg as it stands; the converted one
                    // holds it at its rate.
                    sum += leg.Amount * (rate.Rate!.Value - 1m);
                }
                keys[i] = (leg.PostedAt, leg.Seq);
                delta[i] = sum;
                missing[i] = why.Count;
            }
            return new Walk(keys, delta, missing, why);
        }

        /// <summary>The correction, and the missing rates, through the header at
        /// (<paramref name="postedAt"/>, <paramref name="seq"/>).</summary>
        public (decimal Delta, IReadOnlyList<RateUnavailable> Why) At(DateTime postedAt, long seq)
        {
            // The last leg at or before the position: the walk orders by (posted_at, seq).
            int lo = 0, hi = _keys.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                var k = _keys[mid];
                if (k.PostedAt < postedAt || (k.PostedAt == postedAt && k.Seq <= seq)) lo = mid + 1;
                else hi = mid;
            }
            var i = lo - 1;
            if (i < 0) return (0m, RateReasons.None);
            return (_delta[i], _missing[i] == 0 ? RateReasons.None : _why.GetRange(0, _missing[i]));
        }
    }

    /// <summary>The currency <paramref name="row"/>'s amount is in.</summary>
    private string CurrencyOf(RegisterRowDto row)
    {
        var onCategory = _categories.Contains(row.AccountId);
        if (onCategory && row.CounterpartyAccountType is not (null or "category")
            && row.CounterpartyAccountId is { } paying
            && _currencyOf.TryGetValue(paying, out var payingCurrency))
            return payingCurrency;
        return _currencyOf.TryGetValue(row.AccountId, out var own) ? own : _reporting ?? DisplayCurrency.Fallback;
    }

    /// <summary><paramref name="row"/> with the currencies of its figures, and on a
    /// category its balance converted into the reporting currency.</summary>
    public T Apply<T>(T row) where T : RegisterRowDto
    {
        ArgumentNullException.ThrowIfNull(row);
        var currency = CurrencyOf(row);
        if (!_categories.Contains(row.AccountId))
            return row with { CurrencyCode = currency, BalanceCurrencyCode = currency };

        var reporting = _reporting ?? DisplayCurrency.Fallback;
        if (_converted is null || !_converted.TryGetValue((row.AccountId, row.HeaderId), out var balance))
            return row with { CurrencyCode = currency, BalanceCurrencyCode = reporting };
        return row with
        {
            CurrencyCode = currency,
            BalanceCurrencyCode = reporting,
            BalanceAfter = balance.Amount,
            BalanceRateUnavailable = balance.Unavailable.Count > 0 ? balance.Unavailable : null,
        };
    }

    /// <summary>The balance a row of <paramref name="accountId"/> on
    /// <paramref name="headerId"/> shows, given its <paramref name="stored"/> one.</summary>
    public Converted BalanceOf(Guid accountId, Guid headerId, decimal? stored) =>
        _categories.Contains(accountId) && _converted is not null
        && _converted.TryGetValue((accountId, headerId), out var balance)
            ? balance
            : new Converted(stored, RateReasons.None);

    /// <summary>Every row of <paramref name="entries"/> through <see cref="Apply{T}"/>.</summary>
    public IReadOnlyList<RegisterEntryDto> Apply(IReadOnlyList<RegisterEntryDto> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Select(e => e with
        {
            Txn = e.Txn is null ? null : Apply(e.Txn),
            Legs = e.Legs is null ? null : [.. e.Legs.Select(Apply)],
        })];
    }
}
