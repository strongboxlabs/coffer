using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Pure-logic tests for <see cref="LoanMapper"/> (ADR-0050): MD loan fields →
/// <see cref="Coffer.Importer.Moneydance.Db.LoanTermsRow"/>. Minor-unit
/// conversion, computed-vs-fixed payment, and skipping incomplete data.
/// </summary>
public sealed class LoanMapperTests
{
    private static MdLoanFields FullLoan() => new(
        AnnualRatePercent: 4.00m,
        PaymentCount: 360,
        PaymentsPerYear: 12,
        InitPrincipalMinor: 50000000,        // $500,000.00 in minor units
        Points: 0m,
        EscrowPayment: 500.00m,
        InterestAccountMdId: "md-interest",
        EscrowAccountMdId: "md-escrow",
        PaymentIsComputed: true,
        MonthlyPayment: 0m,
        FirstPaymentDate: new DateOnly(2020, 1, 1));

    [Fact]
    public void Maps_full_loan_converting_minor_units_and_resolving_accounts()
    {
        var account = Guid.NewGuid();
        var ledger = Guid.NewGuid();
        var interest = Guid.NewGuid();
        var escrow = Guid.NewGuid();

        var row = LoanMapper.Map(account, ledger, FullLoan(), interest, escrow);

        Assert.NotNull(row);
        Assert.Equal(account, row!.AccountId);
        Assert.Equal(ledger, row.LedgerId);
        Assert.Equal(500000.00m, row.OriginalPrincipal);   // minor units → dollars
        Assert.Equal(4.00m, row.AnnualInterestRate);
        Assert.Equal(360, row.PaymentCount);
        Assert.Equal(12, row.PaymentsPerYear);
        Assert.Equal(500.00m, row.EscrowAmount);
        Assert.Equal(interest, row.InterestAccountId);
        Assert.Equal(escrow, row.EscrowAccountId);
        Assert.True(row.PaymentIsComputed);
        Assert.Null(row.FixedPayment);                     // computed → no fixed payment
        Assert.Equal(new DateOnly(2020, 1, 1), row.FirstPaymentDate);
    }

    [Fact]
    public void Specified_payment_carries_the_fixed_amount()
    {
        var row = LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(),
            FullLoan() with { PaymentIsComputed = false, MonthlyPayment = 2500.00m },
            interestAccountId: null, escrowAccountId: null);

        Assert.NotNull(row);
        Assert.False(row!.PaymentIsComputed);
        Assert.Equal(2500.00m, row.FixedPayment);
    }

    [Fact]
    public void Returns_null_when_principal_is_missing()
    {
        var row = LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(),
            FullLoan() with { InitPrincipalMinor = null },
            null, null);

        Assert.Null(row);   // no usable amortization → don't seed
    }
}
