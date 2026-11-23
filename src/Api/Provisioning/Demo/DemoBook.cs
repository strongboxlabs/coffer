namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The simulated ledger the patterns post into: accounts with their open and close
/// windows, every transaction with its per-leg status, day-end balances, FIFO lots
/// per holding, statement periods, and the self-checks the export must pass.
/// </summary>
/// <remarks>
/// <para><b>Posting rules, enforced as each row is posted</b> (a violation is a
/// generator bug, so it throws at once rather than surfacing in a test later):
/// every row is dated inside the window; its own account is a real book account,
/// active that day; it has 1 to 32 splits; every split names a book account or a
/// base category; a real counterpart is active that day; a dormant account is never
/// touched; a bank row writes no <c>invest.txntype</c>; every non-<c>sec</c> split has
/// <c>pamt == -samt</c>; a <c>sec</c> split sits on a holding account of the row's own
/// investment account.</para>
///
/// <para><b>Balances.</b> A row moves its own account by the sum of its
/// <c>pamt</c>s and each real counterpart by its <c>samt</c>. A <c>sec</c> split's
/// <c>samt</c> is shares, so holdings are tracked as lots, not as cash. Day-end
/// balances start from each account's opening balance (<c>sbal</c>).</para>
///
/// <para><b>Lots</b> are replayed from the posted rows in export order, so patterns
/// may post in any order. A buy (shares in) opens a lot at the cash paid; a sale
/// (shares out) closes the oldest lots first; a security split multiplies every
/// open lot's shares at the start of its day. Selling more than is held throws.</para>
///
/// <para><b>Self-checks</b> run in <see cref="Verify"/>: every account that must
/// stay non-negative does, every holding's lots replay without an oversell, and
/// every check a pattern registered with <see cref="AddCheck"/> passes.</para>
/// </remarks>
public sealed class DemoBook
{
    /// <summary>The most splits a row may carry (the importer reads 32).</summary>
    public const int MaxSplits = 32;

    private readonly List<DemoAccount> _accounts = [];
    private readonly Dictionary<string, DemoAccount> _byRole = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DemoAccount> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long[]> _deltas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long[]> _prefix = new(StringComparer.Ordinal);
    private readonly List<DemoTxn> _txns = [];
    private readonly Dictionary<string, long> _nextOrdinal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ranks = new(StringComparer.Ordinal);
    private readonly List<DemoSecuritySplit> _securitySplits = [];
    private readonly List<(string Name, Func<DemoBook, IEnumerable<string>> Check)> _checks = [];
    private readonly Dictionary<string, List<DemoTxn>> _marks = new(StringComparer.Ordinal);

    /// <summary>An empty book over the calendar's window.</summary>
    public DemoBook(DemoCalendar calendar, DemoIds ids, DemoBase baseData)
    {
        Calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        Ids = ids ?? throw new ArgumentNullException(nameof(ids));
        Base = baseData ?? throw new ArgumentNullException(nameof(baseData));
    }

    /// <summary>The window the book covers.</summary>
    public DemoCalendar Calendar { get; }

    /// <summary>The id source every generated row draws from.</summary>
    public DemoIds Ids { get; }

    /// <summary>The curated core: the root, the categories.</summary>
    public DemoBase Base { get; }

    /// <summary>Every account, in the order added (the export's order).</summary>
    public IReadOnlyList<DemoAccount> Accounts => _accounts;

    /// <summary>Every posted row, in posting order.</summary>
    public IReadOnlyList<DemoTxn> Transactions => _txns;

    /// <summary>Every security split, in the order applied.</summary>
    public IReadOnlyList<DemoSecuritySplit> SecuritySplits => _securitySplits;

    /// <summary>Add an account. Role and id must be unique; a holding account must
    /// hang under an investment account already in the book.</summary>
    public DemoAccount AddAccount(DemoAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.MdType is not ("b" or "c" or "v" or "a" or "l" or "o" or "s"))
            throw new ArgumentException($"account {account.Role}: '{account.MdType}' is not a real or holding type", nameof(account));
        if (_byRole.ContainsKey(account.Role))
            throw new ArgumentException($"account role {account.Role} is already in the book", nameof(account));
        if (_byId.ContainsKey(account.Id) || Base.IsCategory(account.Id) || account.Id == Base.RootAccountId)
            throw new ArgumentException($"account id {account.Id} ({account.Role}) is already taken", nameof(account));
        if (account.ActiveThrough is { } through && through < account.ActiveFrom)
            throw new ArgumentException($"account {account.Role} closes before it opens", nameof(account));

        if (account.IsHolding)
        {
            if (account.ParentId is null || !_byId.TryGetValue(account.ParentId, out var parent) || parent.MdType != "v")
                throw new ArgumentException($"holding {account.Role} must hang under an investment account in the book", nameof(account));
            if (string.IsNullOrEmpty(account.CurrencyId))
                throw new ArgumentException($"holding {account.Role} must name its security", nameof(account));
        }
        else
        {
            _deltas[account.Id] = new long[Calendar.DayCount];
        }

        _accounts.Add(account);
        _byRole[account.Role] = account;
        _byId[account.Id] = account;
        return account;
    }

    /// <summary>The account with the given role.</summary>
    /// <exception cref="KeyNotFoundException">No account has that role.</exception>
    public DemoAccount Account(string role) =>
        _byRole.TryGetValue(role, out var account)
            ? account
            : throw new KeyNotFoundException($"no Demo account has the role {role}");

    /// <summary>The account with the given MD id, or null.</summary>
    public DemoAccount? AccountById(string mdId) => _byId.GetValueOrDefault(mdId);

    /// <summary>Register a self-check that <see cref="Verify"/> runs; it returns one
    /// message per problem found.</summary>
    public void AddCheck(string name, Func<DemoBook, IEnumerable<string>> check)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add((name, check));
    }

    /// <summary>
    /// Post one row for <paramref name="pattern"/>: check it, give it and its splits
    /// their ids, and apply it to the balances.
    /// </summary>
    /// <exception cref="InvalidOperationException">The row breaks a posting rule.</exception>
    public DemoTxn Post(IDemoPattern pattern, DemoTxn txn)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(txn);
        if (txn.Id.Length != 0) throw new InvalidOperationException($"row {txn.Id} is already posted");

        if (_ranks.TryGetValue(pattern.Name, out var rank) && rank != pattern.Rank)
            throw new InvalidOperationException($"pattern {pattern.Name} posted with two ranks");
        _ranks[pattern.Name] = pattern.Rank;

        Validate(pattern.Name, txn);

        var ordinal = _nextOrdinal.GetValueOrDefault(pattern.Name);
        _nextOrdinal[pattern.Name] = ordinal + 1;

        txn.Pattern = pattern.Name;
        txn.PatternRank = pattern.Rank;
        txn.Ordinal = ordinal;
        txn.Id = Ids.Id("txn", pattern.Name, ordinal);
        for (var i = 0; i < txn.Splits.Count; i++)
        {
            txn.Splits[i].Id = Ids.Id("txn", pattern.Name, ordinal, i);
        }

        var day = Calendar.DayIndex(txn.Date);
        foreach (var (accountId, amount) in CashEffects(txn))
        {
            _deltas[accountId][day] += amount;
            _prefix.Remove(accountId);
        }

        _txns.Add(txn);
        return txn;
    }

    /// <summary>Note a posted row under <paramref name="key"/>, so the manifest, a
    /// later pattern or a test can find it again without matching its text.</summary>
    public DemoTxn Mark(string key, DemoTxn txn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(txn);
        if (txn.Id.Length == 0) throw new InvalidOperationException("only a posted row is marked");
        if (!_marks.TryGetValue(key, out var list)) _marks[key] = list = [];
        list.Add(txn);
        return txn;
    }

    /// <summary>The rows marked <paramref name="key"/>, in the order marked; empty
    /// when there are none.</summary>
    public IReadOnlyList<DemoTxn> Marked(string key) =>
        _marks.TryGetValue(key, out var list) ? list : [];

    /// <summary>Record a security split: every holding of the security has its
    /// shares multiplied by <paramref name="newShares"/>/<paramref name="oldShares"/>
    /// at the start of <paramref name="date"/>.</summary>
    public DemoSecuritySplit ApplySecuritySplit(string securityCurrencyId, DateOnly date, long newShares, long oldShares)
    {
        ArgumentException.ThrowIfNullOrEmpty(securityCurrencyId);
        if (newShares <= 0 || oldShares <= 0) throw new ArgumentOutOfRangeException(nameof(newShares), "a split ratio is positive");
        if (!Calendar.Contains(date)) throw new ArgumentOutOfRangeException(nameof(date), date, "a split is dated inside the window");
        var split = new DemoSecuritySplit(securityCurrencyId, date, newShares, oldShares);
        _securitySplits.Add(split);
        return split;
    }

    /// <summary>How much <paramref name="txn"/> moves each real account's cash:
    /// its own account by the sum of its <c>pamt</c>s, each real counterpart by its
    /// <c>samt</c>. Holdings and categories are not listed.</summary>
    public IEnumerable<(string AccountId, long Amount)> CashEffects(DemoTxn txn)
    {
        ArgumentNullException.ThrowIfNull(txn);
        var own = 0L;
        foreach (var split in txn.Splits) own += split.ParentAmount;
        yield return (txn.AccountId, own);

        foreach (var split in txn.Splits)
        {
            if (_byId.TryGetValue(split.AccountId, out var account) && !account.IsHolding)
                yield return (split.AccountId, split.SplitAmount);
        }
    }

    /// <summary>The account's balance at the end of <paramref name="date"/>: cents for
    /// a real account, shares for a holding. Before the window it is the opening
    /// balance; after AsOf it is AsOf's.</summary>
    public long BalanceAtEndOf(string accountId, DateOnly date)
    {
        var account = RequireAccount(accountId);
        if (account.IsHolding) return Shares(accountId, date);
        if (date < Calendar.WindowStart) return account.OpeningBalance;
        var day = Calendar.DayIndex(date > Calendar.AsOf ? Calendar.AsOf : date);
        return account.OpeningBalance + Prefix(accountId)[day];
    }

    /// <summary>The account's balance at the start of <paramref name="date"/>: the
    /// end of the day before.</summary>
    public long BalanceAtStartOf(string accountId, DateOnly date) => BalanceAtEndOf(accountId, date.AddDays(-1));

    /// <summary>The lowest day-end balance of a real account from
    /// <paramref name="from"/> through <paramref name="through"/>, and the first day
    /// it is reached.</summary>
    public (long Balance, DateOnly Date) LowestDayEnd(string accountId, DateOnly from, DateOnly through)
    {
        var account = RequireAccount(accountId);
        if (account.IsHolding) throw new ArgumentException($"{account.Role} is a holding; its lots are checked instead", nameof(accountId));
        if (from < Calendar.WindowStart) from = Calendar.WindowStart;
        if (through > Calendar.AsOf) through = Calendar.AsOf;
        if (through < from) throw new ArgumentException("the range is empty", nameof(through));

        var prefix = Prefix(accountId);
        var lowest = long.MaxValue;
        var when = from;
        for (var d = from; d <= through; d = d.AddDays(1))
        {
            var balance = account.OpeningBalance + prefix[Calendar.DayIndex(d)];
            if (balance < lowest)
            {
                lowest = balance;
                when = d;
            }
        }
        return (lowest, when);
    }

    /// <summary>The net of the postings on a real account inside the period.</summary>
    public long PostingsIn(string accountId, DemoStatementPeriod period) =>
        BalanceAtEndOf(accountId, period.ClosesOn) - BalanceAtEndOf(accountId, period.OpensAfter);

    /// <summary>
    /// The statements of an account with a <see cref="DemoAccount.Statement"/> cycle
    /// whose close falls on or after it opens and on or before both AsOf and its
    /// close, oldest first. The first may open before the window.
    /// </summary>
    public IReadOnlyList<DemoStatementPeriod> StatementPeriods(string accountId)
    {
        var account = RequireAccount(accountId);
        var cycle = account.Statement
            ?? throw new ArgumentException($"{account.Role} has no statement cycle", nameof(accountId));

        var last = account.ActiveThrough is { } through && through < Calendar.AsOf ? through : Calendar.AsOf;
        var periods = new List<DemoStatementPeriod>();
        for (var month = Calendar.LifeMonthOf(account.ActiveFrom); month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var monthStart = Calendar.MonthStart(month);
            var close = cycle.CloseIn(monthStart);
            if (close < account.ActiveFrom || close > last) continue;
            periods.Add(new DemoStatementPeriod(cycle.CloseIn(monthStart.AddMonths(-1)), close));
        }
        return periods;
    }

    /// <summary>The open lots of a holding at the end of <paramref name="date"/>,
    /// oldest first.</summary>
    /// <exception cref="InvalidOperationException">A sale up to that date oversells.</exception>
    public IReadOnlyList<DemoLot> OpenLots(string holdingId, DateOnly date) => Replay(holdingId, date).Open;

    /// <summary>Every disposal out of a holding's lots through AsOf, in order.</summary>
    /// <exception cref="InvalidOperationException">A sale oversells.</exception>
    public IReadOnlyList<DemoDisposal> Disposals(string holdingId) => Replay(holdingId, Calendar.AsOf).Disposals;

    /// <summary>The shares a holding has at the end of <paramref name="date"/>.</summary>
    public long Shares(string holdingId, DateOnly date)
    {
        var total = 0L;
        foreach (var lot in OpenLots(holdingId, date)) total += lot.Shares;
        return total;
    }

    /// <summary>
    /// The exported and simulated rows in export order: by date, then pattern rank,
    /// then pattern name (ordinal), then the row's ordinal in its pattern.
    /// </summary>
    public IReadOnlyList<DemoTxn> TransactionsInExportOrder()
    {
        var sorted = new List<DemoTxn>(_txns);
        sorted.Sort(CompareExportOrder);
        return sorted;
    }

    /// <summary>Every problem the self-checks find; empty when the book is sound.</summary>
    public IReadOnlyList<string> FindProblems()
    {
        var problems = new List<string>();

        foreach (var account in _accounts)
        {
            if (account.IsHolding)
            {
                try
                {
                    Replay(account.Id, Calendar.AsOf);
                }
                catch (InvalidOperationException ex)
                {
                    problems.Add(ex.Message);
                }
                continue;
            }

            if (account.MustStayNonNegative)
            {
                var (lowest, when) = LowestDayEnd(account.Id, Calendar.WindowStart, Calendar.AsOf);
                if (lowest < 0) problems.Add($"{account.Role} ends {when:yyyy-MM-dd} below zero");
            }
        }

        foreach (var (name, check) in _checks)
        {
            foreach (var problem in check(this)) problems.Add($"{name}: {problem}");
        }
        return problems;
    }

    /// <summary>Run every self-check.</summary>
    /// <exception cref="DemoSelfCheckException">Any check fails; the message lists
    /// every problem.</exception>
    public void Verify()
    {
        var problems = FindProblems();
        if (problems.Count > 0) throw new DemoSelfCheckException(problems);
    }

    /// <summary>The export order: date, pattern rank, pattern name (ordinal), row ordinal.</summary>
    internal static int CompareExportOrder(DemoTxn a, DemoTxn b)
    {
        var byDate = a.Date.CompareTo(b.Date);
        if (byDate != 0) return byDate;
        var byRank = a.PatternRank.CompareTo(b.PatternRank);
        if (byRank != 0) return byRank;
        var byPattern = string.CompareOrdinal(a.Pattern, b.Pattern);
        return byPattern != 0 ? byPattern : a.Ordinal.CompareTo(b.Ordinal);
    }

    private void Validate(string pattern, DemoTxn txn)
    {
        string Where() => $"{pattern} row on {txn.Date:yyyy-MM-dd} ('{txn.Description}')";

        if (!Calendar.Contains(txn.Date))
            throw new InvalidOperationException($"{Where()} is outside the window");
        if (!_byId.TryGetValue(txn.AccountId, out var own) || own.IsHolding)
            throw new InvalidOperationException($"{Where()}: its own account is not a real Demo account");
        if (!own.IsActiveOn(txn.Date))
            throw new InvalidOperationException($"{Where()}: {own.Role} is not active that day");
        if (txn.Splits.Count is 0 or > MaxSplits)
            throw new InvalidOperationException($"{Where()} has {txn.Splits.Count} splits; 1 to {MaxSplits} are allowed");

        if (txn.Route == DemoRoute.Bank)
        {
            if (!string.IsNullOrEmpty(txn.InvestTxnType) || txn.XferType != "xfrtp_bank")
                throw new InvalidOperationException($"{Where()} is a bank row with an investment shape");
        }
        else
        {
            if (string.IsNullOrEmpty(txn.InvestTxnType))
                throw new InvalidOperationException($"{Where()} is an investment row with no invest.txntype");
            if (own.MdType != "v")
                throw new InvalidOperationException($"{Where()} is an investment row on {own.Role}, which is not an investment account");
        }

        foreach (var split in txn.Splits)
        {
            if (split.AccountId == txn.AccountId)
                throw new InvalidOperationException($"{Where()} has a split on its own account");

            var isSec = split.SplitType == "sec";
            if (_byId.TryGetValue(split.AccountId, out var account))
            {
                if (account.IsHolding)
                {
                    if (!isSec || txn.Route != DemoRoute.Investment || account.ParentId != txn.AccountId)
                        throw new InvalidOperationException(
                            $"{Where()}: holding {account.Role} is reachable only by a sec split of its own account");
                }
                else if (!account.IsActiveOn(txn.Date))
                {
                    throw new InvalidOperationException($"{Where()}: {account.Role} is not active that day");
                }
            }
            else if (!Base.IsCategory(split.AccountId))
            {
                throw new InvalidOperationException($"{Where()}: split account {split.AccountId} is neither a Demo account nor a base category");
            }

            if (isSec)
            {
                if (account is not { IsHolding: true })
                    throw new InvalidOperationException($"{Where()}: a sec split must sit on a holding account");
            }
            else if (split.ParentAmount != -split.SplitAmount)
            {
                throw new InvalidOperationException($"{Where()}: a split's pamt and samt must be opposite");
            }
        }
    }

    private DemoAccount RequireAccount(string accountId) =>
        _byId.TryGetValue(accountId, out var account)
            ? account
            : throw new KeyNotFoundException($"no Demo account has the id {accountId}");

    private long[] Prefix(string accountId)
    {
        if (_prefix.TryGetValue(accountId, out var cached)) return cached;
        var deltas = _deltas[accountId];
        var prefix = new long[deltas.Length];
        var running = 0L;
        for (var i = 0; i < deltas.Length; i++)
        {
            running += deltas[i];
            prefix[i] = running;
        }
        _prefix[accountId] = prefix;
        return prefix;
    }

    private (List<DemoLot> Open, List<DemoDisposal> Disposals) Replay(string holdingId, DateOnly through)
    {
        var holding = RequireAccount(holdingId);
        if (!holding.IsHolding) throw new ArgumentException($"{holding.Role} is not a holding", nameof(holdingId));

        var trades = new List<(DemoTxn Txn, DemoSplit Split)>();
        foreach (var txn in _txns)
        {
            if (txn.Date > through) continue;
            foreach (var split in txn.Splits)
            {
                if (split.AccountId == holdingId && split.SplitType == "sec") trades.Add((txn, split));
            }
        }
        trades.Sort((a, b) => CompareExportOrder(a.Txn, b.Txn));

        var splits = _securitySplits
            .Where(s => s.SecurityCurrencyId == holding.CurrencyId && s.Date <= through)
            .OrderBy(s => s.Date)
            .ToList();

        var open = new List<DemoLot>();
        var disposals = new List<DemoDisposal>();
        var nextSplit = 0;
        foreach (var (txn, split) in trades)
        {
            while (nextSplit < splits.Count && splits[nextSplit].Date <= txn.Date)
            {
                ApplySplit(open, splits[nextSplit], holding);
                nextSplit++;
            }

            if (split.SplitAmount > 0)
            {
                var cost = split.ParentAmount < 0 ? -split.ParentAmount : 0L;
                open.Add(new DemoLot(txn.Date, split.SplitAmount, cost));
            }
            else if (split.SplitAmount < 0)
            {
                Sell(open, disposals, -split.SplitAmount, txn, holding);
            }
        }
        while (nextSplit < splits.Count)
        {
            ApplySplit(open, splits[nextSplit], holding);
            nextSplit++;
        }
        return (open, disposals);
    }

    private static void ApplySplit(List<DemoLot> open, DemoSecuritySplit split, DemoAccount holding)
    {
        for (var i = 0; i < open.Count; i++)
        {
            var scaled = open[i].Shares * split.NewShares;
            if (scaled % split.OldShares != 0)
                throw new InvalidOperationException($"{holding.Role}: the split on {split.Date:yyyy-MM-dd} leaves a fractional share unit");
            open[i] = open[i] with { Shares = scaled / split.OldShares };
        }
    }

    private static void Sell(List<DemoLot> open, List<DemoDisposal> disposals, long shares, DemoTxn txn, DemoAccount holding)
    {
        var remaining = shares;
        while (remaining > 0)
        {
            if (open.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{holding.Role} oversells on {txn.Date:yyyy-MM-dd} ('{txn.Description}', {txn.Pattern})");
            }

            var lot = open[0];
            if (lot.Shares <= remaining)
            {
                disposals.Add(new DemoDisposal(lot.Acquired, txn.Date, lot.Shares, lot.Cost));
                remaining -= lot.Shares;
                open.RemoveAt(0);
            }
            else
            {
                // The part sold carries its share of the cost, rounded to the cent;
                // the lot keeps the rest, so no cent is lost between them.
                var cost = decimal.ToInt64(decimal.Round(
                    (decimal)lot.Cost * remaining / lot.Shares, 0, MidpointRounding.AwayFromZero));
                disposals.Add(new DemoDisposal(lot.Acquired, txn.Date, remaining, cost));
                open[0] = lot with { Shares = lot.Shares - remaining, Cost = lot.Cost - cost };
                remaining = 0;
            }
        }
    }
}

/// <summary>A security split: holdings of <see cref="SecurityCurrencyId"/> become
/// <see cref="NewShares"/> for every <see cref="OldShares"/> at the start of
/// <see cref="Date"/>.</summary>
public sealed record DemoSecuritySplit(string SecurityCurrencyId, DateOnly Date, long NewShares, long OldShares);

/// <summary>The book failed a self-check: the generator produced a ledger the
/// export must not carry.</summary>
public sealed class DemoSelfCheckException : InvalidOperationException
{
    /// <summary>A failure listing every problem.</summary>
    public DemoSelfCheckException(IReadOnlyList<string> problems)
        : base("the Demo book failed its self-checks: " + string.Join("; ", problems ?? []))
    {
        Problems = problems ?? [];
    }

    /// <summary>One message per problem.</summary>
    public IReadOnlyList<string> Problems { get; }
}
