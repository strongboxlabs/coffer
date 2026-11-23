namespace Coffer.Api.Db.Entities;

/// <summary>
/// The only way to build a <see cref="TxnHeaderRow"/>: one factory per kind of writer,
/// each taking the originating account as a non-nullable <see cref="Guid"/>.
/// </summary>
/// <remarks>
/// <para>The constructors are private, so a header that names no originating account
/// does not compile — the target-typed <c>new()</c> a grep for
/// <c>new TxnHeaderRow</c> would miss included. The factories set only what is fixed
/// for the life of the row (ids, origin, the template flag, the provider identity) and
/// the owner; the caller assigns the rest as it always has.</para>
///
/// <para>Each writer passes the account the transaction is entered from — the register's account for a bank create, the
/// brokerage for an investment create (the SOURCE brokerage for transfer_shares), the
/// account imported into for an ingested row, the series source for a template, the
/// template's owner for an auto-posted occurrence.</para>
/// </remarks>
internal sealed partial class TxnHeaderRow
{
    /// <summary>A transaction a user enters: bank create, investment create, a
    /// user-posted reminder occurrence (which goes through the create paths).</summary>
    internal static TxnHeaderRow NewTransaction(Guid id, Guid ledgerId, Guid originatingAccountId) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        // 'manual' marks rows the user created in-app (the importer and feeds write
        // their own origins).
        Origin = "manual",
        OriginatingAccountId = originatingAccountId,
    };

    /// <summary>A row a feed or file import inserts, owned by the account it was
    /// imported into. Feed rows follow no other rule: a feed row and a hand-entered
    /// one are owned alike.</summary>
    internal static TxnHeaderRow NewIngested(
        Guid id, Guid ledgerId, Guid originatingAccountId,
        string origin, string? externalId, string? onlineMatchFitid = null, string? onlineMatchFiId = null) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        Origin = origin,
        ExternalId = externalId,
        OnlineMatchFitid = onlineMatchFitid,
        OnlineMatchFiId = onlineMatchFiId,
        OriginatingAccountId = originatingAccountId,
    };

    /// <summary>A reminder's template header (ADR-0047), owned by the series'
    /// source account — the brokerage for an investment reminder. The API's templates
    /// carry no external id; the Moneydance importer's carry <c>mdreminder:&lt;id&gt;</c>.</summary>
    internal static TxnHeaderRow NewTemplate(
        Guid id, Guid ledgerId, Guid originatingAccountId, string? externalId = null) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        Origin = "manual",          // manual ⇔ provider_key NULL ⇔ external_id may be NULL (CHECKs, mig 107/109)
        ExternalId = externalId,
        IsRecurringTemplate = true, // invisible to balances/holdings/register (ADR-0048)
        OriginatingAccountId = originatingAccountId,
    };

    /// <summary>An occurrence the auto-post clones from its template, owned by the
    /// template's owner.</summary>
    internal static TxnHeaderRow NewOccurrence(Guid id, Guid ledgerId, Guid originatingAccountId) => new()
    {
        Id = id,
        LedgerId = ledgerId,
        Origin = "manual",
        ExternalId = null,
        IsRecurringTemplate = false,
        OriginatingAccountId = originatingAccountId,
    };
}
