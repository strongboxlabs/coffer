namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>accounts</c>. The unified table
/// holds both real accounts (bank, credit_card, investment, asset,
/// liability, loan) and budget categories — the discriminator is
/// <see cref="AccountType"/> (ADR-0002, ADR-0017).
/// </summary>
/// <remarks>
/// <para>Class with init-only properties for EF Core compatibility (the
/// same reason <see cref="UserRow"/> is a class). Public because the API
/// maps it through to the per-ledger accounts endpoint; the trimmed
/// wire shape lives in the Accounts feature folder as <c>AccountSummary</c>.</para>
///
/// <para>Distinct from <c>Coffer.Importer.Moneydance.Db.AccountRow</c>:
/// the importer's row carries upsert-time fields (e.g. seed ids) the API
/// never needs. Keeping the namespaces separate avoids a shared library
/// before there's a concrete reason for one (ADR-0005 boundary).</para>
/// </remarks>
public sealed class AccountRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public Guid? ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string AccountType { get; init; } = string.Empty;
    public string? CategoryKind { get; init; }
    public string CurrencyCode { get; init; } = "USD";
    public decimal OpeningBalance { get; init; }
    /// <summary>The account's "Start Date" — the as-of date of the opening
    /// balance (migration 127, ADR-0050). MD records it for every account type;
    /// seeded on import, editable in Coffer. NULL when unknown.</summary>
    public DateOnly? OpenedOn { get; init; }
    public bool IsActive { get; init; }
    /// <summary>How the account is taxed (ADR-0066): taxable / tax_deferred /
    /// tax_free / other; NULL = unknown. Orthogonal to <see cref="AccountType"/>.
    /// Mutable — set via the account-settings PATCH.</summary>
    public string? TaxStatus { get; set; }
    public Guid? FeedConnectionId { get; init; }
    public string? ExternalId { get; init; }
    public bool IsSystem { get; init; }
    public Guid? HoldingsAccountId { get; init; }
    public string? Notes { get; init; }
    public string? AccountNumber { get; init; }
    public string? InstitutionName { get; init; }
    public string? RoutingNumber { get; init; }
    public string? AccountUrl { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Per-account SimpleFIN sync watermark (slice 2c.5). The sync
    /// algorithm uses (this - 7d) as the desired start date for THIS
    /// account; NULL means "no successful sync yet, ask for the full
    /// 90-day window." Mutable: sync writes through, the user can
    /// also reset via /accounts/{id}/sync-from-date.
    /// </summary>
    public DateTime? LastSimpleFinSyncAt { get; set; }

    /// <summary>
    /// Migration 054/056 (slice B0.4): on an investment (brokerage)
    /// account, when TRUE the recompute function adds fee-marked
    /// postings (<c>posting_role='fee'</c>) in this account's
    /// transactions to cost basis. Default FALSE. CHECK constraint
    /// restricts TRUE to <c>account_type='investment'</c>. Mutable
    /// because the user flips it via the account-settings PATCH;
    /// the endpoint runs <c>recompute_holdings_cost_basis</c> in
    /// the same transaction so the hero card and lots converge
    /// before the response returns.
    /// </summary>
    public bool IsTradeCommission { get; set; }

    /// <summary>
    /// Mig 110 / ADR-0035 §3: verbatim per-account JSON from the
    /// source provider — populated by the MD importer with the MD
    /// `acct` item's raw JSON for bootstrap accounts. The classifier
    /// reads this to distinguish online OFX feeds (`olbfi` set on
    /// the MD acct) from QFX file imports (`ofx_import_acct_num`
    /// set) when the per-txn `ol_fi_id` shape is the same for both.
    /// NULL on Coffer-native accounts created via the API.
    /// </summary>
    public string? ProviderRawPayload { get; init; }
}
