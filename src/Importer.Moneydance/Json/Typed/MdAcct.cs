namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>acct</c> item. The <see cref="TypeCode"/>
/// is the raw Moneydance type discriminator; translation to Coffer's
/// <c>account_type</c> happens in the account mapper (PR 2.4).
/// </summary>
public sealed record MdAcct(
    string Id,
    string Name,
    string TypeCode,
    string? ParentId,
    string? CurrId,
    bool IsInactive,
    bool IsHidden,
    long? StartingBalance,
    string? Comment,
    string? AccountUrl,
    string? BankAccountNumber,
    string? BankName,
    string? OfxBankId,
    string? InstName,
    string? InvestAccountNumber,
    /// <summary>
    /// Per-account online-OFX broker config — MD's `olbfi` field
    /// (e.g. `:ofx.example-broker.com:0000`). NON-NULL means MD was
    /// configured for a live online OFX feed on this account. Used
    /// by the classifier to distinguish online OFX from QFX file
    /// imports when the per-txn `ol_fi_id` shape is the same for
    /// both. ADR-0035 §2 / mig 110.
    /// </summary>
    string? OlbFi,
    /// <summary>
    /// Per-account QFX-file import marker — MD's `ofx_import_acct_num`
    /// field. NON-NULL means MD was set up to import QFX files for
    /// this account (the user typically downloads QFX from the
    /// bank's web portal). Used alongside `OlbFi` for the classifier
    /// rule above.
    /// </summary>
    string? OfxImportAcctNum,
    /// <summary>
    /// Verbatim per-row JSON for the MD `acct` item — captured at
    /// parse time via `MdItem.RawJson`. Persisted on
    /// `accounts.provider_raw_payload` so future classifier work
    /// can be pure SQL over the JSONB column (ADR-0035 §3 / mig 110).
    /// Empty string when constructed by hand (test fixtures).
    /// </summary>
    string RawJson = "",
    /// <summary>
    /// Loan amortization fields (ADR-0050) — non-null only for loan
    /// accounts (<c>type="o"</c>); the importer seeds them into
    /// <c>loan_terms</c>.
    /// </summary>
    MdLoanFields? Loan = null)
{
    /// <summary>
    /// Construct a typed account view from a generic <see cref="MdItem"/>.
    /// Throws if the item's <c>obj_type</c> is not <c>"acct"</c> or required
    /// fields are missing.
    /// </summary>
    public static MdAcct From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "acct")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'acct'.", nameof(item));

        return new MdAcct(
            Id: item.Id,
            Name: item.GetString("name") ?? string.Empty,
            TypeCode: item.GetString("type") ?? throw new InvalidDataException(
                $"acct {item.Id}: missing required 'type' field"),
            ParentId: item.GetString("parentid"),
            CurrId: item.GetString("currid"),
            IsInactive: item.GetBool("is_inactive") ?? false,
            IsHidden: item.GetBool("hide") ?? false,
            StartingBalance: item.GetLong("sbal"),
            Comment: item.GetString("comment"),
            AccountUrl: item.GetString("account_url"),
            BankAccountNumber: item.GetString("bank_account_number"),
            BankName: item.GetString("bank_name"),
            OfxBankId: item.GetString("ofx_bank_id"),
            InstName: item.GetString("inst_name"),
            InvestAccountNumber: item.GetString("invst_account_number"),
            OlbFi: item.GetString("olbfi"),
            OfxImportAcctNum: item.GetString("ofx_import_acct_num"),
            RawJson: item.RawJson,
            Loan: item.GetString("type") == "o" ? MdLoanFields.From(item) : null);
    }

    /// <summary>True if this account is a per-security position holder
    /// (Moneydance <c>type='s'</c>) — these become rows in <c>holdings</c>,
    /// not <c>accounts</c>, per ADR-0016.</summary>
    public bool IsSecuritySubAccount => TypeCode == "s";

    /// <summary>True if this is the absolute root container
    /// (Moneydance <c>type='r'</c>) — filtered out at import per ADR-0016.</summary>
    public bool IsRoot => TypeCode == "r";
}
