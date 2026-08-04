using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// State carried across the import pipeline's steps. Each step (security
/// import, account import, transaction import, ...) reads from prior steps'
/// outputs and writes its own. Kept small on purpose — only the
/// MD-id → Coffer-id maps the next step actually needs.
/// </summary>
public sealed class ImportContext
{
    public ImportContext(MdExport export, Guid ledgerId)
    {
        Export   = export;
        LedgerId = ledgerId;
    }

    public MdExport Export { get; }

    /// <summary>
    /// The ledger every anchor row written by this import is stamped with
    /// (ADR-0020 Phase A). Resolved up front by the CLI from the user's
    /// <c>--ledger-id</c>/<c>--ledger-name</c> flags or the default ledger;
    /// every step reads it via this property.
    /// </summary>
    public Guid LedgerId { get; }

    /// <summary>
    /// Maps a Moneydance <c>curr</c> id (the MD UUID for a security row) to
    /// a small descriptor with the persisted <c>securities.id</c> plus the
    /// per-security <c>share_decimals</c> precision (Moneydance's <c>dec</c>
    /// field). Populated by <see cref="SecurityImportStep"/>; consumed by
    /// the holdings and investment-txn mappers.
    /// </summary>
    public Dictionary<string, SecurityRef> SecurityByMdId { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a Moneydance <c>acct</c> id to a small descriptor with the
    /// persisted Coffer <c>accounts.id</c> plus the discriminator fields
    /// the transaction mapper needs (whether the target is a category,
    /// which way money flows for category_kind, etc.). Populated by
    /// <see cref="AccountImportStep"/>; consumed by the transaction and
    /// investment-transaction mappers. MD accounts that were filtered
    /// (root, fake non-category placeholders) or diverted (security
    /// sub-accounts) are absent from this map.
    /// </summary>
    public Dictionary<string, AccountRef> AccountByMdId { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a Moneydance security sub-account (<c>acct.type='s'</c>) id to
    /// the corresponding <see cref="SecurityRef"/>. Built once at the
    /// start of the investment-transaction step by walking every <c>s</c>
    /// account, following its <c>currid</c> through
    /// <see cref="SecurityByMdId"/>. Used by the investment mapper to
    /// resolve the security referenced by a <c>sec</c> split's
    /// <c>acctid</c> and to scale the raw share-quantity integer by the
    /// security's per-security precision.
    /// </summary>
    public Dictionary<string, SecurityRef> SecurityByMdSecAcctId { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Small descriptor of a persisted Coffer security, kept on the import
/// context so the investment mapper can scale Moneydance's raw integer
/// share quantities by the right power of ten without an extra DB
/// round-trip. <see cref="ShareDecimals"/> mirrors Moneydance's
/// <c>dec</c> field (typically 4 for stocks, 5 for mutual funds).
/// </summary>
public sealed record SecurityRef(Guid Id, int ShareDecimals);

/// <summary>
/// Small descriptor of a persisted Coffer account, kept on the import
/// context for the transaction mappers. Holding the discriminator
/// alongside the id lets mappers tell categories from real accounts
/// without an extra DB round-trip; <see cref="HoldingsAccountId"/> is set
/// on brokerage rows and points at the per-brokerage Holdings sibling
/// account (ADR-0019), so the investment mapper can target the right
/// account for holdings-side legs without re-querying.
/// </summary>
public sealed record AccountRef(
    Guid Id,
    string AccountType,
    Guid? HoldingsAccountId = null,
    /// <summary>
    /// MD `acct.olbfi` for this account — NON-NULL when MD was
    /// configured for live online OFX on this account. Used by
    /// TransactionMapper.DecomposeOrigin to distinguish online OFX
    /// from QFX file imports when the per-txn `ol_fi_id` shape is
    /// the same. ADR-0035 §2 / mig 110.
    /// </summary>
    string? OlbFi = null,
    /// <summary>
    /// MD `acct.ofx_import_acct_num` for this account — NON-NULL
    /// when the account was set up for QFX file imports. Paired
    /// with `OlbFi` to drive the classifier.
    /// </summary>
    string? OfxImportAcctNum = null)
{
    public bool IsCategory => AccountType == "category";
}

/// <summary>Counts emitted by a single import step for the CLI summary.</summary>
/// <remarks>
/// <see cref="Skips"/> itemises every transaction the step declined to import.
/// A dropped transaction is silent data loss (see the TDLM/TDLP undercount
/// investigation, 2026-07): the importer MUST name what it dropped and why so
/// the caller can surface it and the validator can fail a lossy import. Empty
/// on a clean run.
/// </remarks>
public sealed record ImportStepResult(
    string StepName,
    int Read,
    int Written,
    int Skipped,
    IReadOnlyList<SkippedTxn>? Skips = null);

/// <summary>
/// One Moneydance transaction a pipeline step could not import, with enough
/// context to identify it in the source data and understand the loss:
/// the MD txn id, the machine reason, and (for investment txns) the security
/// and share quantity that went missing.
/// </summary>
public sealed record SkippedTxn(
    string TxnId,
    string Reason,
    string? Security = null,
    string? Ticker = null,
    decimal? Shares = null,
    int? Date = null);
