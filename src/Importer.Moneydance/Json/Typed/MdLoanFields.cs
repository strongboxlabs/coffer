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
            FirstPaymentDate:    ParseYmd(item.GetLong("date_created")));
    }

    // MD's "date_created" is a packed YYYYMMDD integer (e.g. 20130617).
    private static DateOnly? ParseYmd(long? ymd)
    {
        if (ymd is not { } v) return null;
        var y = (int)(v / 10000);
        var m = (int)((v / 100) % 100);
        var d = (int)(v % 100);
        if (y is < 1900 or > 9999 || m is < 1 or > 12 || d is < 1 or > 31) return null;
        try { return new DateOnly(y, m, d); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
