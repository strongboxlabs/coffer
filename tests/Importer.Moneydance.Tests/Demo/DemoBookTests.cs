using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The simulated ledger the patterns post into: its posting rules throw at once,
/// its balances follow Moneydance's pamt/samt encoding, its lots are FIFO and refuse
/// an oversell, and its self-checks report what an export must not carry.
/// </summary>
public sealed class DemoBookTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    private sealed class Stub(string name, int rank = 10) : IDemoPattern
    {
        public string Name => name;
        public int Rank => rank;
        public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book) { }
    }

    private static DemoBook NewBook()
    {
        var calendar = new DemoCalendar(AsOf);
        var book = new DemoBook(calendar, new DemoIds(DemoSpec.DefaultSeed), DemoBase.Instance);
        HouseholdAccounts.AddTo(book);
        return book;
    }

    private static string Groceries => DemoBase.Instance.Category(MdCategoryType.Expense, "Personal", "Groceries");

    [Fact]
    public void A_bank_row_moves_its_own_account_by_its_pamts_and_a_transfer_both_sides()
    {
        var book = NewBook();
        var checking = book.Account(DemoAccountRoles.JointChecking);
        var savings = book.Account(DemoAccountRoles.Savings);
        var day = new DateOnly(2025, 3, 10);
        var pattern = new Stub("test");

        book.Post(pattern, new DemoTxn
        {
            AccountId = checking.Id, Date = day, Description = "Market",
            Splits = [DemoSplit.Bank(Groceries, 45_67)],
        });
        book.Post(pattern, new DemoTxn
        {
            AccountId = checking.Id, Date = day, Description = "To savings",
            Splits = [DemoSplit.Bank(savings.Id, 500_00)],
        });

        Assert.Equal(checking.OpeningBalance - 45_67 - 500_00, book.BalanceAtEndOf(checking.Id, day));
        Assert.Equal(checking.OpeningBalance, book.BalanceAtStartOf(checking.Id, day));
        Assert.Equal(savings.OpeningBalance + 500_00, book.BalanceAtEndOf(savings.Id, AsOf));
        Assert.Equal(checking.OpeningBalance, book.BalanceAtEndOf(checking.Id, book.Calendar.WindowStart.AddDays(-1)));
    }

    [Fact]
    public void Posting_assigns_ids_ordinals_and_the_export_order()
    {
        var book = NewBook();
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var early = new Stub("early", rank: 1);
        var late = new Stub("late", rank: 2);
        var day = new DateOnly(2025, 3, 10);

        var b = book.Post(late, Row(checking, day));
        var a = book.Post(early, Row(checking, day));
        var c = book.Post(early, Row(checking, day.AddDays(-1)));

        Assert.Equal(0, b.Ordinal);
        Assert.Equal(0, a.Ordinal);
        Assert.Equal(1, c.Ordinal);
        Assert.Equal(book.Ids.Id("txn", "early", 1), c.Id);
        Assert.Equal(book.Ids.Id("txn", "early", 1, 0), c.Splits[0].Id);
        Assert.Equal(new[] { c, a, b }, book.TransactionsInExportOrder());
    }

    [Fact]
    public void A_row_outside_the_window_or_an_account_window_is_refused()
    {
        var book = NewBook();
        var pattern = new Stub("test");
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var rewards = book.Account(DemoAccountRoles.RewardsCard);

        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, Row(checking, AsOf.AddDays(1))));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, Row(checking, book.Calendar.WindowStart.AddDays(-1))));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, Row(rewards.Id, rewards.ActiveFrom.AddDays(-1))));

        var classic = book.Account(DemoAccountRoles.ClassicCard);
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, Row(classic.Id, classic.ActiveThrough!.Value.AddDays(1))));
        book.Post(pattern, Row(classic.Id, classic.ActiveThrough!.Value));
    }

    [Fact]
    public void The_dormant_account_and_unknown_counterparts_are_refused()
    {
        var book = NewBook();
        var pattern = new Stub("test");
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var cd = book.Account(DemoAccountRoles.CertificateOfDeposit).Id;
        var day = new DateOnly(2025, 3, 10);

        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, Row(cd, day)));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, new DemoTxn
        {
            AccountId = checking, Date = day, Description = "x", Splits = [DemoSplit.Bank(cd, 1_00)],
        }));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, new DemoTxn
        {
            AccountId = checking, Date = day, Description = "x", Splits = [DemoSplit.Bank("not-an-account", 1_00)],
        }));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, new DemoTxn
        {
            AccountId = checking, Date = day, Description = "x", Splits = [],
        }));
        Assert.Throws<InvalidOperationException>(() => book.Post(pattern, new DemoTxn
        {
            AccountId = checking, Date = day, Description = "x",
            Splits = [new DemoSplit { AccountId = Groceries, ParentAmount = -5, SplitAmount = 4 }],
        }));
    }

    [Fact]
    public void Lots_are_FIFO_split_aware_and_refuse_an_oversell()
    {
        var book = NewBook();
        var brokerage = book.Account(DemoAccountRoles.Brokerage);
        var holding = book.AddAccount(new DemoAccount
        {
            Role = "test-holding", Id = book.Ids.Id("acct", "test-holding", 0), Name = "Test Security",
            MdType = "s", DateCreated = book.Calendar.WindowStart, ActiveFrom = book.Calendar.WindowStart,
            ParentId = brokerage.Id, CurrencyId = "test-security",
        });
        var pattern = new Stub("trades");
        var fee = DemoBase.Instance.Category(MdCategoryType.Expense, "Investment", "Trading Commission");

        DemoTxn Trade(DateOnly day, long shares, long cash) => new()
        {
            AccountId = brokerage.Id, Date = day, Description = "Trade", Route = DemoRoute.Investment,
            InvestTxnType = shares > 0 ? "buy" : "sell", XferType = "xfrtp_buysell",
            Splits =
            [
                new DemoSplit { AccountId = holding.Id, SplitType = "sec", SplitAmount = shares, ParentAmount = cash },
                new DemoSplit { AccountId = fee, SplitType = "fee", SplitAmount = 5_00, ParentAmount = -5_00 },
            ],
        };

        book.Post(pattern, Trade(new DateOnly(2022, 1, 3), 10_0000, -1_000_00));
        book.Post(pattern, Trade(new DateOnly(2024, 1, 3), 10_0000, -2_000_00));
        book.ApplySecuritySplit("test-security", new DateOnly(2024, 6, 10), 10, 1);
        book.Post(pattern, Trade(new DateOnly(2024, 7, 1), -150_0000, 4_000_00));

        var disposals = book.Disposals(holding.Id);
        Assert.Equal(2, disposals.Count);
        Assert.True(disposals[0].LongTerm);
        Assert.Equal(100_0000, disposals[0].Shares);
        Assert.Equal(1_000_00, disposals[0].Cost);
        Assert.False(disposals[1].LongTerm);
        Assert.Equal(50_0000, disposals[1].Shares);
        Assert.Equal(1_000_00, disposals[1].Cost);

        Assert.Equal(50_0000, book.Shares(holding.Id, AsOf));
        Assert.Equal(20_0000, book.Shares(holding.Id, new DateOnly(2024, 6, 9)));
        Assert.Equal(200_0000, book.Shares(holding.Id, new DateOnly(2024, 6, 10)));
        Assert.Equal(
            brokerage.OpeningBalance - 1_005_00 - 2_005_00 + 3_995_00,
            book.BalanceAtEndOf(brokerage.Id, AsOf));

        book.Post(pattern, Trade(new DateOnly(2025, 1, 6), -60_0000, 1_000_00));
        var problems = book.FindProblems();
        Assert.Contains(problems, p => p.Contains("oversells", StringComparison.Ordinal));
        Assert.Throws<DemoSelfCheckException>(book.Verify);
    }

    [Fact]
    public void A_non_negative_account_below_zero_and_a_failed_registered_check_are_reported()
    {
        var book = NewBook();
        var cash = book.Account(DemoAccountRoles.CashWallet);
        var day = new DateOnly(2025, 3, 10);
        book.Post(new Stub("wallet"), new DemoTxn
        {
            AccountId = cash.Id, Date = day, Description = "Too much",
            Splits = [DemoSplit.Bank(Groceries, cash.OpeningBalance + 1)],
        });
        book.AddCheck("always", _ => ["broken"]);

        var problems = book.FindProblems();
        Assert.Contains($"{DemoAccountRoles.CashWallet} ends 2025-03-10 below zero", problems);
        Assert.Contains("always: broken", problems);
        Assert.Equal((-1L, day), book.LowestDayEnd(cash.Id, book.Calendar.WindowStart, AsOf));
    }

    [Fact]
    public void A_fresh_book_passes_its_self_checks()
    {
        NewBook().Verify();
    }

    [Fact]
    public void Card_statements_close_on_their_day_inside_the_account_window()
    {
        var book = NewBook();
        var rewards = book.Account(DemoAccountRoles.RewardsCard);
        var classic = book.Account(DemoAccountRoles.ClassicCard);

        var rewardsPeriods = book.StatementPeriods(rewards.Id);
        Assert.All(rewardsPeriods, p => Assert.Equal(4, p.ClosesOn.Day));
        Assert.True(rewardsPeriods[0].ClosesOn >= rewards.ActiveFrom);
        Assert.Equal(new DateOnly(2026, 11, 4), rewardsPeriods[^1].ClosesOn);
        Assert.Equal(new DateOnly(2026, 10, 4), rewardsPeriods[^1].OpensAfter);

        var classicPeriods = book.StatementPeriods(classic.Id);
        Assert.Equal(new DateOnly(2021, 11, 22), classicPeriods[0].ClosesOn);
        Assert.Equal(new DateOnly(2021, 10, 22), classicPeriods[0].OpensAfter);
        Assert.True(classicPeriods[^1].ClosesOn <= classic.ActiveThrough);

        var checking = book.StatementPeriods(book.Account(DemoAccountRoles.JointChecking).Id);
        Assert.Equal(new DateOnly(2026, 10, 31), checking[^1].ClosesOn);
        Assert.True(checking[^1].Contains(new DateOnly(2026, 10, 1)));
        Assert.False(checking[^1].Contains(new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void Loan_amortization_leaves_only_a_rounding_residual_after_the_last_level_payment()
    {
        // The level payment is rounded to the cent, so the schedule ends within a
        // cent per payment of zero; a payoff row takes whatever remains.
        foreach (var loan in new[] { HouseholdTerms.CarLoan, HouseholdTerms.Mortgage })
        {
            Assert.Equal(loan.Principal, loan.BalanceAfter(0));
            Assert.InRange(loan.BalanceAfter(loan.Payments), 0, loan.Payments);
            Assert.True(loan.BalanceAfter(loan.Payments - 1) > loan.Payments);
            Assert.True(loan.BalanceAfter(1) < loan.Principal);
        }
    }

    private static DemoTxn Row(string accountId, DateOnly date) => new()
    {
        AccountId = accountId, Date = date, Description = "Row",
        Splits = [DemoSplit.Bank(Groceries, 1_00)],
    };
}
