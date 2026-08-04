namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>curr</c> item. Moneydance uses one table
/// for both fiat currencies (USD, EUR, KRW, ...) and securities (stocks,
/// mutual funds, options); the <see cref="IsSecurity"/> flag discriminates.
/// </summary>
public sealed record MdCurr(
    string Id,
    string Name,
    string CurrId,
    string? Type,
    string? Ticker,
    string? Cusip,
    string? SecType,
    string? SecSubtype,
    string? SecExchange,
    int? Decimals,
    decimal? Rate,
    decimal? RelativeRate,
    bool IsHidden,
    bool IsBase)
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
            SecType: item.GetString("sec_type"),
            SecSubtype: item.GetString("sec_subtype"),
            SecExchange: item.GetString("sec_exchange"),
            Decimals: item.GetInt("dec"),
            Rate: item.GetDecimal("rate"),
            RelativeRate: item.GetDecimal("rrate"),
            IsHidden: item.GetBool("hide_in_ui") ?? false,
            IsBase: item.GetBool("isbase") ?? false);
    }

    /// <summary>True for security rows (stocks, mutual funds, etc.); false
    /// for plain currency entries that exist only to define an ISO code.</summary>
    public bool IsSecurity => Type == "s";
}
