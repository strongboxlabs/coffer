namespace Coffer.Api.Provisioning.Demo;

/// <summary>A trade's size: <see cref="Shares"/> in the security's share units and
/// <see cref="Cash"/> in cents, both positive.</summary>
public readonly record struct DemoTrade(long Shares, long Cash);

/// <summary>
/// Builds investment-route rows in Moneydance's native shapes (the ten actions
/// ADR-0027 maps), priced at the day's close. It builds rows; the caller posts
/// them under its own pattern.
/// </summary>
/// <remarks>
/// <para><b>Shapes</b> (split order as Moneydance writes it; <c>pamt</c> is the
/// investment account's cash, <c>samt</c> the split account's amount, shares on a
/// <c>sec</c> split):</para>
/// <list type="bullet">
///   <item><description><c>buy</c>: sec(-cash, +shares) [+ fee(-fee, +fee)];
///   <c>xfrtp_buysell</c>.</description></item>
///   <item><description><c>sell</c>: sec(+cash, -shares) [+ fee];
///   <c>xfrtp_buysell</c>.</description></item>
///   <item><description><c>buyx</c>: sec(-cash, +shares) [+ fee] + xfr(+total, -total)
///   on the funding account; <c>xfrtp_buysellxfr</c>.</description></item>
///   <item><description><c>sellx</c>: sec(+cash, -shares) [+ fee] + xfr(-net, +net) on
///   the receiving account; <c>xfrtp_buysellxfr</c>.</description></item>
///   <item><description><c>div</c>: sec(0, 0) + inc(+cash, -cash);
///   <c>xfrtp_dividend</c>.</description></item>
///   <item><description><c>divr</c>: sec(-cash, +shares) + inc(+cash, -cash);
///   <c>xfrtp_dividend</c>.</description></item>
///   <item><description><c>divx</c>: sec(0, 0) + inc(+cash, -cash) + xfr(-cash, +cash)
///   on the receiving account; <c>xfrtp_dividendxfr</c>.</description></item>
///   <item><description><c>inc</c>: sec(0, 0) + inc(+cash, -cash);
///   <c>xfrtp_miscincexp</c>.</description></item>
///   <item><description><c>exp</c>: sec(0, 0) + exp(-cash, +cash);
///   <c>xfrtp_miscincexp</c>.</description></item>
///   <item><description><c>bank</c>: one xfr; <c>xfrtp_bank</c>.</description></item>
/// </list>
///
/// <para><b>Pricing.</b> A trade executes at the day's close: a buy for an amount
/// takes <c>shares = round_dec(cash / price)</c> and keeps the amount as its cash; a
/// sale of a share count takes <c>cash = round2(shares * price)</c>. Rounding is half
/// away from zero. Trades fall on business days only, and never on the splitting
/// stock's split day.</para>
///
/// <para><b>Commission.</b> A stock trade carries an Investment &gt; Trading Commission
/// <c>fee</c> split; a fund trade carries none.</para>
/// </remarks>
public sealed class DemoInvestmentDesk
{
    /// <summary>The commission on one stock trade, in cents.</summary>
    public const long StockCommission = 4_95L;

    private readonly string _feeCategory;

    /// <summary>A desk over the book and the market.</summary>
    public DemoInvestmentDesk(DemoBook book, DemoMarket market)
    {
        Book = book ?? throw new ArgumentNullException(nameof(book));
        Market = market ?? throw new ArgumentNullException(nameof(market));
        _feeCategory = book.Base.Category(MdCategoryType.Expense, "Investment", "Trading Commission");
    }

    /// <summary>The book rows are built for.</summary>
    public DemoBook Book { get; }

    /// <summary>The prices trades execute at.</summary>
    public DemoMarket Market { get; }

    /// <summary>The commission a trade in the security carries: a stock's, or none.</summary>
    public static long Commission(string securityKey) => DemoSecurities.Get(securityKey).IsStock ? StockCommission : 0L;

    /// <summary>The holding of <paramref name="securityKey"/> under
    /// <paramref name="accountRole"/>.</summary>
    public DemoAccount Holding(string accountRole, string securityKey) =>
        DemoHoldings.Holding(Book, accountRole, securityKey);

    /// <summary>True when the security can trade on <paramref name="date"/>: a
    /// business day inside the window that is not the splitting stock's split day.</summary>
    public bool CanTrade(string securityKey, DateOnly date) =>
        Book.Calendar.Contains(date)
        && DemoCalendar.IsBusinessDay(date)
        && !(securityKey == DemoSecurities.Nvda && date == DemoSecurities.SplitDate);

    /// <summary>The shares <paramref name="cash"/> buys at the close: rounded to the
    /// security's share units. Zero when the amount buys less than half a unit.</summary>
    public long SharesFor(string securityKey, DateOnly date, long cash)
    {
        var security = DemoSecurities.Get(securityKey);
        var price = Market.Close(securityKey, date);
        return decimal.ToInt64(decimal.Round(
            cash / 100m / price * security.ShareUnit, 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>The cash <paramref name="shares"/> fetch at the close, to the cent.</summary>
    public long CashFor(string securityKey, DateOnly date, long shares)
    {
        var security = DemoSecurities.Get(securityKey);
        var price = Market.Close(securityKey, date);
        return DemoAmortization.RoundCents(shares * price * 100m / security.ShareUnit);
    }

    /// <summary>A <c>buy</c> of <paramref name="cash"/> worth, paid from the account's
    /// cash; a stock buy also pays its commission.</summary>
    public DemoTxn Buy(
        string accountRole, string securityKey, DateOnly date, long cash,
        string? memo = null, string? tags = null, string? securityTags = null)
    {
        var trade = new DemoTrade(RequireShares(securityKey, date, cash), cash);
        var splits = new List<DemoSplit> { SecIn(accountRole, securityKey, trade, securityTags) };
        AddFee(splits, securityKey);
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "buy", "xfrtp_buysell", memo ?? "Buy", tags, splits);
    }

    /// <summary>A <c>buyx</c> of <paramref name="cash"/> worth, funded by a transfer
    /// from the account with <paramref name="fundingRole"/>.</summary>
    public DemoTxn BuyX(
        string accountRole, string securityKey, DateOnly date, long cash, string fundingRole,
        string? memo = null, string? tags = null)
    {
        var trade = new DemoTrade(RequireShares(securityKey, date, cash), cash);
        var splits = new List<DemoSplit> { SecIn(accountRole, securityKey, trade, securityTags: null) };
        AddFee(splits, securityKey);
        var total = cash + Commission(securityKey);
        splits.Add(Xfr(fundingRole, amountOnAccount: total));
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "buyx", "xfrtp_buysellxfr", memo ?? "Buy", tags, splits);
    }

    /// <summary>A <c>sell</c> of <paramref name="shares"/>, the proceeds kept as cash.</summary>
    public DemoTxn Sell(
        string accountRole, string securityKey, DateOnly date, long shares, string? memo = null, string? tags = null)
    {
        var trade = new DemoTrade(shares, RequireCash(securityKey, date, shares));
        var splits = new List<DemoSplit> { SecOut(accountRole, securityKey, trade) };
        AddFee(splits, securityKey);
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "sell", "xfrtp_buysell", memo ?? "Sell", tags, splits);
    }

    /// <summary>A <c>sellx</c> of <paramref name="shares"/>, the proceeds less any
    /// commission transferred to the account with <paramref name="receivingRole"/>.</summary>
    public DemoTxn SellX(
        string accountRole, string securityKey, DateOnly date, long shares, string receivingRole,
        string? memo = null, string? tags = null)
    {
        var trade = new DemoTrade(shares, RequireCash(securityKey, date, shares));
        var splits = new List<DemoSplit> { SecOut(accountRole, securityKey, trade) };
        AddFee(splits, securityKey);
        splits.Add(Xfr(receivingRole, amountOnAccount: -(trade.Cash - Commission(securityKey))));
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "sellx", "xfrtp_buysellxfr", memo ?? "Sell", tags, splits);
    }

    /// <summary>A cash <c>div</c> of <paramref name="cash"/> on the income category.</summary>
    public DemoTxn Div(string accountRole, string securityKey, DateOnly date, long cash, string incomeCategoryId, string? memo = null)
    {
        RequirePositive(cash);
        var splits = new List<DemoSplit>
        {
            SecLink(accountRole, securityKey),
            Category(incomeCategoryId, amountOnAccount: cash, "inc"),
        };
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "div", "xfrtp_dividend", memo ?? "Dividend", tags: null, splits);
    }

    /// <summary>A reinvested <c>divr</c>: the income buys shares at the close.</summary>
    public DemoTxn DivR(string accountRole, string securityKey, DateOnly date, long cash, string incomeCategoryId, string? memo = null)
    {
        var trade = new DemoTrade(RequireShares(securityKey, date, cash), cash);
        var splits = new List<DemoSplit>
        {
            SecIn(accountRole, securityKey, trade, securityTags: null),
            Category(incomeCategoryId, amountOnAccount: cash, "inc"),
        };
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "divr", "xfrtp_dividend", memo ?? "Reinvested dividend", tags: null, splits);
    }

    /// <summary>A transferred <c>divx</c>: the income goes straight to the account
    /// with <paramref name="receivingRole"/>.</summary>
    public DemoTxn DivX(
        string accountRole, string securityKey, DateOnly date, long cash, string incomeCategoryId, string receivingRole,
        string? memo = null)
    {
        RequirePositive(cash);
        var splits = new List<DemoSplit>
        {
            SecLink(accountRole, securityKey),
            Category(incomeCategoryId, amountOnAccount: cash, "inc"),
            Xfr(receivingRole, amountOnAccount: -cash),
        };
        return Row(accountRole, DemoSecurities.Get(securityKey), date, "divx", "xfrtp_dividendxfr", memo ?? "Dividend", tags: null, splits);
    }

    /// <summary>Miscellaneous income (<c>inc</c>) into the account's cash, linked to
    /// a security it holds: an employer match, say.</summary>
    public DemoTxn Inc(
        string accountRole, string securityKey, DateOnly date, long cash, string incomeCategoryId,
        string description, string? memo = null)
    {
        RequirePositive(cash);
        var splits = new List<DemoSplit>
        {
            SecLink(accountRole, securityKey),
            Category(incomeCategoryId, amountOnAccount: cash, "inc"),
        };
        return Row(accountRole, description, date, "inc", "xfrtp_miscincexp", memo, tags: null, splits);
    }

    /// <summary>A miscellaneous expense (<c>exp</c>) paid from the account's cash,
    /// linked to a security it holds: a medical bill paid from the HSA, say.</summary>
    public DemoTxn Exp(
        string accountRole, string securityKey, DateOnly date, long cash, string expenseCategoryId,
        string description, string? memo = null)
    {
        RequirePositive(cash);
        var splits = new List<DemoSplit>
        {
            SecLink(accountRole, securityKey),
            Category(expenseCategoryId, amountOnAccount: -cash, "exp"),
        };
        return Row(accountRole, description, date, "exp", "xfrtp_miscincexp", memo, tags: null, splits);
    }

    /// <summary>A <c>bank</c> cash move entered in the investment account:
    /// <paramref name="cashIn"/> into its cash (negative sends cash out) from the
    /// account with <paramref name="otherRole"/>.</summary>
    public DemoTxn Bank(
        string accountRole, DateOnly date, long cashIn, string otherRole, string description,
        string? memo = null, string? tags = null)
    {
        if (cashIn == 0) throw new ArgumentOutOfRangeException(nameof(cashIn), "a cash move moves something");
        var splits = new List<DemoSplit> { Xfr(otherRole, amountOnAccount: cashIn) };
        return new DemoTxn
        {
            AccountId = Book.Account(accountRole).Id,
            Date = date,
            Description = description,
            Memo = memo,
            Route = DemoRoute.Investment,
            InvestTxnType = "bank",
            XferType = "xfrtp_bank",
            Tags = tags,
            Splits = splits,
        };
    }

    // A trade or a dividend is described by its security's name.
    private DemoTxn Row(
        string accountRole, DemoSecurity security, DateOnly date, string txnType, string xferType,
        string? memo, string? tags, List<DemoSplit> splits) =>
        Row(accountRole, security.Name, date, txnType, xferType, memo, tags, splits);

    private DemoTxn Row(
        string accountRole, string description, DateOnly date, string txnType, string xferType,
        string? memo, string? tags, List<DemoSplit> splits)
    {
        return new DemoTxn
        {
            AccountId = Book.Account(accountRole).Id,
            Date = date,
            Description = description,
            Memo = memo,
            Route = DemoRoute.Investment,
            InvestTxnType = txnType,
            XferType = xferType,
            Tags = tags,
            Splits = splits,
        };
    }

    private DemoSplit SecIn(string accountRole, string securityKey, DemoTrade trade, string? securityTags) => new()
    {
        AccountId = Holding(accountRole, securityKey).Id,
        ParentAmount = -trade.Cash,
        SplitAmount = trade.Shares,
        SplitType = "sec",
        Tags = securityTags,
    };

    private DemoSplit SecOut(string accountRole, string securityKey, DemoTrade trade) => new()
    {
        AccountId = Holding(accountRole, securityKey).Id,
        ParentAmount = trade.Cash,
        SplitAmount = -trade.Shares,
        SplitType = "sec",
    };

    // The zero sec split Moneydance writes on a dividend or a misc row to name the
    // security; nothing moves on it.
    private DemoSplit SecLink(string accountRole, string securityKey) => new()
    {
        AccountId = Holding(accountRole, securityKey).Id,
        ParentAmount = 0L,
        SplitAmount = 0L,
        SplitType = "sec",
    };

    private static DemoSplit Category(string categoryId, long amountOnAccount, string splitType) => new()
    {
        AccountId = categoryId,
        ParentAmount = amountOnAccount,
        SplitAmount = -amountOnAccount,
        SplitType = splitType,
    };

    // A transfer split: amountOnAccount moves the investment account's cash, and the
    // other account takes the opposite.
    private DemoSplit Xfr(string otherRole, long amountOnAccount) => new()
    {
        AccountId = Book.Account(otherRole).Id,
        ParentAmount = amountOnAccount,
        SplitAmount = -amountOnAccount,
        SplitType = "xfr",
    };

    private void AddFee(List<DemoSplit> splits, string securityKey)
    {
        var fee = Commission(securityKey);
        if (fee == 0) return;
        splits.Add(new DemoSplit
        {
            AccountId = _feeCategory,
            ParentAmount = -fee,
            SplitAmount = fee,
            SplitType = "fee",
        });
    }

    private long RequireShares(string securityKey, DateOnly date, long cash)
    {
        RequirePositive(cash);
        RequireTradeDay(securityKey, date);
        var shares = SharesFor(securityKey, date, cash);
        if (shares <= 0)
            throw new InvalidOperationException($"{securityKey} on {date:yyyy-MM-dd}: {cash} cents buys no share unit");
        return shares;
    }

    private long RequireCash(string securityKey, DateOnly date, long shares)
    {
        if (shares <= 0) throw new ArgumentOutOfRangeException(nameof(shares), shares, "a sale sells something");
        RequireTradeDay(securityKey, date);
        return CashFor(securityKey, date, shares);
    }

    private void RequireTradeDay(string securityKey, DateOnly date)
    {
        if (!CanTrade(securityKey, date))
            throw new InvalidOperationException($"{securityKey} does not trade on {date:yyyy-MM-dd}");
    }

    private static void RequirePositive(long cash)
    {
        if (cash <= 0) throw new ArgumentOutOfRangeException(nameof(cash), cash, "an amount is positive");
    }
}
