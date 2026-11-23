namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The Demo's prices: one closing price per security per business day of the
/// window, and the stable-value fund's unit value on every day.
/// </summary>
/// <remarks>
/// <para><b>Walks.</b> <c>p(t) = round4(p(t-1) * (1 + drift + spread * z(t)))</c>, with
/// <c>z</c> built from bell draws (twelve uniforms less six) on the security's own
/// stream (<c>price:&lt;key&gt;</c>, re-keyed monthly): each month's daily draws are
/// centred on their mean and share one month-wide draw, so the drift and the
/// drawdown, not the luck of one seed, set the five-year shape. Drift and spread
/// switch to the security's drawdown terms through
/// <see cref="DemoSecurities.DrawdownLifeYear"/>.</para>
///
/// <para><b>Blend.</b> The target-date fund steps by the weighted sum of its
/// components' daily returns, at the constant weights the manifest's look-through
/// carries.</para>
///
/// <para><b>Fixed</b> is the money-market fund's constant unit price. <b>Stable
/// value</b> accrues daily at its crediting rate, held at ten places and published at
/// four; it has no price rows (its plan's trades carry the price) and its month-end
/// values go to the manifest.</para>
///
/// <para><b>The split.</b> Prices are as traded, not split-adjusted: the splitting
/// stock's walk is in post-split units and a date before
/// <see cref="DemoSecurities.SplitDate"/> trades at the walk times the ratio.</para>
///
/// <para>A market is a pure function of the calendar and the seed. It is not a
/// pattern and posts nothing.</para>
/// </remarks>
public sealed class DemoMarket
{
    private readonly Dictionary<string, decimal[]> _closes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currencyIds = new(StringComparer.Ordinal);
    private readonly List<DateOnly> _businessDays;

    /// <summary>Build every price path for the calendar's window.</summary>
    public DemoMarket(DemoCalendar calendar, DemoRngFactory rng, DemoIds ids)
    {
        Calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(ids);

        _businessDays = calendar.BusinessDaysInWindow().ToList();
        if (_businessDays.Count == 0) throw new InvalidOperationException("the window has no business day");

        foreach (var security in DemoSecurities.All)
        {
            _currencyIds[security.Key] = ids.Id("curr", security.Key, 0);
        }

        foreach (var security in DemoSecurities.All.Where(s => s.Model == DemoPriceModel.Walk))
        {
            _closes[security.Key] = Walk(security, rng);
        }
        foreach (var security in DemoSecurities.All.Where(s => s.Model == DemoPriceModel.Blend))
        {
            _closes[security.Key] = Blend(security);
        }
        foreach (var security in DemoSecurities.All.Where(s => s.Model == DemoPriceModel.Fixed))
        {
            _closes[security.Key] = Fixed(security);
        }

        var stable = DemoSecurities.All.Single(s => s.Model == DemoPriceModel.StableValue);
        _closes[stable.Key] = StableValueDaily(out var monthEnds);
        StableValueMonthEnds = monthEnds;
    }

    /// <summary>The window the prices cover.</summary>
    public DemoCalendar Calendar { get; }

    /// <summary>Every security, in export order.</summary>
    public IReadOnlyList<DemoSecurity> Securities => DemoSecurities.All;

    /// <summary>The business days of the window, oldest first.</summary>
    public IReadOnlyList<DateOnly> BusinessDays => _businessDays;

    /// <summary>The stable-value fund's unit value at each full month's end, oldest
    /// first; the seeder enters the quarter-ends.</summary>
    public IReadOnlyList<DemoUnitValue> StableValueMonthEnds { get; }

    /// <summary>The split date, when it falls inside the window.</summary>
    public DateOnly? SplitDateInWindow =>
        Calendar.Contains(DemoSecurities.SplitDate) ? DemoSecurities.SplitDate : null;

    /// <summary>The MD id of the security's <c>curr</c> item.</summary>
    public string CurrencyId(string key) =>
        _currencyIds.TryGetValue(key, out var id) ? id : throw new KeyNotFoundException($"no Demo security has the key {key}");

    /// <summary>
    /// The security's closing price on <paramref name="date"/>, as traded, to four
    /// places. A priced security has one only on a business day; the stable-value
    /// fund has one every day.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The date is outside the window,
    /// or not a business day for a priced security.</exception>
    public decimal Close(string key, DateOnly date)
    {
        if (!Calendar.Contains(date))
            throw new ArgumentOutOfRangeException(nameof(date), date, "a price is dated inside the window");
        var closes = _closes.TryGetValue(key, out var found)
            ? found
            : throw new KeyNotFoundException($"no Demo security has the key {key}");
        var close = closes[Calendar.DayIndex(date)];
        if (close == 0m)
            throw new ArgumentOutOfRangeException(nameof(date), date, $"{key} has no price on a day markets are shut");
        return close;
    }

    /// <summary>True when the security has price rows (every one but the
    /// stable-value fund).</summary>
    public static bool HasPriceRows(DemoSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        return security.Model != DemoPriceModel.StableValue;
    }

    /// <summary>The security's price on every business day, oldest first: the
    /// <c>csnap</c> rows. Empty for the stable-value fund.</summary>
    public IEnumerable<(DateOnly Date, decimal Price)> PriceRows(string key)
    {
        var security = DemoSecurities.Get(key);
        if (!HasPriceRows(security)) yield break;
        foreach (var day in _businessDays) yield return (day, Close(key, day));
    }

    private decimal[] Walk(DemoSecurity security, DemoRngFactory rng)
    {
        var closes = new decimal[Calendar.DayCount];
        var adjusted = security.InitialPrice;
        var shocks = new Dictionary<DateOnly, decimal>();

        // Each month's steps (every business day but the window's first) draw from
        // that month's stream.
        foreach (var month in _businessDays.Skip(1).GroupBy(d => d.Year * 100 + d.Month))
        {
            var days = month.ToList();
            foreach (var (day, z) in days.Zip(MonthShocks(rng.Stream("price:" + security.Key, days[0]), days.Count)))
                shocks[day] = z;
        }

        for (var i = 0; i < _businessDays.Count; i++)
        {
            var day = _businessDays[i];
            if (i > 0)
            {
                var drawdown = DemoCalendar.LifeYearOf(Calendar.LifeMonthOf(day)) == DemoSecurities.DrawdownLifeYear;
                var drift = drawdown ? security.DrawdownDrift : security.DailyDrift;
                var spread = drawdown ? security.DrawdownVolatility : security.DailyVolatility;
                adjusted = Round4(adjusted * (1m + drift + spread * shocks[day]));
            }
            closes[Calendar.DayIndex(day)] = AsTraded(security, day, adjusted);
        }
        return closes;
    }

    // A month's daily shocks: bell draws centred on the month's own mean, plus one
    // month-wide bell draw spread evenly over its days at MonthShockScale. Centring
    // keeps a run of unlucky draws from swamping the drift and the drawdown that set
    // the five-year shape; the month-wide draw keeps months from moving in lockstep.
    private static IEnumerable<decimal> MonthShocks(DemoRng stream, int count)
    {
        var draws = new decimal[count];
        var sum = 0m;
        for (var i = 0; i < count; i++)
        {
            draws[i] = stream.NextStandardBell();
            sum += draws[i];
        }
        var month = stream.NextStandardBell() * MonthShockScale / count;
        var mean = sum / count;
        foreach (var draw in draws) yield return draw - mean + month;
    }

    // The month-wide shock's spread, in daily spreads.
    private const decimal MonthShockScale = 2m;

    // The splitting stock trades before its split at the post-split price times the
    // ratio; every other security trades at its walk.
    private static decimal AsTraded(DemoSecurity security, DateOnly day, decimal adjusted) =>
        security.Key == DemoSecurities.Nvda && day < DemoSecurities.SplitDate
            ? Round4(adjusted * DemoSecurities.SplitNewShares / DemoSecurities.SplitOldShares)
            : adjusted;

    private decimal[] Blend(DemoSecurity security)
    {
        var closes = new decimal[Calendar.DayCount];
        var price = security.InitialPrice;
        for (var i = 0; i < _businessDays.Count; i++)
        {
            var index = Calendar.DayIndex(_businessDays[i]);
            if (i > 0)
            {
                var previous = Calendar.DayIndex(_businessDays[i - 1]);
                var step = 0m;
                foreach (var (component, weight) in DemoSecurities.TargetDateWeights)
                {
                    var components = _closes[component];
                    step += weight * (components[index] / components[previous] - 1m);
                }
                price = Round4(price * (1m + step));
            }
            closes[index] = price;
        }
        return closes;
    }

    private decimal[] Fixed(DemoSecurity security)
    {
        var closes = new decimal[Calendar.DayCount];
        foreach (var day in _businessDays) closes[Calendar.DayIndex(day)] = security.InitialPrice;
        return closes;
    }

    private decimal[] StableValueDaily(out IReadOnlyList<DemoUnitValue> monthEnds)
    {
        var values = new decimal[Calendar.DayCount];
        var ends = new List<DemoUnitValue>();
        var unit = DemoSecurities.StableValueInitial;
        for (var day = Calendar.WindowStart; day <= Calendar.AsOf; day = day.AddDays(1))
        {
            if (day > Calendar.WindowStart)
            {
                var lifeYear = DemoCalendar.LifeYearOf(Calendar.LifeMonthOf(day));
                var rates = DemoSecurities.StableValueRatePercent;
                var rate = rates[lifeYear - 1 < rates.Count ? lifeYear - 1 : rates.Count - 1];
                unit = decimal.Round(unit * (1m + rate / 36_500m), 10, MidpointRounding.AwayFromZero);
            }
            var published = Round4(unit);
            values[Calendar.DayIndex(day)] = published;
            if (day.AddDays(1).Day == 1) ends.Add(new DemoUnitValue(day, published));
        }
        monthEnds = ends;
        return values;
    }

    private static decimal Round4(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
