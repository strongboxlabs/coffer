using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// <c>POST /api/ledgers/{ledgerId}/accounts/loan-payment-preview</c> carries the loan's
/// currency, and the payment it previews rounds up to one minor unit of it (ADR-0114 D15).
/// A preview that names no currency is refused rather than defaulted.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoanPaymentPreviewTests : DatabaseTest
{
    public LoanPaymentPreviewTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    // The textbook one-year loan at a twelfth of its rate a month, scaled so its exact
    // payment carries a fraction of one whole unit.
    private static LoanPaymentPreviewRequest Terms(string? currencyCode) => new()
    {
        CurrencyCode = currencyCode,
        OriginalPrincipal = 100000m,
        AnnualInterestRate = 12m,
        PaymentCount = 12,
        PaymentsPerYear = 12,
        EscrowAmount = 0m,
        PaymentIsComputed = true,
    };

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client, SyntheticLedger ledger, LoanPaymentPreviewRequest body) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/loan-payment-preview", body);

    [Fact]
    public async Task A_whole_unit_currencys_preview_is_a_whole_payment_rounded_up()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var whole = await PreviewAsync(client, ledger, Terms("jpy"));
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        var wholeBody = (await whole.Content.ReadFromJsonAsync<LoanPaymentPreviewResponse>())!;
        Assert.Equal(8885m, wholeBody.PeriodicPayment);
        Assert.Equal(8885m, wholeBody.TotalPayment);

        // The anchor: the same terms at two places keep their cents.
        var cents = (await (await PreviewAsync(client, ledger, Terms("USD")))
            .Content.ReadFromJsonAsync<LoanPaymentPreviewResponse>())!;
        Assert.Equal(8884.88m, cents.PeriodicPayment);
    }

    [Fact]
    public async Task A_preview_that_names_no_currency_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        foreach (var code in new string?[] { null, "", "US" })
        {
            var response = await PreviewAsync(client, ledger, Terms(code));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("account-currency-invalid", doc.GetProperty("code").GetString());
        }
    }

    // ADR-0114 D15: a code stored before the rule that would now be refused keeps working at
    // 2 places, so its account's loan editor still previews a payment. CLF is a fund unit
    // with more places than the ceiling; XAU is an "N.A." entry. Both preview at 2 places,
    // the same payment as the two-place anchor above, never refused and never at the
    // table's exponent.
    [Fact]
    public async Task A_grandfathered_codes_preview_is_at_two_places_not_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        foreach (var code in new[] { "CLF", "XAU" })
        {
            var response = await PreviewAsync(client, ledger, Terms(code));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<LoanPaymentPreviewResponse>())!;
            Assert.Equal(8884.88m, body.PeriodicPayment);
            Assert.Equal(8884.88m, body.TotalPayment);
        }
    }
}
