namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>curr</c> item. Moneydance uses one table
/// for both fiat currencies (USD, EUR, KRW, ...) and securities (stocks,
/// mutual funds, options); the <see cref="IsSecurity"/> flag discriminates.
/// A security's type, subtype and exchange are not here: Moneydance writes
/// them on the type-<c>s</c> security account (<see cref="MdAcct.Security"/>;
/// ADR-0016, amendment of 2026-11-18).
/// </summary>
public sealed record MdCurr(
    string Id,
    string Name,
    string CurrId,
    string? Type,
    string? Ticker,
    string? Cusip,
    int? Decimals,
    decimal? Rate,
    decimal? RelativeRate,
    bool IsHidden,
    bool IsBase,
    /// <summary>
    /// A security's relative currency, the one its rate and its snapshots' rates are
    /// relative to: MD's <c>rel_curr_id</c>, the <c>id</c> of another <c>curr</c>
    /// item. Null when MD records none, and then the rates are relative to the
    /// file's base currency.
    /// </summary>
    string? RelativeCurrId = null,
    /// <summary>MD's <c>relative_to_currid</c>: the ISO code of
    /// <see cref="RelativeCurrId"/>'s currency, written beside it.</summary>
    string? RelativeToCode = null)
{
    public static MdCurr From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "curr")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'curr'.", nameof(item));

        // Moneydance writes CUSIP under either 'curr_id.CUSIP' or 'curr_id.CUSIP-broken';
        // try the canonical key first.
        var cusip = item.GetString("curr_id.CUSIP")
                 ?? item.GetString("curr_id.CUSIP-broken");

        return new MdCurr(
            Id: item.Id,
            Name: item.GetString("name") ?? string.Empty,
            CurrId: item.GetString("currid") ?? string.Empty,
            Type: item.GetString("type"),
            Ticker: item.GetString("ticker"),
            Cusip: cusip,
            Decimals: item.GetInt("dec"),
            Rate: item.GetDecimal("rate"),
            RelativeRate: item.GetDecimal("rrate"),
            IsHidden: item.GetBool("hide_in_ui") ?? false,
            IsBase: item.GetBool("isbase") ?? false,
            RelativeCurrId: NullIfBlank(item.GetString("rel_curr_id")),
            RelativeToCode: NullIfBlank(item.GetString("relative_to_currid")));
    }

    /// <summary>True for security rows (stocks, mutual funds, etc.); false
    /// for plain currency entries that exist only to define an ISO code.</summary>
    public bool IsSecurity => Type == "s";

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
