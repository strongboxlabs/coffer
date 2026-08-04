using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Translation from a Moneydance <see cref="MdAcct"/> into a Coffer
/// <see cref="AccountRow"/>. Pure logic; the only side effect is the
/// caller-supplied <see cref="Guid.NewGuid"/> for fresh rows. The
/// type-code translation table mirrors ADR-0016.
/// </summary>
/// <remarks>
/// <para>The mapper enforces the asymmetry decided in ADR-0017: real-account
/// hierarchy in MD's data is dropped (children are flattened to the type
/// root, placeholder parents are dropped if they have no own transactions).
/// Categories preserve full hierarchy.</para>
///
/// <para>Two MD type codes are not mapped to <c>accounts</c> rows at all:</para>
/// <list type="bullet">
///   <item><description><c>r</c> — the global root container; filtered.</description></item>
///   <item><description><c>s</c> — per-security position holders; surfaced as
///   <c>holdings</c> rows by a later mapper, not as accounts.</description></item>
/// </list>
///
/// <para>The caller passes a <see cref="MapInputs"/> object with the supporting
/// sets the mapper needs (which accounts have own transactions, which are
/// referenced as parents). They're computed once over the export rather than
/// derived per-row; see <see cref="ComputeInputs"/>.</para>
/// </remarks>
public static class AccountMapper
{
    public sealed record MapInputs(
        IReadOnlySet<string> AccountsWithOwnTransactions,
        IReadOnlySet<string> AccountsThatAreParents);

    public enum SkipReason
    {
        Root,
        SecuritySubAccount,
        FakeNonCategoryPlaceholder,
        UnknownTypeCode,
    }

    public sealed record MapResult(AccountRow? Row, SkipReason? Skip);

    public static MapResult Map(MdAcct acct, MapInputs inputs, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(acct);
        ArgumentNullException.ThrowIfNull(inputs);

        if (acct.IsRoot) return new MapResult(null, SkipReason.Root);
        if (acct.IsSecuritySubAccount) return new MapResult(null, SkipReason.SecuritySubAccount);

        var translation = TranslateType(acct.TypeCode);
        if (translation is null) return new MapResult(null, SkipReason.UnknownTypeCode);
        var (accountType, categoryKind) = translation.Value;

        // ADR-0016: a non-category MD parent with no own transactions is a
        // pure organizational placeholder and is not imported. Its children
        // already have parent_id=NULL in our model regardless.
        if (accountType != "category"
            && inputs.AccountsThatAreParents.Contains(acct.Id)
            && !inputs.AccountsWithOwnTransactions.Contains(acct.Id))
        {
            return new MapResult(null, SkipReason.FakeNonCategoryPlaceholder);
        }

        var openingBalance = accountType == "category"
            ? 0m                                       // CHECK: categories must have opening_balance = 0
            : MinorUnitsToDecimal(acct.StartingBalance ?? 0);

        return new MapResult(new AccountRow(
            Id: Guid.NewGuid(),
            LedgerId: ledgerId,                        // ADR-0020 Phase A: every account is scoped to a ledger
            ParentId: null,                            // populated by the second pass for categories
            Name: string.IsNullOrWhiteSpace(acct.Name) ? "(unnamed)" : acct.Name,
            AccountType: accountType,
            CategoryKind: categoryKind,
            CurrencyCode: "USD",                       // multi-currency support is a future concern
            OpeningBalance: openingBalance,
            // Mig 106 collapse: MD's `is_inactive` AND `hide` flags
            // both map to is_active=false. Either flag on the MD
            // side means the user marked the account "gone" from
            // their working set — single lifecycle flag in Coffer.
            IsActive: !acct.IsInactive && !acct.IsHidden,
            ExternalId: acct.Id,
            IsSystem: false,                           // user-imported accounts are not system rows
            HoldingsAccountId: null,                   // populated by AccountImportStep for brokerages
            Notes: NullIfEmpty(acct.Comment),
            // MD splits the account number across two fields by account flavour;
            // collapse to one column. bank-shaped accounts use bank_account_number,
            // investment-shaped accounts use invst_account_number; whichever is
            // non-empty wins.
            AccountNumber:    NullIfEmpty(acct.BankAccountNumber)
                              ?? NullIfEmpty(acct.InvestAccountNumber),
            // Same collapse for institution name: bank_name vs inst_name.
            InstitutionName:  NullIfEmpty(acct.BankName)
                              ?? NullIfEmpty(acct.InstName),
            RoutingNumber:    NullIfEmpty(acct.OfxBankId),
            AccountUrl:       NullIfEmpty(acct.AccountUrl),
            // Mig 110 / ADR-0035 §3: persist the per-account MD JSON
            // verbatim so the classifier can read `olbfi` /
            // `ofx_import_acct_num` to discriminate online OFX from
            // QFX file imports — a per-txn signal alone can't.
            ProviderRawPayload: string.IsNullOrEmpty(acct.RawJson) ? null : acct.RawJson,
            // ADR-0066: best-guess tax treatment from the account name; seed-once,
            // the user refines in the editor.
            TaxStatus: InferTaxStatus(acct.Name, accountType)),
            Skip: null);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Conservative best-guess of an account's tax treatment from its name
    /// (ADR-0066): Roth → tax_free; IRA/401k/403b/457/SEP/pension/annuity/rollover
    /// → tax_deferred; 529/Coverdell/HSA/ESA → other; otherwise NULL (taxable
    /// brokerages aren't reliably name-detectable — the user sets those). Only
    /// for non-category accounts. Seed-once; the editor owns it after import.
    /// </summary>
    public static string? InferTaxStatus(string? name, string accountType)
    {
        if (accountType == "category" || string.IsNullOrWhiteSpace(name)) return null;
        var n = name.ToLowerInvariant();
        if (n.Contains("roth")) return "tax_free";
        if (n.Contains("529") || n.Contains("coverdell") || n.Contains("hsa") || n.Contains("esa"))
            return "other";
        if (n.Contains("ira") || n.Contains("401") || n.Contains("403b") || n.Contains("457")
            || n.Contains(" sep") || n.Contains("simple") || n.Contains("pension")
            || n.Contains("annuity") || n.Contains("rollover"))
            return "tax_deferred";
        return null;
    }

    /// <summary>
    /// Translate a Moneydance <c>acct.type</c> code into Coffer
    /// <c>account_type</c> + <c>category_kind</c>. Returns <c>null</c> for
    /// codes the mapper does not handle (root, security sub-account, or
    /// unknown).
    /// </summary>
    public static (string AccountType, string? CategoryKind)? TranslateType(string typeCode) => typeCode switch
    {
        "b" => ("bank",        (string?)null),
        "c" => ("credit_card", null),
        "v" => ("investment",  null),
        "a" => ("asset",       null),
        "l" => ("liability",   null),
        "o" => ("loan",        null),
        "i" => ("category",    "income"),
        "e" => ("category",    "expense"),
        // 's' and 'r' are intentionally not in this table — see Map().
        _   => null,
    };

    /// <summary>
    /// Walk the export once and produce the supporting sets the per-row
    /// mapper needs.
    /// </summary>
    public static MapInputs ComputeInputs(Json.MdExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var accountsWithTxns = new HashSet<string>(StringComparer.Ordinal);
        var accountsThatAreParents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in export.AllItems)
        {
            switch (item.ObjType)
            {
                case "txn":
                    var txn = MdTxn.From(item);
                    accountsWithTxns.Add(txn.AcctId);
                    foreach (var split in txn.Splits)
                        accountsWithTxns.Add(split.AcctId);
                    break;

                case "acct":
                    var acct = MdAcct.From(item);
                    if (acct.ParentId is not null)
                        accountsThatAreParents.Add(acct.ParentId);
                    break;
            }
        }

        return new MapInputs(accountsWithTxns, accountsThatAreParents);
    }

    /// <summary>
    /// Convert Moneydance's minor-unit amounts (e.g. <c>"sbal": "123456"</c>
    /// for $1,234.56) into a decimal balance. Phase 2 assumes USD-style
    /// 2-decimal currencies; multi-currency precision is a future concern.
    /// </summary>
    internal static decimal MinorUnitsToDecimal(long minorUnits) =>
        minorUnits / 100m;
}
