using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Pipeline;

/// <summary>
/// A security whose Moneydance <c>sec_type</c> code Coffer does not know is imported
/// with vehicle type Other, and the import's <c>security-types-recognised</c> check
/// says so: nothing is lost, but the classification is a fallback the person should see.
/// </summary>
public sealed class SecurityTypesCheckTests
{
    private static ImportValidator.ValidationReport EmptyReport() =>
        new([ImportValidator.CheckResult.Pass("pre-existing")]);

    [Fact]
    public void Passes_when_every_code_was_recognised()
    {
        var report = MoneydanceImportService.AppendSecurityTypesCheck(EmptyReport(), []);

        var check = report.Checks.Single(c => c.Name == "security-types-recognised");
        Assert.True(check.Passed);
        Assert.Null(check.Message);
        Assert.Null(check.Summary);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Fails_naming_the_codes_and_the_count()
    {
        var report = MoneydanceImportService.AppendSecurityTypesCheck(EmptyReport(),
        [
            new UnrecognisedSecurityType("sec-1", "7"),
            new UnrecognisedSecurityType("sec-2", "X"),
            new UnrecognisedSecurityType("sec-3", "7"),
        ]);

        var check = report.Checks.Single(c => c.Name == "security-types-recognised");
        Assert.False(check.Passed);
        Assert.False(report.AllPassed);
        Assert.Equal("3 security(ies) with an unrecognised sec_type code (7, X) imported with vehicle type Other",
            check.Message);
        Assert.Equal(
            "3 securities have a Moneydance security type Coffer does not recognise (codes 7, X), so they were "
            + "imported with vehicle type Other. Set the vehicle with Edit security on the security's page.",
            check.Summary);
    }

    [Fact]
    public void One_security_reads_in_the_singular()
    {
        Assert.Equal(
            "1 security has a Moneydance security type Coffer does not recognise (code 9), so it was imported "
            + "with vehicle type Other. Set the vehicle with Edit security on the security's page.",
            CheckSummaries.UnrecognisedSecurityTypes(1, ["9"]));
    }
}
