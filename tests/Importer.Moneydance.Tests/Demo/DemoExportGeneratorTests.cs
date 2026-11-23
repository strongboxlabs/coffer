using System.Text;
using System.Text.Json;
using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The generated export's frame: it parses through the importer's own reader,
/// carries the generator's honest metadata, re-emits the base rows verbatim, and
/// writes the household's real accounts with the fields the importer reads.
/// </summary>
public sealed class DemoExportGeneratorTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    private static readonly Lazy<DemoBuild> Build = new(() => DemoExportGenerator.Generate(DemoSpec.Full(AsOf)));

    [Fact]
    public void The_export_parses_with_the_generator_metadata()
    {
        using var export = Build.Value.ToExport();

        Assert.Equal(DemoExportGenerator.Exporter, export.Metadata.Exporter);
        Assert.Equal(0, export.Metadata.MoneydanceBuild);
        Assert.Equal(20261118, export.Metadata.ExportDate);
        Assert.Equal("Demo", export.Metadata.FileName);
    }

    [Fact]
    public void The_same_spec_gives_the_same_bytes_and_another_seed_other_ids()
    {
        var again = DemoExportGenerator.Generate(DemoSpec.Full(AsOf));
        Assert.Equal(Build.Value.Json, again.Json);
        Assert.Equal(Build.Value.CardStatementCsv, again.CardStatementCsv);

        var other = DemoExportGenerator.Generate(new DemoSpec(DemoSpec.DefaultSeed + 1, AsOf, DemoSpan.Full));
        Assert.NotEqual(Build.Value.Json, other.Json);
    }

    [Fact]
    public void Base_rows_come_first_and_unchanged()
    {
        using var document = JsonDocument.Parse(Build.Value.Json);
        var items = document.RootElement.GetProperty("all_items").EnumerateArray().ToList();
        var baseItems = DemoBase.Instance.Items;

        Assert.True(items.Count > baseItems.Count);
        for (var i = 0; i < baseItems.Count; i++)
        {
            Assert.True(
                JsonElement.DeepEquals(baseItems[i], items[i]),
                $"base row {i} ({baseItems[i].GetProperty("id").GetString()}) changed on the way out");
        }

        var settings = document.RootElement.GetProperty("local_settings");
        Assert.True(JsonElement.DeepEquals(DemoBase.Instance.LocalSettings!.Value, settings));
    }

    [Fact]
    public void The_kind_override_passes_through_untouched()
    {
        using var export = Build.Value.ToExport();
        var adjustment = export.AllItems
            .Where(i => i.ObjType == "acct")
            .Select(MdAcct.From)
            .Single(a => a.Name == "Valuation Adjustment");

        Assert.Equal("adjustment", adjustment.CofferCategoryKind);
    }

    [Fact]
    public void Every_real_account_is_written_with_its_type_window_and_balance()
    {
        using var export = Build.Value.ToExport();
        var accounts = export.AllItems
            .Where(i => i.ObjType == "acct")
            .Select(MdAcct.From)
            .Where(a => a.TypeCode is not ("r" or "e" or "i" or "s"))
            .ToDictionary(a => a.Name, StringComparer.Ordinal);

        var windowStart = new DateOnly(2021, 11, 1);
        (string Name, string Type, DateOnly OpenedOn, bool Inactive)[] expected =
        [
            ("Joint Checking", "b", windowStart, false),
            ("Savings", "b", windowStart, false),
            ("Cash Wallet", "b", windowStart, false),
            ("Certificate of Deposit", "b", windowStart, false),
            ("Brokerage", "v", windowStart, false),
            ("Thornbury 401(k)", "v", windowStart, true),
            ("Ashgrove 401(k)", "v", windowStart, false),
            ("Calloway 401(k)", "v", new DateOnly(2023, 2, 1), false),
            ("Rollover IRA", "v", new DateOnly(2023, 3, 1), false),
            ("Family HSA", "v", new DateOnly(2024, 11, 1), false),
            ("House", "a", new DateOnly(2024, 2, 1), false),
            ("Mortgage Escrow", "a", new DateOnly(2024, 3, 1), false),
            ("Mortgage", "o", new DateOnly(2024, 5, 1), false),
            ("Car Loan", "o", new DateOnly(2019, 11, 8), true),
            ("Hospital Payment Plan", "l", new DateOnly(2025, 3, 1), true),
            ("Classic Card", "c", windowStart, true),
            ("Rewards Card", "c", new DateOnly(2024, 1, 1), false),
        ];

        Assert.Equal(expected.Length, accounts.Count);
        foreach (var (name, type, openedOn, inactive) in expected)
        {
            Assert.True(accounts.TryGetValue(name, out var account), $"{name} is missing");
            Assert.Equal(type, account.TypeCode);
            Assert.Equal(openedOn, account.OpenedOn);
            Assert.Equal(inactive, account.IsInactive);
            Assert.Equal(DemoBase.Instance.RootAccountId, account.ParentId);
            Assert.Equal(DemoBase.Instance.BaseCurrencyId, account.CurrId);
            Assert.Null(account.OfxBankId);
        }

        Assert.Equal(HouseholdTerms.CertificateOfDepositOpening, accounts["Certificate of Deposit"].StartingBalance);
        Assert.Equal(0, accounts["Mortgage"].StartingBalance);
        Assert.Equal(
            -HouseholdTerms.CarLoan.BalanceAfter(HouseholdTerms.CarLoanPaymentsBeforeWindow),
            accounts["Car Loan"].StartingBalance);
        Assert.True(accounts["Car Loan"].StartingBalance < 0);

        foreach (var name in new[] { "Joint Checking", "Savings", "Certificate of Deposit", "Classic Card", "Rewards Card" })
        {
            Assert.False(string.IsNullOrEmpty(accounts[name].BankName), $"{name} has no institution");
            Assert.Matches(@"^\*{4}[0-9]{4}$", accounts[name].BankAccountNumber!);
        }
        Assert.Null(accounts["Cash Wallet"].BankName);
    }

    [Fact]
    public void The_loans_carry_their_terms_and_the_mortgage_its_escrow()
    {
        using var export = Build.Value.ToExport();
        var accounts = export.AllItems.Where(i => i.ObjType == "acct").Select(MdAcct.From).ToList();
        var mortgage = accounts.Single(a => a.Name == "Mortgage");
        var carLoan = accounts.Single(a => a.Name == "Car Loan");
        var escrow = accounts.Single(a => a.Name == "Mortgage Escrow");

        Assert.NotNull(mortgage.Loan);
        Assert.Equal(HouseholdTerms.MortgageRatePercent, mortgage.Loan.AnnualRatePercent);
        Assert.Equal(HouseholdTerms.MortgagePayments, mortgage.Loan.PaymentCount);
        Assert.Equal(12, mortgage.Loan.PaymentsPerYear);
        Assert.Equal(HouseholdTerms.MortgagePrincipal, mortgage.Loan.InitPrincipalMinor);
        Assert.True(mortgage.Loan.PaymentIsComputed);
        Assert.Equal(0m, mortgage.Loan.Points);
        Assert.Equal(HouseholdTerms.MonthlyEscrow / 100m, mortgage.Loan.EscrowPayment);
        Assert.Equal(escrow.Id, mortgage.Loan.EscrowAccountMdId);
        Assert.Equal(
            DemoBase.Instance.Category(MdCategoryType.Expense, "Bills", "Mortgage Interest"),
            mortgage.Loan.InterestAccountMdId);
        Assert.Equal(new DateOnly(2024, 5, 1), mortgage.Loan.FirstPaymentDate);

        Assert.NotNull(carLoan.Loan);
        Assert.Equal(HouseholdTerms.CarLoanPrincipal, carLoan.Loan.InitPrincipalMinor);
        Assert.Equal(
            DemoBase.Instance.Category(MdCategoryType.Expense, "Loan", "Interest"),
            carLoan.Loan.InterestAccountMdId);
        Assert.Null(carLoan.Loan.EscrowAccountMdId);
    }

    [Fact]
    public void The_manifest_names_every_account_and_both_folders()
    {
        var manifest = Build.Value.Manifest;

        Assert.Equal(17, manifest.AccountIds.Count);
        var everyday = manifest.Folders.Single(f => f.Name == HouseholdAccounts.EverydayFolder);
        Assert.Equal("bank", everyday.AccountType);
        Assert.Equal(
            new[] { manifest.AccountIds[DemoAccountRoles.JointChecking], manifest.AccountIds[DemoAccountRoles.CashWallet] },
            everyday.AccountIds);

        var retirement = manifest.Folders.Single(f => f.Name == HouseholdAccounts.RetirementFolder);
        Assert.Equal("investment", retirement.AccountType);
        Assert.Equal(4, retirement.AccountIds.Count);
        Assert.DoesNotContain(manifest.AccountIds[DemoAccountRoles.OldPlanA], retirement.AccountIds);
    }

    [Fact]
    public void The_card_statement_csv_has_its_header()
    {
        Assert.StartsWith(CardStatementCsv.Header + "\n", Build.Value.CardStatementCsv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_rows_are_quoted_and_signed_without_a_culture()
    {
        var csv = CardStatementCsv.Build(
        [
            new CardStatementCsvRow(new DateOnly(2026, 10, 7), "MARKET, \"WEST\"", -12_34),
            new CardStatementCsvRow(new DateOnly(2026, 10, 8), "REFUND", 5),
        ]);

        Assert.Equal(
            "Date,Description,Amount\n2026-10-07,\"MARKET, \"\"WEST\"\"\",-12.34\n2026-10-08,REFUND,0.05\n",
            csv);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(-1)]
    public void A_short_span_outside_one_to_sixty_months_is_refused(int months)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoSpan.LastMonths(months));
    }

    [Fact]
    public void A_short_span_keeps_from_day_one_of_its_first_month()
    {
        var calendar = new DemoCalendar(AsOf);

        Assert.Null(DemoSpan.Full.KeepsFrom(calendar));
        Assert.Equal(new DateOnly(2026, 8, 1), DemoSpan.LastMonths(3).KeepsFrom(calendar));
        Assert.Equal(calendar.WindowStart, DemoSpan.LastMonths(60).KeepsFrom(calendar));
        Assert.Equal(DemoSpan.LastMonths(12), DemoSpan.LastMonths(12));
    }

    [Fact]
    public void Writing_is_refused_out_of_section_order()
    {
        using var writer = new MdJsonWriter();
        writer.WriteMetadata("x", 0, 20260101, "y");
        writer.WriteItem(MdSection.Prices, "csnap", "p1", []);

        Assert.Throws<InvalidOperationException>(
            () => writer.WriteItem(MdSection.Securities, "curr", "c1", []));
        Assert.Throws<InvalidOperationException>(() => writer.ToArray());

        writer.WriteLocalSettings(null);
        var json = Encoding.UTF8.GetString(writer.ToArray());
        Assert.Equal(
            """{"metadata":{"exporter":"x","moneydance_build":0,"export_date":20260101,"file_name":"y"},"all_items":[{"obj_type":"csnap","id":"p1"}],"local_settings":{}}""",
            json);
    }
}
