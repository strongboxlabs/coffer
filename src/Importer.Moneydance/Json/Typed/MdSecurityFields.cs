namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// The security-type parameters Moneydance writes on a type-<c>s</c> security
/// account: <c>sec_type</c> (a numeric code, see
/// <see cref="Mappers.MdSecurityType"/>), <c>sec_subtype</c> (one of Moneydance's
/// own subtype names) and <c>sec_exchange</c>. They describe the security the
/// account holds, not the account; the <c>curr</c> item carries none of them
/// (ADR-0016, amendment of 2026-11-18).
/// </summary>
public sealed record MdSecurityFields(string? SecType, string? SecSubtype, string? SecExchange)
{
    /// <summary>No parameters: an unclassified security with no exchange.</summary>
    public static MdSecurityFields None { get; } = new(null, null, null);

    /// <summary>Read the three parameters off an item; blank values are null.</summary>
    public static MdSecurityFields From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new MdSecurityFields(
            SecType: NullIfEmpty(item.GetString("sec_type")),
            SecSubtype: NullIfEmpty(item.GetString("sec_subtype")),
            SecExchange: NullIfEmpty(item.GetString("sec_exchange")));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
