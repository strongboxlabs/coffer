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
        IReadOnlySet<string> AccountsThatAreParents,
        // The ids of MD's root container(s) (type 'r'), so a caller can tell a
        // top-level account from one nested under another.
        IReadOnlySet<string> RootAccounts);

    public enum SkipReason
    {
        Root,
        SecuritySubAccount,
        FakeNonCategoryPlaceholder,
        UnknownTypeCode,
    }

    public sealed record MapResult(AccountRow? Row, SkipReason? Skip);

    /// <param name="currencies">The export's currencies: a mapped account takes its own
    /// Moneydance currency (<see cref="MdCurrencies.RequireAccount"/>), its opening balance
    /// is scaled by that currency's decimal places, and one whose currency cannot be read
    /// throws rather than being stamped with another.</param>
    /// <param name="reportingCurrency">The ledger's reporting currency, which a category
    /// takes: a category holds no money of its own, so it has no currency of its own
    /// (ADR-0114 D7), whatever Moneydance recorded for it.</param>
    public static MapResult Map(
        MdAcct acct, MapInputs inputs, Guid ledgerId, MdCurrencies currencies, string reportingCurrency)
    {
        ArgumentNullException.ThrowIfNull(acct);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(currencies);
        if (!MdCurrencies.IsCurrencyCode(reportingCurrency))
            throw new ArgumentException($"'{reportingCurrency}' is not a currency code in stored form.", nameof(reportingCurrency));

        // Checked before any skip, so the key on a root, a security or an
        // unknown type code fails the import rather than being dropped with it.
        var isAdjustment = IsAdjustmentCategory(acct, inputs);

        if (acct.IsRoot) return new MapResult(null, SkipReason.Root);
        if (acct.IsSecuritySubAccount) return new MapResult(null, SkipReason.SecuritySubAccount);

        var translation = TranslateType(acct.TypeCode);
        if (translation is null) return new MapResult(null, SkipReason.UnknownTypeCode);
        var (accountType, categoryKind) = translation.Value;
        if (isAdjustment) categoryKind = "adjustment";

        if (IsPlaceholder(acct, inputs)) return new MapResult(null, SkipReason.FakeNonCategoryPlaceholder);

        // Resolved for a category too: the file must name a readable currency for every
        // account it imports (MdCurrencies.Problems), even one Coffer does not stamp.
        var currency = currencies.RequireAccount(acct);
        var isCategory = accountType == "category";
        var openingBalance = isCategory
            ? 0m                                       // CHECK: categories must have opening_balance = 0
            : MdAmounts.ToMoney(acct.StartingBalance ?? 0, currency.Decimals);

        return new MapResult(new AccountRow(
            Id: Guid.NewGuid(),
            LedgerId: ledgerId,                        // ADR-0020 Phase A: every account is scoped to a ledger
            ParentId: null,                            // populated by the second pass for categories
            Name: string.IsNullOrWhiteSpace(acct.Name) ? "(unnamed)" : acct.Name,
            AccountType: accountType,
            CategoryKind: categoryKind,
            // ADR-0114: a real account's own MD currency; a category the ledger's.
            CurrencyCode: isCategory ? reportingCurrency : currency.Code,
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
            TaxStatus: InferTaxStatus(acct.Name, accountType),
            // ADR-0050 / mig 127: the Start Date MD records for every account.
            // Categories have no opening balance, so a start date is meaningless
            // for them — leave it null rather than carrying MD's value across.
            OpenedOn: accountType == "category" ? null : acct.OpenedOn),
            Skip: null);
    }

    /// <summary>
    /// True for a pure organizational placeholder, which <see cref="Map"/> skips
    /// (ADR-0016): a non-category Moneydance account that parents others and has no
    /// transactions of its own. Its children already have parent_id=NULL in Coffer, so
    /// nothing of it is imported, and <see cref="MdCurrencies.Problems"/> skips it too.
    /// </summary>
    public static bool IsPlaceholder(MdAcct acct, MapInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(acct);
        ArgumentNullException.ThrowIfNull(inputs);

        return TranslateType(acct.TypeCode) is (not "category", _)
            && inputs.AccountsThatAreParents.Contains(acct.Id)
            && !inputs.AccountsWithOwnTransactions.Contains(acct.Id);
    }

    /// <summary>
    /// True for Moneydance's own "uncategorized": a TOP-LEVEL income or expense
    /// category (MD type <c>i</c> / <c>e</c>, its parent MD's root or none)
    /// whose name is blank and which parents no other account.
    /// </summary>
    /// <remarks>
    /// <see cref="Map"/> still names any blank account <c>"(unnamed)"</c>;
    /// AccountImportStep uses this predicate to land the category on the
    /// ledger's system Uncategorized instead, the account bank feeds already
    /// put unclassified postings in. A blank category WITH children, or one
    /// nested under another category, is a real node in the user's tree, so it
    /// fails closed and keeps its own <c>"(unnamed)"</c> row in its place. So
    /// does one whose parent the file does not mark as its root.
    /// </remarks>
    public static bool IsBlankLeafCategory(MdAcct acct, MapInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(acct);
        ArgumentNullException.ThrowIfNull(inputs);

        return TranslateType(acct.TypeCode) is ("category", _)
            // A row carrying the kind key goes through Map, which checks it.
            && acct.CofferCategoryKind is null
            && string.IsNullOrWhiteSpace(acct.Name)
            && (acct.ParentId is null || inputs.RootAccounts.Contains(acct.ParentId))
            && !inputs.AccountsThatAreParents.Contains(acct.Id);
    }

    /// <summary>
    /// True when the row carries <c>coffer_category_kind: "adjustment"</c>, which
    /// turns a top-level expense category into an adjustment category
    /// (ADR-0091, amendment of 2026-11-18). Moneydance has no third kind, so the
    /// key keeps the row's type <c>e</c>: an unknown type code would be dropped
    /// and its postings skipped, which fails open.
    /// </summary>
    /// <exception cref="InvalidDataException">The key carries any other value,
    /// or sits on a row that is not a type <c>e</c> category, or on a category
    /// nested under another category. The import fails closed.</exception>
    public static bool IsAdjustmentCategory(MdAcct acct, MapInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(acct);
        ArgumentNullException.ThrowIfNull(inputs);

        var value = acct.CofferCategoryKind;
        if (value is null) return false;

        var topLevel = acct.ParentId is null || inputs.RootAccounts.Contains(acct.ParentId);
        if (value != "adjustment" || acct.TypeCode != "e" || !topLevel)
        {
            throw new InvalidDataException(
                $"acct {acct.Id} '{acct.Name}': coffer_category_kind '{value}' is not supported; " +
                "only \"adjustment\" on a top-level expense category");
        }
        return true;
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
        var rootAccounts = new HashSet<string>(StringComparer.Ordinal);

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
                    if (acct.IsRoot)
                        rootAccounts.Add(acct.Id);
                    break;
            }
        }

        return new MapInputs(accountsWithTxns, accountsThatAreParents, rootAccounts);
    }
}
