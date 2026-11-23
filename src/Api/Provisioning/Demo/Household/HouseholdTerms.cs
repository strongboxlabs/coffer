namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The household's life events (as life months) and its synthetic build constants.
/// </summary>
/// <remarks>
/// Every figure here is an invented build parameter, kept in code only; the repo's
/// prose describes shapes, never these values. Amounts are cents.
/// </remarks>
public static class HouseholdTerms
{
    // --- life months -----------------------------------------------------------

    /// <summary>Earner A's new plan opens.</summary>
    public const int NewPlanAOpens = 15;

    /// <summary>The rollover: the old plan sells out and closes, the IRA opens.</summary>
    public const int RolloverMonth = 16;

    /// <summary>The replacement card opens.</summary>
    public const int RewardsCardOpens = 26;

    /// <summary>The house is bought (earnest money) and the original card closes.</summary>
    public const int HouseOpens = 27;

    /// <summary>The original card's last month.</summary>
    public const int ClassicCardCloses = 27;

    /// <summary>Closing: the escrow account and the mortgage's closing legs.</summary>
    public const int ClosingMonth = 28;

    /// <summary>The first mortgage payment, and the car loan's payoff.</summary>
    public const int FirstMortgagePayment = 30;

    /// <summary>The car loan's last month.</summary>
    public const int CarLoanCloses = 30;

    /// <summary>The family HSA opens.</summary>
    public const int HsaOpens = 36;

    /// <summary>The hospital payment plan opens at the birth.</summary>
    public const int PaymentPlanOpens = 40;

    /// <summary>The hospital payment plan's last month.</summary>
    public const int PaymentPlanCloses = 46;

    // --- house and mortgage ----------------------------------------------------

    /// <summary>The purchase price.</summary>
    public const long HousePrice = 465_000_00L;

    /// <summary>The down payment as a percent of the price.</summary>
    public const decimal DownPaymentPercent = 20m;

    /// <summary>The mortgage's annual rate, as a percent.</summary>
    public const decimal MortgageRatePercent = 6.25m;

    /// <summary>The mortgage's payment count.</summary>
    public const int MortgagePayments = 360;

    /// <summary>The yearly property tax as a percent of the price.</summary>
    public const decimal PropertyTaxPercent = 1.1m;

    /// <summary>The yearly home-insurance premium.</summary>
    public const long HomeInsuranceAnnual = 1_680_00L;

    /// <summary>The day of the month a mortgage payment is due.</summary>
    public const int MortgagePaymentDay = 1;

    /// <summary>The mortgage principal: the price less the down payment.</summary>
    public static long MortgagePrincipal =>
        DemoAmortization.RoundCents(HousePrice * (100m - DownPaymentPercent) / 100m);

    /// <summary>The yearly property tax.</summary>
    public static long PropertyTaxAnnual => DemoAmortization.RoundCents(HousePrice * PropertyTaxPercent / 100m);

    /// <summary>The monthly escrow: a twelfth of the yearly tax and premium, to the cent.</summary>
    public static long MonthlyEscrow => DemoAmortization.RoundCents((PropertyTaxAnnual + HomeInsuranceAnnual) / 12m);

    /// <summary>The mortgage's schedule.</summary>
    public static DemoAmortization Mortgage { get; } =
        new(MortgagePrincipal, MortgageRatePercent, MortgagePayments, 12);

    /// <summary>The earnest money moved to the house account before closing; it is
    /// part of the price, so the closing's house line carries the rest.</summary>
    public const long EarnestMoney = 5_000_00L;

    /// <summary>The settlement costs capitalized into the house's cost.</summary>
    public const long SettlementCosts = 6_500_00L;

    /// <summary>The revaluation's markup as a percent of the price.</summary>
    public const decimal RevaluationPercent = 6m;

    /// <summary>The life month of the revaluation.</summary>
    public const int RevaluationMonth = 50;

    /// <summary>Months of escrow the initial deposit carries beyond what keeps the
    /// escrow account from going negative.</summary>
    public const int EscrowCushionMonths = 2;

    /// <summary>The nominal day of the closing month the purchase closes.</summary>
    public const int ClosingDay = 14;

    /// <summary>The nominal day of the house month the earnest money is wired.</summary>
    public const int EarnestMoneyDay = 3;

    /// <summary>The wire fee on the down payment.</summary>
    public const long WireFee = 30_00L;

    // --- rent ------------------------------------------------------------------

    /// <summary>The monthly rent before the lease renewal.</summary>
    public const long Rent = 1_950_00L;

    /// <summary>The rent's multiplier at the lease renewal.</summary>
    public const decimal RentStep = 1.04m;

    /// <summary>The life month of the lease renewal.</summary>
    public const int LeaseRenewalMonth = 12;

    /// <summary>The first rent cheque's number.</summary>
    public const int FirstRentCheque = 1201;

    /// <summary>The life month the lease deposit comes back.</summary>
    public const int LeaseDepositReturnMonth = 29;

    /// <summary>The lease deposit: the first month's rent.</summary>
    public const long LeaseDeposit = Rent;

    // --- car loan --------------------------------------------------------------

    /// <summary>The car loan's principal.</summary>
    public const long CarLoanPrincipal = 32_000_00L;

    /// <summary>The car loan's annual rate, as a percent.</summary>
    public const decimal CarLoanRatePercent = 4.9m;

    /// <summary>The car loan's payment count.</summary>
    public const int CarLoanPayments = 60;

    /// <summary>Car loan payments made before the window opens.</summary>
    public const int CarLoanPaymentsBeforeWindow = 24;

    /// <summary>The day of the month a car loan payment is due.</summary>
    public const int CarLoanPaymentDay = 8;

    /// <summary>The car loan's schedule.</summary>
    public static DemoAmortization CarLoan { get; } =
        new(CarLoanPrincipal, CarLoanRatePercent, CarLoanPayments, 12);

    // --- the birth (L40) -------------------------------------------------------

    /// <summary>The delivery bill in full: the HSA pays its whole cash balance on the
    /// bill date in whole dollars, the payment plan carries the rest.</summary>
    public const long DeliveryBillTotal = 9_800_00L;

    /// <summary>The day of the birth month the delivery bill is paid.</summary>
    public const int DeliveryBillDay = 24;

    // --- opening balances (sbal) -------------------------------------------------
    // Sized so the book's self-checks pass once every pattern runs; a pattern stage
    // may retune them.

    /// <summary>Joint Checking at the window start.</summary>
    public const long CheckingOpening = 8_500_00L;

    /// <summary>Savings at the window start.</summary>
    public const long SavingsOpening = 45_000_00L;

    /// <summary>The cash wallet at the window start.</summary>
    public const long CashOpening = 180_00L;

    /// <summary>The dormant certificate of deposit, which never moves.</summary>
    public const long CertificateOfDepositOpening = 10_000_00L;

    /// <summary>The brokerage's cash, which buys its opening positions on day 1.</summary>
    public const long BrokerageOpening = 60_000_00L;

    /// <summary>Earner A's old plan's cash, which buys its opening position on day 1.</summary>
    public const long OldPlanAOpening = 38_000_00L;

    /// <summary>Earner B's plan's cash, which buys its opening positions on day 1.</summary>
    public const long PlanBOpening = 31_000_00L;

    /// <summary>The original card at the window start: the statement before the
    /// window is taken as paid, so it starts at zero.</summary>
    public const long ClassicCardOpening = 0L;
}
