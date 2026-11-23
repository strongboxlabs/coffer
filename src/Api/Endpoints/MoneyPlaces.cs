using Microsoft.AspNetCore.Http;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Domain.Money;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The one check every writer makes of a typed money figure against its currency's places
/// (ADR-0114 D15): a value with more places than its currency allows is refused with
/// <see cref="BusinessError.Codes.AmountPlacesExceedCurrency"/>, naming the currency, its
/// places and the request field.
/// </summary>
/// <remarks>
/// <para>The value is compared, not its scale, so trailing zeros fit: a whole figure sent
/// with two zero decimals fits a whole-unit currency. The places are
/// <see cref="CurrencyMinorUnits.PlacesOf"/>'s, so a stored code that would now be refused
/// takes 2, as it did before the rule.</para>
/// <para>A write validates only the values it changes. A caller that has the stored figure
/// passes it as <c>stored</c>, and a value equal to it passes even when it does not fit,
/// because stored amounts that do not fit are kept and an edit that leaves one as it was
/// must not be refused for it.</para>
/// <para>Only a typed figure is checked here. A figure the API computes is rounded at its
/// currency's places with <see cref="CurrencyMinorUnits.RoundAwayFromZero"/>, never
/// refused.</para>
/// </remarks>
internal static class MoneyPlaces
{
    /// <summary>Null when <paramref name="value"/> fits <paramref name="currencyCode"/>, or
    /// equals <paramref name="stored"/>; otherwise the 422 refusal.</summary>
    /// <param name="value">The typed figure.</param>
    /// <param name="currencyCode">The currency the figure lands in, as stored.</param>
    /// <param name="field">The request field's wire name, as the client sent it.</param>
    /// <param name="stored">The figure the write would replace, when there is one.</param>
    public static IResult? Check(decimal value, string currencyCode, string field, decimal? stored = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (Unchanged(value, stored) || CurrencyMinorUnits.Fits(value, currencyCode)) return null;
        return Refusal(currencyCode, field);
    }

    /// <summary>True when <paramref name="value"/> equals the stored figure, compared by
    /// value, so a resent figure passes whatever its scale.</summary>
    public static bool Unchanged(decimal value, decimal? stored) => stored is { } s && value == s;

    /// <summary>The refusal for a figure in <paramref name="currencyCode"/>, sent as
    /// <paramref name="field"/>, that carries more places than the currency allows.</summary>
    public static IResult Refusal(string currencyCode, string field)
    {
        var places = CurrencyMinorUnits.PlacesOf(currencyCode);
        return BusinessError.Problem(
            BusinessError.Codes.AmountPlacesExceedCurrency,
            Detail(currencyCode, places),
            "Too many decimal places",
            new Dictionary<string, object?>
            {
                ["currencyCode"] = currencyCode,
                ["places"] = places,
                ["field"] = field,
            });
    }

    /// <summary>The account-level figures a relabel refusal can name, as
    /// <see cref="UnfitAmountKinds"/> values.</summary>
    public static readonly IReadOnlyList<string> RelabelAccountFigures =
        [UnfitAmountKinds.OpeningBalance, UnfitAmountKinds.LoanTerms];

    /// <summary>
    /// The refusal of a move or a currency change whose stored figures carry more places
    /// than the new currency allows (ADR-0114 D13, D15,
    /// <see cref="BusinessError.Codes.RelabelPlacesExceedCurrency"/>), naming how many
    /// transactions and which account-level figures do not fit.
    /// </summary>
    /// <param name="currencyCode">The new currency.</param>
    /// <param name="count">How many transactions have a leg that does not fit, on the
    /// account or its Holdings sibling, a category's mirror of that leg not counted
    /// again and a transaction merged into another left out (ADR-0114 D15).</param>
    /// <param name="accountFigures">The account-level figures that do not fit, each one of
    /// <see cref="RelabelAccountFigures"/>; empty when none does.</param>
    public static IResult RelabelRefusal(string currencyCode, int count, IReadOnlyCollection<string> accountFigures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(accountFigures);
        foreach (var figure in accountFigures)
        {
            if (!RelabelAccountFigures.Contains(figure, StringComparer.Ordinal))
                throw new ArgumentException($"'{figure}' is not an account-level figure a relabel names.", nameof(accountFigures));
        }
        if (count == 0 && accountFigures.Count == 0)
            throw new ArgumentException("A relabel refusal names at least one figure that does not fit.", nameof(count));

        var places = CurrencyMinorUnits.PlacesOf(currencyCode);
        var figures = RelabelAccountFigures.Where(f => accountFigures.Contains(f, StringComparer.Ordinal)).ToList();
        return BusinessError.Problem(
            BusinessError.Codes.RelabelPlacesExceedCurrency,
            RelabelDetail(currencyCode, places, count, figures),
            "Too many decimal places",
            new Dictionary<string, object?>
            {
                ["count"] = count,
                ["accountFigures"] = figures,
                ["currencyCode"] = currencyCode,
                ["places"] = places,
            });
    }

    /// <summary>The relabel refusal's message: a sentence for the transactions when any
    /// does not fit, then one for each account-level figure that does not, in
    /// <see cref="RelabelAccountFigures"/>' order, joined by a space (ADR-0114 D15).</summary>
    private static string RelabelDetail(string currencyCode, int places, int count, IReadOnlyList<string> figures)
    {
        var sentences = new List<string>(3);
        if (count > 0)
            sentences.Add($"{count} transaction(s) carry more decimal places than {currencyCode} allows ({places}). Edit them to fit first.");
        foreach (var figure in figures)
        {
            sentences.Add(figure switch
            {
                UnfitAmountKinds.OpeningBalance => $"The opening balance has more decimal places than {currencyCode} allows.",
                UnfitAmountKinds.LoanTerms => $"The loan terms have more decimal places than {currencyCode} allows.",
                _ => throw new ArgumentException($"'{figure}' is not an account-level figure a relabel names.", nameof(figures)),
            });
        }
        return string.Join(' ', sentences);
    }

    private static string Detail(string currencyCode, int places) => places switch
    {
        0 => $"An amount in {currencyCode} is a whole number: {currencyCode} has no decimal places.",
        1 => $"An amount in {currencyCode} has at most one decimal place.",
        _ => $"An amount in {currencyCode} has at most {places} decimal places.",
    };
}
