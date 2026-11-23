using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;

namespace Coffer.Api.Tests.Unit.Errors;

/// <summary>
/// <see cref="MoneyPlaces"/>, the one check of a typed money figure against its currency's
/// places (ADR-0114 D15): the refusal's wire shape, value rather than scale, the 0-, 2- and
/// 3-place cases, a stored code that would now be refused, and the stored-value pass.
/// </summary>
public sealed class MoneyPlacesTests
{
    private sealed record Refused(int Status, string Code, string CurrencyCode, int Places, string Field, string Detail);

    private static async Task<Refused> ExecuteAsync(IResult result)
    {
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        var root = doc.RootElement;
        return new Refused(
            ctx.Response.StatusCode,
            root.GetProperty("code").GetString()!,
            root.GetProperty("currencyCode").GetString()!,
            root.GetProperty("places").GetInt32(),
            root.GetProperty("field").GetString()!,
            root.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task A_fraction_on_a_whole_unit_currency_is_refused_naming_the_currency_its_places_and_the_field()
    {
        var result = MoneyPlaces.Check(1.5m, "JPY", "amount");

        Assert.NotNull(result);
        var refused = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
        Assert.Equal(BusinessError.Codes.AmountPlacesExceedCurrency, refused.Code);
        Assert.Equal("JPY", refused.CurrencyCode);
        Assert.Equal(0, refused.Places);
        Assert.Equal("amount", refused.Field);
        Assert.Contains("JPY", refused.Detail, StringComparison.Ordinal);
    }

    [Theory]
    // A whole figure written with two zero decimals: its value fits, whatever its scale.
    [InlineData("5.00", "JPY")]
    [InlineData("5", "JPY")]
    // Three places on a 3-place currency.
    [InlineData("1.234", "KWD")]
    // Two places on a 2-place currency, written with a trailing zero beyond them.
    [InlineData("1.230", "USD")]
    public void A_value_that_fits_its_currency_passes(string value, string currencyCode) =>
        Assert.Null(MoneyPlaces.Check(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), currencyCode, "amount"));

    [Theory]
    [InlineData("1.234", "USD", 2)]
    [InlineData("1.2345", "KWD", 3)]
    [InlineData("0.5", "JPY", 0)]
    public async Task A_value_finer_than_its_currency_is_refused_with_the_currencys_own_places(
        string value, string currencyCode, int places)
    {
        var result = MoneyPlaces.Check(
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), currencyCode, "counterpartyAmount");

        Assert.NotNull(result);
        var refused = await ExecuteAsync(result);
        Assert.Equal(currencyCode, refused.CurrencyCode);
        Assert.Equal(places, refused.Places);
        Assert.Equal("counterpartyAmount", refused.Field);
    }

    // A code stored before the rule that would now be refused (an "N.A." entry here) keeps
    // working at 2 places: two decimals pass, a third is refused naming 2.
    [Fact]
    public async Task A_stored_code_that_would_now_be_refused_takes_two_places()
    {
        Assert.Null(MoneyPlaces.Check(1.23m, "XAU", "openingBalance"));

        var result = MoneyPlaces.Check(1.234m, "XAU", "openingBalance");
        Assert.NotNull(result);
        var refused = await ExecuteAsync(result);
        Assert.Equal(2, refused.Places);
        Assert.Equal("XAU", refused.CurrencyCode);
    }

    // A write validates only the values it changes: a figure resent equal to its stored
    // value passes though it does not fit its currency, and a changed one is checked.
    [Fact]
    public void A_value_equal_to_its_stored_figure_passes_even_when_it_does_not_fit()
    {
        Assert.Null(MoneyPlaces.Check(12.34m, "JPY", "amount", stored: 12.34m));
        // Equal by value, not by scale.
        Assert.Null(MoneyPlaces.Check(12.340m, "JPY", "amount", stored: 12.34m));

        Assert.NotNull(MoneyPlaces.Check(12.35m, "JPY", "amount", stored: 12.34m));
        Assert.NotNull(MoneyPlaces.Check(12.34m, "JPY", "amount"));
    }

    // A currency change on a loan whose opening balance and terms carry places the new
    // currency lacks: the refusal counts the transactions and names each account-level
    // figure, so the editor can point at those fields (ADR-0114 D15).
    [Fact]
    public async Task A_relabel_refusal_counts_the_transactions_and_names_each_account_level_figure()
    {
        var result = MoneyPlaces.RelabelRefusal(
            "JPY", count: 3, accountFigures: [UnfitAmountKinds.LoanTerms, UnfitAmountKinds.OpeningBalance]);

        var (status, root) = await ExecuteJsonAsync(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        Assert.Equal(BusinessError.Codes.RelabelPlacesExceedCurrency, root.GetProperty("code").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(
            ["opening_balance", "loan_terms"],
            root.GetProperty("accountFigures").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("JPY", root.GetProperty("currencyCode").GetString());
        Assert.Equal(0, root.GetProperty("places").GetInt32());
        // The message counts the transactions, then names each account-level figure, in
        // that order whatever order the caller found them in.
        Assert.Equal(
            "3 transaction(s) carry more decimal places than JPY allows (0). Edit them to fit first. "
            + "The opening balance has more decimal places than JPY allows. "
            + "The loan terms have more decimal places than JPY allows.",
            root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_relabel_refusal_with_no_account_level_figure_sends_an_empty_list()
    {
        var (_, root) = await ExecuteJsonAsync(MoneyPlaces.RelabelRefusal("JPY", count: 1, accountFigures: []));

        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("accountFigures").ValueKind);
        Assert.Equal(0, root.GetProperty("accountFigures").GetArrayLength());
        Assert.Equal(
            "1 transaction(s) carry more decimal places than JPY allows (0). Edit them to fit first.",
            root.GetProperty("detail").GetString());
    }

    // Only the sentences that apply: with every transaction fitting, the message names the
    // account-level figure alone, without the transactions' sentence.
    [Theory]
    [InlineData(UnfitAmountKinds.OpeningBalance, "The opening balance has more decimal places than KWD allows.")]
    [InlineData(UnfitAmountKinds.LoanTerms, "The loan terms have more decimal places than KWD allows.")]
    public async Task A_relabel_refusal_with_no_transaction_names_only_the_account_level_figure(string figure, string detail)
    {
        var (_, root) = await ExecuteJsonAsync(MoneyPlaces.RelabelRefusal("KWD", count: 0, accountFigures: [figure]));

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Equal(detail, root.GetProperty("detail").GetString());
    }

    [Fact]
    public void A_relabel_refusal_names_only_account_level_figures_and_at_least_one_figure()
    {
        Assert.Throws<ArgumentException>(() =>
            MoneyPlaces.RelabelRefusal("JPY", count: 1, accountFigures: [UnfitAmountKinds.Transaction]));
        Assert.Throws<ArgumentException>(() =>
            MoneyPlaces.RelabelRefusal("JPY", count: 0, accountFigures: []));
    }

    private static async Task<(int Status, JsonElement Root)> ExecuteJsonAsync(IResult result)
    {
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
