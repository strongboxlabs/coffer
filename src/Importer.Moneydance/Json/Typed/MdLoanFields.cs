namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over the loan-specific fields Moneydance stores on a loan
/// account (<c>type="o"</c>): the amortization parameters Coffer seeds into
/// <c>loan_terms</c> (ADR-0050). Every field is nullable — MD writes them only
/// for loan accounts, and even there some may be absent. Account references are
/// MD uuids (resolved to Coffer ids by the import pipeline).
/// </summary>
public sealed record MdLoanFields(
    decimal? AnnualRatePercent,
    int? PaymentCount,
    int? PaymentsPerYear,
    long? InitPrincipalMinor,
    decimal? Points,
    decimal? EscrowPayment,
    string? InterestAccountMdId,
    string? EscrowAccountMdId,
    bool? PaymentIsComputed,
    decimal? MonthlyPayment,
    DateOnly? FirstPaymentDate)
{
    /// <summary>
    /// Extract the loan fields from a Moneydance <c>acct</c> item. Caller is
    /// responsible for only invoking this on a loan account (<c>type="o"</c>).
    /// </summary>
    public static MdLoanFields From(Json.MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new MdLoanFields(
            AnnualRatePercent:   item.GetDecimal("int_rate"),
            PaymentCount:        item.GetInt("num_payments"),
            PaymentsPerYear:     item.GetInt("pmts_per_year"),
            InitPrincipalMinor:  item.GetLong("init_principal"),
            Points:              item.GetDecimal("points"),
            EscrowPayment:       item.GetDecimal("escrow_payment"),
            InterestAccountMdId: item.GetString("interest_account_id"),
            EscrowAccountMdId:   item.GetString("escrow_account_id"),
            // MD's "calc_pmt" flag: 1 = amortize (computed), 0 = a fixed
            // specified payment in "monthly_pmt".
            PaymentIsComputed:   item.GetBool("calc_pmt"),
            MonthlyPayment:      item.GetDecimal("monthly_pmt"),
            // Same two-source read as accounts.opened_on, and for the same
            // reason: MD writes the account's creation stamp as either a
            // yyyyMMdd int or epoch millis, inconsistently. On a real export only
            // 2 of 6 loans carry `date_created`, so reading it alone left the
            // other 4 with no first-payment date at all.
            FirstPaymentDate:    item.GetMdDate("date_created")
                                 ?? item.GetMdEpochDate("creation_date"));
    }
}
