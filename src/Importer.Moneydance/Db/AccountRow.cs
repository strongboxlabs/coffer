namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>accounts</c>. Reflects the schema with
/// the <c>category</c>/<c>category_kind</c> discriminator (migration 007),
/// the symmetric-postings additions from migration 011 (<see cref="IsSystem"/>,
/// <see cref="HoldingsAccountId"/>), and the import-fidelity columns added
/// in migration 012 (<see cref="Notes"/>,
/// <see cref="AccountNumber"/>, <see cref="InstitutionName"/>,
/// <see cref="RoutingNumber"/>, <see cref="AccountUrl"/>).
/// </summary>
/// <remarks>
/// <para>The CHECK constraints in <c>accounts</c> enforce the cross-column
/// rules: <see cref="CategoryKind"/> non-null iff <see cref="AccountType"/>
/// is <c>"category"</c>; <see cref="ParentId"/> non-null only on category
/// rows; categories carry no <see cref="OpeningBalance"/> (must be 0) and
/// no feed connection.</para>
///
/// <para><see cref="IsSystem"/> flags rows the importer/API creates and the
/// user UI hides by default — currently the per-brokerage Holdings sibling
/// accounts that host the holdings-side legs of investment transactions
/// (ADR-0019). <see cref="HoldingsAccountId"/> on a brokerage points at its
/// Holdings sibling; on every other account it is <c>NULL</c>.</para>
///
/// <para>Mig 106 collapsed the original orthogonal-flag pair (is_active
/// for "open" + is_hidden for "clutter-hide") into a single
/// <see cref="IsActive"/> flag. MD imports translate either MD source
/// flag (<c>IsInactive</c> or <c>IsHidden</c>) into
/// <c>IsActive = false</c>. The remaining metadata fields preserve
/// Moneydance's account-edit-dialog values for matching against
/// feeds and for round-trip fidelity.</para>
/// </remarks>
public sealed record AccountRow(
    Guid Id,
    Guid LedgerId,
    Guid? ParentId,
    string Name,
    string AccountType,
    string? CategoryKind,
    string CurrencyCode,
    decimal OpeningBalance,
    bool IsActive,
    string? ExternalId,
    bool IsSystem,
    Guid? HoldingsAccountId,
    string? Notes,
    string? AccountNumber,
    string? InstitutionName,
    string? RoutingNumber,
    string? AccountUrl,
    /// <summary>
    /// Mig 110 / ADR-0035 §3: verbatim per-account JSON from MD's
    /// `acct` item. Drives the per-account classifier discriminator
    /// (`olbfi` set → online OFX; `ofx_import_acct_num` set → QFX
    /// file). NULL on Coffer-native accounts.
    /// </summary>
    string? ProviderRawPayload = null,
    /// <summary>ADR-0066: best-guess tax treatment seeded from the account name
    /// (taxable / tax_deferred / tax_free / other); NULL when not inferable.
    /// Seed-once — the user refines it in the editor.</summary>
    string? TaxStatus = null,
    /// <summary>ADR-0050 / mig 127: the account's Start Date, from MD's
    /// <c>date_created</c>. Seed-once — a value already in Coffer survives
    /// re-import, since the editor owns the field afterwards.</summary>
    DateOnly? OpenedOn = null);
