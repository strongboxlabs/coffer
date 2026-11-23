namespace Coffer.Domain.Investment;

/// <summary>
/// The identity of a ledger's system Uncategorized category: the one
/// <c>is_system</c> category that unclassified postings land in. Bank feeds
/// create it lazily (<c>IngestOrchestrator.EnsureUncategorizedAsync</c> in the
/// API) and the Moneydance importer lands MD's blank-named category on it
/// (<c>AccountsRepository.EnsureUncategorizedAsync</c>). Both look the row up by
/// (ledger, <c>is_system</c>, <see cref="AccountType"/>, <see cref="Name"/>) and
/// create it with these values, so they must share one copy or an import and a
/// later feed would each mint their own. Both create it in the ledger's reporting
/// currency, as every category is (ADR-0109 D9, ADR-0114 D7); the currency is not
/// part of the lookup.
/// </summary>
/// <remarks>
/// Not an investment concept. It lives here because this is the one domain
/// project both the API and the importer reference.
/// </remarks>
public static class UncategorizedCategory
{
    public const string Name         = "Uncategorized";
    public const string AccountType  = "category";
    public const string CategoryKind = "expense";
}
