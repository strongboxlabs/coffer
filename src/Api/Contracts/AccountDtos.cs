namespace Coffer.Api.Contracts;

/// <summary>
/// Public DTO returned by <c>GET /api/ledgers/{id}/accounts</c>. Trimmed
/// to the fields the account picker / sidebar needs; the importer-side
/// metadata (institution_name, account_number, routing_number, …) stays
/// behind the row type until a UI surface needs it.
/// </summary>
/// <param name="FeedConnectionId">Set on accounts already bound to a
/// SimpleFIN connection (slice 2c.2). The feed-mapping wizard filters
/// these out so the user can't double-map a Coffer account to two
/// SimpleFIN accounts.</param>
/// <param name="NeedsReviewCount">Aggregated count of
/// <c>txn_headers</c> rows touching this account where
/// <c>needs_review = true</c> (slice 2c.2). Drives the sidebar
/// review-dot per ADR-0021: present-vs-absent signal, not a
/// number on the UI. Always 0 for categories + system rows.</param>
/// <param name="HoldingsAccountId">For brokerage (investment) accounts:
/// the id of the system-managed Holdings sibling sub-account that
/// carries security positions per ADR-0019. Surfaced so the SPA can
/// suppress that sibling as a counterparty chip on the brokerage
/// register (it's structural noise — the user sees Buy / Sell etc.
/// against THEIR account, not against an internal sub-account). Null
/// on every non-investment account and on the Holdings sibling itself.</param>
public sealed record AccountSummary(
    Guid Id,
    Guid LedgerId,
    Guid? ParentId,
    string Name,
    string AccountType,
    string? CategoryKind,
    string CurrencyCode,
    bool IsActive,
    bool IsSystem,
    Guid? FeedConnectionId,
    int NeedsReviewCount,
    Guid? HoldingsAccountId,
    // Migration 056 (slice B0.4): on an investment account, when TRUE
    // the recompute function adds fee-marked postings to cost basis
    // for this account's transactions. Null on non-investment rows
    // (DB CHECK constraint disallows TRUE elsewhere). Drives the
    // account-settings dialog's "Treat in-transaction fees as cost
    // basis" toggle in slice A4.a.
    bool IsTradeCommission,
    // ADR-0050: the account's institution label (nullable). Surfaced
    // now that the account editor needs to prefill + edit it; null on
    // categories + accounts with no institution recorded.
    string? InstitutionName);

/// <summary>
/// Body for <c>POST /api/ledgers/{ledgerId}/accounts</c> — create an account
/// of any type (ADR-0050). The <see cref="AccountType"/> discriminator follows
/// ADR-0017: real accounts (<c>bank</c> / <c>credit_card</c> / <c>investment</c>
/// / <c>asset</c> / <c>liability</c> / <c>loan</c>) carry no
/// <see cref="CategoryKind"/>; a <c>category</c> requires one
/// (<c>income</c> | <c>expense</c>) and may set a <see cref="ParentId"/>.
/// Creating an <c>investment</c> account also materializes its system-managed
/// Holdings sibling (ADR-0019), mirroring the importer.
/// </summary>
public sealed class CreateAccountRequest
{
    public string Name { get; init; } = string.Empty;
    public string AccountType { get; init; } = string.Empty;
    /// <summary>Required + only valid when <see cref="AccountType"/> is
    /// <c>category</c>; <c>income</c> | <c>expense</c>.</summary>
    public string? CategoryKind { get; init; }
    /// <summary>Optional category-tree parent; only valid for a
    /// <c>category</c>, and the parent must itself be a category in this
    /// ledger.</summary>
    public Guid? ParentId { get; init; }
    /// <summary>ISO currency code; defaults to <c>USD</c> when null/blank.</summary>
    public string? CurrencyCode { get; init; }
    public string? InstitutionName { get; init; }
    /// <summary>Free-form metadata (ADR-0050). Blank → null.</summary>
    public string? AccountNumber { get; init; }
    public string? RoutingNumber { get; init; }
    public string? AccountUrl { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
    /// <summary>Starting balance (ADR-0050). Must be 0 for categories (DB CHECK).</summary>
    public decimal OpeningBalance { get; init; }
    /// <summary>The account's "Start Date" — the as-of date of the opening
    /// balance (mig 127). Optional.</summary>
    public DateOnly? OpenedOn { get; init; }
    /// <summary>Amortization terms. REQUIRED when <see cref="AccountType"/> is
    /// <c>loan</c> (a loan can't be saved without complete terms); must be null
    /// for every other type.</summary>
    public LoanTermsDto? LoanTerms { get; init; }
}

/// <summary>
/// Editable amortization parameters for a loan account (ADR-0050 slice 3),
/// 1:1 with <c>loan_terms</c>. Carried on account create / edit / detail. The
/// current balance owed is NOT here — it's derived from the loan account's
/// posted legs (ADR-0050 D3). Required fields mirror the DB CHECKs:
/// <see cref="OriginalPrincipal"/> &gt; 0, <see cref="AnnualInterestRate"/> ≥ 0,
/// <see cref="PaymentCount"/> &gt; 0, <see cref="PaymentsPerYear"/> &gt; 0,
/// <see cref="Points"/> ≥ 0.
/// </summary>
public sealed class LoanTermsDto
{
    public decimal OriginalPrincipal { get; init; }
    /// <summary>Annual rate as a percent, e.g. <c>3.65</c>.</summary>
    public decimal AnnualInterestRate { get; init; }
    public decimal Points { get; init; }
    /// <summary>Total scheduled payments (term).</summary>
    public int PaymentCount { get; init; }
    /// <summary>Payment frequency, e.g. 12 for monthly.</summary>
    public int PaymentsPerYear { get; init; }
    public DateOnly? FirstPaymentDate { get; init; }
    public decimal EscrowAmount { get; init; }
    /// <summary>Category the interest portion posts to. Must be an account in
    /// this ledger when set.</summary>
    public Guid? InterestAccountId { get; init; }
    /// <summary>Account/category the escrow portion posts to. Must be an account
    /// in this ledger when set.</summary>
    public Guid? EscrowAccountId { get; init; }
    /// <summary>TRUE = derive the fixed payment via amortization; FALSE = use
    /// <see cref="FixedPayment"/>.</summary>
    public bool PaymentIsComputed { get; init; } = true;
    /// <summary>Required (and &gt; 0) when <see cref="PaymentIsComputed"/> is
    /// FALSE; ignored otherwise.</summary>
    public decimal? FixedPayment { get; init; }
}

/// <summary>
/// Body for <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}</c> — edit an
/// account's general attributes (ADR-0050). PARTIAL: a null scalar means
/// "leave unchanged". <see cref="AccountType"/> is IMMUTABLE after creation
/// (changing it would invalidate register rendering / holdings / existing
/// postings) and is intentionally absent. System accounts reject with
/// <c>account-is-system</c>.
/// </summary>
public sealed class UpdateAccountRequest
{
    // Text fields: null = unchanged; an empty string clears (→ null). The
    // editor sends the full current value of each field it manages, so it
    // clears by sending "" rather than needing a per-field clear flag.
    public string? Name { get; init; }
    public string? CurrencyCode { get; init; }
    public string? InstitutionName { get; init; }
    public string? AccountNumber { get; init; }
    public string? RoutingNumber { get; init; }
    public string? AccountUrl { get; init; }
    public string? Notes { get; init; }
    public bool? IsActive { get; init; }
    /// <summary>Reclassify a <c>category</c> (income ⇄ expense). Ignored /
    /// rejected on non-category accounts.</summary>
    public string? CategoryKind { get; init; }
    /// <summary>Starting balance (null = unchanged). Editing it on an account
    /// with history retroactively shifts its running balance (ADR-0050).</summary>
    public decimal? OpeningBalance { get; init; }
    /// <summary>Start date (null = unchanged). Use <see cref="ClearOpenedOn"/> to
    /// set it back to null.</summary>
    public DateOnly? OpenedOn { get; init; }
    /// <summary>Explicit clear for the nullable <see cref="OpenedOn"/> (a null
    /// value already means "unchanged").</summary>
    public bool ClearOpenedOn { get; init; }
    /// <summary>Amortization terms (null = unchanged). On a loan account the
    /// editor always sends the full current terms; rejected on non-loan types.</summary>
    public LoanTermsDto? LoanTerms { get; init; }
    /// <summary>Tax treatment (ADR-0066): "taxable" | "tax_deferred" | "tax_free"
    /// | "other". null = unchanged; "" clears (→ null).</summary>
    public string? TaxStatus { get; init; }
}

/// <summary>
/// Full editable shape of one account (ADR-0050), returned by
/// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}</c>. Carries the
/// metadata the lean <see cref="AccountSummary"/> omits (account / routing
/// number, URL, notes) so the editor can prefill on edit without bloating
/// every list / picker fetch.
/// </summary>
public sealed record AccountDetail(
    Guid Id,
    Guid LedgerId,
    Guid? ParentId,
    string Name,
    string AccountType,
    string? CategoryKind,
    string CurrencyCode,
    bool IsActive,
    bool IsSystem,
    string? InstitutionName,
    string? AccountNumber,
    string? RoutingNumber,
    string? AccountUrl,
    string? Notes,
    decimal OpeningBalance,
    DateOnly? OpenedOn,
    // ADR-0066: tax treatment (nullable); prefilled in the account editor.
    string? TaxStatus,
    // Present only on loan accounts that have a loan_terms row; null otherwise.
    LoanTermsDto? LoanTerms,
    // Present only on loan accounts with a managed payment reminder set up
    // (migration 168); null otherwise.
    ManagedReminderDto? ManagedReminder);

/// <summary>Summary of a loan account's managed payment reminder (ADR-0050
/// extension) — the scheduled auto-payment whose split is computed from the loan
/// terms. The loan account editor uses it to show the cadence + next due + a link
/// to the reminder; null when the loan has none set up.</summary>
public sealed record ManagedReminderDto(Guid ReminderId, string? Rrule, DateOnly? NextDue);

/// <summary>Body for
/// <c>POST /api/ledgers/{ledgerId}/accounts/{accountId}/payment-reminder</c> —
/// set up the managed payment reminder for a loan account. No amounts: the split
/// is derived from the loan terms; the cadence comes from the loan's
/// payments-per-year anchored on <see cref="StartDate"/>.
/// <see cref="SourceAccountId"/> is the bank account the payment is drawn from.</summary>
public sealed record SetupPaymentReminderRequest(Guid SourceAccountId, DateOnly StartDate);

/// <summary>
/// Body for <c>POST /api/ledgers/{ledgerId}/accounts/loan-payment-preview</c>
/// (ADR-0050 slice 3) — a stateless amortization compute so the account editor
/// can show the estimated payment live as the user types, with the C#
/// <c>LoanAmortization</c> service as the single source of truth (no duplicated
/// math in the SPA). Reads no ledger data; the route is ledger-scoped only for
/// auth consistency.
/// </summary>
public sealed class LoanPaymentPreviewRequest
{
    public decimal OriginalPrincipal { get; init; }
    public decimal AnnualInterestRate { get; init; }
    public int PaymentCount { get; init; }
    public int PaymentsPerYear { get; init; }
    public decimal EscrowAmount { get; init; }
    public bool PaymentIsComputed { get; init; } = true;
    public decimal? FixedPayment { get; init; }
}

/// <summary>Estimated payment for <see cref="LoanPaymentPreviewRequest"/>:
/// <see cref="PeriodicPayment"/> is the P&amp;I portion (amortized, or the fixed
/// value); <see cref="TotalPayment"/> adds escrow. Zero when the terms are
/// incomplete/invalid (the editor shows nothing rather than a bogus figure).</summary>
public sealed record LoanPaymentPreviewResponse(
    decimal PeriodicPayment,
    decimal EscrowAmount,
    decimal TotalPayment);

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/trade-commission</c>
/// (slice A4.a). Flips the per-brokerage "treat in-transaction fees
/// as cost basis" flag and triggers a recompute on the response path
/// so the hero card + lots reflect the change immediately.
/// </summary>
public sealed class PatchAccountTradeCommissionRequest
{
    /// <summary>TRUE = include <c>posting_role='fee'</c> postings in
    /// cost basis on this brokerage; FALSE = ignore them. Defaults
    /// FALSE on every account. The CHECK constraint
    /// <c>accounts_is_trade_commission_only_on_investment</c> means
    /// this endpoint returns 422 on non-investment accounts.</summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/active</c>
/// — inactive-account lifecycle slice. Flips the per-account
/// <c>is_active</c> flag; symmetric (re-activate = false → true).
/// System accounts (holdings siblings, Uncategorized) reject with
/// 422 <c>account-is-system</c>. The SPA owns the confirm-dialog
/// flow when the account still has positions or non-zero balance —
/// the server doesn't refuse a deactivation just because the
/// account isn't empty (MD-parity decision; locked in
/// follow-ups.md).
/// </summary>
public sealed class PatchAccountActiveRequest
{
    /// <summary>TRUE = mark account active (default for all new
    /// accounts); FALSE = mark inactive. Inactive accounts stay in
    /// the DB with all historical transactions intact, but disappear
    /// from the default account-list endpoint, pickers, and the
    /// sidebar's default rendering.</summary>
    public bool Active { get; init; }
}

/// <summary>
/// Response for
/// <c>GET /api/ledgers/{id}/accounts/{aid}/frequent-counterparties</c>.
/// The most-used counterparties of the source account, derived from
/// existing transaction history (no usage table) — the picker floats
/// these to the top so the common cases are one click away
/// (ADR-0043). Split by domain so the SPA can pin frequent accounts
/// and frequent categories separately.
/// </summary>
public sealed record FrequentCounterpartiesResponse(
    IReadOnlyList<FrequentCounterpartyDto> Accounts,
    IReadOnlyList<FrequentCounterpartyDto> Categories);

/// <summary>One ranked counterparty. <c>UseCount</c> is the number of
/// the source account's transactions that posted against this
/// counterparty (higher = more frequent).</summary>
public sealed record FrequentCounterpartyDto(
    Guid Id,
    string Name,
    string AccountType,
    string? CategoryKind,
    int UseCount);
