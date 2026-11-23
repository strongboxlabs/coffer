namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The household's dated life events, each the mark of the row that
/// carries it and the life month it falls in, in the order they happen.
/// </summary>
/// <remarks>
/// A pattern marks the row with <see cref="DemoBook.Mark"/>; a test reads
/// <see cref="All"/> to check that every event happened once, in its month, and in
/// this order.
/// </remarks>
public static class DemoEvents
{
    /// <summary>The first rent cheque at the renewed rent.</summary>
    public const string RentStep = "event:rent-step";

    /// <summary>The first video-streaming charge at the first raised price.</summary>
    public const string StreamingRise1 = "event:streaming-rise-1";

    /// <summary>The first news-subscription charge.</summary>
    public const string NewsStarts = "event:news-starts";

    /// <summary>The home inspection.</summary>
    public const string Inspection = "event:inspection";

    /// <summary>The appraisal.</summary>
    public const string Appraisal = "event:appraisal";

    /// <summary>The replacement card's first row.</summary>
    public const string RewardsFirst = "event:rewards-first";

    /// <summary>The earnest money to the house.</summary>
    public const string EarnestMoney = "event:earnest-money";

    /// <summary>Savings' part of the down payment.</summary>
    public const string DownPayment = "event:down-payment";

    /// <summary>The wire fee.</summary>
    public const string WireFee = "event:wire-fee";

    /// <summary>The original card's prorated annual-fee refund.</summary>
    public const string ClassicFeeRefund = "event:classic-fee-refund";

    /// <summary>The last rent cheque.</summary>
    public const string LastRent = "event:last-rent";

    /// <summary>The original card's final statement, paid in full.</summary>
    public const string ClassicPayoff = "event:classic-payoff";

    /// <summary>The closing.</summary>
    public const string Closing = "event:closing";

    /// <summary>The movers.</summary>
    public const string Movers = "event:movers";

    /// <summary>The lease deposit returned.</summary>
    public const string LeaseDeposit = "event:lease-deposit";

    /// <summary>The first mortgage payment.</summary>
    public const string FirstMortgagePayment = "event:first-mortgage-payment";

    /// <summary>The car loan's payoff.</summary>
    public const string CarPayoff = "event:car-payoff";

    /// <summary>The first prenatal visit.</summary>
    public const string PrenatalFirst = "event:prenatal-first";

    /// <summary>The last prenatal visit.</summary>
    public const string PrenatalLast = "event:prenatal-last";

    /// <summary>The crib and car seat.</summary>
    public const string Crib = "event:crib";

    /// <summary>The delivery bill's remainder on the payment plan.</summary>
    public const string PaymentPlanOrigin = "event:payment-plan-origin";

    /// <summary>The first payment-plan transfer.</summary>
    public const string PlanTransferFirst = "event:plan-transfer-first";

    /// <summary>The second video-streaming price rise.</summary>
    public const string StreamingRise2 = "event:streaming-rise-2";

    /// <summary>The first daycare debit.</summary>
    public const string DaycareFirst = "event:daycare-first";

    /// <summary>The insurer's reimbursement.</summary>
    public const string Reimbursement = "event:reimbursement";

    /// <summary>The last news-subscription charge.</summary>
    public const string NewsLast = "event:news-last";

    /// <summary>The sixth and last payment-plan transfer.</summary>
    public const string PlanTransferLast = "event:plan-transfer-last";

    /// <summary>The grocery row with two lines on one category.</summary>
    public const string SymmetricSplit = "event:symmetric-split";

    /// <summary>The one-split row whose other side has a memo of its own.</summary>
    public const string CounterpartMemo = "event:counterpart-memo";

    /// <summary>The house's revaluation.</summary>
    public const string Revaluation = "event:revaluation";

    /// <summary>Every event and its life month, in the order they happen.</summary>
    public static IReadOnlyList<(string Mark, int LifeMonth)> All { get; } =
    [
        (RentStep, HouseholdTerms.LeaseRenewalMonth),
        (StreamingRise1, BillsPattern.StreamingRise1Month),
        (NewsStarts, BillsPattern.NewsStartMonth),
        (Inspection, HousingPattern.InspectionMonth),
        (Appraisal, HousingPattern.InspectionMonth),
        (RewardsFirst, HouseholdTerms.RewardsCardOpens),
        (EarnestMoney, HouseholdTerms.HouseOpens),
        (DownPayment, HouseholdTerms.HouseOpens),
        (WireFee, HouseholdTerms.HouseOpens),
        (ClassicFeeRefund, HouseholdTerms.ClassicCardCloses),
        (ClassicPayoff, HouseholdTerms.ClassicCardCloses),
        (Closing, HouseholdTerms.ClosingMonth),
        (Movers, HouseholdTerms.ClosingMonth),
        (LeaseDeposit, HouseholdTerms.LeaseDepositReturnMonth),
        (FirstMortgagePayment, HouseholdTerms.FirstMortgagePayment),
        (CarPayoff, HouseholdTerms.CarLoanCloses),
        (PrenatalFirst, MedicalPattern.PrenatalFrom),
        (PrenatalLast, MedicalPattern.PrenatalThrough),
        (Crib, HouseholdTerms.PaymentPlanOpens),
        (PaymentPlanOrigin, HouseholdTerms.PaymentPlanOpens),
        (PlanTransferFirst, HouseholdTerms.PaymentPlanOpens + 1),
        (StreamingRise2, BillsPattern.StreamingRise2Month),
        (DaycareFirst, BillsPattern.DaycareFrom),
        (Reimbursement, MedicalPattern.ReimbursementMonth),
        (NewsLast, BillsPattern.NewsLastMonth),
        (PlanTransferLast, HouseholdTerms.PaymentPlanCloses),
        (SymmetricSplit, RegisterExamplesPattern.SymmetricSplitMonth),
        (CounterpartMemo, RegisterExamplesPattern.CounterpartMemoMonth),
        (Revaluation, HouseholdTerms.RevaluationMonth),
    ];
}
