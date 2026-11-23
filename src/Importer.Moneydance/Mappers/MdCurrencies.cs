using Coffer.Domain.Money;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// The currency of an account as Moneydance records it: its ISO code, and the decimal
/// places its amounts are scaled by. Moneydance stores every amount as an integer of the
/// currency's minor units, <see cref="Decimals"/> places (its <c>curr</c> item's
/// <c>dec</c>): 0 for the yen, 2 for the euro. <see cref="Decimals"/> is the file's
/// count, at most the places Coffer keeps for <see cref="Code"/> (ADR-0114 D15).
/// </summary>
public sealed record AccountCurrency(string Code, int Decimals);

/// <summary>
/// The currencies a Moneydance export declares, and the ISO code each account and
/// price is in (ADR-0114). Moneydance keeps fiat currencies and securities in one
/// <c>curr</c> table: an <c>acct</c>'s <c>currid</c> is the <c>id</c> of a
/// <c>curr</c> item, whose own <c>currid</c> is the ISO code and whose <c>dec</c> is
/// the decimal places its amounts are stored at. The file's base currency is the one
/// <c>curr</c> item with <c>isbase</c> set. A security's prices are relative to its
/// relative currency (<c>rel_curr_id</c>) when it records one, else to the base.
/// </summary>
/// <remarks>
/// <para>Nothing here defaults to USD or to two decimals. A currency that cannot be read is a
/// problem the import preview refuses with, naming the account or the security
/// (<see cref="Problems"/>), and the pipeline throws on one rather than stamping or
/// scaling by a currency the file does not hold.</para>
/// <para>An account's or the base currency's code must be one Coffer accepts for an
/// account or a ledger (<see cref="CurrencyMinorUnits.IsAcceptedAccountCurrency"/>: an
/// ISO 4217 code with at most 3 places, so not an "N.A." entry, CLF or UYW). An
/// account's places are checked per code (ADR-0114 D15): the file's <c>dec</c> is
/// accepted when it is at most the places Coffer keeps for that code, so every amount
/// fits exactly, and refused when it is more, naming both counts.</para>
/// </remarks>
public sealed class MdCurrencies
{
    /// <summary>The most decimal places any Moneydance amount is scaled by: the most an
    /// accepted currency has, which is the database's 3-place ceiling on leg money
    /// (ADR-0114 D15). Each account's own limit is its code's places
    /// (<see cref="ResolveAccount"/>).</summary>
    public const int MaxAmountDecimals = CurrencyMinorUnits.MaxPlaces;

    private readonly IReadOnlyDictionary<string, MdCurr> _byId;

    private MdCurrencies(IReadOnlyDictionary<string, MdCurr> byId)
    {
        _byId = byId;
        var bases = byId.Values.Where(c => c.IsBase).ToList();
        if (bases.Count == 0)
        {
            BaseCurrencyProblem = "The export marks no currency as its base currency.";
        }
        else if (bases.Count > 1)
        {
            BaseCurrencyProblem =
                $"The export marks {bases.Count} currencies as its base currency, and a file has one.";
        }
        else if (bases[0].IsSecurity)
        {
            BaseCurrencyProblem = $"The export's base currency '{bases[0].Name}' is a security, not a currency.";
        }
        else if (!IsCurrencyCode(bases[0].CurrId))
        {
            BaseCurrencyProblem =
                $"The export's base currency '{bases[0].Name}' has the code '{bases[0].CurrId}', "
                + "which is not a 3-letter currency code.";
        }
        else if (!CurrencyMinorUnits.IsAcceptedAccountCurrency(bases[0].CurrId))
        {
            BaseCurrencyProblem =
                $"The export's base currency '{bases[0].Name}' has the code '{bases[0].CurrId}', "
                + "which is not a currency Coffer accepts for a ledger.";
        }
        else
        {
            BaseCurrencyCode = bases[0].CurrId;
        }
    }

    /// <summary>The ISO code of the file's base currency; null when
    /// <see cref="BaseCurrencyProblem"/> says why it cannot be read.</summary>
    public string? BaseCurrencyCode { get; }

    /// <summary>Why the base currency cannot be read; null when it can.</summary>
    public string? BaseCurrencyProblem { get; }

    /// <summary>Read every <c>curr</c> item of the export.</summary>
    public static MdCurrencies From(MdExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        return From(export.AllItems.Where(i => i.ObjType == "curr").Select(MdCurr.From));
    }

    /// <summary>Build from typed <c>curr</c> items. A repeated id keeps the first.</summary>
    public static MdCurrencies From(IEnumerable<MdCurr> currencies)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        var byId = new Dictionary<string, MdCurr>(StringComparer.Ordinal);
        foreach (var curr in currencies) byId.TryAdd(curr.Id, curr);
        return new MdCurrencies(byId);
    }

    /// <summary>
    /// <paramref name="acct"/>'s own currency, or why it has none: its <c>currid</c> is
    /// missing, names no <c>curr</c> item of the file, names a security, names a
    /// currency whose code is not a 3-letter code or not one Coffer accepts for an
    /// account, or one whose decimal places are missing or more than Coffer keeps for
    /// that code (ADR-0114 D15).
    /// </summary>
    public (AccountCurrency? Currency, string? Problem) ResolveAccount(MdAcct acct)
    {
        ArgumentNullException.ThrowIfNull(acct);
        if (string.IsNullOrWhiteSpace(acct.CurrId))
            return (null, "has no currency");
        if (!_byId.TryGetValue(acct.CurrId, out var curr))
            return (null, $"names a currency ('{acct.CurrId}') the export does not contain");
        if (curr.IsSecurity)
            return (null, $"names the security '{curr.Name}' as its currency");
        if (!IsCurrencyCode(curr.CurrId))
            return (null, $"is in '{curr.Name}', whose code '{curr.CurrId}' is not a 3-letter currency code");
        if (!CurrencyMinorUnits.TryGetAccepted(curr.CurrId, out var places))
            return (null, $"is in '{curr.Name}', whose code '{curr.CurrId}' is not a currency Coffer accepts for an account");
        if (curr.Decimals is not { } decimals || decimals < 0)
            return (null, $"is in '{curr.Name}', which records no decimal places for its amounts");
        if (decimals > places)
            return (null, $"is in '{curr.Name}', whose amounts the file keeps at {decimals} decimal places, "
                + $"and Coffer keeps {curr.CurrId} at {places}");
        return (new AccountCurrency(curr.CurrId, decimals), null);
    }

    /// <summary>
    /// <paramref name="acct"/>'s currency; throws, naming the account, when it has
    /// none. The preview refuses such a file first (<see cref="Problems"/>), so reaching
    /// the throw means a caller skipped that check.
    /// </summary>
    public AccountCurrency RequireAccount(MdAcct acct)
    {
        var (currency, problem) = ResolveAccount(acct);
        return currency ?? throw new InvalidDataException(DescribeAccount(acct, problem!));
    }

    /// <summary>
    /// The ISO code the prices of the security <paramref name="securityCurrId"/> (a
    /// <c>curr</c> item's id) are in, or why it cannot be read. A snapshot's rate is
    /// relative to the security's relative currency when MD records one
    /// (<c>rel_curr_id</c>), else to the file's base currency. A relative currency that
    /// names no currency of the file, names a security, has no 3-letter code, or
    /// disagrees with the <c>relative_to_currid</c> code written beside it, is a
    /// problem; so is a <c>relative_to_currid</c> without the id it names.
    /// </summary>
    public (string? Code, string? Problem) ResolvePriceCurrency(string securityCurrId)
    {
        ArgumentNullException.ThrowIfNull(securityCurrId);
        if (!_byId.TryGetValue(securityCurrId, out var security) || !security.IsSecurity)
            return (null, $"names a security ('{securityCurrId}') the export does not contain");

        if (security.RelativeCurrId is null)
        {
            if (security.RelativeToCode is not null)
                return (null, $"names the relative currency '{security.RelativeToCode}' without the currency it is");
            return BaseCurrencyCode is not null
                ? (BaseCurrencyCode, null)
                : (null, $"is quoted in the file's base currency, which cannot be read ({BaseCurrencyProblem})");
        }

        if (!_byId.TryGetValue(security.RelativeCurrId, out var relative))
            return (null, $"is quoted in a currency ('{security.RelativeCurrId}') the export does not contain");
        if (relative.IsSecurity)
            return (null, $"is quoted in the security '{relative.Name}', not a currency");
        if (!IsCurrencyCode(relative.CurrId))
            return (null, $"is quoted in '{relative.Name}', whose code '{relative.CurrId}' is not a 3-letter currency code");
        if (security.RelativeToCode is { } named && !string.Equals(named, relative.CurrId, StringComparison.Ordinal))
            return (null, $"is quoted in '{relative.CurrId}' by id and '{named}' by code");
        return (relative.CurrId, null);
    }

    /// <summary>The prices' currency of <paramref name="securityCurrId"/>; throws, naming
    /// the security, when it cannot be read (<see cref="ResolvePriceCurrency"/>).</summary>
    public string RequirePriceCurrency(string securityCurrId)
    {
        var (code, problem) = ResolvePriceCurrency(securityCurrId);
        return code ?? throw new InvalidDataException(DescribeSecurity(securityCurrId, problem!));
    }

    /// <summary>
    /// Every reason the export's currencies cannot be imported: the base currency, each
    /// account whose currency the import reads (any type
    /// <see cref="AccountMapper.TranslateType"/> handles: every account it creates, and each
    /// blank top-level leaf category it maps to Uncategorized, whose places it still reads;
    /// less the placeholders <see cref="AccountMapper.IsPlaceholder"/> names, whose currency
    /// and terms are never read) whose own currency cannot be
    /// read, each loan whose escrow or fixed payment carries more places than its
    /// currency (<see cref="LoanMapper.Problem"/>), each category whose own amounts are
    /// written under the reporting currency at more places than that currency keeps
    /// (<see cref="CategoryPlacesProblem"/>), and each security whose prices' currency
    /// cannot be read. Empty when the file can be imported. Shared by the preview, the
    /// start and the pipeline, so they refuse the same files.
    /// </summary>
    /// <param name="reportingCurrency">The new ledger's reporting currency, which every
    /// category is stamped with (ADR-0114 D7): the one the import was given, else the
    /// file's base currency (null).</param>
    public IReadOnlyList<string> Problems(MdExport export, string? reportingCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        var problems = new List<string>();
        if (BaseCurrencyProblem is not null) problems.Add(BaseCurrencyProblem);
        var reporting = reportingCurrency ?? BaseCurrencyCode;
        int? reportingPlaces = reporting is not null && CurrencyMinorUnits.TryGetAccepted(reporting, out var places)
            ? places : null;
        var categoryOrigins = reportingPlaces is null
            ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal)
            : CategoryToCategoryOrigins(export);
        var inputs = AccountMapper.ComputeInputs(export);
        foreach (var item in export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            var acct = MdAcct.From(item);
            if (AccountMapper.TranslateType(acct.TypeCode) is null) continue;
            // The import does not create a placeholder (ADR-0016), so nothing of it is read.
            if (AccountMapper.IsPlaceholder(acct, inputs)) continue;
            var (currency, problem) = ResolveAccount(acct);
            // The loan's terms are checked at the same resolved currency the account
            // step hands LoanMapper.Map.
            problem ??= acct.Loan is { } loan ? LoanMapper.Problem(loan, currency!) : null;
            if (problem is not null)
            {
                problems.Add(DescribeAccount(acct, problem));
                continue;
            }
            if (reportingPlaces is { } reportingAt && categoryOrigins.Contains(acct.Id)
                && CategoryPlacesProblem(acct, currency!, reporting!, reportingAt) is { } categoryProblem)
                problems.Add(categoryProblem);
        }
        foreach (var security in _byId.Values.Where(c => c.IsSecurity))
        {
            // The base currency's own problem is already listed once above.
            if (security.RelativeCurrId is null && security.RelativeToCode is null) continue;
            var (_, problem) = ResolvePriceCurrency(security.Id);
            if (problem is not null) problems.Add(DescribeSecurity(security.Id, problem));
        }
        return problems;
    }

    /// <summary>
    /// Why <paramref name="category"/>'s own amounts do not fit the reporting currency,
    /// or null when they do. A category is stamped with the reporting currency (ADR-0114
    /// D7), and a posting between two categories has no real side, so its legs carry the
    /// primary category's own <c>pamt</c>, scaled by that category's Moneydance places
    /// (<see cref="MdAmounts.Posting"/>), and a figure is kept at its account's
    /// currency's places (ADR-0114 D15). When those places are more than the reporting currency
    /// keeps, the figure would not fit, so the file is refused, naming the category and
    /// both place counts (ADR-0114 D5). Asked only of a category that is the primary account of
    /// such a posting: a category paired with a real account is read at the real side's
    /// places, so its own never reach a stored figure.
    /// </summary>
    /// <returns>The whole sentence, in the owner's words (decided 2026-11-18): it names a
    /// category, not an account. An unnamed category is named by its id, as
    /// <see cref="DescribeAccount"/> names an unnamed account (settled by Claude,
    /// 2026-11-18).</returns>
    private string? CategoryPlacesProblem(
        MdAcct category, AccountCurrency currency, string reportingCurrency, int reportingPlaces)
    {
        if (currency.Decimals <= reportingPlaces) return null;
        var name = _byId.TryGetValue(category.CurrId!, out var curr) ? curr.Name : currency.Code;
        var problem = $"moves money to another category in '{name}' at {currency.Decimals} decimal places, "
            + $"and Coffer keeps a category in the ledger's reporting currency, {reportingCurrency}, at {reportingPlaces}";
        return string.IsNullOrWhiteSpace(category.Name)
            ? $"An unnamed category ({category.Id}) {problem}."
            : $"The category '{category.Name}' {problem}.";
    }

    /// <summary>
    /// The Moneydance ids of each category that is the primary account of a posting whose
    /// other side is another category, in a transaction the bank route imports (not
    /// investment-shaped) or in a reminder's template: the postings whose legs take the
    /// primary category's own <c>pamt</c> (<see cref="MdAmounts.Posting"/>).
    /// </summary>
    /// <remarks>
    /// Only a posting the mappers write counts, judged as they judge it, by the Coffer
    /// account each Moneydance account becomes. Every blank top-level leaf category
    /// becomes the one Uncategorized (<see cref="AccountMapper.IsBlankLeafCategory"/>), so
    /// a split from one onto another lands on its own primary and is never written, like
    /// a split onto the primary itself. A transaction or a reminder the mappers skip
    /// whole writes none of its postings: one with a split onto an account the import
    /// does not create, or a reminder with no start date it can read.
    /// </remarks>
    private static HashSet<string> CategoryToCategoryOrigins(MdExport export)
    {
        var inputs = AccountMapper.ComputeInputs(export);
        // Each account the import creates or maps, by the Coffer account it becomes: its
        // own id, or one key for every blank leaf category. The rule AccountImportStep
        // follows, so the mappers' account lookup holds exactly these.
        const string Uncategorized = "\0uncategorized";
        var becomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            var acct = MdAcct.From(item);
            if (AccountMapper.IsBlankLeafCategory(acct, inputs))
            {
                becomes[acct.Id] = Uncategorized;
                categories.Add(acct.Id);
                continue;
            }
            if (acct.IsRoot || acct.IsSecuritySubAccount) continue;
            if (AccountMapper.TranslateType(acct.TypeCode) is not { } type) continue;
            if (AccountMapper.IsPlaceholder(acct, inputs)) continue;
            becomes[acct.Id] = acct.Id;
            if (type.AccountType == "category") categories.Add(acct.Id);
        }

        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in export.AllItems)
        {
            var prefix = item.ObjType switch
            {
                // Investment-shaped transactions take the investment route, which reads
                // amounts at the brokerage's places only.
                "txn" when !MdTxn.IsInvestmentShapeOf(item.GetString("invest.txntype"), item.GetString("xfer_type")) => "",
                // ReminderMapper skips a reminder whose start date it cannot read.
                "reminder" when MdItem.ParseMdDate(MdReminder.From(item).StartDate) is not null => "txn.",
                _ => null,
            };
            if (prefix is null) continue;
            var primary = item.GetString(prefix + "acctid");
            if (primary is null || !categories.Contains(primary) || origins.Contains(primary)) continue;
            var splits = MdTxn.ExtractSplits(item, prefix);
            if (splits.Any(s => !becomes.ContainsKey(s.AcctId))) continue;
            if (splits.Any(s => categories.Contains(s.AcctId) && becomes[s.AcctId] != becomes[primary]))
                origins.Add(primary);
        }
        return origins;
    }

    private string DescribeSecurity(string securityCurrId, string problem) =>
        _byId.TryGetValue(securityCurrId, out var security) && !string.IsNullOrWhiteSpace(security.Name)
            ? $"The security '{security.Name}' {problem}."
            : $"A security ({securityCurrId}) {problem}.";

    private static string DescribeAccount(MdAcct acct, string problem) =>
        string.IsNullOrWhiteSpace(acct.Name)
            ? $"An unnamed account ({acct.Id}) {problem}."
            : $"The account '{acct.Name}' {problem}.";

    /// <summary>Three upper-case ASCII letters: the stored form of a currency code.</summary>
    internal static bool IsCurrencyCode(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');
}
