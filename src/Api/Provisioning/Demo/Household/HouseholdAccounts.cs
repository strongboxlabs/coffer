namespace Coffer.Api.Provisioning.Demo;

/// <summary>The stable roles the generator, the manifest and the seeder name the
/// Demo's accounts by.</summary>
public static class DemoAccountRoles
{
    /// <summary>The joint checking account.</summary>
    public const string JointChecking = "joint-checking";

    /// <summary>The savings account.</summary>
    public const string Savings = "savings";

    /// <summary>The cash wallet.</summary>
    public const string CashWallet = "cash-wallet";

    /// <summary>The dormant certificate of deposit.</summary>
    public const string CertificateOfDeposit = "certificate-of-deposit";

    /// <summary>The taxable brokerage.</summary>
    public const string Brokerage = "brokerage";

    /// <summary>Earner A's old 401(k), closed at the rollover.</summary>
    public const string OldPlanA = "plan-a-old";

    /// <summary>Earner B's 401(k).</summary>
    public const string PlanB = "plan-b";

    /// <summary>Earner A's new 401(k).</summary>
    public const string NewPlanA = "plan-a-new";

    /// <summary>The IRA the old plan rolls into.</summary>
    public const string RolloverIra = "rollover-ira";

    /// <summary>The family HSA.</summary>
    public const string FamilyHsa = "family-hsa";

    /// <summary>The house.</summary>
    public const string House = "house";

    /// <summary>The mortgage's escrow account.</summary>
    public const string MortgageEscrow = "mortgage-escrow";

    /// <summary>The mortgage, a Moneydance loan.</summary>
    public const string Mortgage = "mortgage";

    /// <summary>The car loan, paid off early.</summary>
    public const string CarLoan = "car-loan";

    /// <summary>The hospital payment plan, a plain liability.</summary>
    public const string PaymentPlan = "payment-plan";

    /// <summary>The original credit card, closed and replaced.</summary>
    public const string ClassicCard = "classic-card";

    /// <summary>The replacement credit card.</summary>
    public const string RewardsCard = "rewards-card";
}

/// <summary>
/// The household's real accounts: names, Moneydance types, open and close months,
/// opening balances, institutions and folders.
/// </summary>
/// <remarks>
/// <para>Every account open at the window start is created that day with its
/// modelled balance then. Two carve-outs: the car loan's <c>date_created</c> is its
/// first payment, two years before the window, with its balance after the payments
/// made by then; and the mortgage's <c>date_created</c> is its first payment date,
/// which the importer reads as the loan's first payment, while its closing legs
/// post two months earlier.</para>
///
/// <para>Tax status is guessed from the name (AccountMapper.InferTaxStatus): the
/// retirement and HSA names carry a marker, the taxable and bank-like names none.
/// Bank and card accounts carry an invented institution and a masked last-four
/// number, never a routing number.</para>
///
/// <para>Provenance fields (an online-banking service, a file-import account
/// number) are added by <see cref="DemoProvenance.ConfigureAccounts"/>, not here.</para>
/// </remarks>
public static class HouseholdAccounts
{
    /// <summary>The folder of everyday cash accounts.</summary>
    public const string EverydayFolder = "Everyday";

    /// <summary>The folder of retirement and health-savings accounts.</summary>
    public const string RetirementFolder = "Retirement";

    internal const string HomeBank = "Larchmere Community Bank";
    internal const string ClassicIssuer = "Brindlewood Card Services";
    internal const string RewardsIssuer = "Harrowgate Card Services";

    // The kind and pattern every account id is hashed under; an account's ordinal is
    // always 0 and its role is the pattern coordinate, so adding an account moves no
    // other's id.
    private const string IdKind = "acct";

    /// <summary>Add every real account to the book, in export order.</summary>
    public static void AddTo(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var calendar = book.Calendar;
        var start = calendar.WindowStart;

        DemoAccount Account(
            string role, string name, string type, long opening,
            int? opensMonth = null, int? closesMonth = null,
            string? bank = null, string? number = null, DemoStatementCycle? statement = null,
            bool nonNegative = false, bool dormant = false, DateOnly? dateCreated = null)
        {
            var activeFrom = opensMonth is { } open ? calendar.MonthStart(open) : start;
            return new DemoAccount
            {
                Role = role,
                Id = book.Ids.Id(IdKind, role, 0),
                Name = name,
                MdType = type,
                DateCreated = dateCreated ?? activeFrom,
                ActiveFrom = activeFrom,
                ActiveThrough = closesMonth is { } close ? calendar.MonthEnd(close) : null,
                IsInactive = closesMonth is not null,
                OpeningBalance = opening,
                BankName = bank,
                BankAccountNumber = number,
                Statement = statement,
                MustStayNonNegative = nonNegative,
                Dormant = dormant,
            };
        }

        book.AddAccount(Account(DemoAccountRoles.JointChecking, "Joint Checking", "b", HouseholdTerms.CheckingOpening,
            bank: HomeBank, number: "****4821", statement: DemoStatementCycle.MonthEnd, nonNegative: true));
        book.AddAccount(Account(DemoAccountRoles.Savings, "Savings", "b", HouseholdTerms.SavingsOpening,
            bank: HomeBank, number: "****4836", statement: DemoStatementCycle.MonthEnd, nonNegative: true));
        book.AddAccount(Account(DemoAccountRoles.CashWallet, "Cash Wallet", "b", HouseholdTerms.CashOpening,
            nonNegative: true));
        book.AddAccount(Account(DemoAccountRoles.CertificateOfDeposit, "Certificate of Deposit", "b",
            HouseholdTerms.CertificateOfDepositOpening, bank: HomeBank, number: "****4907", dormant: true));

        book.AddAccount(Account(DemoAccountRoles.Brokerage, "Brokerage", "v", HouseholdTerms.BrokerageOpening));
        book.AddAccount(Account(DemoAccountRoles.OldPlanA, "Thornbury 401(k)", "v", HouseholdTerms.OldPlanAOpening,
            closesMonth: HouseholdTerms.RolloverMonth));
        book.AddAccount(Account(DemoAccountRoles.PlanB, "Ashgrove 401(k)", "v", HouseholdTerms.PlanBOpening));
        book.AddAccount(Account(DemoAccountRoles.NewPlanA, "Calloway 401(k)", "v", 0L,
            opensMonth: HouseholdTerms.NewPlanAOpens));
        book.AddAccount(Account(DemoAccountRoles.RolloverIra, "Rollover IRA", "v", 0L,
            opensMonth: HouseholdTerms.RolloverMonth));
        book.AddAccount(Account(DemoAccountRoles.FamilyHsa, "Family HSA", "v", 0L,
            opensMonth: HouseholdTerms.HsaOpens, nonNegative: true));

        book.AddAccount(Account(DemoAccountRoles.House, "House", "a", 0L, opensMonth: HouseholdTerms.HouseOpens));
        var escrow = book.AddAccount(Account(DemoAccountRoles.MortgageEscrow, "Mortgage Escrow", "a", 0L,
            opensMonth: HouseholdTerms.ClosingMonth, nonNegative: true));

        var mortgage = book.AddAccount(Account(DemoAccountRoles.Mortgage, "Mortgage", "o", 0L,
            opensMonth: HouseholdTerms.ClosingMonth,
            dateCreated: calendar.DayInMonth(HouseholdTerms.FirstMortgagePayment, HouseholdTerms.MortgagePaymentDay)));
        AddLoanTerms(mortgage, HouseholdTerms.Mortgage,
            book.Base.Category(MdCategoryType.Expense, "Bills", "Mortgage Interest"),
            HouseholdTerms.MonthlyEscrow, escrow.Id);

        var carLoan = book.AddAccount(Account(DemoAccountRoles.CarLoan, "Car Loan", "o",
            -HouseholdTerms.CarLoan.BalanceAfter(HouseholdTerms.CarLoanPaymentsBeforeWindow),
            closesMonth: HouseholdTerms.CarLoanCloses,
            dateCreated: calendar.DayInMonth(-HouseholdTerms.CarLoanPaymentsBeforeWindow, HouseholdTerms.CarLoanPaymentDay)));
        AddLoanTerms(carLoan, HouseholdTerms.CarLoan,
            book.Base.Category(MdCategoryType.Expense, "Loan", "Interest"),
            monthlyEscrow: null, escrowAccountId: null);

        book.AddAccount(Account(DemoAccountRoles.PaymentPlan, "Hospital Payment Plan", "l", 0L,
            opensMonth: HouseholdTerms.PaymentPlanOpens, closesMonth: HouseholdTerms.PaymentPlanCloses));

        book.AddAccount(Account(DemoAccountRoles.ClassicCard, "Classic Card", "c", HouseholdTerms.ClassicCardOpening,
            closesMonth: HouseholdTerms.ClassicCardCloses,
            bank: ClassicIssuer, number: "****3307", statement: DemoStatementCycle.OnDay(22)));
        book.AddAccount(Account(DemoAccountRoles.RewardsCard, "Rewards Card", "c", 0L,
            opensMonth: HouseholdTerms.RewardsCardOpens,
            bank: RewardsIssuer, number: "****6612", statement: DemoStatementCycle.OnDay(4)));
    }

    /// <summary>The account folders and their members' MD ids.</summary>
    public static IReadOnlyList<DemoFolder> Folders(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        string Id(string role) => book.Account(role).Id;
        return
        [
            new DemoFolder(EverydayFolder, "bank",
                [Id(DemoAccountRoles.JointChecking), Id(DemoAccountRoles.CashWallet)]),
            new DemoFolder(RetirementFolder, "investment",
                [Id(DemoAccountRoles.NewPlanA), Id(DemoAccountRoles.PlanB),
                 Id(DemoAccountRoles.RolloverIra), Id(DemoAccountRoles.FamilyHsa)]),
        ];
    }

    /// <summary>
    /// Moneydance's loan fields (MdLoanFields): the rate as a percent, the counts,
    /// the principal in minor units, a computed payment (<c>calc_pmt</c> 1, so the
    /// importer stores no fixed payment) beside the level payment it implies, no
    /// points, the interest category, and the escrow terms when there are any.
    /// </summary>
    private static void AddLoanTerms(
        DemoAccount loan, DemoAmortization terms, string interestCategoryId, long? monthlyEscrow, string? escrowAccountId)
    {
        var fields = loan.ExtraFields;
        fields.Add(new("int_rate", DemoMoney.Invariant(terms.AnnualRatePercent)));
        fields.Add(new("num_payments", DemoMoney.Invariant(terms.Payments)));
        fields.Add(new("pmts_per_year", DemoMoney.Invariant(terms.PaymentsPerYear)));
        fields.Add(new("init_principal", DemoMoney.Invariant(terms.Principal)));
        fields.Add(new("points", "0"));
        fields.Add(new("calc_pmt", "1"));
        fields.Add(new("monthly_pmt", DemoMoney.Major(terms.Payment)));
        fields.Add(new("interest_account_id", interestCategoryId));
        fields.Add(new("escrow_payment", monthlyEscrow is { } escrow ? DemoMoney.Major(escrow) : "0"));
        if (escrowAccountId is not null) fields.Add(new("escrow_account_id", escrowAccountId));
    }
}
