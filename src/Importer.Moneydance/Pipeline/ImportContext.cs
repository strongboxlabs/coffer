using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// State carried across the import pipeline's steps. Each step (security
/// import, account import, transaction import, ...) reads from prior steps'
/// outputs and writes its own. Kept small on purpose — only the
/// MD-id → Coffer-id maps the next step actually needs, and what a
/// step found that a validation check reports.
/// </summary>
public sealed class ImportContext
{
    public ImportContext(MdExport export, Guid ledgerId)
    {
        Export   = export;
        LedgerId = ledgerId;
    }

    public MdExport Export { get; }

    private Mappers.MdCurrencies? _currencies;

    /// <summary>
    /// The export's currencies (ADR-0114): the base currency, and the code each
    /// account's own <c>currid</c> names. Read once, on first use.
    /// </summary>
    public Mappers.MdCurrencies Currencies => _currencies ??= Mappers.MdCurrencies.From(Export);

    /// <summary>
    /// The ledger every anchor row written by this import is stamped with
    /// (ADR-0020 Phase A). Resolved up front by
    /// <see cref="MoneydanceImportService"/> before any step runs; every step
    /// reads it via this property.
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
    /// The Moneydance ids in <see cref="AccountByMdId"/> that resolve to the
    /// ledger's system Uncategorized category rather than to an account of
    /// their own — MD's blank-named top-level leaf categories
    /// (<see cref="Mappers.AccountMapper.IsBlankLeafCategory"/>). Populated by
    /// <see cref="AccountImportStep"/>. They have no <c>account_external_ids</c>
    /// junction row, which the account-count parity check has to know.
    /// </summary>
    public HashSet<string> MdIdsOnUncategorized { get; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The securities whose <c>sec_type</c> code is not in
    /// <see cref="Mappers.MdSecurityType"/>'s table, so they were imported with
    /// vehicle <c>other</c>. Populated by <see cref="SecurityImportStep"/>; read by
    /// the <c>security-types-recognised</c> check.
    /// </summary>
    public List<UnrecognisedSecurityType> UnrecognisedSecurityTypes { get; } = [];
}

/// <summary>A security imported with vehicle <c>other</c> because its account's
/// <c>sec_type</c> code is not one Coffer recognises.</summary>
public sealed record UnrecognisedSecurityType(string SecurityMdId, string Code);

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
    string? OfxImportAcctNum = null,
    /// <summary>
    /// The decimal places this account's Moneydance amounts are stored at: its MD
    /// currency's <c>dec</c> (0 for the yen, 2 for the euro). Null where it was never
    /// resolved; scaling an amount on such an account throws (<c>MdAmounts.ToMoney</c>)
    /// rather than assume two places.
    /// </summary>
    int? AmountDecimals = null)
{
    public bool IsCategory => AccountType == "category";
}

/// <summary>Counts emitted by a single import step, for the validation report and
/// the reconcile diagnostic's summary.</summary>
/// <remarks>
/// <see cref="Skips"/> itemises every transaction the step declined to import.
/// A dropped transaction is silent data loss (see the share-undercount
/// investigation on a pair of securities, 2026-07): the importer MUST name what it dropped and why so
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
