using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// The originating account the importer stamps on a header (migration 263): the
/// resolved Moneydance home account, literally. <see cref="Db.ImportAnnotations.Stamp"/>
/// applies it to the account its plan was made from.
/// </summary>
/// <remarks>
/// <para>Every mapper builds each posting from the home, so the home carries a leg in
/// every posting of what it emits — on the bank route, the investment route (the
/// brokerage) and for a reminder's template (the reminder's account). That is what
/// makes "the home" and "the side it was entered from" the same account here.</para>
///
/// <para>A header with no legs names no account and keeps NULL — the one state the
/// column allows it. A header WITH legs on none of which the home sits cannot come
/// out of the mappers; if one ever does, the stamp would point at an account the
/// transaction does not touch, so the import fails instead (fail closed).</para>
/// </remarks>
public static class ImportOriginatingAccount
{
    public static Guid? For(Guid homeAccountId, IReadOnlyCollection<TxnLegRow> legs, string? externalId)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count == 0) return null;
        if (legs.Any(l => l.AccountId == homeAccountId)) return homeAccountId;
        throw new InvalidOperationException(
            $"Moneydance transaction '{externalId}' was mapped with legs on none of which its home "
            + "account sits; its originating account would name an account it does not touch. "
            + "The import is stopped rather than stamping it.");
    }
}
