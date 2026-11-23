using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Pure-logic tests for <see cref="LoanMapper"/> (ADR-0050): MD loan fields →
/// <see cref="Coffer.Importer.Moneydance.Db.LoanTermsRow"/>. Minor-unit
/// conversion, computed-vs-fixed payment, skipping incomplete data, and the
/// escrow and fixed payment checked at the loan currency's places (ADR-0114 D15).
/// </summary>
public sealed class LoanMapperTests
{
    private static readonly AccountCurrency Dollars = new("USD", 2);

    private static MdLoanFields FullLoan() => new(
        AnnualRatePercent: 4.00m,
        PaymentCount: 360,
        PaymentsPerYear: 12,
        InitPrincipalMinor: 50000000,        // the principal in minor units, at the currency's dec
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

        var row = LoanMapper.Map(account, ledger, FullLoan(), interest, escrow, Dollars);

        Assert.NotNull(row);
        Assert.Equal(account, row!.AccountId);
        Assert.Equal(ledger, row.LedgerId);
        Assert.Equal(500000.00m, row.OriginalPrincipal);   // minor units → major units
        Assert.Equal(4.00m, row.AnnualInterestRate);
        Assert.Equal(360, row.PaymentCount);
        Assert.Equal(12, row.PaymentsPerYear);
        Assert.Equal(500.00m, row.EscrowAmount);
        Assert.Equal(interest, row.InterestAccountId);
        Assert.Equal(escrow, row.EscrowAccountId);
        Assert.True(row.PaymentIsComputed);
        Assert.Null(row.FixedPayment);                     // computed → no fixed payment
        Assert.Equal(new DateOnly(2020, 1, 1), row.FirstPaymentDate);
        Assert.Null(LoanMapper.Problem(FullLoan(), Dollars));
    }

    [Fact]
    public void Specified_payment_carries_the_fixed_amount()
    {
        var row = LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(),
            FullLoan() with { PaymentIsComputed = false, MonthlyPayment = 2500.00m },
            interestAccountId: null, escrowAccountId: null, Dollars);

        Assert.NotNull(row);
        Assert.False(row!.PaymentIsComputed);
        Assert.Equal(2500.00m, row.FixedPayment);
    }

    [Fact]
    public void A_loan_in_a_currency_with_no_minor_unit_keeps_its_principal_whole()
    {
        // MD stores the principal at the loan account's currency's own places: a yen
        // loan's integer is already whole yen (dec 0), not hundredths.
        var row = LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(),
            FullLoan() with { InitPrincipalMinor = 3_000_000, EscrowPayment = 5000m },
            interestAccountId: null, escrowAccountId: null, new AccountCurrency("JPY", 0));

        Assert.Equal(3_000_000m, row!.OriginalPrincipal);
    }

    [Fact]
    public void Returns_null_when_principal_is_missing()
    {
        var row = LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(),
            FullLoan() with { InitPrincipalMinor = null },
            null, null, Dollars);

        Assert.Null(row);   // no usable amortization → don't seed
    }

    // --- the loan currency's places (ADR-0114 D15) -----------------------------------

    [Fact]
    public void An_escrow_finer_than_its_currency_is_a_problem_naming_both_place_counts()
    {
        var problem = LoanMapper.Problem(FullLoan() with { EscrowPayment = 12.345m }, Dollars);

        Assert.Equal("has a loan escrow payment with 3 decimal places, and Coffer keeps USD at 2", problem);
    }

    [Fact]
    public void A_fixed_payment_finer_than_its_currency_is_a_problem_naming_both_place_counts()
    {
        var problem = LoanMapper.Problem(
            FullLoan() with { PaymentIsComputed = false, MonthlyPayment = 1.5m },
            new AccountCurrency("JPY", 0));

        Assert.Equal("has a loan fixed payment with 1 decimal place, and Coffer keeps JPY at 0", problem);
    }

    [Fact]
    public void A_computed_payment_ignores_the_monthly_payment_figure()
    {
        // calc_pmt set (and calc_pmt absent, which reads as computed): monthly_pmt is
        // never seeded, so its places do not matter.
        Assert.Null(LoanMapper.Problem(FullLoan() with { MonthlyPayment = 1.2345m }, Dollars));
        Assert.Null(LoanMapper.Problem(
            FullLoan() with { PaymentIsComputed = null, MonthlyPayment = 1.2345m }, Dollars));
    }

    [Fact]
    public void A_three_place_currency_takes_three_place_terms_unchanged()
    {
        var currency = new AccountCurrency("KWD", 3);
        var loan = FullLoan() with
        {
            InitPrincipalMinor = 1_234_567, EscrowPayment = 12.345m,
            PaymentIsComputed = false, MonthlyPayment = 100.125m,
        };

        Assert.Null(LoanMapper.Problem(loan, currency));
        var row = LoanMapper.Map(Guid.NewGuid(), Guid.NewGuid(), loan, null, null, currency);
        Assert.Equal(1234.567m, row!.OriginalPrincipal);
        Assert.Equal(12.345m, row.EscrowAmount);
        Assert.Equal(100.125m, row.FixedPayment);
    }

    [Fact]
    public void An_unusable_loan_is_never_a_problem_since_it_is_not_seeded()
    {
        Assert.Null(LoanMapper.Problem(FullLoan() with { InitPrincipalMinor = null, EscrowPayment = 1.234m }, Dollars));
    }

    [Fact]
    public void Map_throws_when_reached_with_a_figure_finer_than_its_currency()
    {
        // The guard behind the preview: a caller that skipped MdCurrencies.Problems.
        var ex = Assert.Throws<InvalidDataException>(() => LoanMapper.Map(
            Guid.NewGuid(), Guid.NewGuid(), FullLoan() with { EscrowPayment = 12.345m }, null, null, Dollars));

        Assert.Contains("3 decimal places", ex.Message, StringComparison.Ordinal);
    }
}
